using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Parsers;

/// <summary>
/// Parser for Douyin (抖音) short videos.
/// Supports:
///   - v.douyin.com / vm.douyin.com short links
///   - www.douyin.com/video/ direct links
///   - douyinvod.com / douyinpic.com / ixigua.com CDN direct video URLs
/// Strategy:
///   1. If the URL is already a CDN direct link → return immediately
///   2. Follow redirects → extract video ID → call API → strip watermark from play URL
/// </summary>
public class DouyinParser : IVideoParser
{
    public VideoPlatform Platform => VideoPlatform.Douyin;

    // --- URL patterns ---

    private static readonly Regex _shortUrlPattern = new(
        @"(https?://)?(v\.douyin\.com|vm\.douyin\.com)/\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _directUrlPattern = new(
        @"(https?://)?(www\.)?douyin\.com/video/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _iesdouyinPattern = new(
        @"(https?://)?(www\.)?iesdouyin\.com/share/video/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _videoIdFromUrl = new(
        @"/video/(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Matches CDN direct video URLs (douyinvod.com, ixigua.com, etc.)
    /// These are already the final no-watermark video files and can be downloaded directly.
    /// </summary>
    private static readonly Regex _cdnDirectUrlPattern = new(
        @"(https?://)[^\s]*(douyinvod\.com|douyinpic\.com|ixigua\.com|ixiguavideo\.com|bytevcloudtp\.com|byteimg\.com|amemv\.com)[^\s]*/video/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Broader CDN pattern: any URL on known Douyin CDN domains containing media-video or /video/ path.
    /// </summary>
    private static readonly Regex _cdnBroadPattern = new(
        @"https?://[a-z0-9\-]+\.(douyinvod\.com|ixiguavideo\.com|bytevcloudtp\.com|amemv\.com)/[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _renderDataPattern = new(
        @"<script\s+id=""RENDER_DATA""\s+type=""application/json"">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public bool CanParse(string url)
    {
        return _shortUrlPattern.IsMatch(url) ||
               _directUrlPattern.IsMatch(url) ||
               _iesdouyinPattern.IsMatch(url) ||
               IsCdnDirectUrl(url);
    }

    public async Task<VideoInfo> ParseAsync(string url, CancellationToken ct = default)
    {
        // Fast path: CDN direct video URL → no parsing needed, return as-is
        if (IsCdnDirectUrl(url))
            return BuildCdnDirectResult(url);

        var videoId = await ExtractVideoIdAsync(url, ct);
        if (string.IsNullOrEmpty(videoId))
            throw new InvalidOperationException("无法从链接中提取抖音视频ID");

        return await GetVideoInfoByIdAsync(videoId, url, ct);
    }

    /// <summary>
    /// Checks if the URL is a CDN direct video link (douyinvod.com, ixigua.com, etc.)
    /// </summary>
    private static bool IsCdnDirectUrl(string url)
    {
        return _cdnDirectUrlPattern.IsMatch(url) || _cdnBroadPattern.IsMatch(url);
    }

    /// <summary>
    /// Builds a VideoInfo for a CDN direct URL that needs no further parsing.
    /// </summary>
    private static VideoInfo BuildCdnDirectResult(string url)
    {
        return new VideoInfo
        {
            Platform = VideoPlatform.Douyin,
            OriginalUrl = url,
            VideoUrl = url,
            Title = $"douyin_{DateTime.Now:yyyyMMdd_HHmmss}",
            Author = "抖音",
            VideoId = ExtractCdnVideoId(url) ?? string.Empty
        };
    }

    /// <summary>
    /// Attempts to extract a meaningful ID from a CDN URL for filename generation.
    /// </summary>
    private static string? ExtractCdnVideoId(string url)
    {
        var match = Regex.Match(url, @"/([a-f0-9]{32})/", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value[..12];

        match = Regex.Match(url, @"/video/tos/[^/]+/([^/?]+)");
        if (match.Success)
            return match.Groups[1].Value;

        return null;
    }

    private async Task<string?> ExtractVideoIdAsync(string url, CancellationToken ct)
    {
        var directMatch = _directUrlPattern.Match(url);
        if (directMatch.Success)
            return directMatch.Groups[3].Value;

        var iesdMatch = _iesdouyinPattern.Match(url);
        if (iesdMatch.Success)
            return iesdMatch.Groups[3].Value;

        var finalUrl = await HttpHelper.FollowRedirectsAsync(url, ct);
        var idMatch = _videoIdFromUrl.Match(finalUrl);
        if (idMatch.Success)
            return idMatch.Groups[1].Value;

        var modalMatch = Regex.Match(finalUrl, @"modal_id=(\d+)");
        if (modalMatch.Success)
            return modalMatch.Groups[1].Value;

        return null;
    }

    private async Task<VideoInfo> GetVideoInfoByIdAsync(string videoId, string originalUrl, CancellationToken ct)
    {
        var info = new VideoInfo
        {
            Platform = VideoPlatform.Douyin,
            OriginalUrl = originalUrl,
            VideoId = videoId
        };

        // Strategy 1: try the iesdouyin API
        try
        {
            var apiUrl = $"https://www.iesdouyin.com/web/api/v2/aweme/iteminfo/?item_ids={videoId}";
            var json = await HttpHelper.GetStringMobileAsync(apiUrl, ct);
            ParseApiResponse(json, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* fallthrough to strategy 2 */ }

        // Strategy 2: scrape the Douyin web page for RENDER_DATA
        try
        {
            var pageUrl = $"https://www.douyin.com/video/{videoId}";
            var html = await HttpHelper.GetStringDesktopAsync(pageUrl, ct);
            ParsePageData(html, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* fallthrough */ }

        // Strategy 3: try the Douyin detail API with cookie
        try
        {
            var detailApiUrl = $"https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id={videoId}&aid=1128&version_name=23.5.0";
            var json = await HttpHelper.GetStringDesktopAsync(detailApiUrl, ct);
            ParseDetailApiResponse(json, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* fallthrough */ }

        info.Title = string.IsNullOrEmpty(info.Title) ? $"douyin_{videoId}" : info.Title;
        info.Author = string.IsNullOrEmpty(info.Author) ? "未知" : info.Author;

        if (string.IsNullOrEmpty(info.VideoUrl))
            throw new InvalidOperationException(
                $"无法获取视频地址。视频ID: {videoId}。请尝试直接提供抖音 CDN 视频链接（douyinvod.com 域名）。");

        return info;
    }

    private static void ParseApiResponse(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("item_list", out var itemList) || itemList.GetArrayLength() == 0)
            return;

        var item = itemList[0];

        if (item.TryGetProperty("desc", out var desc))
            info.Title = desc.GetString() ?? string.Empty;

        if (item.TryGetProperty("author", out var author) &&
            author.TryGetProperty("nickname", out var nickname))
            info.Author = nickname.GetString() ?? string.Empty;

        if (item.TryGetProperty("video", out var video))
        {
            if (video.TryGetProperty("cover", out var cover) &&
                cover.TryGetProperty("url_list", out var coverUrls) &&
                coverUrls.GetArrayLength() > 0)
            {
                info.CoverUrl = coverUrls[0].GetString() ?? string.Empty;
            }

            // Try play_addr first (most common)
            ExtractVideoUrlFromPlayAddr(video, info);

            // Try bit_rate array for higher quality URL
            if (string.IsNullOrEmpty(info.VideoUrl))
                ExtractVideoUrlFromBitRate(video, info);

            if (video.TryGetProperty("duration", out var duration))
                info.Duration = duration.GetInt64();
        }
    }

    private static void ExtractVideoUrlFromPlayAddr(JsonElement video, VideoInfo info)
    {
        if (!video.TryGetProperty("play_addr", out var playAddr))
            return;

        if (playAddr.TryGetProperty("url_list", out var urlList) && urlList.GetArrayLength() > 0)
        {
            // Prefer URLs on CDN domains (douyinvod.com, etc.) over API redirect URLs
            string? bestUrl = null;
            foreach (var urlElem in urlList.EnumerateArray())
            {
                var candidate = urlElem.GetString();
                if (string.IsNullOrEmpty(candidate)) continue;

                if (IsCdnVideoUrl(candidate))
                {
                    bestUrl = candidate;
                    break;
                }
                bestUrl ??= candidate;
            }

            if (!string.IsNullOrEmpty(bestUrl))
                info.VideoUrl = RemoveWatermark(bestUrl);
        }

        if (string.IsNullOrEmpty(info.VideoUrl) &&
            playAddr.TryGetProperty("uri", out var uri))
        {
            var videoUri = uri.GetString();
            if (!string.IsNullOrEmpty(videoUri))
                info.VideoUrl = $"https://aweme.snssdk.com/aweme/v1/play/?video_id={videoUri}&ratio=1080p&line=0";
        }
    }

    private static void ExtractVideoUrlFromBitRate(JsonElement video, VideoInfo info)
    {
        if (!video.TryGetProperty("bit_rate", out var bitRate) ||
            bitRate.ValueKind != JsonValueKind.Array ||
            bitRate.GetArrayLength() == 0)
            return;

        // bit_rate[0] is usually the highest quality
        var best = bitRate[0];
        if (best.TryGetProperty("play_addr", out var playAddr) &&
            playAddr.TryGetProperty("url_list", out var urlList) &&
            urlList.GetArrayLength() > 0)
        {
            foreach (var urlElem in urlList.EnumerateArray())
            {
                var candidate = urlElem.GetString();
                if (!string.IsNullOrEmpty(candidate) && IsCdnVideoUrl(candidate))
                {
                    info.VideoUrl = RemoveWatermark(candidate);
                    return;
                }
            }

            var fallback = urlList[0].GetString();
            if (!string.IsNullOrEmpty(fallback))
                info.VideoUrl = RemoveWatermark(fallback);
        }
    }

    private static void ParseDetailApiResponse(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement detail;
        if (root.TryGetProperty("aweme_detail", out detail) ||
            root.TryGetProperty("aweme", out detail))
        {
            // fall through to extract
        }
        else
        {
            return;
        }

        if (detail.TryGetProperty("desc", out var desc))
            info.Title = desc.GetString() ?? info.Title;

        if (detail.TryGetProperty("author", out var author) &&
            author.TryGetProperty("nickname", out var nick))
            info.Author = nick.GetString() ?? info.Author;

        if (detail.TryGetProperty("video", out var video))
        {
            ExtractVideoUrlFromPlayAddr(video, info);
            if (string.IsNullOrEmpty(info.VideoUrl))
                ExtractVideoUrlFromBitRate(video, info);

            if (video.TryGetProperty("cover", out var cover) &&
                cover.TryGetProperty("url_list", out var coverUrls) &&
                coverUrls.GetArrayLength() > 0)
            {
                info.CoverUrl = coverUrls[0].GetString() ?? string.Empty;
            }

            if (video.TryGetProperty("duration", out var duration))
                info.Duration = duration.GetInt64();
        }
    }

    private static void ParsePageData(string html, VideoInfo info)
    {
        var match = _renderDataPattern.Match(html);
        if (!match.Success)
            return;

        var encoded = match.Groups[1].Value;
        var decoded = HttpUtility.UrlDecode(encoded);

        using var doc = JsonDocument.Parse(decoded);
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            try
            {
                var val = prop.Value;
                if (val.ValueKind != JsonValueKind.Object) continue;

                if (val.TryGetProperty("aweme", out var aweme) ||
                    val.TryGetProperty("awemeDetail", out aweme))
                {
                    if (aweme.TryGetProperty("detail", out var detail))
                        aweme = detail;

                    if (aweme.TryGetProperty("desc", out var desc))
                        info.Title = desc.GetString() ?? info.Title;

                    if (aweme.TryGetProperty("authorInfo", out var authorInfo) &&
                        authorInfo.TryGetProperty("nickname", out var nick))
                        info.Author = nick.GetString() ?? info.Author;
                    else if (aweme.TryGetProperty("author", out var author2) &&
                             author2.TryGetProperty("nickname", out var nick2))
                        info.Author = nick2.GetString() ?? info.Author;

                    if (aweme.TryGetProperty("video", out var v))
                    {
                        // Try playApi field
                        if (v.TryGetProperty("playApi", out var playApi))
                        {
                            var playUrl = playApi.GetString() ?? string.Empty;
                            if (!playUrl.StartsWith("http"))
                                playUrl = "https:" + playUrl;
                            info.VideoUrl = RemoveWatermark(playUrl);
                        }

                        // Try play_addr for CDN URLs
                        if (string.IsNullOrEmpty(info.VideoUrl) || !IsCdnVideoUrl(info.VideoUrl))
                            ExtractVideoUrlFromPlayAddr(v, info);

                        // Try bit_rate
                        if (string.IsNullOrEmpty(info.VideoUrl) || !IsCdnVideoUrl(info.VideoUrl))
                            ExtractVideoUrlFromBitRate(v, info);
                    }

                    break;
                }
            }
            catch { continue; }
        }
    }

    /// <summary>
    /// Checks if a URL is on a known Douyin CDN domain (the actual video file host).
    /// </summary>
    private static bool IsCdnVideoUrl(string url)
    {
        return url.Contains("douyinvod.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("ixigua.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("ixiguavideo.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bytevcloudtp.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("amemv.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes watermark from Douyin play URL by replacing "playwm" with "play"
    /// and adjusting the watermark query parameter.
    /// </summary>
    private static string RemoveWatermark(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        url = url.Replace("playwm", "play");
        url = Regex.Replace(url, @"[?&]watermark=\d+", "");

        return url;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Parsers;

/// <summary>
/// Parser for Douyin (抖音) short videos.
/// Supports: v.douyin.com short links, www.douyin.com/video/ direct links.
/// Strategy: follow redirects → extract video ID → call API → strip watermark from play URL.
/// </summary>
public class DouyinParser : IVideoParser
{
    public VideoPlatform Platform => VideoPlatform.Douyin;

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

    private static readonly Regex _renderDataPattern = new(
        @"<script\s+id=""RENDER_DATA""\s+type=""application/json"">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public bool CanParse(string url)
    {
        return _shortUrlPattern.IsMatch(url) ||
               _directUrlPattern.IsMatch(url) ||
               _iesdouyinPattern.IsMatch(url);
    }

    public async Task<VideoInfo> ParseAsync(string url, CancellationToken ct = default)
    {
        var videoId = await ExtractVideoIdAsync(url, ct);
        if (string.IsNullOrEmpty(videoId))
            throw new InvalidOperationException("无法从链接中提取抖音视频ID");

        return await GetVideoInfoByIdAsync(videoId, url, ct);
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

        // Strategy 3: construct a direct play URL as last resort
        info.VideoUrl = $"https://aweme.snssdk.com/aweme/v1/play/?video_id={videoId}&ratio=1080p&line=0";
        info.Title = string.IsNullOrEmpty(info.Title) ? $"douyin_{videoId}" : info.Title;
        info.Author = string.IsNullOrEmpty(info.Author) ? "未知" : info.Author;

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

            if (video.TryGetProperty("play_addr", out var playAddr) &&
                playAddr.TryGetProperty("url_list", out var urlList) &&
                urlList.GetArrayLength() > 0)
            {
                var playUrl = urlList[0].GetString() ?? string.Empty;
                info.VideoUrl = RemoveWatermark(playUrl);
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

        // Navigate through RENDER_DATA structure to find video info
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

                    if (aweme.TryGetProperty("video", out var v) &&
                        v.TryGetProperty("playApi", out var playApi))
                    {
                        var playUrl = playApi.GetString() ?? string.Empty;
                        if (!playUrl.StartsWith("http"))
                            playUrl = "https:" + playUrl;
                        info.VideoUrl = RemoveWatermark(playUrl);
                    }

                    break;
                }
            }
            catch { continue; }
        }
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

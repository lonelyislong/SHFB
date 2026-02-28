using System.Text.Json;
using System.Text.RegularExpressions;
using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Parsers;

/// <summary>
/// Parser for TikTok international short videos.
/// Supports: vm.tiktok.com, vt.tiktok.com short links, www.tiktok.com/@user/video/ direct links.
/// Strategy: follow redirects → extract video ID → scrape page for __UNIVERSAL_DATA → strip watermark.
/// </summary>
public class TikTokParser : IVideoParser
{
    public VideoPlatform Platform => VideoPlatform.TikTok;

    private static readonly Regex _shortUrlPattern = new(
        @"(https?://)?(vm|vt)\.tiktok\.com/\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _directUrlPattern = new(
        @"(https?://)?(www\.)?tiktok\.com/@[\w.]+/video/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _videoIdPattern = new(
        @"/video/(\d+)", RegexOptions.Compiled);

    private static readonly Regex _universalDataPattern = new(
        @"<script\s+id=""__UNIVERSAL_DATA_FOR_REHYDRATION__""\s+type=""application/json"">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex _sigiStatePattern = new(
        @"<script\s+id=""SIGI_STATE""[^>]*>(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public bool CanParse(string url)
    {
        return _shortUrlPattern.IsMatch(url) ||
               _directUrlPattern.IsMatch(url) ||
               url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<VideoInfo> ParseAsync(string url, CancellationToken ct = default)
    {
        var (videoId, finalUrl) = await ExtractVideoIdAsync(url, ct);
        if (string.IsNullOrEmpty(videoId))
            throw new InvalidOperationException("无法从链接中提取TikTok视频ID");

        return await GetVideoInfoAsync(videoId, finalUrl ?? url, ct);
    }

    private async Task<(string? id, string? finalUrl)> ExtractVideoIdAsync(string url, CancellationToken ct)
    {
        var directMatch = _directUrlPattern.Match(url);
        if (directMatch.Success)
            return (directMatch.Groups[3].Value, url);

        var finalUrl = await HttpHelper.FollowRedirectsAsync(url, ct);
        var idMatch = _videoIdPattern.Match(finalUrl);
        return idMatch.Success ? (idMatch.Groups[1].Value, finalUrl) : (null, finalUrl);
    }

    private async Task<VideoInfo> GetVideoInfoAsync(string videoId, string pageUrl, CancellationToken ct)
    {
        var info = new VideoInfo
        {
            Platform = VideoPlatform.TikTok,
            OriginalUrl = pageUrl,
            VideoId = videoId
        };

        // Strategy 1: oEmbed API for basic metadata
        try
        {
            var oembedUrl = $"https://www.tiktok.com/oembed?url={Uri.EscapeDataString(pageUrl)}";
            var oembedJson = await HttpHelper.GetStringDesktopAsync(oembedUrl, ct);
            ParseOembed(oembedJson, info);
        }
        catch { /* continue */ }

        // Strategy 2: scrape page for embedded JSON data
        try
        {
            var directPageUrl = $"https://www.tiktok.com/@placeholder/video/{videoId}";
            var html = await HttpHelper.GetStringDesktopAsync(directPageUrl, ct);
            ParsePageData(html, info);
        }
        catch { /* continue */ }

        // Strategy 3: construct a no-watermark play URL
        if (string.IsNullOrEmpty(info.VideoUrl))
        {
            info.VideoUrl = $"https://api16-normal-c-useast1a.tiktokv.com/aweme/v1/play/?video_id={videoId}&is_play_url=1&source=PackSourceEnum_PUBLISH";
        }

        info.Title = string.IsNullOrEmpty(info.Title) ? $"tiktok_{videoId}" : info.Title;
        info.Author = string.IsNullOrEmpty(info.Author) ? "Unknown" : info.Author;

        return info;
    }

    private static void ParseOembed(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("title", out var title))
            info.Title = title.GetString() ?? info.Title;

        if (root.TryGetProperty("author_name", out var author))
            info.Author = author.GetString() ?? info.Author;

        if (root.TryGetProperty("thumbnail_url", out var thumb))
            info.CoverUrl = thumb.GetString() ?? info.CoverUrl;
    }

    private static void ParsePageData(string html, VideoInfo info)
    {
        // Try __UNIVERSAL_DATA_FOR_REHYDRATION__
        var match = _universalDataPattern.Match(html);
        if (match.Success)
        {
            try
            {
                ParseUniversalData(match.Groups[1].Value, info);
                if (!string.IsNullOrEmpty(info.VideoUrl))
                    return;
            }
            catch { /* try next pattern */ }
        }

        // Try SIGI_STATE
        var sigiMatch = _sigiStatePattern.Match(html);
        if (sigiMatch.Success)
        {
            try
            {
                ParseSigiState(sigiMatch.Groups[1].Value, info);
            }
            catch { /* continue */ }
        }
    }

    private static void ParseUniversalData(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("__DEFAULT_SCOPE__", out var scope))
            return;

        if (!scope.TryGetProperty("webapp.video-detail", out var videoDetail))
            return;

        if (!videoDetail.TryGetProperty("itemInfo", out var itemInfo) ||
            !itemInfo.TryGetProperty("itemStruct", out var item))
            return;

        ExtractFromItemStruct(item, info);
    }

    private static void ParseSigiState(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ItemModule", out var itemModule))
            return;

        foreach (var prop in itemModule.EnumerateObject())
        {
            ExtractFromItemStruct(prop.Value, info);
            break;
        }
    }

    private static void ExtractFromItemStruct(JsonElement item, VideoInfo info)
    {
        if (item.TryGetProperty("desc", out var desc))
            info.Title = desc.GetString() ?? info.Title;

        if (item.TryGetProperty("author", out var author))
        {
            if (author.ValueKind == JsonValueKind.Object &&
                author.TryGetProperty("nickname", out var nickname))
                info.Author = nickname.GetString() ?? info.Author;
            else if (author.ValueKind == JsonValueKind.String)
                info.Author = author.GetString() ?? info.Author;
        }

        if (item.TryGetProperty("video", out var video))
        {
            if (video.TryGetProperty("downloadAddr", out var downloadAddr))
            {
                var downloadUrl = downloadAddr.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    info.VideoUrl = RemoveWatermark(downloadUrl);
                    return;
                }
            }

            if (video.TryGetProperty("playAddr", out var playAddr))
            {
                var playUrl = playAddr.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(playUrl))
                    info.VideoUrl = RemoveWatermark(playUrl);
            }

            if (video.TryGetProperty("cover", out var cover))
                info.CoverUrl = cover.GetString() ?? info.CoverUrl;

            if (video.TryGetProperty("duration", out var duration))
                info.Duration = duration.GetInt64();
        }
    }

    private static string RemoveWatermark(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        url = url.Replace("watermark=1", "watermark=0");
        url = url.Replace("/playwm/", "/play/");

        return url;
    }
}

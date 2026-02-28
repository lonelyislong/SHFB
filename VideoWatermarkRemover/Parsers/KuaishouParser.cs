using System.Text.Json;
using System.Text.RegularExpressions;
using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Parsers;

/// <summary>
/// Parser for Kuaishou (快手) short videos.
/// Supports: v.kuaishou.com short links, www.kuaishou.com/short-video/ direct links.
/// Strategy: follow redirects → scrape page for embedded video data in JSON → extract no-watermark URL.
/// </summary>
public class KuaishouParser : IVideoParser
{
    public VideoPlatform Platform => VideoPlatform.Kuaishou;

    private static readonly Regex _shortUrlPattern = new(
        @"(https?://)?(v\.kuaishou\.com|v\.m\.chenzhongtech\.com)/\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _directUrlPattern = new(
        @"(https?://)?(www\.)?kuaishou\.com/(short-video|f)/(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _videoUrlPattern = new(
        @"""photoUrl""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _videoUrlPattern2 = new(
        @"""srcNoMark""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _titlePattern = new(
        @"""caption""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _authorPattern = new(
        @"""userName""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _coverPattern = new(
        @"""coverUrl""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _photoIdPattern = new(
        @"/(short-video|f|fw/photo)/(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex _apolloStatePattern = new(
        @"window\.__APOLLO_STATE__\s*=\s*(\{.*?\});\s*</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex _initialDataPattern = new(
        @"window\.__INITIAL_DATA__\s*=\s*(\{.*?\});\s*</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public bool CanParse(string url)
    {
        return _shortUrlPattern.IsMatch(url) ||
               _directUrlPattern.IsMatch(url) ||
               url.Contains("kuaishou.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("chenzhongtech.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<VideoInfo> ParseAsync(string url, CancellationToken ct = default)
    {
        var finalUrl = url;
        if (_shortUrlPattern.IsMatch(url))
            finalUrl = await HttpHelper.FollowRedirectsAsync(url, ct);

        var photoId = ExtractPhotoId(finalUrl) ?? ExtractPhotoId(url);

        var info = new VideoInfo
        {
            Platform = VideoPlatform.Kuaishou,
            OriginalUrl = url,
            VideoId = photoId ?? string.Empty
        };

        // Strategy 1: mobile page scraping
        try
        {
            var mobileUrl = !string.IsNullOrEmpty(photoId)
                ? $"https://v.m.chenzhongtech.com/fw/photo/{photoId}"
                : finalUrl;
            var html = await HttpHelper.GetStringMobileAsync(mobileUrl, ct);
            ParseMobilePage(html, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* try next */ }

        // Strategy 2: desktop page scraping
        try
        {
            var desktopUrl = !string.IsNullOrEmpty(photoId)
                ? $"https://www.kuaishou.com/short-video/{photoId}"
                : finalUrl;
            var html = await HttpHelper.GetStringDesktopAsync(desktopUrl, ct);
            ParseDesktopPage(html, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* continue */ }

        if (string.IsNullOrEmpty(info.VideoUrl))
            throw new InvalidOperationException("无法从快手链接中获取视频地址");

        return info;
    }

    private static string? ExtractPhotoId(string url)
    {
        var match = _photoIdPattern.Match(url);
        return match.Success ? match.Groups[2].Value : null;
    }

    private static void ParseMobilePage(string html, VideoInfo info)
    {
        var videoUrlMatch = _videoUrlPattern2.Match(html);
        if (!videoUrlMatch.Success)
            videoUrlMatch = _videoUrlPattern.Match(html);

        if (videoUrlMatch.Success)
        {
            var videoUrl = videoUrlMatch.Groups[1].Value
                .Replace("\\u002F", "/")
                .Replace("\\/", "/");
            if (!videoUrl.StartsWith("http"))
                videoUrl = "https:" + videoUrl;
            info.VideoUrl = videoUrl;
        }

        var titleMatch = _titlePattern.Match(html);
        if (titleMatch.Success)
            info.Title = UnescapeJson(titleMatch.Groups[1].Value);

        var authorMatch = _authorPattern.Match(html);
        if (authorMatch.Success)
            info.Author = UnescapeJson(authorMatch.Groups[1].Value);

        var coverMatch = _coverPattern.Match(html);
        if (coverMatch.Success)
        {
            var coverUrl = coverMatch.Groups[1].Value.Replace("\\u002F", "/").Replace("\\/", "/");
            if (!coverUrl.StartsWith("http"))
                coverUrl = "https:" + coverUrl;
            info.CoverUrl = coverUrl;
        }

        // Also try parsing structured JSON from script tags
        if (string.IsNullOrEmpty(info.VideoUrl))
            TryParseJsonFromScript(html, info);
    }

    private static void ParseDesktopPage(string html, VideoInfo info)
    {
        // Try Apollo state
        var apolloMatch = _apolloStatePattern.Match(html);
        if (apolloMatch.Success)
        {
            try
            {
                ParseApolloState(apolloMatch.Groups[1].Value, info);
                if (!string.IsNullOrEmpty(info.VideoUrl))
                    return;
            }
            catch { /* continue */ }
        }

        // Try INITIAL_DATA
        var initialMatch = _initialDataPattern.Match(html);
        if (initialMatch.Success)
        {
            try
            {
                ParseInitialData(initialMatch.Groups[1].Value, info);
            }
            catch { /* continue */ }
        }

        // Fallback to regex
        ParseMobilePage(html, info);
    }

    private static void TryParseJsonFromScript(string html, VideoInfo info)
    {
        var scriptPattern = new Regex(@"<script[^>]*>(.*?)</script>", RegexOptions.Singleline);
        foreach (Match m in scriptPattern.Matches(html))
        {
            var content = m.Groups[1].Value;
            if (!content.Contains("photoUrl") && !content.Contains("srcNoMark"))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(content);
                ExtractFromJson(doc.RootElement, info);
                if (!string.IsNullOrEmpty(info.VideoUrl))
                    return;
            }
            catch { continue; }
        }
    }

    private static void ParseApolloState(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        ExtractFromJson(doc.RootElement, info);
    }

    private static void ParseInitialData(string json, VideoInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        ExtractFromJson(doc.RootElement, info);
    }

    private static void ExtractFromJson(JsonElement element, VideoInfo info)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == "srcNoMark" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var url = prop.Value.GetString()!;
                    if (!url.StartsWith("http")) url = "https:" + url;
                    info.VideoUrl = url;
                }
                else if (prop.Name == "photoUrl" && prop.Value.ValueKind == JsonValueKind.String &&
                         string.IsNullOrEmpty(info.VideoUrl))
                {
                    var url = prop.Value.GetString()!;
                    if (!url.StartsWith("http")) url = "https:" + url;
                    info.VideoUrl = url;
                }
                else if (prop.Name == "caption" && prop.Value.ValueKind == JsonValueKind.String &&
                         string.IsNullOrEmpty(info.Title))
                {
                    info.Title = prop.Value.GetString()!;
                }
                else if (prop.Name == "userName" && prop.Value.ValueKind == JsonValueKind.String &&
                         string.IsNullOrEmpty(info.Author))
                {
                    info.Author = prop.Value.GetString()!;
                }
                else
                {
                    ExtractFromJson(prop.Value, info);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractFromJson(item, info);
        }
    }

    private static string UnescapeJson(string value)
    {
        return value
            .Replace("\\n", "\n")
            .Replace("\\t", "\t")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\u002F", "/");
    }
}

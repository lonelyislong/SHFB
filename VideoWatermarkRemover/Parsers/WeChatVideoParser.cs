using System.Text.Json;
using System.Text.RegularExpressions;
using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Parsers;

/// <summary>
/// Parser for WeChat Channels (微信视频号) videos.
/// Supports: channels.weixin.qq.com share links.
/// Strategy: scrape the share page → extract embedded video data → return the direct play URL.
/// Note: WeChat is a closed ecosystem. Some share links require cookie/login state.
/// </summary>
public class WeChatVideoParser : IVideoParser
{
    public VideoPlatform Platform => VideoPlatform.WeChatVideo;

    private static readonly Regex _channelsPattern = new(
        @"(https?://)?channels\.weixin\.qq\.com/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _weixinPattern = new(
        @"(https?://)?mp\.weixin\.qq\.com/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _videoSrcPattern = new(
        @"""url""\s*:\s*""(https?://[^""]*finder\.video\.qq\.com[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _videoSrcPattern2 = new(
        @"""mediaUrl""\s*:\s*""(https?://[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _mpVideoPattern = new(
        @"mpVideo\.src\s*=\s*""(https?://[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _videoTagPattern = new(
        @"<video[^>]+src\s*=\s*[""'](https?://[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _objectDataPattern = new(
        @"window\.__INITIAL_DATA__\s*=\s*(\{.*?\})\s*;",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex _titlePattern = new(
        @"""nickname""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _descPattern = new(
        @"""desc""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex _feedIdPattern = new(
        @"feed/(\w+)", RegexOptions.Compiled);

    public bool CanParse(string url)
    {
        return _channelsPattern.IsMatch(url) ||
               (_weixinPattern.IsMatch(url) && url.Contains("sph", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<VideoInfo> ParseAsync(string url, CancellationToken ct = default)
    {
        var finalUrl = await HttpHelper.FollowRedirectsAsync(url, ct);
        var feedId = ExtractFeedId(finalUrl) ?? ExtractFeedId(url);

        var info = new VideoInfo
        {
            Platform = VideoPlatform.WeChatVideo,
            OriginalUrl = url,
            VideoId = feedId ?? string.Empty
        };

        // Strategy 1: scrape the share page
        try
        {
            var html = await HttpHelper.GetStringDesktopAsync(
                string.IsNullOrEmpty(finalUrl) ? url : finalUrl, ct);
            ParseSharePage(html, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* continue */ }

        // Strategy 2: try mobile page if different
        try
        {
            var html = await HttpHelper.GetStringMobileAsync(
                string.IsNullOrEmpty(finalUrl) ? url : finalUrl, ct);
            ParseSharePage(html, info);
            if (!string.IsNullOrEmpty(info.VideoUrl))
                return info;
        }
        catch { /* continue */ }

        if (string.IsNullOrEmpty(info.VideoUrl))
            throw new InvalidOperationException(
                "无法从微信视频号链接中获取视频地址。微信视频号为封闭平台，部分链接需要登录态才能访问。");

        return info;
    }

    private static string? ExtractFeedId(string url)
    {
        var match = _feedIdPattern.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void ParseSharePage(string html, VideoInfo info)
    {
        // Try finder.video.qq.com URL pattern
        var videoMatch = _videoSrcPattern.Match(html);
        if (videoMatch.Success)
        {
            info.VideoUrl = UnescapeUrl(videoMatch.Groups[1].Value);
        }

        // Try mediaUrl pattern
        if (string.IsNullOrEmpty(info.VideoUrl))
        {
            var mediaMatch = _videoSrcPattern2.Match(html);
            if (mediaMatch.Success)
                info.VideoUrl = UnescapeUrl(mediaMatch.Groups[1].Value);
        }

        // Try mpVideo.src pattern (for embedded WeChat articles)
        if (string.IsNullOrEmpty(info.VideoUrl))
        {
            var mpMatch = _mpVideoPattern.Match(html);
            if (mpMatch.Success)
                info.VideoUrl = UnescapeUrl(mpMatch.Groups[1].Value);
        }

        // Try plain <video src=...> tag
        if (string.IsNullOrEmpty(info.VideoUrl))
        {
            var tagMatch = _videoTagPattern.Match(html);
            if (tagMatch.Success)
                info.VideoUrl = UnescapeUrl(tagMatch.Groups[1].Value);
        }

        // Try to extract structured data
        var initialMatch = _objectDataPattern.Match(html);
        if (initialMatch.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(initialMatch.Groups[1].Value);
                ExtractFromInitialData(doc.RootElement, info);
            }
            catch { /* continue */ }
        }

        // Extract title/author from regex if not found
        if (string.IsNullOrEmpty(info.Title))
        {
            var descMatch = _descPattern.Match(html);
            if (descMatch.Success)
                info.Title = UnescapeJson(descMatch.Groups[1].Value);
        }

        if (string.IsNullOrEmpty(info.Author))
        {
            var nickMatch = _titlePattern.Match(html);
            if (nickMatch.Success)
                info.Author = UnescapeJson(nickMatch.Groups[1].Value);
        }

        // Set defaults
        if (string.IsNullOrEmpty(info.Title))
            info.Title = $"wechat_{info.VideoId}";
        if (string.IsNullOrEmpty(info.Author))
            info.Author = "微信视频号";
    }

    private static void ExtractFromInitialData(JsonElement element, VideoInfo info)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == "url" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var url = prop.Value.GetString() ?? string.Empty;
                    if (url.Contains("finder.video") || url.Contains(".mp4") || url.Contains("video"))
                    {
                        if (string.IsNullOrEmpty(info.VideoUrl))
                            info.VideoUrl = UnescapeUrl(url);
                    }
                }
                else if (prop.Name == "nickname" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    info.Author = prop.Value.GetString() ?? info.Author;
                }
                else if (prop.Name == "desc" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    info.Title = prop.Value.GetString() ?? info.Title;
                }
                else
                {
                    ExtractFromInitialData(prop.Value, info);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractFromInitialData(item, info);
        }
    }

    private static string UnescapeUrl(string url)
    {
        return url
            .Replace("\\u002F", "/")
            .Replace("\\/", "/")
            .Replace("\\u0026", "&")
            .Replace("&amp;", "&");
    }

    private static string UnescapeJson(string value)
    {
        return value
            .Replace("\\n", "\n")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\u002F", "/");
    }
}

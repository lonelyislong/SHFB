using VideoWatermarkRemover.Core;
using VideoWatermarkRemover.Models;
using VideoWatermarkRemover.Parsers;

namespace VideoWatermarkRemover.Services;

/// <summary>
/// Orchestrates video parsing and downloading across all supported platforms.
/// </summary>
public class WatermarkRemovalService
{
    private readonly List<IVideoParser> _parsers;

    public WatermarkRemovalService()
    {
        _parsers = new List<IVideoParser>
        {
            new DouyinParser(),
            new TikTokParser(),
            new KuaishouParser(),
            new WeChatVideoParser()
        };
    }

    /// <summary>
    /// Returns a human-readable list of supported platforms.
    /// </summary>
    public IReadOnlyList<string> SupportedPlatforms => new[]
    {
        "抖音 (Douyin)  - v.douyin.com / www.douyin.com",
        "TikTok         - vm.tiktok.com / www.tiktok.com",
        "快手 (Kuaishou) - v.kuaishou.com / www.kuaishou.com",
        "微信视频号      - channels.weixin.qq.com"
    };

    /// <summary>
    /// Detects the platform from a URL or share text.
    /// </summary>
    public VideoPlatform DetectPlatform(string input)
    {
        var url = HttpHelper.ExtractUrl(input) ?? input;
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(url))
                return parser.Platform;
        }
        return VideoPlatform.Unknown;
    }

    /// <summary>
    /// Parses video information from a URL or share text, returning no-watermark video URL.
    /// </summary>
    public async Task<VideoInfo> ParseAsync(string input, CancellationToken ct = default)
    {
        var url = HttpHelper.ExtractUrl(input);
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("输入中未找到有效的URL链接");

        var parser = _parsers.FirstOrDefault(p => p.CanParse(url));
        if (parser == null)
            throw new NotSupportedException($"暂不支持该平台链接: {url}");

        return await parser.ParseAsync(url, ct);
    }

    /// <summary>
    /// Downloads the video to the specified path with progress reporting.
    /// </summary>
    public async Task DownloadAsync(
        VideoInfo info,
        string outputPath,
        IProgress<(long downloaded, long? total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.VideoUrl))
            throw new InvalidOperationException("视频地址为空，无法下载");

        var sanitizedPath = SanitizeFileName(outputPath);
        await HttpHelper.DownloadFileAsync(info.VideoUrl, sanitizedPath, info.OriginalUrl, progress, ct);
    }

    /// <summary>
    /// Generates a safe filename for the video.
    /// </summary>
    public static string GenerateFileName(VideoInfo info, string outputDir = "Downloads")
    {
        var safeName = SanitizeFileName(info.Title);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"{info.Platform}_{info.VideoId}";

        if (safeName.Length > 80)
            safeName = safeName[..80];

        var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        return Path.Combine(outputDir, fileName);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Concat(new[] { '#', '?', '&' })
            .Distinct()
            .ToArray();

        var result = name;
        foreach (var c in invalid)
            result = result.Replace(c, '_');

        return result.Trim().TrimEnd('.');
    }
}

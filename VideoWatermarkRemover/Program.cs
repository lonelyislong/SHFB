using VideoWatermarkRemover.Models;
using VideoWatermarkRemover.Services;

namespace VideoWatermarkRemover;

class Program
{
    private static readonly WatermarkRemovalService _service = new();

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0)
            return await RunBatchMode(args);

        return await RunInteractiveMode();
    }

    /// <summary>
    /// Batch mode: process URLs passed as command-line arguments.
    /// Usage: VideoWatermarkRemover [url1] [url2] ... [--download] [--output dir]
    /// </summary>
    private static async Task<int> RunBatchMode(string[] args)
    {
        var urls = new List<string>();
        var download = false;
        var outputDir = "Downloads";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--download" or "-d":
                    download = true;
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    urls.Add(args[i]);
                    break;
            }
        }

        if (urls.Count == 0)
        {
            PrintUsage();
            return 1;
        }

        var exitCode = 0;
        foreach (var url in urls)
        {
            try
            {
                var info = await _service.ParseAsync(url);
                PrintVideoInfo(info);

                if (download)
                    await DownloadWithProgress(info, outputDir);
            }
            catch (Exception ex)
            {
                WriteError($"处理失败: {ex.Message}");
                exitCode = 1;
            }

            Console.WriteLine();
        }

        return exitCode;
    }

    /// <summary>
    /// Interactive mode: prompt user for URLs in a loop.
    /// </summary>
    private static async Task<int> RunInteractiveMode()
    {
        PrintBanner();

        while (true)
        {
            Console.WriteLine();
            WriteColored("请输入视频分享链接", ConsoleColor.Cyan);
            Console.Write(" (输入 ");
            WriteColored("q", ConsoleColor.Yellow);
            Console.Write(" 退出, ");
            WriteColored("h", ConsoleColor.Yellow);
            Console.Write(" 帮助): ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            if (input.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                WriteColored("再见！\n", ConsoleColor.Green);
                break;
            }

            if (input.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            var platform = _service.DetectPlatform(input);
            if (platform == VideoPlatform.Unknown)
            {
                WriteError("无法识别该链接所属平台，请检查链接是否正确。");
                continue;
            }

            WriteColored($"  识别平台: {GetPlatformName(platform)}\n", ConsoleColor.DarkGray);
            Console.Write("  正在解析...");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var info = await _service.ParseAsync(input, cts.Token);

                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', 40));
                Console.SetCursorPosition(0, Console.CursorTop);

                PrintVideoInfo(info);

                Console.Write("\n  是否下载视频? [");
                WriteColored("Y", ConsoleColor.Green);
                Console.Write("/n]: ");

                var answer = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(answer) ||
                    answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                    answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    await DownloadWithProgress(info);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                WriteError("请求超时，请检查网络连接后重试。");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                WriteError($"解析失败: {ex.Message}");
            }
        }

        return 0;
    }

    private static void PrintVideoInfo(VideoInfo info)
    {
        Console.WriteLine();
        WriteColored("  ┌─ 视频信息 ─────────────────────────\n", ConsoleColor.DarkCyan);
        WriteColored($"  │ 标题: ", ConsoleColor.DarkCyan);
        Console.WriteLine(TruncateString(info.Title, 60));
        WriteColored($"  │ 作者: ", ConsoleColor.DarkCyan);
        Console.WriteLine(info.Author);
        WriteColored($"  │ 平台: ", ConsoleColor.DarkCyan);
        Console.WriteLine(GetPlatformName(info.Platform));
        if (info.Duration > 0)
        {
            WriteColored($"  │ 时长: ", ConsoleColor.DarkCyan);
            Console.WriteLine(FormatDuration(info.Duration));
        }
        WriteColored($"  │ 视频: ", ConsoleColor.DarkCyan);
        WriteColored(TruncateString(info.VideoUrl, 70) + "\n", ConsoleColor.Blue);
        if (!string.IsNullOrEmpty(info.CoverUrl))
        {
            WriteColored($"  │ 封面: ", ConsoleColor.DarkCyan);
            WriteColored(TruncateString(info.CoverUrl, 70) + "\n", ConsoleColor.Blue);
        }
        WriteColored("  └────────────────────────────────────\n", ConsoleColor.DarkCyan);
    }

    private static async Task DownloadWithProgress(VideoInfo info, string outputDir = "Downloads")
    {
        var outputPath = WatermarkRemovalService.GenerateFileName(info, outputDir);
        Console.Write($"  下载中: {Path.GetFileName(outputPath)} ");

        var lastPercent = -1;
        var progress = new Progress<(long downloaded, long? total)>(p =>
        {
            if (p.total.HasValue && p.total > 0)
            {
                var percent = (int)(p.downloaded * 100 / p.total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    Console.Write($"\r  下载中: {FormatSize(p.downloaded)} / {FormatSize(p.total.Value)} [{BuildProgressBar(percent, 20)}] {percent}%  ");
                }
            }
            else
            {
                Console.Write($"\r  下载中: {FormatSize(p.downloaded)}  ");
            }
        });

        try
        {
            await _service.DownloadAsync(info, outputPath, progress);
            Console.WriteLine();
            WriteColored($"  ✓ 下载完成: {outputPath}\n", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            WriteError($"下载失败: {ex.Message}");
        }
    }

    private static void PrintBanner()
    {
        WriteColored(@"
  ╔══════════════════════════════════════╗
  ║       短视频去水印工具 v1.0          ║
  ╠══════════════════════════════════════╣
  ║  支持: 抖音 | TikTok | 快手 | 视频号║
  ╚══════════════════════════════════════╝
", ConsoleColor.Cyan);
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        WriteColored("  使用说明:\n", ConsoleColor.Yellow);
        Console.WriteLine("  1. 在短视频APP中复制分享链接");
        Console.WriteLine("  2. 粘贴到此处并回车");
        Console.WriteLine("  3. 工具自动识别平台并解析无水印地址");
        Console.WriteLine("  4. 选择是否下载视频");
        Console.WriteLine();
        WriteColored("  支持的平台:\n", ConsoleColor.Yellow);
        foreach (var p in _service.SupportedPlatforms)
            Console.WriteLine($"    • {p}");
        Console.WriteLine();
        WriteColored("  命令行模式:\n", ConsoleColor.Yellow);
        Console.WriteLine("    VideoWatermarkRemover <url> [--download] [--output <dir>]");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("短视频去水印工具 v1.0");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  VideoWatermarkRemover                   # 交互模式");
        Console.WriteLine("  VideoWatermarkRemover <url> [options]   # 命令行模式");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  -d, --download    自动下载视频");
        Console.WriteLine("  -o, --output DIR  指定下载目录 (默认: Downloads)");
        Console.WriteLine("  -h, --help        显示帮助");
        Console.WriteLine();
        Console.WriteLine("支持平台: 抖音 / TikTok / 快手 / 微信视频号");
    }

    private static string GetPlatformName(VideoPlatform platform) => platform switch
    {
        VideoPlatform.Douyin => "抖音 (Douyin)",
        VideoPlatform.TikTok => "TikTok",
        VideoPlatform.Kuaishou => "快手 (Kuaishou)",
        VideoPlatform.WeChatVideo => "微信视频号",
        _ => "未知"
    };

    private static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
            _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
        };
    }

    private static string BuildProgressBar(int percent, int width)
    {
        var filled = percent * width / 100;
        return new string('█', filled) + new string('░', width - filled);
    }

    private static string TruncateString(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    private static void WriteError(string message)
    {
        WriteColored($"  ✗ {message}\n", ConsoleColor.Red);
    }
}

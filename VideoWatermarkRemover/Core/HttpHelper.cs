using System.Net;

namespace VideoWatermarkRemover.Core;

public static class HttpHelper
{
    private static readonly Lazy<HttpClient> _redirectClient = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", MobileUserAgent);
        return client;
    });

    private static readonly Lazy<HttpClient> _normalClient = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", DesktopUserAgent);
        return client;
    });

    private static readonly Lazy<HttpClient> _downloadClient = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Add("User-Agent", MobileUserAgent);
        return client;
    });

    public const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";

    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public static HttpClient NoRedirect => _redirectClient.Value;
    public static HttpClient Normal => _normalClient.Value;
    public static HttpClient Download => _downloadClient.Value;

    /// <summary>
    /// Follows redirect chain and returns the final Location URL.
    /// </summary>
    public static async Task<string> FollowRedirectsAsync(string url, CancellationToken ct = default)
    {
        var current = url;
        for (int i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Add("User-Agent", MobileUserAgent);

            using var response = await NoRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 301 and <= 308 &&
                response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri
                    ? location.AbsoluteUri
                    : new Uri(new Uri(current), location).AbsoluteUri;
                continue;
            }

            return current;
        }

        return current;
    }

    /// <summary>
    /// Sends a GET with mobile UA and returns the response body.
    /// </summary>
    public static async Task<string> GetStringMobileAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", MobileUserAgent);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        using var response = await Normal.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Sends a GET with desktop UA and returns the response body.
    /// </summary>
    public static async Task<string> GetStringDesktopAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", DesktopUserAgent);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.Add("Referer", url);

        using var response = await Normal.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Downloads file with progress reporting.
    /// </summary>
    public static async Task DownloadFileAsync(
        string url,
        string outputPath,
        string? referer = null,
        IProgress<(long downloaded, long? total)>? progress = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", MobileUserAgent);
        if (referer != null)
            request.Headers.Add("Referer", referer);

        using var response = await Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            progress?.Report((totalRead, totalBytes));
        }
    }

    /// <summary>
    /// Extracts a URL from text (e.g. from share messages containing a link).
    /// </summary>
    public static string? ExtractUrl(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s\u4e00-\u9fff""'<>]+");
        return match.Success ? match.Value.TrimEnd('/', ' ') : null;
    }
}

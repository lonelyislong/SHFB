using VideoWatermarkRemover.Models;

namespace VideoWatermarkRemover.Core;

public interface IVideoParser
{
    VideoPlatform Platform { get; }

    bool CanParse(string url);

    Task<VideoInfo> ParseAsync(string url, CancellationToken cancellationToken = default);
}

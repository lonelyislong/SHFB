namespace VideoWatermarkRemover.Models;

public class VideoInfo
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public VideoPlatform Platform { get; set; } = VideoPlatform.Unknown;
    public string OriginalUrl { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public long Duration { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();

    public override string ToString()
    {
        return $"[{Platform}] {Author} - {Title}";
    }
}

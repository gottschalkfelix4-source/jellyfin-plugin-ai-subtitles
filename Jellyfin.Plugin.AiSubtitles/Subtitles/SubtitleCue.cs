namespace Jellyfin.Plugin.AiSubtitles.Subtitles;

/// <summary>
/// One subtitle line with timing.
/// </summary>
/// <param name="Index">Zero-based position in the track.</param>
/// <param name="StartMs">Start in milliseconds.</param>
/// <param name="EndMs">End in milliseconds.</param>
/// <param name="Text">Plain text, lines separated by \n.</param>
public sealed record SubtitleCue(int Index, long StartMs, long EndMs, string Text);

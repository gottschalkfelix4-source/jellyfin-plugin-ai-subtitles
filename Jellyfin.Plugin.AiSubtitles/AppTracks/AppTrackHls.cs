using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AiSubtitles.AppTracks;

/// <summary>
/// A translated track as an HLS subtitle rendition.
/// </summary>
/// <param name="Name">Name shown in the player.</param>
/// <param name="Language">BCP-47 language (e.g. "de").</param>
/// <param name="Uri">Playlist URI, relative to the master playlist.</param>
/// <param name="IsDefault">Whether the rendition is preselected.</param>
public sealed record HlsRendition(string Name, string? Language, string Uri, bool IsDefault);

/// <summary>
/// Playlist handling for players that only take subtitles from the HLS stream (AVPlayer on iPhone, iPad, Apple TV
/// and Safari). Mirrors the format Jellyfin uses for its own subtitle renditions.
/// </summary>
public static class AppTrackHls
{
    /// <summary>
    /// Group id Jellyfin uses for subtitle renditions.
    /// </summary>
    public const string Group = "subs";

    /// <summary>
    /// MPEG-TS offset of Jellyfin's TS segments (fMP4 segments start at 0).
    /// </summary>
    public const long DefaultTimestampMapMpegts = 900000;

    /// <summary>
    /// Adds subtitle renditions to a master playlist.
    /// </summary>
    /// <param name="playlist">Master playlist from Jellyfin.</param>
    /// <param name="renditions">Renditions to add.</param>
    /// <returns>The new playlist, or null if it was left unchanged.</returns>
    public static string? InjectIntoMaster(string playlist, IReadOnlyList<HlsRendition> renditions)
    {
        renditions = renditions.Where(r => !playlist.Contains(r.Uri.Split('?')[0], StringComparison.Ordinal)).ToList();
        if (renditions.Count == 0
            || !playlist.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)
            || !playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            return null;
        }

        var newline = playlist.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = playlist.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Variant streams must reference the subtitle group; Jellyfin only adds it when it lists subtitles itself.
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) && !lines[i].Contains("SUBTITLES=", StringComparison.Ordinal))
            {
                lines[i] += ",SUBTITLES=\"" + Group + "\"";
            }
        }

        var hasDefault = renditions.Any(r => r.IsDefault);
        if (hasDefault)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (IsSubtitleMedia(lines[i]))
                {
                    lines[i] = lines[i].Replace("DEFAULT=YES", "DEFAULT=NO", StringComparison.Ordinal);
                }
            }
        }

        var insertAt = lines.FindLastIndex(IsSubtitleMedia) + 1;
        if (insertAt == 0)
        {
            insertAt = lines.FindIndex(l => l.StartsWith("#EXTM3U", StringComparison.Ordinal)) + 1;
        }

        lines.InsertRange(insertAt, renditions.Select(MediaLine));
        return string.Join(newline, lines);
    }

    /// <summary>
    /// Builds the subtitle playlist for a translated track (one WebVTT segment per window).
    /// </summary>
    /// <param name="runtimeTicks">Runtime of the media source.</param>
    /// <param name="segmentLengthSeconds">Segment length.</param>
    /// <param name="accessToken">Access token for the segment URLs.</param>
    /// <param name="timestampMapMpegts">MPEG-TS offset for X-TIMESTAMP-MAP.</param>
    /// <returns>Playlist text.</returns>
    public static string BuildSubtitlePlaylist(long runtimeTicks, int segmentLengthSeconds, string? accessToken, long timestampMapMpegts)
    {
        var segmentTicks = TimeSpan.FromSeconds(Math.Clamp(segmentLengthSeconds, 1, 600)).Ticks;
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(segmentTicks / TimeSpan.TicksPerSecond).Append('\n');
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        for (long start = 0; start < runtimeTicks; start += segmentTicks)
        {
            var end = Math.Min(start + segmentTicks, runtimeTicks);
            sb.Append("#EXTINF:")
                .Append(TimeSpan.FromTicks(end - start).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture))
                .Append(",\n");
            sb.Append("stream.vtt?CopyTimestamps=true&AddVttTimeMap=true")
                .Append("&StartPositionTicks=").Append(start.ToString(CultureInfo.InvariantCulture))
                .Append("&EndPositionTicks=").Append(end.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(accessToken))
            {
                sb.Append("&ApiKey=").Append(Uri.EscapeDataString(accessToken));
            }

            sb.Append("&VttTimestampMapMpegts=").Append(timestampMapMpegts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Header of a WebVTT segment, including the timestamp map HLS players need to align cues with the video.
    /// </summary>
    /// <param name="timestampMapMpegts">MPEG-TS offset, or null for no map.</param>
    /// <returns>Header text.</returns>
    public static string VttHeader(long? timestampMapMpegts)
        => timestampMapMpegts is long mpegts
            ? "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:" + mpegts.ToString(CultureInfo.InvariantCulture) + ",LOCAL:00:00:00.000\n\n"
            : "WEBVTT\n\n";

    private static bool IsSubtitleMedia(string line)
        => line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && line.Contains("TYPE=SUBTITLES", StringComparison.Ordinal);

    private static string MediaLine(HlsRendition r)
    {
        var sb = new StringBuilder("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"").Append(Group).Append('"');
        sb.Append(",NAME=\"").Append(r.Name.Replace("\"", "'", StringComparison.Ordinal)).Append('"');
        sb.Append(",DEFAULT=").Append(r.IsDefault ? "YES" : "NO");
        sb.Append(",FORCED=NO,AUTOSELECT=YES");
        sb.Append(",URI=\"").Append(r.Uri).Append('"');
        if (!string.IsNullOrEmpty(r.Language))
        {
            sb.Append(",LANGUAGE=\"").Append(r.Language).Append('"');
        }

        return sb.ToString();
    }
}

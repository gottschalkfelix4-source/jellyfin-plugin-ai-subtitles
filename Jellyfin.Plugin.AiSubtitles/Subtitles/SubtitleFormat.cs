using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AiSubtitles.Subtitles;

/// <summary>
/// Minimal SRT / WebVTT reader and writer.
/// </summary>
public static partial class SubtitleFormat
{
    /// <summary>
    /// Parses SRT or WebVTT text into cues (tags stripped, sorted by start).
    /// </summary>
    /// <param name="content">File content.</param>
    /// <returns>Cues.</returns>
    public static List<SubtitleCue> Parse(string content)
    {
        var raw = new List<(long Start, long End, string Text)>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimingRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var start = ParseTime(match.Groups["s"].Value);
            var end = ParseTime(match.Groups["e"].Value);
            var text = new StringBuilder();
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                // A timing line right after another block without blank line: step back so it is parsed.
                if (TimingRegex().IsMatch(lines[i]))
                {
                    i--;
                    break;
                }

                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(lines[i]);
                i++;
            }

            var clean = Clean(text.ToString());
            if (clean.Length > 0 && end > start)
            {
                raw.Add((start, end, clean));
            }
        }

        return raw
            .OrderBy(c => c.Start)
            .ThenBy(c => c.End)
            .Select((c, index) => new SubtitleCue(index, c.Start, c.End, c.Text))
            .ToList();
    }

    /// <summary>
    /// Writes cues as SRT.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <returns>SRT text.</returns>
    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var cue in cues)
        {
            sb.Append(n++).Append('\n');
            sb.Append(FormatTime(cue.StartMs, ',')).Append(" --> ").Append(FormatTime(cue.EndMs, ',')).Append('\n');
            sb.Append(cue.Text).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes cues as WebVTT.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <returns>VTT text.</returns>
    public static string ToVtt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            sb.Append(FormatTime(cue.StartMs, '.')).Append(" --> ").Append(FormatTime(cue.EndMs, '.')).Append('\n');
            sb.Append(cue.Text.Replace("-->", "->", StringComparison.Ordinal)).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string Clean(string text)
    {
        text = AssTagRegex().Replace(text, string.Empty);
        text = text.Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\h", " ", StringComparison.Ordinal);
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    private static long ParseTime(string value)
    {
        // Accepts hh:mm:ss,fff / hh:mm:ss.fff / mm:ss.fff
        var parts = value.Replace(',', '.').Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            seconds = (seconds * 60) + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return (long)Math.Round(seconds * 1000);
    }

    private static string FormatTime(long ms, char separator)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}");
    }

    [GeneratedRegex(@"(?<s>(?:\d+:)?\d{1,2}:\d{2}[,\.]\d{1,3})\s*-->\s*(?<e>(?:\d+:)?\d{1,2}:\d{2}[,\.]\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssTagRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTagRegex();
}

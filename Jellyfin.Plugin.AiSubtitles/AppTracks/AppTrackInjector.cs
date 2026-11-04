using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;

namespace Jellyfin.Plugin.AiSubtitles.AppTracks;

/// <summary>
/// A language that is offered as a track in all apps.
/// </summary>
/// <param name="Position">Position in the configured list (part of the virtual stream index).</param>
/// <param name="Target">Resolved target language.</param>
public sealed record AppLanguage(int Position, TargetLanguage Target);

/// <summary>
/// A translated track that was added to a response.
/// </summary>
/// <param name="MediaSourceId">Media source id.</param>
/// <param name="Index">Virtual stream index.</param>
/// <param name="SourceIndex">Index of the original subtitle stream.</param>
/// <param name="Language">Target language.</param>
public sealed record InjectedTrack(string MediaSourceId, int Index, int SourceIndex, AppLanguage Language);

/// <summary>
/// Options for <see cref="AppTrackInjector.Inject"/>.
/// </summary>
public sealed class InjectOptions
{
    /// <summary>Gets the item id (any GUID format).</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the offered languages.</summary>
    public required IReadOnlyList<AppLanguage> Languages { get; init; }

    /// <summary>Gets a function mapping a stream language code ("eng", "ger") to a language key ("en", "de").</summary>
    public required Func<string?, string?> LanguageKey { get; init; }

    /// <summary>Gets a function mapping a stream language code to a readable name.</summary>
    public Func<string?, string?>? LanguageName { get; init; }

    /// <summary>Gets the delivery format ("srt" or "vtt"); null only lists the tracks (item details).</summary>
    public string? Format { get; init; }

    /// <summary>Gets the delivery method: "External" (whole file) or "Hls" (segmented, for AVPlayer).</summary>
    public string Method { get; init; } = "External";

    /// <summary>Gets the access token appended to the delivery URL.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the subtitle stream index the client asked for (null = not given).</summary>
    public int? RequestedIndex { get; init; }

    /// <summary>Gets the key of the user's preferred subtitle language.</summary>
    public string? PreferredKey { get; init; }

    /// <summary>Gets a value indicating whether the translated track may be preselected.</summary>
    public bool AutoSelect { get; init; }
}

/// <summary>
/// Adds translated subtitle tracks to Jellyfin's JSON responses (PlaybackInfo, item details).
/// Works on the JSON alone, so it needs no database access.
/// </summary>
public static class AppTrackInjector
{
    /// <summary>
    /// Property that marks the added streams in the JSON (clients ignore unknown properties).
    /// </summary>
    public const string Marker = "AiTranslated";

    /// <summary>
    /// First stream index after the existing streams. The translated tracks are numbered from here, so a track's
    /// index equals its position in the stream list – the web player looks up a preselected subtitle by position.
    /// </summary>
    /// <param name="indexes">Indexes of the existing streams.</param>
    /// <returns>First free index.</returns>
    public static int FirstFreeIndex(IReadOnlyCollection<int> indexes)
        => indexes.Count == 0 ? 0 : Math.Max(indexes.Count, indexes.Max() + 1);

    /// <summary>
    /// Chooses the original track to translate from.
    /// </summary>
    /// <param name="streams">Text subtitle streams.</param>
    /// <param name="targetKey">Target language key.</param>
    /// <param name="languageKey">Language key function.</param>
    /// <param name="preferredSourceKey">Source language to prefer (e.g. that of the previous episode).</param>
    /// <returns>The stream, or null.</returns>
    public static SourceStream? PickSource(IEnumerable<SourceStream> streams, string targetKey, Func<string?, string?> languageKey, string? preferredSourceKey = null)
    {
        return streams
            .Where(s => !s.IsForced && s.Index >= 0)
            .Where(s => !string.Equals(languageKey(s.Language), targetKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => preferredSourceKey is not null && string.Equals(languageKey(s.Language), preferredSourceKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => string.Equals(languageKey(s.Language), "en", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.IsDefault)
            .ThenBy(s => s.Index)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets whether the item already has a regular track in the language.
    /// </summary>
    /// <param name="streams">Text subtitle streams.</param>
    /// <param name="targetKey">Target language key.</param>
    /// <param name="languageKey">Language key function.</param>
    /// <returns>True if a non-forced track exists.</returns>
    public static bool HasNativeTrack(IEnumerable<SourceStream> streams, string targetKey, Func<string?, string?> languageKey)
        => streams.Any(s => !s.IsForced && string.Equals(languageKey(s.Language), targetKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds the translated tracks to a PlaybackInfo response or an item.
    /// </summary>
    /// <param name="root">Parsed JSON.</param>
    /// <param name="options">Options.</param>
    /// <param name="selected">Track that was preselected (to be prepared at once), if any.</param>
    /// <returns>Added tracks (empty = response unchanged).</returns>
    public static List<InjectedTrack> Inject(JsonNode? root, InjectOptions options, out InjectedTrack? selected)
    {
        selected = null;
        var added = new List<InjectedTrack>();
        if (root is not JsonObject obj || options.Languages.Count == 0)
        {
            return added;
        }

        var camel = obj.ContainsKey("mediaSources") || obj.ContainsKey("mediaStreams");
        string N(string name) => camel ? char.ToLowerInvariant(name[0]) + name[1..] : name;

        var sources = obj[N("MediaSources")] as JsonArray;
        string? firstSourceId = null;

        foreach (var source in sources?.OfType<JsonObject>() ?? [])
        {
            var sourceId = AsString(source[N("Id")]);
            firstSourceId ??= sourceId;
            if (sourceId is null || source[N("MediaStreams")] is not JsonArray streams)
            {
                continue;
            }

            var tracks = InjectInto(streams, sourceId, options, N);
            added.AddRange(tracks);
            if (options.Format is not null && selected is null)
            {
                selected = Preselect(source, streams, tracks, options, N);
            }
        }

        // Item details also carry the streams of the default source at the top level.
        if (obj[N("MediaStreams")] is JsonArray topStreams)
        {
            var id = firstSourceId ?? AsString(obj[N("Id")]);
            if (id is not null)
            {
                var tracks = InjectInto(topStreams, id, options, N);
                if (added.Count == 0)
                {
                    added.AddRange(tracks);
                }
            }
        }

        return added;
    }

    private static List<InjectedTrack> InjectInto(JsonArray streams, string sourceId, InjectOptions options, Func<string, string> n)
    {
        var result = new List<InjectedTrack>();
        if (streams.OfType<JsonObject>().Any(s => s.ContainsKey(Marker)))
        {
            return result; // already done
        }

        var parsed = streams.OfType<JsonObject>().Select(s => (Node: s, Index: AsInt(s[n("Index")]))).ToList();
        var nextIndex = FirstFreeIndex(parsed.Where(p => p.Index is not null).Select(p => p.Index!.Value).ToList());

        var text = parsed
            .Where(p => p.Index is not null
                && string.Equals(AsString(p.Node[n("Type")]), "Subtitle", StringComparison.OrdinalIgnoreCase)
                && AsBool(p.Node[n("IsTextSubtitleStream")]))
            .Select(p => ToSource(p.Node, p.Index!.Value, n))
            .ToList();

        if (text.Count == 0)
        {
            return result;
        }

        foreach (var language in options.Languages)
        {
            var key = language.Target.Key;
            if (HasNativeTrack(text, key, options.LanguageKey))
            {
                continue;
            }

            var source = PickSource(text, key, options.LanguageKey);
            if (source is null)
            {
                continue;
            }

            var index = nextIndex++;
            var sourceName = options.LanguageName?.Invoke(source.Language) ?? source.Language ?? "Original";
            var title = language.Target.DisplayName + " (KI)";
            var node = new JsonObject
            {
                [n("Codec")] = options.Format == "vtt" ? "webvtt" : "subrip",
                [n("Language")] = language.Target.Iso3 == "und" ? null : language.Target.Iso3,
                [n("Title")] = title,
                [n("DisplayTitle")] = $"{title} – aus {sourceName}",
                [n("IsDefault")] = false,
                [n("IsForced")] = false,
                [n("IsHearingImpaired")] = false,
                [n("Type")] = "Subtitle",
                [n("Index")] = index,
                [n("IsExternal")] = true,
                [n("IsTextSubtitleStream")] = true,
                [n("SupportsExternalStream")] = true,
                [n("IsExternalUrl")] = false,
                [Marker] = true
            };

            if (options.Format is not null)
            {
                var hls = string.Equals(options.Method, "Hls", StringComparison.OrdinalIgnoreCase);
                var url = hls
                    ? $"/Videos/{options.ItemId}/{sourceId}/Subtitles/{index}/subtitles.m3u8?SegmentLength=30"
                    : $"/Videos/{options.ItemId}/{sourceId}/Subtitles/{index}/0/Stream.{options.Format}";
                if (!string.IsNullOrEmpty(options.ApiKey))
                {
                    url += (hls ? "&" : "?") + "ApiKey=" + Uri.EscapeDataString(options.ApiKey);
                }

                node[n("DeliveryMethod")] = hls ? "Hls" : "External";
                node[n("DeliveryUrl")] = url;
            }

            streams.Add(node);
            result.Add(new InjectedTrack(sourceId, index, source.Index, language));
        }

        return result;
    }

    private static InjectedTrack? Preselect(JsonObject source, JsonArray streams, List<InjectedTrack> tracks, InjectOptions options, Func<string, string> n)
    {
        if (tracks.Count == 0)
        {
            return null;
        }

        InjectedTrack? choice = null;
        if (options.RequestedIndex is int requested && tracks.Any(t => t.Index == requested))
        {
            choice = tracks.First(t => t.Index == requested);
        }
        else if (options.AutoSelect && options.RequestedIndex is null && options.PreferredKey is not null
            && AsInt(source[n("DefaultSubtitleStreamIndex")]) is int current && current >= 0)
        {
            // The server decided to show subtitles, but not in the user's language: offer the translation instead.
            var stream = streams.OfType<JsonObject>().FirstOrDefault(s => AsInt(s[n("Index")]) == current);
            var method = stream is null ? null : AsString(stream[n("DeliveryMethod")]);
            var replaceable = stream is not null
                && AsBool(stream[n("IsTextSubtitleStream")])
                && !AsBool(stream[n("IsForced")])
                && !string.Equals(options.LanguageKey(AsString(stream[n("Language")])), options.PreferredKey, StringComparison.OrdinalIgnoreCase)
                && (method is null || method.Equals("External", StringComparison.OrdinalIgnoreCase) || method.Equals("Embed", StringComparison.OrdinalIgnoreCase));
            if (replaceable)
            {
                choice = tracks.FirstOrDefault(t => string.Equals(t.Language.Target.Key, options.PreferredKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (choice is not null)
        {
            source[n("DefaultSubtitleStreamIndex")] = choice.Index;
        }

        return choice;
    }

    private static SourceStream ToSource(JsonObject node, int index, Func<string, string> n)
        => new(
            index,
            AsString(node[n("Language")]),
            AsString(node[n("DisplayTitle")]) ?? AsString(node[n("Title")]) ?? "#" + index,
            AsString(node[n("Codec")]),
            AsBool(node[n("IsExternal")]),
            AsBool(node[n("IsForced")]),
            AsBool(node[n("IsDefault")]));

    private static string? AsString(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool AsBool(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.Number when v.TryGetValue<int>(out var i) => i,
            JsonValueKind.Number => (int)v.GetValue<double>(),
            JsonValueKind.String when int.TryParse(v.GetValue<string>(), out var s) => s,
            _ => null
        };
    }
}

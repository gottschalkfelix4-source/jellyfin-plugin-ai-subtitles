using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using Jellyfin.Plugin.AiSubtitles.Providers;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Translation;

/// <summary>
/// A slice of the track that is translated in one AI request.
/// </summary>
/// <param name="Index">Batch number.</param>
/// <param name="StartMs">Start of the first cue.</param>
/// <param name="EndMs">Latest end of all cues.</param>
/// <param name="FirstCue">Index of the first cue.</param>
/// <param name="Count">Number of cues.</param>
public sealed record BatchInfo(int Index, long StartMs, long EndMs, int FirstCue, int Count);

/// <summary>
/// Translates subtitle cues in batches with caching, de-duplication and a global concurrency limit.
/// </summary>
public partial class TranslationService
{
    private const string PromptVersion = "v1";

    private readonly TranslatorFactory _translatorFactory;
    private readonly TranslationCache _cache;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<TranslationService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string[]>>> _inflight = new();
    private readonly object _semaphoreLock = new();
    private SemaphoreSlim? _semaphore;
    private int _semaphoreSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationService"/> class.
    /// </summary>
    /// <param name="translatorFactory">Backend factory.</param>
    /// <param name="cache">Cache.</param>
    /// <param name="localization">Localization manager.</param>
    /// <param name="logger">Logger.</param>
    public TranslationService(
        TranslatorFactory translatorFactory,
        TranslationCache cache,
        ILocalizationManager localization,
        ILogger<TranslationService> logger)
    {
        _translatorFactory = translatorFactory;
        _cache = cache;
        _localization = localization;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.CurrentConfiguration;

    /// <summary>
    /// Resolves a language string.
    /// </summary>
    /// <param name="input">Code, name or free text.</param>
    /// <returns>Resolved language.</returns>
    public TargetLanguage ResolveLanguage(string? input) => LanguageResolver.Resolve(input, Config, _localization);

    /// <summary>
    /// Gets the configured batch size, clamped.
    /// </summary>
    /// <param name="requested">Requested size (0 = configured).</param>
    /// <returns>Batch size.</returns>
    public static int GetBatchSize(int requested = 0)
        => Math.Clamp(requested > 0 ? requested : Config.BatchSize, 5, 200);

    /// <summary>
    /// Splits the cues into batches.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="batchSize">Batch size.</param>
    /// <returns>Batches.</returns>
    public static List<BatchInfo> BuildBatches(IReadOnlyList<SubtitleCue> cues, int batchSize)
    {
        var result = new List<BatchInfo>();
        for (int first = 0, index = 0; first < cues.Count; first += batchSize, index++)
        {
            var count = Math.Min(batchSize, cues.Count - first);
            var slice = cues.Skip(first).Take(count).ToList();
            result.Add(new BatchInfo(index, slice.Min(c => c.StartMs), slice.Max(c => c.EndMs), first, count));
        }

        return result;
    }

    /// <summary>
    /// Translates one batch.
    /// </summary>
    /// <param name="cues">All cues of the track.</param>
    /// <param name="batch">Batch to translate.</param>
    /// <param name="language">Target language.</param>
    /// <param name="cancellationToken">Cancellation token (only stops waiting, not the shared request).</param>
    /// <returns>Translated cues of the batch.</returns>
    public async Task<List<SubtitleCue>> TranslateBatchAsync(
        IReadOnlyList<SubtitleCue> cues,
        BatchInfo batch,
        TargetLanguage language,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var lines = cues.Skip(batch.FirstCue).Take(batch.Count).ToList();
        var contextStart = Math.Max(0, batch.FirstCue - Math.Clamp(config.ContextLines, 0, 50));
        var context = cues.Skip(contextStart).Take(batch.FirstCue - contextStart).Select(c => c.Text).ToList();
        var hash = ComputeHash(language, config.ExtraInstructions, context, lines);

        if (!_cache.TryGet(language.Key, hash, lines.Count, out var translated))
        {
            var lazy = _inflight.GetOrAdd(
                hash,
                _ => new Lazy<Task<string[]>>(() => RunAsync(hash, lines, context, language, config)));
            translated = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return lines.Select((cue, i) => cue with { Text = translated[i] }).ToList();
    }

    /// <summary>
    /// Translates the whole track.
    /// </summary>
    /// <param name="cues">All cues.</param>
    /// <param name="language">Target language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Translated cues.</returns>
    public async Task<List<SubtitleCue>> TranslateAllAsync(IReadOnlyList<SubtitleCue> cues, TargetLanguage language, CancellationToken cancellationToken)
    {
        var batches = BuildBatches(cues, GetBatchSize());
        var tasks = batches.Select(b => TranslateBatchAsync(cues, b, language, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Sends a small sample to the configured backend (used by the settings page).
    /// </summary>
    /// <param name="language">Target language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The translated sample lines.</returns>
    public async Task<string[]> TestAsync(TargetLanguage language, CancellationToken cancellationToken)
    {
        var sample = new List<SubtitleCue>
        {
            new(0, 0, 2000, "Where the hell have you been?"),
            new(1, 2000, 4000, "I told you not to wait up for me.\nIt's been a long night."),
            new(2, 4000, 6000, "♪ Here comes the sun ♪")
        };

        var result = await AskAsync(sample, [], language, Config, cancellationToken).ConfigureAwait(false);
        return result.Select((t, i) => t ?? "(fehlt) " + sample[i].Text).ToArray();
    }

    private async Task<string[]> RunAsync(string hash, List<SubtitleCue> lines, List<string> context, TargetLanguage language, PluginConfiguration config)
    {
        try
        {
            var semaphore = GetSemaphore(config.MaxParallelRequests);
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // Overall guard; the backend applies its own per-request timeout.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, config.RequestTimeoutSeconds) * 3));
                var started = DateTime.UtcNow;

                var result = await AskAsync(lines, context, language, config, cts.Token).ConfigureAwait(false);

                var missing = Enumerable.Range(0, result.Length).Where(i => result[i] is null).ToList();
                if (missing.Count > 0)
                {
                    _logger.LogWarning("{Missing} von {Total} Zeilen fehlten in der KI-Antwort, frage erneut", missing.Count, lines.Count);
                    var retryLines = missing.Select(i => lines[i]).ToList();
                    var retry = await AskAsync(retryLines, context, language, config, cts.Token).ConfigureAwait(false);
                    for (var k = 0; k < missing.Count; k++)
                    {
                        result[missing[k]] = retry[k];
                    }
                }

                var complete = result.All(r => r is not null);
                var final = result.Select((r, i) => r ?? lines[i].Text).ToArray();
                if (complete)
                {
                    _cache.Set(language.Key, hash, final);
                }

                _logger.LogDebug(
                    "Batch mit {Count} Zeilen nach {Lang} übersetzt in {Ms} ms",
                    lines.Count,
                    language.Key,
                    (int)(DateTime.UtcNow - started).TotalMilliseconds);
                return final;
            }
            finally
            {
                semaphore.Release();
            }
        }
        finally
        {
            _inflight.TryRemove(hash, out _);
        }
    }

    private async Task<string?[]> AskAsync(
        List<SubtitleCue> lines,
        List<string> context,
        TargetLanguage language,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var system = BuildSystemPrompt(language, config);
        var user = BuildUserPrompt(lines, context, language);
        var translator = _translatorFactory.Get(config);
        var answer = await translator.CompleteAsync(config, system, user, cancellationToken).ConfigureAwait(false);
        return ParseAnswer(answer, lines.Count);
    }

    private static string BuildSystemPrompt(TargetLanguage language, PluginConfiguration config)
    {
        var sb = new StringBuilder();
        sb.Append("You are an expert subtitle translator for films and TV series. ");
        sb.Append("Translate every subtitle line you receive into ").Append(language.PromptName).Append(".\n\n");
        sb.Append("Rules:\n");
        sb.Append("- Return exactly one translation per input line, using the same \"id\". Never merge, split, skip or reorder lines.\n");
        sb.Append("- Translate naturally and idiomatically, the way professional subtitles in the target language read. Keep tone, register, slang, swearing and humour. Use forms of address (formal/informal) consistently and as the scene suggests.\n");
        sb.Append("- Keep each translation about as short as the original so it can be read in time. Use \\n for line breaks; at most two lines per subtitle.\n");
        sb.Append("- Keep names, places and invented terms unless there is an established translation. Keep symbols such as ♪ and leading speaker dashes (\"- \").\n");
        sb.Append("- The \"context\" lines are the preceding subtitles, given for reference only. Do not translate or return them.\n");
        sb.Append("- If a line is already in the target language, return it unchanged.\n");
        sb.Append("- Answer with JSON only, in exactly this shape: {\"translations\":[{\"id\":0,\"text\":\"...\"}]}\n");

        if (!string.IsNullOrWhiteSpace(config.ExtraInstructions))
        {
            sb.Append("\nAdditional instructions:\n").Append(config.ExtraInstructions.Trim()).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildUserPrompt(List<SubtitleCue> lines, List<string> context, TargetLanguage language)
    {
        var payload = new JsonObject
        {
            ["target_language"] = language.PromptName,
            ["context"] = new JsonArray(context.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["lines"] = new JsonArray(lines.Select((cue, i) => (JsonNode?)new JsonObject
            {
                ["id"] = i,
                ["text"] = cue.Text
            }).ToArray())
        };

        return payload.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Extracts the translations from a model answer.
    /// </summary>
    /// <param name="answer">Raw answer.</param>
    /// <param name="count">Expected number of lines.</param>
    /// <returns>Translations by id; null where missing.</returns>
    internal static string?[] ParseAnswer(string answer, int count)
    {
        var result = new string?[count];
        var text = ThinkRegex().Replace(answer, string.Empty).Trim();

        // Strip markdown code fences.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence];
            }
        }

        JsonNode? root = null;
        foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
        {
            var start = text.IndexOf(open, StringComparison.Ordinal);
            var end = text.LastIndexOf(close);
            if (start < 0 || end <= start)
            {
                continue;
            }

            try
            {
                root = JsonNode.Parse(text[start..(end + 1)]);
                break;
            }
            catch (JsonException)
            {
                // try next shape
            }
        }

        var items = root switch
        {
            JsonObject obj => (obj["translations"] ?? obj["lines"] ?? obj.FirstOrDefault(p => p.Value is JsonArray).Value) as JsonArray,
            JsonArray arr => arr,
            _ => null
        };

        if (items is null)
        {
            throw new TranslatorException("Die KI-Antwort enthielt kein auswertbares JSON: " + (text.Length > 300 ? text[..300] + "…" : text));
        }

        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];
            int id;
            string? value;

            if (item is JsonObject o)
            {
                var idNode = o["id"];
                if (idNode is null || !TryGetInt(idNode, out id))
                {
                    id = position;
                }

                value = (o["text"] ?? o["translation"])?.ToString();
            }
            else
            {
                id = position;
                value = item?.ToString();
            }

            if (id >= 0 && id < count && !string.IsNullOrWhiteSpace(value))
            {
                result[id] = value.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
            }
        }

        return result;
    }

    private static bool TryGetInt(JsonNode node, out int value)
    {
        value = -1;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                value = node.GetValue<int>();
                return true;
            }

            return int.TryParse(node.ToString(), out value);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ComputeHash(TargetLanguage language, string? extra, List<string> context, List<SubtitleCue> lines)
    {
        var sb = new StringBuilder();
        sb.Append(PromptVersion).Append('\u0001').Append(language.Key).Append('\u0001').Append(language.PromptName)
            .Append('\u0001').Append(extra?.Trim()).Append('\u0001');
        foreach (var c in context)
        {
            sb.Append(c).Append('\u0002');
        }

        sb.Append('\u0001');
        foreach (var l in lines)
        {
            sb.Append(l.Text).Append('\u0002');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private SemaphoreSlim GetSemaphore(int size)
    {
        size = Math.Clamp(size, 1, 16);
        lock (_semaphoreLock)
        {
            if (_semaphore is null || _semaphoreSize != size)
            {
                _semaphore = new SemaphoreSlim(size, size);
                _semaphoreSize = size;
            }

            return _semaphore;
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkRegex();
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Translation;

/// <summary>
/// Content-addressed on-disk cache for translated batches.
/// </summary>
public class TranslationCache
{
    private readonly ILogger<TranslationCache> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationCache"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public TranslationCache(ILogger<TranslationCache> logger)
    {
        _logger = logger;
    }

    private static string Root => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "translations");

    /// <summary>
    /// Tries to read a cached batch.
    /// </summary>
    /// <param name="languageKey">Language key.</param>
    /// <param name="hash">Content hash.</param>
    /// <param name="count">Expected number of lines.</param>
    /// <param name="lines">Cached lines.</param>
    /// <returns>True if found.</returns>
    public bool TryGet(string languageKey, string hash, int count, out string[] lines)
    {
        lines = [];
        var path = GetPath(languageKey, hash);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var data = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path, Encoding.UTF8));
            if (data is null || data.Length != count)
            {
                return false;
            }

            lines = data;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cache-Eintrag {Path} unlesbar", path);
            return false;
        }
    }

    /// <summary>
    /// Stores a translated batch.
    /// </summary>
    /// <param name="languageKey">Language key.</param>
    /// <param name="hash">Content hash.</param>
    /// <param name="lines">Lines.</param>
    public void Set(string languageKey, string hash, string[] lines)
    {
        var path = GetPath(languageKey, hash);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(lines), Encoding.UTF8);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cache-Eintrag {Path} konnte nicht geschrieben werden", path);
        }
    }

    /// <summary>
    /// Gets the number of files and bytes in the cache.
    /// </summary>
    /// <returns>Statistics.</returns>
    public (int Files, long Bytes) GetStats()
    {
        if (!Directory.Exists(Root))
        {
            return (0, 0);
        }

        var files = new DirectoryInfo(Root).EnumerateFiles("*.json", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>
    /// Deletes all cached translations.
    /// </summary>
    public void Clear()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private static string GetPath(string languageKey, string hash)
    {
        var safeLang = new string(languageKey.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        if (safeLang.Length is 0 or > 40)
        {
            safeLang = "lang_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(languageKey)))[..12];
        }

        return Path.Combine(Root, safeLang, hash[..2], hash + ".json");
    }
}

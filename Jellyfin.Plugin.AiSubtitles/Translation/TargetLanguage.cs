using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.AiSubtitles.Translation;

/// <summary>
/// A resolved target language.
/// </summary>
/// <param name="Key">Stable key used for caching and URLs (e.g. "de" or free text).</param>
/// <param name="PromptName">Name given to the model (e.g. "German (Deutsch)").</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="Iso3">ISO 639-2 code for saving ("und" if unknown).</param>
public sealed record TargetLanguage(string Key, string PromptName, string DisplayName, string Iso3);

/// <summary>
/// One entry of the configured language list.
/// </summary>
/// <param name="Code">Code.</param>
/// <param name="Name">Display name.</param>
public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Resolves user input (code, name or free text) into a <see cref="TargetLanguage"/>.
/// </summary>
public static class LanguageResolver
{
    /// <summary>
    /// Parses the configured language list.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>Options.</returns>
    public static List<LanguageOption> GetOptions(PluginConfiguration config)
    {
        return (config.Languages ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var idx = line.IndexOf('=', StringComparison.Ordinal);
                return idx > 0
                    ? new LanguageOption(line[..idx].Trim(), line[(idx + 1)..].Trim())
                    : new LanguageOption(line, line);
            })
            .Where(o => o.Code.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Resolves the target language.
    /// </summary>
    /// <param name="input">Code, name or free text.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="localization">Localization manager.</param>
    /// <returns>Resolved language.</returns>
    public static TargetLanguage Resolve(string? input, PluginConfiguration config, ILocalizationManager localization)
    {
        var value = string.IsNullOrWhiteSpace(input) ? config.DefaultTargetLanguage : input.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "de";
        }

        if (value.Length > 60)
        {
            value = value[..60];
        }

        var option = GetOptions(config).FirstOrDefault(o =>
            string.Equals(o.Code, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.Name, value, StringComparison.OrdinalIgnoreCase));

        var lookup = option?.Code ?? value;
        CultureDto? culture = null;
        try
        {
            culture = localization.FindLanguageInfo(lookup);
        }
        catch (ArgumentException)
        {
            // Free text such as "Schweizerdeutsch" – handled below.
        }

        if (culture is not null)
        {
            var english = culture.DisplayName;
            var display = option?.Name ?? english;
            var prompt = string.Equals(english, display, StringComparison.OrdinalIgnoreCase) ? english : $"{english} ({display})";
            var key = (culture.TwoLetterISOLanguageName ?? culture.ThreeLetterISOLanguageName ?? lookup).ToLowerInvariant();

            // Region variants such as "pt-BR" are kept apart from plain "pt".
            if (lookup.Contains('-', StringComparison.Ordinal))
            {
                key = lookup.ToLowerInvariant();
                prompt = $"{prompt} [{lookup}]";
            }

            return new TargetLanguage(key, prompt, display, culture.ThreeLetterISOLanguageName ?? "und");
        }

        var name = option?.Name ?? value;
        return new TargetLanguage(value.ToLowerInvariant(), name, name, "und");
    }
}

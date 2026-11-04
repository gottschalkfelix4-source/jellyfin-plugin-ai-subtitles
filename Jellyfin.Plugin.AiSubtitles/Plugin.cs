using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AiSubtitles;

/// <summary>
/// Plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Display name, also used as subtitle provider name.
    /// </summary>
    public const string PluginName = "AI Subtitle Translator";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override string Description => "Übersetzt vorhandene Untertitel live im Player per KI (OpenAI-kompatibel oder Anthropic).";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c3e9a51-2f4b-4d8e-9b61-5a0d2c8e4f17");

    /// <summary>
    /// Gets the active configuration (falls back to defaults outside a running server).
    /// </summary>
    internal static PluginConfiguration CurrentConfiguration
        => Instance?.Configuration ?? TestHooks.ConfigurationOverride ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "AiSubtitles",
                DisplayName = "KI-Untertitel",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}

/// <summary>
/// Hooks for unit tests.
/// </summary>
internal static class TestHooks
{
    /// <summary>
    /// Gets or sets a configuration used when no plugin instance exists.
    /// </summary>
    internal static PluginConfiguration? ConfigurationOverride { get; set; }
}

using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Providers;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using Jellyfin.Plugin.AiSubtitles.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AiSubtitles;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<OpenAiCompatibleTranslator>();
        serviceCollection.AddSingleton<AnthropicTranslator>();
        serviceCollection.AddSingleton<TranslatorFactory>();
        serviceCollection.AddSingleton<TranslationCache>();
        serviceCollection.AddSingleton<SubtitleSourceService>();
        serviceCollection.AddSingleton<TranslationService>();
        serviceCollection.AddSingleton<AppTrackService>();

        // AiSubtitleProvider (ISubtitleProvider) is discovered automatically by Jellyfin's export scan.
        serviceCollection.AddTransient<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}

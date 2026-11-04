using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.AiSubtitles.AppTracks;

/// <summary>
/// Adds <see cref="AppTrackMiddleware"/> in front of Jellyfin's pipeline.
/// </summary>
public class AppTrackStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<AppTrackMiddleware>();
            next(app);
        };
    }
}

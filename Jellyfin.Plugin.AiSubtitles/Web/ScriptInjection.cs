using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Web;

/// <summary>
/// Adds the plugin's middlewares (script injection, tracks for all apps) in front of Jellyfin's pipeline.
/// </summary>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            app.UseMiddleware<AppTracks.AppTrackMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Injects the player script tag into the web client's index.html while it is served
/// (the file on disk is never modified).
/// </summary>
public class ScriptInjectionMiddleware
{
    /// <summary>
    /// Marker used to detect an existing injection.
    /// </summary>
    public const string Marker = "data-ai-subtitles";

    private const string Tag = "<script defer=\"defer\" " + Marker + "=\"1\" src=\"../AiSubtitles/ClientScript\"></script>";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next delegate.</param>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>Task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsIndexRequest(context.Request) || Plugin.Instance?.Configuration.EnableWebPlayerIntegration != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for an uncompressed, complete response so it can be rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        var contentType = context.Response.ContentType ?? string.Empty;
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            && !context.Response.Headers.ContainsKey("Content-Encoding"))
        {
            try
            {
                var html = Encoding.UTF8.GetString(bytes);
                if (!html.Contains(Marker, StringComparison.Ordinal))
                {
                    var idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                    html = idx >= 0 ? html.Insert(idx, Tag) : html + Tag;
                    bytes = Encoding.UTF8.GetBytes(html);
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    context.Response.Headers.CacheControl = "no-cache";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                _logger.LogWarning(ex, "Skript konnte nicht in index.html eingefügt werden");
            }
        }

        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = (request.PathBase + request.Path).Value ?? string.Empty;
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}

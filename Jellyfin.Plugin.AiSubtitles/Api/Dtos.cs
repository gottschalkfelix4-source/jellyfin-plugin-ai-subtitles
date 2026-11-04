using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AiSubtitles.Api;

/// <summary>Subtitle stream.</summary>
/// <param name="Index">Index.</param>
/// <param name="Language">Language.</param>
/// <param name="Title">Title.</param>
/// <param name="Codec">Codec.</param>
/// <param name="IsExternal">External file.</param>
/// <param name="IsForced">Forced.</param>
/// <param name="IsDefault">Default.</param>
public sealed record StreamDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("isForced")] bool IsForced,
    [property: JsonPropertyName("isDefault")] bool IsDefault);

/// <summary>Stream list.</summary>
/// <param name="MediaSourceId">Media source.</param>
/// <param name="Streams">Streams.</param>
public sealed record StreamsDto(
    [property: JsonPropertyName("mediaSourceId")] string MediaSourceId,
    [property: JsonPropertyName("streams")] IReadOnlyList<StreamDto> Streams);

/// <summary>Error response.</summary>
/// <param name="Error">Message.</param>
public sealed record ErrorDto([property: JsonPropertyName("error")] string Error);

/// <summary>Result of a save or test.</summary>
/// <param name="Ok">Success.</param>
/// <param name="Message">Message.</param>
/// <param name="Lines">Sample lines (test only).</param>
/// <param name="Milliseconds">Duration.</param>
public sealed record ResultDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("lines")] IReadOnlyList<string>? Lines = null,
    [property: JsonPropertyName("ms")] long Milliseconds = 0);

/// <summary>Cache statistics.</summary>
/// <param name="Files">Number of cached batches.</param>
/// <param name="Bytes">Size in bytes.</param>
public sealed record CacheInfoDto(
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("bytes")] long Bytes);

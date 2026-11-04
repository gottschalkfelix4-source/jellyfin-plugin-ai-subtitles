using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AiSubtitles.Api;

/// <summary>Language option.</summary>
/// <param name="Code">Code.</param>
/// <param name="Name">Name.</param>
public sealed record LanguageDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Settings the player script needs.</summary>
/// <param name="Enabled">Whether the player integration is on.</param>
/// <param name="Languages">Language list.</param>
/// <param name="DefaultLanguage">Default target language.</param>
/// <param name="CanSave">Whether the user may save translations.</param>
/// <param name="Configured">Whether an AI backend is configured.</param>
public sealed record ClientConfigDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("languages")] IReadOnlyList<LanguageDto> Languages,
    [property: JsonPropertyName("defaultLanguage")] string DefaultLanguage,
    [property: JsonPropertyName("canSave")] bool CanSave,
    [property: JsonPropertyName("configured")] bool Configured);

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

/// <summary>Batch timing.</summary>
/// <param name="Index">Batch index.</param>
/// <param name="Start">Start (s).</param>
/// <param name="End">End (s).</param>
public sealed record BatchDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);

/// <summary>Translation plan.</summary>
/// <param name="MediaSourceId">Media source.</param>
/// <param name="StreamIndex">Stream.</param>
/// <param name="BatchSize">Batch size.</param>
/// <param name="CueCount">Number of cues.</param>
/// <param name="Batches">Batches.</param>
public sealed record PlanDto(
    [property: JsonPropertyName("mediaSourceId")] string MediaSourceId,
    [property: JsonPropertyName("streamIndex")] int StreamIndex,
    [property: JsonPropertyName("batchSize")] int BatchSize,
    [property: JsonPropertyName("cueCount")] int CueCount,
    [property: JsonPropertyName("batches")] IReadOnlyList<BatchDto> Batches);

/// <summary>Translated cue.</summary>
/// <param name="S">Start (s).</param>
/// <param name="E">End (s).</param>
/// <param name="T">Text.</param>
public sealed record CueDto(
    [property: JsonPropertyName("s")] double S,
    [property: JsonPropertyName("e")] double E,
    [property: JsonPropertyName("t")] string T);

/// <summary>Translated batch.</summary>
/// <param name="Batch">Batch index.</param>
/// <param name="Cues">Cues.</param>
public sealed record BatchResultDto(
    [property: JsonPropertyName("batch")] int Batch,
    [property: JsonPropertyName("cues")] IReadOnlyList<CueDto> Cues);

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

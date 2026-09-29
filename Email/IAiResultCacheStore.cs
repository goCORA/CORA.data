namespace CORA.Data.Email;

/// <summary>Which AI feature a cached result belongs to - each kind is looked up independently.</summary>
public enum AiResultCacheKind
{
    /// <summary>A single message's structured summary (<c>AiAssistantService.SummarizeAsync</c>).</summary>
    Summary,

    /// <summary>A single message's extracted fields (<c>AiAssistantService.ExtractInformationAsync</c>).</summary>
    ExtractedInformation,

    /// <summary>
    /// One attachment's analysis (<c>AiAssistantService.AnalyzeAttachmentAsync</c>). A message can
    /// have more than one attachment, so this kind always requires <c>attachmentPartSpecifier</c>
    /// to tell them apart - the two other kinds are one-per-message and never pass it.
    /// </summary>
    AttachmentAnalysis,
}

/// <summary>
/// Local, encrypted cache of AI results keyed to one message, so re-opening the same message and
/// asking for the same result again reads it back instead of paying for another AI request.
/// Only sound for results whose input can never change once the message exists - a single
/// message's own content is immutable, which is why this is used for <see cref="AiResultCacheKind.Summary"/>
/// and <see cref="AiResultCacheKind.ExtractedInformation"/> but deliberately not for a thread
/// summary (new replies can arrive later) or anything with a per-request parameter such as a
/// translation's target language.
/// </summary>
public interface IAiResultCacheStore
{
    /// <summary>
    /// Returns the cached raw result for this message/kind, or null if there is none or it was
    /// cached under a different <paramref name="schemaVersion"/> - a schema change (the prompt
    /// asking for a different JSON shape) must be treated as a cache miss, not returned as if it
    /// still matched what today's parser/UI expects.
    /// </summary>
    /// <param name="attachmentPartSpecifier">
    /// Required (and only meaningful) for <see cref="AiResultCacheKind.AttachmentAnalysis"/>,
    /// where a message's several attachments would otherwise collide on the same cache entry.
    /// </param>
    Task<string?> GetAsync(
        string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default);

    /// <summary>Stores (or overwrites) the raw result for this message/kind under the given schema version.</summary>
    Task SetAsync(
        string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string rawResult, string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default);
}

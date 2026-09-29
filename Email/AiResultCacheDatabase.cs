using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IAiResultCacheStore"/> implementation, following the same pattern as
/// <see cref="AiAutonomyDatabase"/>/<see cref="BlacklistDatabase"/>. The backing file is
/// encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class AiResultCacheDatabase : IAiResultCacheStore, IFlushableStore, IDisposable
{
    private sealed class CacheDoc
    {
        [BsonId]
        public string Key { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public string RawResult { get; set; } = string.Empty;
        public DateTimeOffset CachedAtUtc { get; set; }
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<CacheDoc> _entries;

    public AiResultCacheDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _entries = _file.Database.GetCollection<CacheDoc>("AiResultCache");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    public Task<string?> GetAsync(
        string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = _entries.FindById(BuildKey(accountKey, folderName, uid, kind, attachmentPartSpecifier));
            return doc is not null && doc.SchemaVersion == schemaVersion ? doc.RawResult : null;
        }, cancellationToken);

    public Task SetAsync(
        string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string rawResult, string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _entries.Upsert(new CacheDoc
            {
                Key = BuildKey(accountKey, folderName, uid, kind, attachmentPartSpecifier),
                SchemaVersion = schemaVersion,
                RawResult = rawResult,
                CachedAtUtc = DateTimeOffset.UtcNow,
            });
        }, cancellationToken);

    private static string BuildKey(
        string accountKey, string folderName, uint uid, AiResultCacheKind kind, string? attachmentPartSpecifier) =>
        attachmentPartSpecifier is null
            ? $"{accountKey}|{folderName}|{uid}|{kind}"
            : $"{accountKey}|{folderName}|{uid}|{kind}|{attachmentPartSpecifier}";
}

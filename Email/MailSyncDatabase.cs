using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IMailSyncStore"/> implementation, following the same
/// pattern as <see cref="ContactDatabase"/>. Tracks POP3 message UIDLs per account
/// and folder so that subsequent syncs only download headers for new messages,
/// and remembers local read state (POP3 has no server-side flags).
/// Singleton — the database file/collections are created on first use. The backing
/// file is encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class MailSyncDatabase : IMailSyncStore, IFlushableStore, IDisposable
{
    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<MailSummaryDoc> _summaries;
    private readonly ILiteCollection<DeletedUidlDoc> _deletedUidls;
    private readonly ILiteCollection<MessageBodyDoc> _bodies;
    private readonly ILiteCollection<FolderInfoDoc> _folders;
    private readonly ILiteCollection<CustomFolderDoc> _customFolders;
    private readonly ILiteCollection<RescuedMessageDoc> _rescued;
    private readonly string _attachmentsRoot;
    // Tracks (accountKey, folder) pairs already checked for duplicate uids this process,
    // so the full-collection repair scan runs at most once per folder per app run instead
    // of on every message load (GetMessagesAsync/GetCachedSummariesAsync are called far
    // more often than a repair could ever be needed). ConcurrentDictionary since repairs
    // can be triggered concurrently from GetMessagesAsync and SyncFolderAsync.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _repairedFolders = new(StringComparer.OrdinalIgnoreCase);

    public MailSyncDatabase(string path, string attachmentsRoot)
    {
        _file = EncryptedLiteDbFile.Open(path);

        // Once all bodies have been compressed (see CompressExistingBodies), the next start gives
        // the space they used back. Must run before any collection is taken from the database.
        var compactBackup = path + ".before-compact.bak";
        if (ReadMeta(MetaCompactPending))
        {
            try
            {
                // Safety copy of the (still encrypted) file as it was, kept until the next start
                // has opened the compacted store successfully. Wiping all data deletes it too
                // (it is named after the store file).
                if (File.Exists(path))
                    File.Copy(path, compactBackup, overwrite: true);
                var (before, after) = _file.Compact();
                System.Diagnostics.Debug.WriteLine($"[MailSync] compacted {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB");
                WriteMeta(MetaCompactPending, false);
            }
            catch (Exception ex)
            {
                // The database is unchanged; it simply stays larger. Try again next start.
                System.Diagnostics.Debug.WriteLine($"[MailSync] compaction failed: {ex.Message}");
            }
        }
        else if (File.Exists(compactBackup))
        {
            // The compacted store opened fine on a later start: the safety copy is no longer needed.
            try { File.Delete(compactBackup); } catch (Exception) { }
        }

        _summaries = _file.Database.GetCollection<MailSummaryDoc>("MailSummaries");
        _summaries.EnsureIndex(s => s.AccountKey);
        _summaries.EnsureIndex(s => s.FolderName);

        _deletedUidls = _file.Database.GetCollection<DeletedUidlDoc>("DeletedUidls");
        _deletedUidls.EnsureIndex(d => d.AccountKey);

        _bodies = _file.Database.GetCollection<MessageBodyDoc>("MessageBodies");
        _bodies.EnsureIndex(b => b.AccountKey);

        _folders = _file.Database.GetCollection<FolderInfoDoc>("FolderInfos");
        _folders.EnsureIndex(f => f.AccountKey);

        _customFolders = _file.Database.GetCollection<CustomFolderDoc>("CustomFolders");
        _customFolders.EnsureIndex(f => f.AccountKey);

        _rescued = _file.Database.GetCollection<RescuedMessageDoc>("RescuedMessages");
        _rescued.EnsureIndex(r => r.AccountKey);

        _attachmentsRoot = attachmentsRoot;
        Directory.CreateDirectory(_attachmentsRoot);

        if (!ReadMeta(MetaBodiesCompressed))
            _ = Task.Run(CompressExistingBodies);

#if DEBUG
        _ = Task.Run(() => LogSizeBreakdown(path));
#endif
    }

#if DEBUG
    /// <summary>
    /// Debug builds only: writes what the mail cache file is made of to the Output window
    /// (filter on "[DbSize]"), per collection and per account/folder, to decide how to shrink it.
    /// </summary>
    private void LogSizeBreakdown(string path)
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var fileBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            System.Diagnostics.Debug.WriteLine($"[DbSize] file on disk: {fileBytes / 1048576.0:F1} MB");

            long total = 0;
            foreach (var name in _file.Database.GetCollectionNames().OrderBy(n => n))
            {
                long bytes = 0, count = 0;
                foreach (var doc in _file.Database.GetCollection(name).FindAll())
                {
                    bytes += BsonSerializer.Serialize(doc).Length;
                    count++;
                }
                total += bytes;
                System.Diagnostics.Debug.WriteLine($"[DbSize] {name}: {count} docs, {bytes / 1048576.0:F1} MB");
            }
            System.Diagnostics.Debug.WriteLine($"[DbSize] all documents: {total / 1048576.0:F1} MB (the rest of the file is indexes and free space)");

            // Bodies by account/folder, biggest first, and how much of them is HTML.
            var byFolder = new Dictionary<string, (long Count, long Bytes, long Html, long Text)>();
            foreach (var doc in _file.Database.GetCollection("MessageBodies").FindAll())
            {
                var key = $"{doc["AccountKey"].AsString} / {doc["FolderName"].AsString}";
                var html = doc["HtmlBody"].IsString ? doc["HtmlBody"].AsString.Length : 0;
                var text = doc["TextBody"].IsString ? doc["TextBody"].AsString.Length : 0;
                byFolder.TryGetValue(key, out var v);
                byFolder[key] = (v.Count + 1, v.Bytes + BsonSerializer.Serialize(doc).Length, v.Html + html, v.Text + text);
            }
            foreach (var (key, v) in byFolder.OrderByDescending(kv => kv.Value.Bytes).Take(25))
                System.Diagnostics.Debug.WriteLine(
                    $"[DbSize] bodies {key}: {v.Count} messages, {v.Bytes / 1048576.0:F1} MB (HTML {v.Html / 1048576.0:F1} M chars, text {v.Text / 1048576.0:F1} M chars)");

            System.Diagnostics.Debug.WriteLine($"[DbSize] done in {started.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DbSize] failed: {ex.Message}");
        }
    }
#endif

    public void Dispose() => _file.Dispose();

    // --- Body compression --------------------------------------------------------------------
    // Message bodies (mostly HTML) were by far the largest part of this store, and the whole
    // store is re-encrypted and rewritten on every save. They are now kept Brotli-compressed in
    // HtmlBodyZ/TextBodyZ (typically a fifth to a tenth of the size); compression happens before
    // the store is encrypted, so nothing on disk is less protected. Older rows that still hold
    // plain HtmlBody/TextBody are read as they are and converted once in the background.

    private const string MetaBodiesCompressed = "BodiesCompressed";
    private const string MetaCompactPending = "CompactPending";

    private bool ReadMeta(string key)
    {
        var doc = _file.Database.GetCollection("Meta").FindById(key);
        return doc is not null && doc["Value"].IsBoolean && doc["Value"].AsBoolean;
    }

    private void WriteMeta(string key, bool value) =>
        _file.Database.GetCollection("Meta").Upsert(new BsonDocument { ["_id"] = key, ["Value"] = value });

    internal static byte[]? PackText(string? text)
    {
        if (text is null)
            return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream(bytes.Length / 4 + 16);
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    internal static string? UnpackText(byte[]? packed, string? legacy)
    {
        if (packed is null)
            return legacy;
        try
        {
            using var input = new MemoryStream(packed);
            using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream(packed.Length * 4);
            brotli.CopyTo(output);
            return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            // A damaged entry must not stop the message from opening at all: show what we have.
            System.Diagnostics.Debug.WriteLine($"[MailSync] could not decompress a stored body: {ex.Message}");
            return legacy;
        }
    }

    /// <summary>
    /// One-time background conversion of bodies stored before compression existed. Each row is
    /// re-read and updated on its own (Update, never Upsert), so a row that was moved or deleted
    /// meanwhile is simply skipped. When done, the next start compacts the store.
    /// </summary>
    private void CompressExistingBodies()
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var ids = _bodies.Query()
                .Where(b => b.HtmlBody != null || b.TextBody != null)
                .Select(b => b.Id)
                .ToList();

            foreach (var id in ids)
            {
                var doc = _bodies.FindById(id);
                if (doc is null || (doc.HtmlBody is null && doc.TextBody is null))
                    continue;
                doc.HtmlBodyZ ??= PackText(doc.HtmlBody);
                doc.TextBodyZ ??= PackText(doc.TextBody);
                doc.HtmlBody = null;
                doc.TextBody = null;
                _bodies.Update(doc);
            }

            WriteMeta(MetaBodiesCompressed, true);
            if (ids.Count > 0)
                WriteMeta(MetaCompactPending, true);
            System.Diagnostics.Debug.WriteLine($"[MailSync] compressed {ids.Count} stored bodies in {started.ElapsedMilliseconds} ms; space is given back at the next start");
        }
        catch (Exception ex)
        {
            // Unconverted rows still read fine; the conversion is retried at the next start.
            System.Diagnostics.Debug.WriteLine($"[MailSync] body compression failed: {ex.Message}");
        }
    }

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static string MakeKey(string accountKey, string folderFullName, string uidl) =>
        string.Join("|", accountKey, folderFullName, uidl);

    /// <summary>
    /// Repairs Trash rows written by an older, buggy version of <see cref="MoveToTrashAsync"/>
    /// that cleared Uidl to string.Empty instead of keeping it in sync with the row's id.
    /// The id was still built as "accountKey|folder|&lt;guid&gt;" at move time, so the guid can
    /// be recovered from the id itself and written back as the row's Uidl. No-ops for rows
    /// that already have a Uidl.
    /// </summary>
    private string RepairUidlIfEmpty(MailSummaryDoc doc)
    {
        if (!string.IsNullOrEmpty(doc.Uidl))
            return doc.Uidl;

        var idx = doc.Id.LastIndexOf('|');
        if (idx < 0 || idx == doc.Id.Length - 1)
            return doc.Uidl;

        var recovered = doc.Id[(idx + 1)..];
        if (string.IsNullOrEmpty(recovered))
            return doc.Uidl;

        doc.Uidl = recovered;
        _summaries.Update(doc);

        // The body row shares the same id, so its Uidl needs the same repair for consistency
        // (GetBodyAsync itself only needs the id, but keep the field truthful).
        var bodyDoc = _bodies.FindById(doc.Id);
        if (bodyDoc is not null && string.IsNullOrEmpty(bodyDoc.Uidl))
        {
            bodyDoc.Uidl = recovered;
            _bodies.Update(bodyDoc);
        }

        return recovered;
    }

    public Task<HashSet<string>> GetKnownUidlsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var result = new HashSet<string>();

            foreach (var s in _summaries.Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName))
                result.Add(s.Uidl);

            foreach (var d in _deletedUidls.Find(d => d.AccountKey == accountKey && d.FolderName == folderFullName))
                result.Add(d.Uidl);

            return result;
        }, cancellationToken);

    public Task<HashSet<string>> GetUidlsMissingBodyAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var summaryUidls = _summaries
                .Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName)
                .Select(s => s.Uidl)
                .ToHashSet();

            var bodyUidls = _bodies
                .Find(b => b.AccountKey == accountKey && b.FolderName == folderFullName)
                .Select(b => b.Uidl)
                .ToHashSet();

            summaryUidls.ExceptWith(bodyUidls);
            return summaryUidls;
        }, cancellationToken);

    public Task UpsertAsync(
        string accountKey, string folderFullName, IEnumerable<StoredMailSummary> summaries,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            foreach (var summary in summaries)
            {
                var id = MakeKey(accountKey, folderFullName, summary.Uidl);
                var existing = _summaries.FindById(id);
                var isRead = existing?.IsRead ?? summary.IsRead;
                var isImportant = existing?.IsImportant ?? summary.IsImportant;

                _summaries.Upsert(new MailSummaryDoc
                {
                    Id = id,
                    AccountKey = accountKey,
                    FolderName = folderFullName,
                    Uidl = summary.Uidl,
                    Uid = summary.Uid,
                    From = summary.From,
                    Subject = summary.Subject,
                    Date = summary.Date,
                    IsRead = isRead,
                    HasAttachments = summary.HasAttachments,
                    IsLocalOnly = existing?.IsLocalOnly ?? summary.IsLocalOnly,
                    IsImportant = isImportant,

                    // Prefer freshly-captured headers, but never let a summary that lacks them
                    // wipe values already stored - that keeps a later backfill idempotent and
                    // protects rows whose headers were captured by a different code path.
                    MessageId = Coalesce(summary.MessageId, existing?.MessageId),
                    InReplyTo = Coalesce(summary.InReplyTo, existing?.InReplyTo),
                    References = Coalesce(summary.References, existing?.References),
                });
            }
        }, cancellationToken);

    public Task<List<StoredMailSummary>> GetCachedSummariesAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var docs = _summaries
                .Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName)
                .OrderByDescending(s => s.Date)
                .ToList();

            // Trash rows written before MoveToTrashAsync started reassigning Uid can still
            // carry the Uid they had in their original folder, which may collide with another
            // message moved there from a different folder/position. Since Uid is the only key
            // used to address a message within a folder elsewhere in the app, a collision means
            // acting on one Trash message (e.g. permanent delete) can silently affect the wrong
            // one instead. Repair any duplicates found on read by reassigning fresh Uids.
            // Only do this once per folder per process: it's a full-collection scan+rewrite,
            // and once repaired a folder cannot develop new duplicates just by being read.
            if (string.Equals(folderFullName, "Trash", StringComparison.OrdinalIgnoreCase)
                && _repairedFolders.TryAdd(MakeKey(accountKey, folderFullName, "__trash-dupe-check__"), 0))
                RepairDuplicateUids(accountKey, folderFullName, docs);

            return docs
                .Select(s => new StoredMailSummary
                {
                    Uidl = RepairUidlIfEmpty(s),
                    Uid = s.Uid,
                    From = s.From,
                    Subject = s.Subject,
                    Date = s.Date,
                    IsRead = s.IsRead,
                    HasAttachments = s.HasAttachments,
                    IsLocalOnly = s.IsLocalOnly,
                    IsImportant = s.IsImportant,
                    MessageId = s.MessageId ?? string.Empty,
                    InReplyTo = s.InReplyTo ?? string.Empty,
                    References = s.References ?? string.Empty,
                })
                .ToList();
        }, cancellationToken);

    /// <summary>
    /// Returns <paramref name="incoming"/> when it has a value, otherwise falls back to what is
    /// already stored. Used for threading headers so a sync path that does not capture them
    /// leaves existing values intact instead of blanking them.
    /// </summary>
    private static string Coalesce(string? incoming, string? existing) =>
        !string.IsNullOrWhiteSpace(incoming) ? incoming
        : !string.IsNullOrWhiteSpace(existing) ? existing
        : string.Empty;

    /// <summary>Reassigns a fresh, unique Uid to every summary after the first found with a given Uid.</summary>
    private void RepairDuplicateUids(string accountKey, string folderFullName, List<MailSummaryDoc> docs)
    {
        var seenUids = new HashSet<uint>();
        foreach (var doc in docs)
        {
            if (seenUids.Add(doc.Uid))
                continue;

            doc.Uid = NextUid(accountKey, folderFullName);
            seenUids.Add(doc.Uid);
            _summaries.Update(doc);
        }
    }

    /// <summary>
    /// POP3-only maintenance: repairs a folder whose summaries have colliding uids, e.g. from
    /// before uids were reserved uniquely (see <see cref="ReserveUidAsync"/>) instead of being
    /// derived from a positional server index that could clash with an already-cached message.
    /// Callers must only invoke this for POP3 accounts/folders — IMAP uids are the server's own
    /// stable identifiers and must never be reassigned.
    /// </summary>
    public Task RepairDuplicateUidsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // This is a full-collection scan; once a folder has been checked in this process
            // it cannot develop new uid duplicates just from normal reads/syncs, so skip repeat
            // scans on every message load/sync to avoid unnecessary LiteDB work.
            if (!_repairedFolders.TryAdd(MakeKey(accountKey, folderFullName, "__dupe-check__"), 0))
                return;

            var docs = _summaries.Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName).ToList();
            RepairDuplicateUids(accountKey, folderFullName, docs);
        }, cancellationToken);

    public Task SetReadStateAsync(
        string accountKey, string folderFullName, string uidl, bool isRead,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // Try the fast primary-key lookup first, then fall back to a field query
            // so a key-construction mismatch never silently leaves the entry unchanged.
            var id = MakeKey(accountKey, folderFullName, uidl);
            var doc = _summaries.FindById(id)
                   ?? _summaries.FindOne(s =>
                          s.AccountKey == accountKey &&
                          s.FolderName == folderFullName &&
                          s.Uidl == uidl);

            if (doc is null)
            {
                // Fall back to matching by uid (numeric) in case the uidl string format
                // differs between sync and update (e.g. IMAP uid stored as "123" vs uint).
                if (uint.TryParse(uidl, out var numericUid))
                    doc = _summaries.FindOne(s =>
                              s.AccountKey == accountKey &&
                              s.FolderName == folderFullName &&
                              s.Uid == numericUid);
            }

            if (doc is null)
                return;

            doc.IsRead = isRead;
            _summaries.Update(doc);
        }, cancellationToken);

    public Task SetImportantAsync(
        string accountKey, string folderFullName, string uidl, bool isImportant,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeKey(accountKey, folderFullName, uidl);
            var doc = _summaries.FindById(id)
                   ?? _summaries.FindOne(s =>
                          s.AccountKey == accountKey &&
                          s.FolderName == folderFullName &&
                          s.Uidl == uidl);

            if (doc is null)
            {
                if (uint.TryParse(uidl, out var numericUid))
                    doc = _summaries.FindOne(s =>
                              s.AccountKey == accountKey &&
                              s.FolderName == folderFullName &&
                              s.Uid == numericUid);
            }

            if (doc is null)
                return;

            doc.IsImportant = isImportant;
            _summaries.Update(doc);
        }, cancellationToken);

    public Task<string?> GetUidlForUidAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
            _summaries
                .Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName && s.Uid == uid)
                .Select(s => (string?)RepairUidlIfEmpty(s))
                .FirstOrDefault(), cancellationToken);

    public Task DeleteAsync(
        string accountKey, string folderFullName, string uidl, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeKey(accountKey, folderFullName, uidl);

            var body = _bodies.FindById(id);
            var cachePaths = body?.Attachments.Select(a => a.CachePath).Where(p => !string.IsNullOrEmpty(p)).ToList()
                              ?? [];

            _summaries.Delete(id);
            _bodies.Delete(id);

            _deletedUidls.Upsert(new DeletedUidlDoc
            {
                Id = id,
                AccountKey = accountKey,
                FolderName = folderFullName,
                Uidl = uidl,
            });

            foreach (var path in cachePaths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // Best-effort cleanup; a stray cache file is not worth failing the delete over.
                }
            }
        }, cancellationToken);

    public Task MoveSyncedMessageAsync(
        string accountKey, string sourceFolderFullName, string sourceUidl,
        string destinationFolderFullName, uint destinationUid, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var sourceId = MakeKey(accountKey, sourceFolderFullName, sourceUidl);
            var destUidl = destinationUid.ToString();
            var destId = MakeKey(accountKey, destinationFolderFullName, destUidl);

            var summary = _summaries.FindById(sourceId);
            if (summary is not null)
            {
                _summaries.Delete(sourceId);
                summary.Id = destId;
                summary.FolderName = destinationFolderFullName;
                summary.Uid = destinationUid;
                summary.Uidl = destUidl;
                summary.IsLocalOnly = false;
                _summaries.Upsert(summary);
            }

            var body = _bodies.FindById(sourceId);
            if (body is not null)
            {
                _bodies.Delete(sourceId);
                body.Id = destId;
                body.FolderName = destinationFolderFullName;
                body.Uidl = destUidl;
                _bodies.Upsert(body);
            }

            // The destination row is a real server message now; never let a stale tombstone hide it.
            _deletedUidls.Delete(destId);

            // Same as DeleteAsync: the source folder must not re-add the message on its next sync.
            _deletedUidls.Upsert(new DeletedUidlDoc
            {
                Id = sourceId,
                AccountKey = accountKey,
                FolderName = sourceFolderFullName,
                Uidl = sourceUidl,
            });
        }, cancellationToken);

    public Task MoveLocalMessageAsync(
        string accountKey, string sourceFolderFullName, string destinationFolderFullName, string uidl,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var sourceId = MakeKey(accountKey, sourceFolderFullName, uidl);
            var destId = MakeKey(accountKey, destinationFolderFullName, uidl);

            var summary = _summaries.FindById(sourceId);
            if (summary is not null)
            {
                _summaries.Delete(sourceId);
                summary.Id = destId;
                summary.FolderName = destinationFolderFullName;
                // Assign a fresh Uid that is unique within the destination folder so
                // the moved message can be addressed/read correctly from there.
                summary.Uid = NextUid(accountKey, destinationFolderFullName);
                // Mark this row as locally-moved (not synced from the server), so a
                // subsequent server sync of the destination folder never treats its
                // (possibly non-server) Uidl as an orphan and moves it straight back
                // to Trash - see SyncFolderAsync's orphan-pruning in MailKitEmailService.
                summary.IsLocalOnly = true;
                _summaries.Upsert(summary);
            }

            var body = _bodies.FindById(sourceId);
            if (body is not null)
            {
                _bodies.Delete(sourceId);
                body.Id = destId;
                body.FolderName = destinationFolderFullName;
                _bodies.Upsert(body);
            }

            // Record a tombstone in the source folder so a background sync of that folder
            // never re-adds the message we just moved away from it.
            _deletedUidls.Upsert(new DeletedUidlDoc
            {
                Id = sourceId,
                AccountKey = accountKey,
                FolderName = sourceFolderFullName,
                Uidl = uidl,
            });
        }, cancellationToken);

    // Tracks the highest Uid handed out so far per (accountKey, folderFullName) this
    // process, seeded lazily from the DB's current max on first use. NextUid must not
    // rely solely on querying _summaries for the current max: callers (e.g. SyncFolderAsync)
    // reserve a uid for each new message *before* the whole batch is upserted at the end of
    // the sync loop, so every reservation within the same sync would otherwise see the same
    // stale DB max and hand out the same (colliding) uid to every new message in the batch.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _uidCounters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a Uid guaranteed not to already be in use by any summary in the given account/folder.</summary>
    private uint NextUid(string accountKey, string folderFullName)
    {
        var counterKey = MakeKey(accountKey, folderFullName, "__uid-counter__");
        return _uidCounters.AddOrUpdate(
            counterKey,
            _ => SeedUidCounter(accountKey, folderFullName) + 1,
            (_, current) => current + 1);
    }

    private uint SeedUidCounter(string accountKey, string folderFullName)
    {
        var maxUid = _summaries
            .Find(s => s.AccountKey == accountKey && s.FolderName == folderFullName)
            .Select(s => (uint?)s.Uid)
            .Max();
        return maxUid ?? 0;
    }

    public Task<uint> ReserveUidAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() => NextUid(accountKey, folderFullName), cancellationToken);

    public Task MoveToTrashAsync(
        string accountKey, string sourceFolderFullName, string uidl, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            MoveToTrashCore(accountKey, sourceFolderFullName, uidl);
        }, cancellationToken);

    private void MoveToTrashCore(string accountKey, string sourceFolderFullName, string uidl)
    {
            const string trashFolder = "Trash";
            var sourceId = MakeKey(accountKey, sourceFolderFullName, uidl);

            // A message moved into Trash has no reliable server UIDL any more (it may have
            // already been removed from the server, or be an orphaned local copy), so key
            // the Trash copy on a freshly generated id instead. The generated id is also
            // stored back as the Uidl so later lookups (which resolve uid -> Uidl -> body id)
            // can still find the body; clearing it to string.Empty would make the summary and
            // body ids disagree and the message would fail to load from cache.
            var trashUidl = Guid.NewGuid().ToString("N");
            var trashId = MakeKey(accountKey, trashFolder, trashUidl);

            var summary = _summaries.FindById(sourceId);
            if (summary is not null)
            {
                _summaries.Delete(sourceId);
                summary.Id = trashId;
                summary.FolderName = trashFolder;
                summary.Uidl = trashUidl;

                // The Uid a message had in its source folder (a POP3 positional index) has
                // no meaning in Trash and can collide with another message moved there from a
                // different source folder/position. Since Uid is the only key the rest of the
                // app (GetUidlForUidAsync, SetReadStateAsync, DeleteAsync, ...) uses to address
                // a message within a folder, a collision means an action on one Trash message
                // (e.g. permanent delete) can silently resolve to and affect a different one.
                // Reassign a Uid that is guaranteed unique within Trash for this account.
                summary.Uid = NextUid(accountKey, trashFolder);
                _summaries.Upsert(summary);
            }

            var body = _bodies.FindById(sourceId);
            if (body is not null)
            {
                _bodies.Delete(sourceId);
                body.Id = trashId;
                body.FolderName = trashFolder;
                body.Uidl = trashUidl;
                _bodies.Upsert(body);
            }

            // Record a tombstone in the source folder so a background sync of that folder
            // never re-adds the message we just moved away from it.
            _deletedUidls.Upsert(new DeletedUidlDoc
            {
                Id = sourceId,
                AccountKey = accountKey,
                FolderName = sourceFolderFullName,
                Uidl = uidl,
            });
    }

    public Task UpsertBodyAsync(
        string accountKey, string folderFullName, string uidl, StoredMailBody body,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeKey(accountKey, folderFullName, uidl);
            _bodies.Upsert(new MessageBodyDoc
            {
                Id = id,
                AccountKey = accountKey,
                FolderName = folderFullName,
                Uidl = uidl,
                To = body.To,
                TextBodyZ = PackText(body.TextBody),
                HtmlBodyZ = PackText(body.HtmlBody),
                Attachments = body.Attachments,
            });
        }, cancellationToken);

    public Task<StoredMailBody?> GetBodyAsync(
        string accountKey, string folderFullName, string uidl, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeKey(accountKey, folderFullName, uidl);
            var doc = _bodies.FindById(id);
            if (doc is null)
                return null;

            return new StoredMailBody
            {
                To = doc.To,
                TextBody = UnpackText(doc.TextBodyZ, doc.TextBody),
                HtmlBody = UnpackText(doc.HtmlBodyZ, doc.HtmlBody),
                Attachments = doc.Attachments,
            };
        }, cancellationToken);

    public async Task<string> SaveAttachmentAsync(
        string accountKey, string folderFullName, string uidl, string partSpecifier, string fileName,
        Stream content, CancellationToken cancellationToken = default)
    {
        var safeAccount = MakeSafeSegment(accountKey);
        var safeFolder = MakeSafeSegment(folderFullName);
        var safeUidl = MakeSafeSegment(uidl);
        var safePart = MakeSafeSegment(partSpecifier);
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : MakeSafeSegment(fileName);

        var dir = Path.Combine(_attachmentsRoot, safeAccount, safeFolder, safeUidl);
        Directory.CreateDirectory(dir);

        // Attempt to create the target file. If the file is currently locked by an
        // external process (e.g. a PDF viewer holding the file open), creating a
        // file with the same name will fail. To avoid hard failures that surface as
        // "Could not load messages" to the user, fall back to a unique filename
        // (appending a GUID) when a create collision/lock occurs.
        var basePath = Path.Combine(dir, $"{safePart}_{safeFileName}");
        var path = basePath;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await using (var file = File.Create(path))
                {
                    await content.CopyToAsync(file, cancellationToken);
                }

                // Success
                return path;
            }
            catch (IOException) when (attempt < 3)
            {
                // File is likely in use. Try a new unique filename and retry.
                path = Path.Combine(dir, $"{safePart}_{Guid.NewGuid().ToString("N")}_{safeFileName}");
            }
        }

        // If we reached here the last attempt will throw so do one final try to let
        // the caller observe the exception if it still fails.
        await using (var finalFile = File.Create(path))
        {
            await content.CopyToAsync(finalFile, cancellationToken);
        }

        return path;
    }

    public Stream OpenAttachment(string cachePath) =>
        File.OpenRead(cachePath);

    public Task UpsertFoldersAsync(
        string accountKey, IEnumerable<MailFolderInfo> folders, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = accountKey;
            _folders.Upsert(new FolderInfoDoc
            {
                Id = id,
                AccountKey = accountKey,
                Folders = folders.Select(f => new FolderInfoEntry
                {
                    FullName = f.FullName,
                    Name = f.Name,
                    Total = f.Total,
                    Unread = f.Unread,
                    IsJunk = f.IsJunk,
                }).ToList(),
            });
        }, cancellationToken);

    public Task<List<MailFolderInfo>> GetCachedFoldersAsync(
        string accountKey, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = _folders.FindById(accountKey);
            if (doc is null)
                return new List<MailFolderInfo>();

            return doc.Folders.Select(f => new MailFolderInfo
            {
                FullName = f.FullName,
                Name = f.Name,
                Total = f.Total,
                Unread = f.Unread,
                IsJunk = f.IsJunk,
            }).ToList();
        }, cancellationToken);

    // Message-Ids come back from servers and MimeKit with or without angle brackets; the key
    // ignores them (and surrounding spaces) so the same message always matches itself.
    internal static string NormalizeMessageId(string? messageId) =>
        (messageId ?? string.Empty).Trim().Trim('<', '>').Trim();

    public Task MarkRescuedAsync(
        string accountKey, IEnumerable<string> messageIds, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            foreach (var messageId in messageIds.Select(NormalizeMessageId).Where(id => id.Length > 0).Distinct())
            {
                _rescued.Upsert(new RescuedMessageDoc
                {
                    Id = accountKey + "|" + messageId,
                    AccountKey = accountKey,
                    MessageId = messageId,
                    RescuedUtc = DateTime.UtcNow,
                });
            }
        }, cancellationToken);

    public Task<HashSet<string>> GetRescuedAsync(
        string accountKey, IEnumerable<string> messageIds, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var messageId in messageIds.Select(NormalizeMessageId).Where(id => id.Length > 0).Distinct())
            {
                if (_rescued.FindById(accountKey + "|" + messageId) is not null)
                    found.Add(messageId);
            }

            return found;
        }, cancellationToken);

    public Task DeleteAccountDataAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // Same normalization as MailKitEmailService.AccountKey.
            var accountKey = emailAddress.Trim().ToLowerInvariant();
            if (accountKey.Length == 0)
                return;

            _summaries.DeleteMany(s => s.AccountKey == accountKey);
            _bodies.DeleteMany(b => b.AccountKey == accountKey);
            _deletedUidls.DeleteMany(d => d.AccountKey == accountKey);
            _folders.DeleteMany(f => f.AccountKey == accountKey);
            _customFolders.DeleteMany(f => f.AccountKey == accountKey);
            _rescued.DeleteMany(r => r.AccountKey == accountKey);

            // Forget per-process bookkeeping so a later re-add of the same address starts clean.
            var prefix = accountKey + "|";
            foreach (var key in _uidCounters.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _uidCounters.TryRemove(key, out _);
            foreach (var key in _repairedFolders.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _repairedFolders.TryRemove(key, out _);

            // Attachment bytes live outside the database, one folder per account.
            try
            {
                var dir = Path.Combine(_attachmentsRoot, MakeSafeSegment(accountKey));
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort, like DeleteAsync: the rows are gone; a stray cache file is not worth failing over.
            }
        }, cancellationToken);

    private static string MakeSafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }

    public Task AddCustomFolderAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = string.Join("|", accountKey, folderFullName);
            _customFolders.Upsert(new CustomFolderDoc
            {
                Id = id,
                AccountKey = accountKey,
                FolderFullName = folderFullName,
            });
        }, cancellationToken);

    public Task RemoveCustomFolderAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = string.Join("|", accountKey, folderFullName);
            _customFolders.Delete(id);
        }, cancellationToken);

    public Task RenameCustomFolderAsync(
        string accountKey, string oldFolderFullName, string newFolderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var oldId = string.Join("|", accountKey, oldFolderFullName);
            var newId = string.Join("|", accountKey, newFolderFullName);
            var customFolder = _customFolders.FindById(oldId);
            if (customFolder is not null)
            {
                _customFolders.Delete(oldId);
                customFolder.Id = newId;
                customFolder.FolderFullName = newFolderFullName;
                _customFolders.Upsert(customFolder);
            }

            foreach (var summary in _summaries.Find(s => s.AccountKey == accountKey && s.FolderName == oldFolderFullName).ToList())
            {
                _summaries.Delete(summary.Id);
                summary.Id = MakeKey(accountKey, newFolderFullName, summary.Uidl);
                summary.FolderName = newFolderFullName;
                _summaries.Upsert(summary);
            }

            foreach (var body in _bodies.Find(b => b.AccountKey == accountKey && b.FolderName == oldFolderFullName).ToList())
            {
                _bodies.Delete(body.Id);
                body.Id = MakeKey(accountKey, newFolderFullName, body.Uidl);
                body.FolderName = newFolderFullName;
                _bodies.Upsert(body);
            }

            foreach (var tombstone in _deletedUidls.Find(d => d.AccountKey == accountKey && d.FolderName == oldFolderFullName).ToList())
            {
                _deletedUidls.Delete(tombstone.Id);
                tombstone.Id = MakeKey(accountKey, newFolderFullName, tombstone.Uidl);
                tombstone.FolderName = newFolderFullName;
                _deletedUidls.Upsert(tombstone);
            }

            // Forget per-process uid-counter/repair bookkeeping keyed on the old folder name
            // so it is reseeded fresh under the new name.
            var oldCounterPrefix = string.Join("|", accountKey, oldFolderFullName) + "|";
            foreach (var key in _uidCounters.Keys.Where(k => k.StartsWith(oldCounterPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _uidCounters.TryRemove(key, out _);
            _repairedFolders.TryRemove(MakeKey(accountKey, oldFolderFullName, "__dupe-check__"), out _);
        }, cancellationToken);

    public Task<List<string>> GetCustomFoldersAsync(
        string accountKey, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
            _customFolders.Find(f => f.AccountKey == accountKey)
                .Select(f => f.FolderFullName)
                .ToList(),
        cancellationToken);

    private sealed class CustomFolderDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string FolderFullName { get; set; } = string.Empty;
    }

    private sealed class MailSummaryDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string Uidl { get; set; } = string.Empty;
        public uint Uid { get; set; }
        public string From { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; }
        public bool IsRead { get; set; }
        public bool HasAttachments { get; set; }
        public bool IsLocalOnly { get; set; }
        public bool IsImportant { get; set; }

        // RFC 5322 threading headers. Absent from documents written before threading capture
        // was added; LiteDB deserializes those as empty strings rather than failing.
        public string MessageId { get; set; } = string.Empty;
        public string InReplyTo { get; set; } = string.Empty;
        public string References { get; set; } = string.Empty;
    }

    private sealed class DeletedUidlDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string Uidl { get; set; } = string.Empty;
    }

    private sealed class MessageBodyDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string Uidl { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        // Only in rows written before bodies were compressed; see CompressExistingBodies.
        public string? TextBody { get; set; }
        public string? HtmlBody { get; set; }
        // Brotli-compressed UTF-8 (PackText/UnpackText).
        public byte[]? TextBodyZ { get; set; }
        public byte[]? HtmlBodyZ { get; set; }
        public List<StoredMailAttachment> Attachments { get; set; } = [];
    }

    private sealed class FolderInfoEntry
    {
        public string FullName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Unread { get; set; }
        public bool IsJunk { get; set; }
    }

    private sealed class RescuedMessageDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public DateTime RescuedUtc { get; set; }
    }

    private sealed class FolderInfoDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public List<FolderInfoEntry> Folders { get; set; } = [];
    }
}

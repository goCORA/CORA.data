using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>What this device knows about one synced item (claude/design-cloud-backup-sync.md).</summary>
public sealed class SyncItemState
{
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Goes up with every change. Clocks aren't trusted: highest seen + 1.</summary>
    public long Revision { get; set; }

    /// <summary>The device that made this revision; breaks ties between equal revisions.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the item as last merged. A different hash now means the item changed on this device.
    /// A hash, not a copy, so mail passwords aren't stored a second time.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>A deletion marker: the item is gone, and this stops it coming back from an older copy.</summary>
    public bool Deleted { get; set; }

    public DateTime ChangedUtc { get; set; }
}

/// <summary>This device's position for one kind of synced data.</summary>
public sealed class SyncKindState
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>The highest version seen. A snapshot older than this is refused (rollback protection).</summary>
    public long HighestVersionSeen { get; set; }

    /// <summary>The version last merged into this device.</summary>
    public long AppliedVersion { get; set; }

    /// <summary>This device has merged changes the cloud doesn't have yet (an upload failed or lost the race).</summary>
    public bool NeedsUpload { get; set; }
}

public enum SyncHeldState
{
    /// <summary>A new account, waiting for the confirmation.</summary>
    WaitingAdd,

    /// <summary>On the other devices, and not wanted here ("Synced accounts not on this device").</summary>
    Skipped,

    /// <summary>A server change, waiting for Accept or Reject.</summary>
    WaitingChange,

    /// <summary>A server change this device rejected: it stays as it is.</summary>
    RejectedChange,

    /// <summary>Removed elsewhere, waiting for Accept or Reject.</summary>
    WaitingRemove,

    /// <summary>A removal this device rejected: the account stays here only.</summary>
    RejectedRemove,
}

/// <summary>A mail account sync hasn't applied here. Settings only: never a password or token.</summary>
public sealed class SyncHeldAccount
{
    public string Email { get; set; } = string.Empty;
    public SyncHeldState State { get; set; }
    public string? Data { get; set; }
}

/// <summary>A tag from another device, waiting for its email to arrive here.</summary>
public sealed class SyncPendingTag
{
    public string AccountKey { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
}

/// <summary>
/// This device's own sync bookkeeping. It belongs to the device: it is never synced and is in no
/// backup (a restored copy would wrongly say what this device has), and Erase deletes it.
/// </summary>
public interface ISyncStateStore
{
    /// <summary>The goCORA account this state belongs to, or null when there is none yet.</summary>
    Task<string?> GetOwnerAsync(CancellationToken cancellationToken = default);
    Task SetOwnerAsync(string ownerUid, CancellationToken cancellationToken = default);

    /// <summary>Never null: a kind not seen yet has version 0.</summary>
    Task<SyncKindState> GetKindAsync(string kind, CancellationToken cancellationToken = default);

    /// <summary>Saves a kind's position. <see cref="SyncKindState.HighestVersionSeen"/> never goes down.</summary>
    Task SetKindAsync(SyncKindState state, CancellationToken cancellationToken = default);

    Task<List<SyncItemState>> GetItemsAsync(string kind, CancellationToken cancellationToken = default);
    Task SaveItemsAsync(string kind, IEnumerable<SyncItemState> items, CancellationToken cancellationToken = default);

    /// <summary>Removes deletion markers older than <paramref name="beforeUtc"/>; returns how many.</summary>
    Task<int> PurgeDeletedAsync(DateTime beforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Mail accounts waiting for an answer, skipped or rejected on this device.</summary>
    Task<List<SyncHeldAccount>> GetHeldAccountsAsync(CancellationToken cancellationToken = default);
    Task SetHeldAccountAsync(SyncHeldAccount account, CancellationToken cancellationToken = default);
    Task RemoveHeldAccountAsync(string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>A small named value, or null.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<List<SyncPendingTag>> GetPendingTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a waiting tag; a newer one for the same email replaces it.</summary>
    Task SetPendingTagAsync(SyncPendingTag tag, CancellationToken cancellationToken = default);
    Task RemovePendingTagAsync(string accountKey, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Forgets everything (sync turned off and on, another goCORA account).</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>LiteDB-backed <see cref="ISyncStateStore"/>, encrypted at rest like every other store.</summary>
public class SyncStateDatabase : ISyncStateStore, IFlushableStore, IDisposable
{
    private const string OwnerKey = "owner";

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<MetaDoc> _meta;
    private readonly ILiteCollection<KindDoc> _kinds;
    private readonly ILiteCollection<ItemDoc> _items;
    private readonly ILiteCollection<HeldDoc> _held;
    private readonly ILiteCollection<PendingTagDoc> _pendingTags;

    public SyncStateDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _meta = _file.Database.GetCollection<MetaDoc>("Meta");
        _kinds = _file.Database.GetCollection<KindDoc>("Kinds");
        _items = _file.Database.GetCollection<ItemDoc>("Items");
        _items.EnsureIndex(i => i.Kind);
        _held = _file.Database.GetCollection<HeldDoc>("HeldAccounts");
        _pendingTags = _file.Database.GetCollection<PendingTagDoc>("PendingTags");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    // Same normalization as MailKitEmailService.AccountKey.
    private static string Normalize(string emailAddress) => emailAddress.Trim().ToLowerInvariant();

    private static string ItemKey(string kind, string itemId) => string.Join("|", kind, itemId);

    private static string PendingKey(string accountKey, string messageId) => string.Join("|", accountKey, messageId);

    public Task<string?> GetOwnerAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _meta.FindById(OwnerKey)?.Value, cancellationToken);

    public Task SetOwnerAsync(string ownerUid, CancellationToken cancellationToken = default) =>
        Task.Run(() => _meta.Upsert(new MetaDoc { Key = OwnerKey, Value = ownerUid }), cancellationToken);

    public Task<SyncKindState> GetKindAsync(string kind, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = _kinds.FindById(kind);
            return new SyncKindState
            {
                Kind = kind,
                HighestVersionSeen = doc?.HighestVersionSeen ?? 0,
                AppliedVersion = doc?.AppliedVersion ?? 0,
                NeedsUpload = doc?.NeedsUpload ?? false,
            };
        }, cancellationToken);

    public Task SetKindAsync(SyncKindState state, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var seen = _kinds.FindById(state.Kind)?.HighestVersionSeen ?? 0;
            _kinds.Upsert(new KindDoc
            {
                Kind = state.Kind,
                HighestVersionSeen = Math.Max(seen, state.HighestVersionSeen),
                AppliedVersion = state.AppliedVersion,
                NeedsUpload = state.NeedsUpload,
            });
        }, cancellationToken);

    public Task<List<SyncItemState>> GetItemsAsync(string kind, CancellationToken cancellationToken = default) =>
        Task.Run(() => _items.Find(i => i.Kind == kind)
            .Select(i => new SyncItemState
            {
                ItemId = i.ItemId,
                Revision = i.Revision,
                DeviceId = i.DeviceId ?? string.Empty, // LiteDB stores empty strings as null
                Hash = i.Hash ?? string.Empty,
                Deleted = i.Deleted,
                ChangedUtc = i.ChangedUtc.ToUniversalTime(), // LiteDB reads dates back as local time
            })
            .ToList(), cancellationToken);

    public Task SaveItemsAsync(string kind, IEnumerable<SyncItemState> items, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _items.Upsert(items.Select(i => new ItemDoc
            {
                Id = ItemKey(kind, i.ItemId),
                Kind = kind,
                ItemId = i.ItemId,
                Revision = i.Revision,
                DeviceId = i.DeviceId,
                Hash = i.Hash,
                Deleted = i.Deleted,
                ChangedUtc = i.ChangedUtc,
            }));
        }, cancellationToken);

    public Task<int> PurgeDeletedAsync(DateTime beforeUtc, CancellationToken cancellationToken = default) =>
        Task.Run(() => _items.DeleteMany(i => i.Deleted && i.ChangedUtc < beforeUtc), cancellationToken);

    public Task<List<SyncHeldAccount>> GetHeldAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _held.FindAll()
            .Select(h => new SyncHeldAccount { Email = h.Email, State = h.State, Data = h.Data })
            .ToList(), cancellationToken);

    public Task SetHeldAccountAsync(SyncHeldAccount account, CancellationToken cancellationToken = default) =>
        Task.Run(() => _held.Upsert(new HeldDoc { Email = Normalize(account.Email), State = account.State, Data = account.Data }), cancellationToken);

    public Task RemoveHeldAccountAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        Task.Run(() => _held.Delete(Normalize(emailAddress)), cancellationToken);

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() => _meta.FindById(key)?.Value, cancellationToken);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
        Task.Run(() => _meta.Upsert(new MetaDoc { Key = key, Value = value }), cancellationToken);

    public Task<List<SyncPendingTag>> GetPendingTagsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _pendingTags.FindAll()
            .Select(t => new SyncPendingTag { AccountKey = t.AccountKey, MessageId = t.MessageId, TagId = t.TagId })
            .ToList(), cancellationToken);

    public Task SetPendingTagAsync(SyncPendingTag tag, CancellationToken cancellationToken = default) =>
        Task.Run(() => _pendingTags.Upsert(new PendingTagDoc
        {
            Id = PendingKey(tag.AccountKey, tag.MessageId),
            AccountKey = tag.AccountKey,
            MessageId = tag.MessageId,
            TagId = tag.TagId,
        }), cancellationToken);

    public Task RemovePendingTagAsync(string accountKey, string messageId, CancellationToken cancellationToken = default) =>
        Task.Run(() => _pendingTags.Delete(PendingKey(accountKey, messageId)), cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _meta.DeleteAll();
            _kinds.DeleteAll();
            _items.DeleteAll();
            _held.DeleteAll();
            _pendingTags.DeleteAll();
        }, cancellationToken);

    private sealed class MetaDoc
    {
        [BsonId]
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class KindDoc
    {
        [BsonId]
        public string Kind { get; set; } = string.Empty;
        public long HighestVersionSeen { get; set; }
        public long AppliedVersion { get; set; }
        public bool NeedsUpload { get; set; }
    }

    private sealed class ItemDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public bool Deleted { get; set; }
        public DateTime ChangedUtc { get; set; }
    }

    private sealed class HeldDoc
    {
        [BsonId]
        public string Email { get; set; } = string.Empty;
        public SyncHeldState State { get; set; }
        public string? Data { get; set; }
    }

    private sealed class PendingTagDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string TagId { get; set; } = string.Empty;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CORA.Data.Email;

namespace CORA.Data.Sync;

/// <summary>
/// Merges this device with goCORA's cloud (claude/design-cloud-backup-sync.md). For each kind:
/// find what changed here by hash, fetch the cloud snapshot when needed, merge item by item (the
/// highest revision wins, then the device id; clocks aren't trusted), apply the cloud's winners
/// here, and save a new sealed version when this device has something the cloud doesn't.
/// </summary>
public sealed class SyncEngine
{
    /// <summary>Deletion markers are kept this long, so a long-unused device can't bring items back.</summary>
    public static readonly TimeSpan DeletionMarkerAge = TimeSpan.FromDays(365);

    private const int MassDeleteMinimum = 20;

    private readonly ISyncStateStore _state;
    private readonly ISyncCloud _cloud;
    private readonly ISyncSealer _sealer;
    private readonly string _deviceId;
    private readonly Func<DateTime> _utcNow;

    public SyncEngine(ISyncStateStore state, ISyncCloud cloud, ISyncSealer sealer, string deviceId, Func<DateTime>? utcNow = null)
    {
        _state = state;
        _cloud = cloud;
        _sealer = sealer;
        _deviceId = deviceId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private sealed class Item
    {
        public string Id { get; set; } = string.Empty;
        public long Rev { get; set; }
        public string Dev { get; set; } = string.Empty;
        public bool Del { get; set; }
        public DateTime At { get; set; }
        public string? Data { get; set; }
    }

    private sealed class Snapshot
    {
        public int V { get; set; } = 1;
        public List<Item> Items { get; set; } = [];
    }

    /// <summary>
    /// Runs every kind. <paramref name="allowMassDelete"/> names kinds the user has confirmed.
    /// <paramref name="freshStart"/>: sync was just turned on, so a deleted cloud copy is simply
    /// started again. Throws <see cref="SyncOwnerMismatchException"/> and
    /// <see cref="SyncCloudCopyDeletedException"/> before anything is touched.
    /// </summary>
    public async Task<IReadOnlyList<SyncKindResult>> RunAsync(
        IEnumerable<ISyncAdapter> adapters, IReadOnlySet<string>? allowMassDelete = null, bool freshStart = false, CancellationToken cancellationToken = default)
    {
        var owner = await _state.GetOwnerAsync(cancellationToken).ConfigureAwait(false);
        if (owner is not null && owner != _sealer.Group)
            throw new SyncOwnerMismatchException();
        if (owner is null)
            await _state.SetOwnerAsync(_sealer.Group, cancellationToken).ConfigureAwait(false);

        await _state.PurgeDeletedAsync(_utcNow() - DeletionMarkerAge, cancellationToken).ConfigureAwait(false);
        var status = await _cloud.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var kinds = adapters.ToList();
        if (!freshStart)
        {
            // Without this, a device that still has the data would upload it straight back.
            foreach (var adapter in kinds)
            {
                if (status.TryGetValue(adapter.Kind, out var c) && c.Parts == 0
                    && (await _state.GetKindAsync(adapter.Kind, cancellationToken).ConfigureAwait(false)).AppliedVersion > 0)
                {
                    throw new SyncCloudCopyDeletedException();
                }
            }
        }

        var results = new List<SyncKindResult>();
        foreach (var adapter in kinds)
        {
            status.TryGetValue(adapter.Kind, out var cloud);
            results.Add(await RunKindAsync(adapter, cloud, allowMassDelete?.Contains(adapter.Kind) == true, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// Undo: makes this device match a daily copy. The next run sees the difference as this device's
    /// own edits, with revisions above the cloud's, and takes it to every device.
    /// </summary>
    public async Task ApplyCopyAsync(ISyncAdapter adapter, long version, CancellationToken cancellationToken = default)
    {
        var copy = await _cloud.GetDailyCopyAsync(adapter.Kind, version, cancellationToken).ConfigureAwait(false);
        var blob = await _cloud.DownloadAsync(adapter.Kind, copy, cancellationToken).ConfigureAwait(false);
        var plain = await _sealer.OpenAsync(blob, adapter.Kind, version, cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(plain) ?? throw new InvalidDataException("An empty sync snapshot.");
            var wanted = snapshot.Items.Where(i => !i.Del && i.Data is not null).ToDictionary(i => i.Id, i => i.Data!);
            var local = await adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
            await adapter.ApplyAsync(
                wanted.Where(w => !local.TryGetValue(w.Key, out var d) || d != w.Value).ToDictionary(w => w.Key, w => w.Value),
                local.Keys.Where(id => !wanted.ContainsKey(id)).ToList(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static string Hash(string data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    private static bool Wins(Item a, Item b) =>
        a.Rev != b.Rev ? a.Rev > b.Rev : string.CompareOrdinal(a.Dev, b.Dev) > 0;

    private async Task<SyncKindResult> RunKindAsync(ISyncAdapter adapter, SyncCloudVersion? cloud, bool allowMassDelete, CancellationToken cancellationToken)
    {
        var kind = adapter.Kind;
        var position = await _state.GetKindAsync(kind, cancellationToken).ConfigureAwait(false);
        if (cloud is not null && cloud.Version < position.HighestVersionSeen)
            return new SyncKindResult(kind, SyncKindOutcome.Rollback);

        var cloudVersion = cloud?.Version ?? 0;
        var hasCloudCopy = cloud is { Parts: > 0 };
        var known = (await _state.GetItemsAsync(kind, cancellationToken).ConfigureAwait(false)).ToDictionary(i => i.ItemId);

        // The first sync here has to see the cloud before it reads, to line identities up.
        var cloudItems = new Dictionary<string, Item>();
        var fetched = false;
        var unreadable = false;
        async Task FetchAsync()
        {
            if (fetched || !hasCloudCopy)
                return;
            fetched = true;
            var blob = await _cloud.DownloadAsync(kind, cloud!, cancellationToken).ConfigureAwait(false);
            byte[] plain;
            try
            {
                plain = await _sealer.OpenAsync(blob, kind, cloud!.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (SyncSnapshotUnreadableException)
            {
                unreadable = true; // from before a Reset: this device's copy replaces it
                return;
            }
            try
            {
                var snapshot = JsonSerializer.Deserialize<Snapshot>(plain) ?? throw new InvalidDataException("An empty sync snapshot.");
                foreach (var item in snapshot.Items)
                    cloudItems[item.Id] = item;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        if (known.Count == 0 && position.AppliedVersion == 0)
        {
            await FetchAsync().ConfigureAwait(false);
            await adapter.AlignAsync(
                cloudItems.Values.Where(i => !i.Del && i.Data is not null).ToDictionary(i => i.Id, i => i.Data!),
                cancellationToken).ConfigureAwait(false);
        }

        var local = await adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = _utcNow();

        // What this device has, with its own edits and deletions as new revisions.
        var mine = new Dictionary<string, Item>();
        foreach (var (id, data) in local)
        {
            var edited = !known.TryGetValue(id, out var k) || k.Deleted || k.Hash != Hash(data);
            mine[id] = edited
                ? new Item { Id = id, Rev = (k?.Revision ?? 0) + 1, Dev = _deviceId, At = now, Data = data }
                : new Item { Id = id, Rev = k!.Revision, Dev = k.DeviceId, At = k.ChangedUtc, Data = data };
        }
        foreach (var k in known.Values.Where(k => !local.ContainsKey(k.ItemId)))
        {
            mine[k.ItemId] = k.Deleted
                ? new Item { Id = k.ItemId, Rev = k.Revision, Dev = k.DeviceId, At = k.ChangedUtc, Del = true }
                : new Item { Id = k.ItemId, Rev = k.Revision + 1, Dev = _deviceId, At = now, Del = true };
        }

        var editedHere = mine.Values.Any(m => !known.TryGetValue(m.Id, out var k) || k.Revision != m.Rev || k.Deleted != m.Del);
        var sealedOlder = hasCloudCopy && cloud!.Generation != _sealer.Generation;
        if (hasCloudCopy && (editedHere || sealedOlder || position.NeedsUpload || cloudVersion != position.AppliedVersion))
            await FetchAsync().ConfigureAwait(false);

        // Merge. Anything only in the cloud, or newer there, comes here.
        var merged = new Dictionary<string, Item>(mine);
        var upserts = new Dictionary<string, string>();
        var deletes = new List<string>();
        foreach (var c in cloudItems.Values)
        {
            if (mine.TryGetValue(c.Id, out var m))
            {
                // The same content is no conflict (a first sync): take the cloud's revision.
                var same = !c.Del && !m.Del && c.Data == m.Data;
                if (!same && !Wins(c, m))
                    continue;
            }
            merged[c.Id] = c;
            if (c.Del)
            {
                if (local.ContainsKey(c.Id))
                    deletes.Add(c.Id);
            }
            else if (c.Data is not null && (!local.TryGetValue(c.Id, out var data) || data != c.Data))
            {
                upserts[c.Id] = c.Data;
            }
        }

        if (!allowMassDelete && deletes.Count >= MassDeleteMinimum && deletes.Count * 2 > local.Count)
            return new SyncKindResult(kind, SyncKindOutcome.NeedsConfirmation, deletes.Count);

        if (upserts.Count > 0 || deletes.Count > 0)
            await adapter.ApplyAsync(upserts, deletes, cancellationToken).ConfigureAwait(false);

        // Hash what is really here now: that is what the next run compares with.
        var applied = upserts.Count > 0 || deletes.Count > 0 ? await adapter.ReadAsync(cancellationToken).ConfigureAwait(false) : local;
        await _state.SaveItemsAsync(kind, merged.Values.Select(i => new SyncItemState
        {
            ItemId = i.Id,
            Revision = i.Rev,
            DeviceId = i.Dev,
            Deleted = i.Del,
            ChangedUtc = i.At,
            Hash = !i.Del && applied.TryGetValue(i.Id, out var d) ? Hash(d) : string.Empty,
        }).ToList(), cancellationToken).ConfigureAwait(false);

        var seen = Math.Max(position.HighestVersionSeen, cloudVersion);
        var read = fetched && !unreadable ? cloudVersion : position.AppliedVersion;

        // Save when the cloud lacks something: a change here, an item it never had, or an old seal.
        var cloudBehind = !hasCloudCopy || unreadable || sealedOlder
            || merged.Values.Any(i => !cloudItems.TryGetValue(i.Id, out var c) || c.Rev != i.Rev || c.Dev != i.Dev || c.Del != i.Del);
        if (fetched || !hasCloudCopy)
        {
            if (merged.Count == 0 && !hasCloudCopy)
                cloudBehind = false; // nothing here and nothing there
        }
        else
        {
            cloudBehind = sealedOlder; // not fetched: nothing changed on either side
        }

        var outcome = SyncKindOutcome.Done;
        var needsUpload = false;
        if (cloudBehind)
        {
            // Recorded first: this run's edits now look unchanged, so a failed upload must not
            // leave them stranded on this device.
            await _state.SetKindAsync(new SyncKindState { Kind = kind, HighestVersionSeen = seen, AppliedVersion = read, NeedsUpload = true }, cancellationToken).ConfigureAwait(false);
            var next = cloudVersion + 1;
            var snapshot = JsonSerializer.SerializeToUtf8Bytes(new Snapshot { Items = merged.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToList() });
            try
            {
                var blob = _sealer.Seal(snapshot, kind, next);
                await _cloud.UploadAsync(kind, next, _sealer.Generation, blob, cancellationToken).ConfigureAwait(false);
                seen = next;
                read = next;
            }
            catch (SyncVersionOutdatedException)
            {
                outcome = SyncKindOutcome.Retry; // the revisions are saved: the next run merges and saves
                needsUpload = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
        }

        await _state.SetKindAsync(new SyncKindState { Kind = kind, HighestVersionSeen = seen, AppliedVersion = read, NeedsUpload = needsUpload }, cancellationToken).ConfigureAwait(false);
        return new SyncKindResult(kind, outcome);
    }
}

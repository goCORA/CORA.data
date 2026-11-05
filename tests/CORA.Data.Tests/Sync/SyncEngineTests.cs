using System.Text;
using CORA.Data.Sync;
using CORA.Data.Email;

namespace CORA.Data.Tests.Sync;

/// <summary>The merge itself: devices meeting through a fake cloud, with plain items.</summary>
public class SyncEngineTests
{
    private const string Group = "uid1";

    private sealed class FakeCloud : ISyncCloud, ISyncSealer
    {
        public Dictionary<string, (long Version, long Gen, byte[] Blob)> Stored { get; } = [];
        public int Uploads { get; private set; }
        public int Downloads { get; private set; }
        public bool LoseNextUpload { get; set; }
        public bool FailNextUpload { get; set; }
        public long Generation { get; set; } = 1;
        public bool ThrowUnreadable { get; set; }
        public string Group => SyncEngineTests.Group;

        public Task<IReadOnlyDictionary<string, SyncCloudVersion>> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, SyncCloudVersion>>(
                Stored.Where(s => !s.Key.StartsWith("daily:", StringComparison.Ordinal)).ToDictionary(s => s.Key, s => new SyncCloudVersion(s.Value.Version, s.Value.Gen, s.Value.Blob.Length == 0 ? 0 : 1, "u")));

        public Task<byte[]> DownloadAsync(string kind, SyncCloudVersion version, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(Daily.TryGetValue((kind, version.Version), out var copy) ? copy : Stored[kind].Blob);
        }

        public Task UploadAsync(string kind, long version, long generation, byte[] blob, CancellationToken cancellationToken)
        {
            if (FailNextUpload)
            {
                FailNextUpload = false;
                throw new HttpRequestException("offline");
            }
            if (LoseNextUpload)
            {
                LoseNextUpload = false;
                throw new SyncVersionOutdatedException();
            }
            Uploads++;
            Stored[kind] = (version, generation, blob);
            return Task.CompletedTask;
        }

        public Dictionary<(string Kind, long Version), byte[]> Daily { get; } = [];

        public Task<SyncCloudVersion> GetDailyCopyAsync(string kind, long version, CancellationToken cancellationToken)
        {
            Stored["daily:" + kind] = (version, Generation, Daily[(kind, version)]);
            return Task.FromResult(new SyncCloudVersion(version, Generation, 1, "u"));
        }

        public byte[] Seal(byte[] snapshot, string kind, long version) => (byte[])snapshot.Clone();

        public Task<byte[]> OpenAsync(byte[] blob, string kind, long version, CancellationToken cancellationToken) =>
            ThrowUnreadable ? throw new SyncSnapshotUnreadableException() : Task.FromResult((byte[])blob.Clone());
    }

    private sealed class MemoryAdapter(string kind = "contacts") : ISyncAdapter
    {
        public Dictionary<string, string> Items { get; } = [];
        public Dictionary<string, string> Rename { get; } = [];
        public string Kind => kind;
        public HashSet<string> Elsewhere { get; } = [];
        public Task<IReadOnlySet<string>> GetElsewhereAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(Elsewhere);
        public Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, string>(Items));

        public Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken)
        {
            foreach (var (from, to) in Rename)
            {
                if (cloudItems.ContainsKey(to) && Items.Remove(from, out var data))
                    Items[to] = data;
            }
            return Task.CompletedTask;
        }

        public Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
        {
            foreach (var (id, data) in upserts)
                Items[id] = data;
            foreach (var id in deletes)
                Items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryState : ISyncStateStore
    {
        private string? _owner;
        private readonly Dictionary<string, SyncKindState> _kinds = [];
        private readonly Dictionary<string, SyncItemState> _items = [];
        private readonly Dictionary<string, string> _values = [];
        public List<SyncHistoryEntry> History { get; } = [];

        public Task AddHistoryAsync(IEnumerable<SyncHistoryEntry> entries, CancellationToken cancellationToken = default) { History.AddRange(entries); return Task.CompletedTask; }
        public Task<List<SyncHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(History.ToList());
        public Task RemoveHistoryAsync(DateTime? beforeUtc, CancellationToken cancellationToken = default) { History.Clear(); return Task.CompletedTask; }
        public Task<string?> GetOwnerAsync(CancellationToken cancellationToken = default) => Task.FromResult(_owner);
        public Task SetOwnerAsync(string ownerUid, CancellationToken cancellationToken = default) { _owner = ownerUid; return Task.CompletedTask; }
        public Task<SyncKindState> GetKindAsync(string kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(_kinds.GetValueOrDefault(kind) ?? new SyncKindState { Kind = kind });
        public Task SetKindAsync(SyncKindState state, CancellationToken cancellationToken = default) { _kinds[state.Kind] = state; return Task.CompletedTask; }
        public Task<List<SyncItemState>> GetItemsAsync(string kind, CancellationToken cancellationToken = default) => Task.FromResult(_items.Values.ToList());
        public Task SaveItemsAsync(string kind, IEnumerable<SyncItemState> items, CancellationToken cancellationToken = default)
        {
            foreach (var i in items)
                _items[i.ItemId] = i;
            return Task.CompletedTask;
        }
        public Task<int> PurgeDeletedAsync(DateTime beforeUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<List<SyncHeldAccount>> GetHeldAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<SyncHeldAccount>());
        public Task SetHeldAccountAsync(SyncHeldAccount account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveHeldAccountAsync(string emailAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
        public Task<List<SyncPendingTag>> GetPendingTagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<SyncPendingTag>());
        public Task SetPendingTagAsync(SyncPendingTag tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemovePendingTagAsync(string accountKey, string messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetOwnerForTest(string owner) => _owner = owner;
    }

    private sealed class Device(FakeCloud cloud, string id)
    {
        public MemoryAdapter Data { get; } = new();
        public MemoryState State { get; } = new();

        public async Task<SyncKindResult> SyncAsync(IReadOnlySet<string>? allow = null, bool fresh = false) =>
            (await new SyncEngine(State, cloud, cloud, id).RunAsync([Data], allow, fresh)).Single();

        public Task ApplyCopyAsync(long version) => new SyncEngine(State, cloud, cloud, id).ApplyCopyAsync(Data, version);
    }

    [Fact]
    public async Task ANewDevice_ReceivesEverything_AndSavesNothingBack()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        pc.Data.Items["b"] = "Ben";
        await pc.SyncAsync();
        Assert.Equal(1, cloud.Uploads);

        var phone = new Device(cloud, "phone");
        await phone.SyncAsync();
        Assert.Equal(pc.Data.Items, phone.Data.Items);
        Assert.Equal(1, cloud.Uploads);
    }

    [Fact]
    public async Task History_ListsWhatWentOutAndCameIn_CountedAndNeverTheContent()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        pc.Data.Items["b"] = "Ben";
        await pc.SyncAsync();
        await phone.SyncAsync();
        pc.State.History.Clear();
        phone.State.History.Clear();

        pc.Data.Items["a"] = "Anne";
        pc.Data.Items.Remove("b");
        await pc.SyncAsync();
        await phone.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal(2, pc.State.History.Count);
        Assert.All(pc.State.History, h => Assert.False(h.Incoming));
        Assert.Contains(pc.State.History, h => h.Item == "contacts" && !h.Removed && h.Count == 1);
        Assert.Contains(pc.State.History, h => h.Item == "contacts" && h.Removed && h.Count == 1);
        Assert.Equal(2, phone.State.History.Count);
        Assert.All(phone.State.History, h => Assert.True(h.Incoming));
    }

    [Fact]
    public async Task History_FirstSyncOfTheSameItems_ListsNothing()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        phone.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await phone.SyncAsync();
        Assert.Empty(phone.State.History);
    }

    [Fact]
    public async Task History_ListsAnEditOnlyOnceItHasLeft_EvenWhenTheUploadFailsFirst()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        cloud.LoseNextUpload = true;
        await pc.SyncAsync();
        Assert.Empty(pc.State.History);

        await pc.SyncAsync();
        var line = Assert.Single(pc.State.History);
        Assert.False(line.Incoming);
        Assert.Equal(1, line.Count);
        await pc.SyncAsync();
        Assert.Single(pc.State.History);
    }

    [Fact]
    public async Task NothingChanged_DownloadsAndUploadsNothing()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await pc.SyncAsync();
        Assert.Equal(1, cloud.Uploads);
        Assert.Equal(0, cloud.Downloads);
    }

    [Fact]
    public async Task Edits_AddsAndDeletes_ReachTheOtherDevice()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        pc.Data.Items["b"] = "Ben";
        await pc.SyncAsync();
        await phone.SyncAsync();

        pc.Data.Items["a"] = "Anne";
        pc.Data.Items.Remove("b");
        pc.Data.Items["c"] = "Cora";
        await pc.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal(new Dictionary<string, string> { ["a"] = "Anne", ["c"] = "Cora" }, phone.Data.Items);
        var uploads = cloud.Uploads;
        await phone.SyncAsync();
        await pc.SyncAsync();
        Assert.Equal(uploads, cloud.Uploads);
    }

    [Fact]
    public async Task DifferentItems_ChangedOnBothDevices_BothSurvive()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await phone.SyncAsync();

        pc.Data.Items["p"] = "from the PC";
        phone.Data.Items["q"] = "from the phone";
        await pc.SyncAsync();
        await phone.SyncAsync();
        await pc.SyncAsync();

        Assert.Equal(3, pc.Data.Items.Count);
        Assert.Equal(pc.Data.Items, phone.Data.Items);
    }

    [Fact]
    public async Task TheSameItem_ChangedOnBothDevices_EndsTheSameOnBoth()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await phone.SyncAsync();

        pc.Data.Items["a"] = "PC's edit";
        phone.Data.Items["a"] = "phone's edit";
        await pc.SyncAsync();
        await phone.SyncAsync();
        await pc.SyncAsync();

        Assert.Equal(pc.Data.Items["a"], phone.Data.Items["a"]);
    }

    [Fact]
    public async Task ADeletedItem_DoesNotComeBackFromADeviceThatWasAway()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await phone.SyncAsync();

        pc.Data.Items.Remove("a");
        await pc.SyncAsync();
        await phone.SyncAsync();
        Assert.Empty(phone.Data.Items);
        await pc.SyncAsync();
        Assert.Empty(pc.Data.Items);
    }

    [Fact]
    public async Task AnotherDeviceWinningTheVersion_LosesNothing()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        await phone.SyncAsync();

        phone.Data.Items["q"] = "from the phone";
        cloud.LoseNextUpload = true;
        Assert.Equal(SyncKindOutcome.Retry, (await phone.SyncAsync()).Outcome);

        pc.Data.Items["p"] = "from the PC";
        await pc.SyncAsync();
        await phone.SyncAsync();
        await pc.SyncAsync();
        Assert.Equal(3, phone.Data.Items.Count);
        Assert.Equal(phone.Data.Items, pc.Data.Items);
    }

    [Fact]
    public async Task AFailedUpload_IsTriedAgainAtTheNextRun()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();

        pc.Data.Items["a"] = "Anne";
        cloud.FailNextUpload = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => pc.SyncAsync());
        Assert.Equal(1, cloud.Stored["contacts"].Version);

        await pc.SyncAsync();
        Assert.Equal(2, cloud.Stored["contacts"].Version);
        var phone = new Device(cloud, "phone");
        await phone.SyncAsync();
        Assert.Equal("Anne", phone.Data.Items["a"]);
    }

    [Fact]
    public async Task AFirstSync_LinesUpItemsAlreadyOnBothDevices()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["cloud-id"] = "Anna";
        await pc.SyncAsync();

        phone.Data.Items["phone-id"] = "Anna";
        phone.Data.Rename["phone-id"] = "cloud-id";
        await phone.SyncAsync();

        Assert.Equal(new Dictionary<string, string> { ["cloud-id"] = "Anna" }, phone.Data.Items);
        Assert.Equal(1, cloud.Uploads);
    }

    [Fact]
    public async Task AnItemHeldAwayFromThisDevice_IsNotADeletion_AndSurvivesItsUploads()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        pc.Data.Items["secret"] = "held";
        await pc.SyncAsync();
        await phone.SyncAsync();

        phone.Data.Items.Remove("secret");
        phone.Data.Elsewhere.Add("secret");
        phone.Data.Items["b"] = "Ben";
        await phone.SyncAsync();
        await pc.SyncAsync();

        Assert.Equal("held", pc.Data.Items["secret"]);
        Assert.Equal("Ben", pc.Data.Items["b"]);
    }

    [Fact]
    public async Task AMassDelete_WaitsForTheUser()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        for (var i = 0; i < 25; i++)
            pc.Data.Items[$"c{i}"] = "x";
        await pc.SyncAsync();
        await phone.SyncAsync();

        pc.Data.Items.Clear();
        await pc.SyncAsync();

        var held = await phone.SyncAsync();
        Assert.Equal(SyncKindOutcome.NeedsConfirmation, held.Outcome);
        Assert.Equal(25, held.Deletions);
        Assert.Equal(25, phone.Data.Items.Count);

        await phone.SyncAsync(new HashSet<string> { "contacts" });
        Assert.Empty(phone.Data.Items);
    }

    [Fact]
    public async Task AnOlderVersionFromTheCloud_IsRefused()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        var old = cloud.Stored["contacts"];
        pc.Data.Items["a"] = "Anne";
        await pc.SyncAsync();

        cloud.Stored["contacts"] = old;
        Assert.Equal(SyncKindOutcome.Rollback, (await pc.SyncAsync()).Outcome);
        Assert.Equal("Anne", pc.Data.Items["a"]);
    }

    [Fact]
    public async Task AnotherAccountsState_StopsBeforeAnythingIsTouched()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.State.SetOwnerForTest("someone-else");
        pc.Data.Items["a"] = "Anna";
        await Assert.ThrowsAsync<SyncOwnerMismatchException>(() => pc.SyncAsync());
        Assert.Equal(0, cloud.Uploads);
    }

    [Fact]
    public async Task AfterAKeyCodeChange_TheNextDeviceReseals()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        cloud.Generation = 2;
        await pc.SyncAsync();
        Assert.Equal(2, cloud.Uploads);
        Assert.Equal(2, cloud.Stored["contacts"].Gen);
    }

    [Fact]
    public async Task AfterAReset_ThisDevicesCopyReplacesTheUnreadableOne()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        cloud.Generation = 2;
        cloud.ThrowUnreadable = true;
        await pc.SyncAsync();
        Assert.Equal("Anna", pc.Data.Items["a"]);
        Assert.Equal(2, cloud.Stored["contacts"].Version);
    }

    [Fact]
    public async Task AfterTheCloudCopyIsDeleted_DevicesStop_AndTurningSyncBackOnStartsAgain()
    {
        var cloud = new FakeCloud();
        var pc = new Device(cloud, "pc");
        pc.Data.Items["a"] = "Anna";
        await pc.SyncAsync();
        cloud.Stored["contacts"] = (1, 1, []);

        await Assert.ThrowsAsync<SyncCloudCopyDeletedException>(() => pc.SyncAsync());
        Assert.Equal("Anna", pc.Data.Items["a"]);
        Assert.Empty(cloud.Stored["contacts"].Blob);

        await pc.SyncAsync(fresh: true);
        Assert.Equal(2, cloud.Stored["contacts"].Version);
        Assert.NotEmpty(cloud.Stored["contacts"].Blob);
        await pc.SyncAsync();
    }

    [Fact]
    public async Task ADeletedCloudCopy_DoesNotStopADeviceThatNeverSynced()
    {
        var cloud = new FakeCloud();
        cloud.Stored["contacts"] = (1, 1, []);
        var phone = new Device(cloud, "phone");
        phone.Data.Items["a"] = "Anna";
        await phone.SyncAsync();
        Assert.Equal(2, cloud.Stored["contacts"].Version);
    }

    [Fact]
    public async Task Undo_BringsADailyCopyBack_OnEveryDevice()
    {
        var cloud = new FakeCloud();
        var (pc, phone) = (new Device(cloud, "pc"), new Device(cloud, "phone"));
        pc.Data.Items["a"] = "Anna";
        pc.Data.Items["b"] = "Ben";
        await pc.SyncAsync();
        await phone.SyncAsync();
        cloud.Daily[("contacts", 1)] = cloud.Stored["contacts"].Blob;

        pc.Data.Items["a"] = "wrong";
        pc.Data.Items.Remove("b");
        pc.Data.Items["c"] = "added by mistake";
        await pc.SyncAsync();
        await phone.SyncAsync();
        Assert.Equal("wrong", phone.Data.Items["a"]);

        await phone.ApplyCopyAsync(1);
        await phone.SyncAsync();
        await pc.SyncAsync();

        var before = new Dictionary<string, string> { ["a"] = "Anna", ["b"] = "Ben" };
        Assert.Equal(before, phone.Data.Items);
        Assert.Equal(before, pc.Data.Items);
    }
}

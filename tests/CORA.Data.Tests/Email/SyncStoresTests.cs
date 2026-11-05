using CORA.Core.Security;
using CORA.Data.Contacts;
using CORA.Data.Email;
using CORA.Data.Sync;
using CORA.Data.Tests.App;
using LiteDB;

namespace CORA.Data.Tests.Email;

/// <summary>Step 3 of claude/design-cloud-backup-sync.md: identities and bookkeeping for sync.</summary>
public class SyncStoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
    private readonly KeyStorage _keys = new();
    private readonly InMemoryPreferenceStore _prefs = new();
    private IDataStores? _stores;

    public void Dispose()
    {
        _stores?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class TempAppData(string dir) : IAppDataLocation
    {
        public string AppDataDirectory { get; } = dir;
    }

    // Shared between "launches" so a reopened store can decrypt what the first one wrote.
    private sealed class KeyStorage : ISecureKeyStorage
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task History_IsNewestFirst_SurvivesAReopen_AndRemovesByAge()
    {
        var stores = await OpenAsync();
        var now = DateTime.UtcNow;
        await stores.SyncState.AddHistoryAsync(
        [
            new SyncHistoryEntry { AtUtc = now.AddDays(-100), Incoming = true, Item = "contacts", Count = 3 },
            new SyncHistoryEntry { AtUtc = now, Item = "password", Detail = "a@b.c", Count = 1 },
        ]);
        stores.Flush();

        stores = await OpenAsync();
        var history = await stores.SyncState.GetHistoryAsync();
        Assert.Equal(new[] { "password", "contacts" }, history.Select(h => h.Item));
        Assert.Equal(string.Empty, history[1].Detail);
        Assert.Equal(DateTimeKind.Utc, history[0].AtUtc.Kind);

        await stores.SyncState.RemoveHistoryAsync(now.AddDays(-90));
        Assert.Equal("password", Assert.Single(await stores.SyncState.GetHistoryAsync()).Item);
        await stores.SyncState.RemoveHistoryAsync(null);
        Assert.Empty(await stores.SyncState.GetHistoryAsync());
    }

    private async Task<IDataStores> OpenAsync()
    {
        _stores?.Dispose();
        Directory.CreateDirectory(_dir);
        _stores = await SecureDataStores.CreateAsync(new TempAppData(_dir), _keys, _prefs);
        return _stores;
    }

    private void Close()
    {
        _stores?.Dispose();
        _stores = null;
    }

    [Fact]
    public async Task Contacts_FromBeforeSync_GetAStableSyncId_AndKeepTheirData()
    {
        await OpenAsync();
        Close();
        using (var file = EncryptedLiteDbFile.Open(Path.Combine(_dir, StoreFiles.Contacts)))
        {
            file.Database.GetCollection(LegacyContactMigration.CollectionName).Insert(new BsonDocument
            {
                ["_id"] = 5,
                ["FirstName"] = "Anna",
                ["LastName"] = "Roy",
                ["Email"] = "anna@example.com",
            });
        }

        var contact = Assert.Single(await (await OpenAsync()).Contacts.GetAllAsync());
        Assert.Equal(5, contact.Id);
        Assert.Equal("Anna", contact.FirstName);
        Assert.Equal("anna@example.com", contact.Email);
        Assert.Equal(32, contact.SyncId.Length);

        var again = Assert.Single(await (await OpenAsync()).Contacts.GetAllAsync());
        Assert.Equal(contact.SyncId, again.SyncId);
    }

    [Fact]
    public async Task Contacts_KeepTheirSyncId_ThroughEditsAndUndo()
    {
        var store = (await OpenAsync()).Contacts;
        var contact = new Contact { FirstName = "Anna", Email = "anna@example.com" };
        await store.SaveAsync(contact);
        var syncId = contact.SyncId;
        Assert.Equal(32, syncId.Length);

        // A page that builds a new object for an existing contact doesn't split its identity.
        await store.SaveAsync(new Contact { Id = contact.Id, FirstName = "Annie", Email = "anna@example.com" });
        var edited = await store.GetAsync(contact.Id);
        Assert.Equal(syncId, edited!.SyncId);
        Assert.Equal("Annie", edited.FirstName);

        await store.DeleteAsync(edited);
        await store.SaveAsync(edited); // undo
        Assert.Equal(syncId, (await store.GetAsync(contact.Id))!.SyncId);

        // Sync creates a contact from another device with that device's SyncId.
        var incoming = new Contact { SyncId = "abc", FirstName = "Ben" };
        await store.SaveAsync(incoming);
        Assert.Equal("abc", (await store.GetAsync(incoming.Id))!.SyncId);
    }

    private static StoredMailSummary Mail(string uidl, uint uid, string messageId) =>
        new() { Uidl = uidl, Uid = uid, Subject = "s", Date = DateTimeOffset.UnixEpoch, MessageId = messageId };

    [Fact]
    public async Task TagAssignments_GetTheirMessageId_FromCachedMail()
    {
        var stores = await OpenAsync();
        await stores.MailSync.UpsertAsync("a@x.com", "INBOX", [Mail("1", 1, "<one@x>"), Mail("2", 2, "")]);
        await stores.MailSync.UpsertAsync("a@x.com", "Work", [Mail("1", 1, "<work@x>")]);
        await stores.MailSync.UpsertAsync("b@x.com", "INBOX", [Mail("9", 9, "<nine@x>")]);

        var identities = await stores.MailSync.GetMessageIdentitiesAsync("a@x.com");
        Assert.Equal(new[] { "<one@x>", "<work@x>" }, identities.Select(i => i.MessageId).Order());
        Assert.Contains(identities, i => i is { FolderName: "Work", Uid: 1, MessageId: "<work@x>" });

        var tag = await stores.Tags.AddTagAsync("Follow up", "#F0C419");
        await stores.Tags.SetMessageTagAsync("a@x.com", "INBOX", 1, tag.Id);
        await stores.Tags.SetMessageTagAsync("a@x.com", "Work", 1, tag.Id);
        await stores.Tags.SetMessageTagAsync("a@x.com", "INBOX", 2, tag.Id); // no Message-Id
        await stores.Tags.SetMessageTagAsync("a@x.com", "INBOX", 7, tag.Id); // not cached any more
        await stores.Tags.SetMessageTagAsync("b@x.com", "INBOX", 9, tag.Id);

        Assert.Equal(3, await TagMessageIds.BackfillAsync(stores.Tags, stores.MailSync));
        var all = await stores.Tags.GetAssignmentsAsync();
        Assert.Equal(5, all.Count);
        Assert.Equal("<one@x>", all.Single(a => a is { AccountKey: "a@x.com", FolderName: "INBOX", Uid: 1 }).MessageId);
        Assert.Equal("<work@x>", all.Single(a => a.FolderName == "Work").MessageId);
        Assert.Equal("<nine@x>", all.Single(a => a.AccountKey == "b@x.com").MessageId);
        Assert.Equal(2, all.Count(a => a.MessageId.Length == 0));
        Assert.Equal(0, await TagMessageIds.BackfillAsync(stores.Tags, stores.MailSync));

        // A new tag on the same email keeps what is known about it.
        var other = await stores.Tags.AddTagAsync("Personal", "#000000");
        await stores.Tags.SetMessageTagAsync("a@x.com", "INBOX", 1, other.Id);
        var changed = (await stores.Tags.GetAssignmentsAsync()).Single(a => a is { FolderName: "INBOX", Uid: 1 });
        Assert.Equal(other.Id, changed.TagId);
        Assert.Equal("<one@x>", changed.MessageId);

        // The lookups the mailbox uses are unchanged.
        Assert.Equal(other.Id, (await stores.Tags.GetMessageTagAsync("a@x.com", "INBOX", 1))!.Id);
        Assert.Equal(3, (await stores.Tags.GetMessageTagIdsAsync("a@x.com", "INBOX")).Count);
    }

    [Fact]
    public async Task MailGoneFromTheServer_StaysInItsFolder_AndKeepsItsBody()
    {
        var mail = (await OpenAsync()).MailSync;
        await mail.UpsertAsync("a@x.com", "INBOX", [Mail("1", 1, "<one@x>"), Mail("2", 2, "<two@x>")]);
        await mail.UpsertBodyAsync("a@x.com", "INBOX", "1", new StoredMailBody { TextBody = "hello" });

        var gone = MailKitEmailService.FindStaleUidls(["1", "2"], new HashSet<string> { "2" }, new HashSet<string>());
        Assert.Equal(new[] { "1" }, gone);
        await mail.MarkLocalOnlyAsync("a@x.com", "INBOX", gone);
        await mail.MarkLocalOnlyAsync("a@x.com", "INBOX", ["missing"]);

        var rows = await mail.GetCachedSummariesAsync("a@x.com", "INBOX");
        Assert.Equal(2, rows.Count);
        Assert.True(rows.Single(r => r.Uidl == "1").IsLocalOnly);
        Assert.False(rows.Single(r => r.Uidl == "2").IsLocalOnly);
        Assert.Empty(await mail.GetCachedSummariesAsync("a@x.com", "Trash"));
        Assert.Equal("hello", (await mail.GetBodyAsync("a@x.com", "INBOX", "1"))!.TextBody);

        // The next sync leaves it alone.
        var localOnly = rows.Where(r => r.IsLocalOnly).Select(r => r.Uidl).ToHashSet();
        Assert.Empty(MailKitEmailService.FindStaleUidls(["1", "2"], new HashSet<string> { "2" }, localOnly));
    }

    [Fact]
    public async Task FirstSync_MatchesContacts_ByEmail_OrByNameAndPhone_WhenNeitherHasAnEmail()
    {
        var store = (await OpenAsync()).Contacts;
        var adapter = new ContactsSyncAdapter(store);
        var byEmail = new Contact { FirstName = "Anna", Email = "Anna@Example.com" };
        var byPhone = new Contact { FirstName = "Ben", LastName = "Roy", Phone = "(613) 555-0100" };
        var otherPhone = new Contact { FirstName = "Ben", LastName = "Roy", Phone = "613-555-0199" };
        var noName = new Contact { Phone = "613-555-0100" };
        foreach (var c in new[] { byEmail, byPhone, otherPhone, noName })
            await store.SaveAsync(c);

        await adapter.AlignAsync(new Dictionary<string, string>
        {
            ["cloud-email"] = """{"First":"Ann","Last":"","Email":"anna@example.com","Phone":"","Photo":"","IsGoCORA":false,"Tags":""}""",
            ["cloud-phone"] = """{"First":"ben","Last":"ROY","Email":"","Phone":"613 555 0100","Photo":"","IsGoCORA":false,"Tags":""}""",
            ["cloud-noname"] = """{"First":"","Last":"","Email":"","Phone":"6135550100","Photo":"","IsGoCORA":false,"Tags":""}""",
        }, CancellationToken.None);

        Assert.Equal("cloud-email", (await store.GetAsync(byEmail.Id))!.SyncId);
        Assert.Equal("cloud-phone", (await store.GetAsync(byPhone.Id))!.SyncId);
        Assert.Equal(otherPhone.SyncId, (await store.GetAsync(otherPhone.Id))!.SyncId);
        Assert.Equal(noName.SyncId, (await store.GetAsync(noName.Id))!.SyncId);
    }

    [Fact]
    public async Task ChangesThatSyncCarries_RaiseAnEvent_ButFillingInMessageIdsDoesNot()
    {
        var stores = await OpenAsync();
        var raised = 0;
        stores.SyncableDataChanged += () => Interlocked.Increment(ref raised);

        await stores.Contacts.SaveAsync(new Contact { FirstName = "Anna" });
        Assert.Equal(1, raised);
        var tag = await stores.Tags.AddTagAsync("Work", "#000000");
        Assert.Equal(2, raised);
        await stores.Tags.SetMessageTagAsync("a@x.com", "INBOX", 1, tag.Id);
        Assert.Equal(3, raised);
        await stores.Blacklist.BlacklistSenderAsync("spam@x.com");
        await stores.Blacklist.RemoveSenderAsync("spam@x.com");
        Assert.Equal(5, raised);
        await stores.TrustedImageSenders.SetTrustedAsync("a@x.com", true);
        Assert.Equal(6, raised);

        await stores.Tags.SetAssignmentMessageIdAsync("a@x.com", "INBOX", 1, "<m@x>");
        Assert.Equal(6, raised);
    }

    [Fact]
    public async Task SyncState_SurvivesARestart()
    {
        var state = (await OpenAsync()).SyncState;
        Assert.Null(await state.GetOwnerAsync());
        Assert.Equal(0, (await state.GetKindAsync("tags")).HighestVersionSeen);

        await state.SetOwnerAsync("uid1");
        await state.SetKindAsync(new SyncKindState { Kind = "contacts", HighestVersionSeen = 7, AppliedVersion = 6 });
        var changed = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        await state.SaveItemsAsync("contacts", [new SyncItemState { ItemId = "a", Revision = 3, DeviceId = "d1", Hash = "h", ChangedUtc = changed }]);
        await state.SetHeldAccountAsync(new SyncHeldAccount { Email = " Work@X.com ", State = SyncHeldState.Skipped, Data = "{}" });
        await state.SetValueAsync("passwords", "off");
        await state.SetPendingTagAsync(new SyncPendingTag { AccountKey = "a@x.com", MessageId = "<m>", TagId = "t1" });

        Close();
        state = (await OpenAsync()).SyncState;
        Assert.Equal("uid1", await state.GetOwnerAsync());
        Assert.Equal(7, (await state.GetKindAsync("contacts")).HighestVersionSeen);
        Assert.Equal(6, (await state.GetKindAsync("contacts")).AppliedVersion);
        var item = Assert.Single(await state.GetItemsAsync("contacts"));
        Assert.Equal(("a", 3L, "d1", "h", false), (item.ItemId, item.Revision, item.DeviceId, item.Hash, item.Deleted));
        Assert.Equal(changed, item.ChangedUtc);
        Assert.Empty(await state.GetItemsAsync("tags"));
        var held = Assert.Single(await state.GetHeldAccountsAsync());
        Assert.Equal(("work@x.com", SyncHeldState.Skipped, "{}"), (held.Email, held.State, held.Data));
        Assert.Equal("off", await state.GetValueAsync("passwords"));
        Assert.Equal("t1", Assert.Single(await state.GetPendingTagsAsync()).TagId);
    }

    [Fact]
    public async Task SyncState_NeverLowersTheVersionSeen()
    {
        var state = (await OpenAsync()).SyncState;
        await state.SetKindAsync(new SyncKindState { Kind = "tags", HighestVersionSeen = 40, AppliedVersion = 40 });
        await state.SetKindAsync(new SyncKindState { Kind = "tags", HighestVersionSeen = 3, AppliedVersion = 3 });
        var kind = await state.GetKindAsync("tags");
        Assert.Equal(40, kind.HighestVersionSeen);
        Assert.Equal(3, kind.AppliedVersion);
    }

    [Fact]
    public async Task SyncState_UpdatesRemovesAndPurges()
    {
        var state = (await OpenAsync()).SyncState;
        var old = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await state.SaveItemsAsync("tags",
        [
            new SyncItemState { ItemId = "kept", Revision = 1, ChangedUtc = old },
            new SyncItemState { ItemId = "old-marker", Revision = 1, Deleted = true, ChangedUtc = old },
            new SyncItemState { ItemId = "new-marker", Revision = 1, Deleted = true, ChangedUtc = recent },
        ]);
        await state.SaveItemsAsync("tags", [new SyncItemState { ItemId = "kept", Revision = 2, ChangedUtc = old }]);
        Assert.All(await state.GetItemsAsync("tags"), i => Assert.Equal((string.Empty, string.Empty), (i.DeviceId, i.Hash)));
        Assert.Equal(1, await state.PurgeDeletedAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var items = await state.GetItemsAsync("tags");
        Assert.Equal(new[] { "kept", "new-marker" }, items.Select(i => i.ItemId).Order());
        Assert.Equal(2, items.Single(i => i.ItemId == "kept").Revision);

        await state.SetPendingTagAsync(new SyncPendingTag { AccountKey = "a", MessageId = "m", TagId = "t1" });
        await state.SetPendingTagAsync(new SyncPendingTag { AccountKey = "a", MessageId = "m", TagId = "t2" });
        Assert.Equal("t2", Assert.Single(await state.GetPendingTagsAsync()).TagId);
        await state.RemovePendingTagAsync("a", "m");
        Assert.Empty(await state.GetPendingTagsAsync());

        await state.SetHeldAccountAsync(new SyncHeldAccount { Email = "a@x.com", State = SyncHeldState.WaitingRemove });
        await state.RemoveHeldAccountAsync("A@X.com");
        Assert.Empty(await state.GetHeldAccountsAsync());
    }

    [Fact]
    public async Task SyncState_ResetForgetsEverything()
    {
        var state = (await OpenAsync()).SyncState;
        await state.SetOwnerAsync("uid1");
        await state.SetKindAsync(new SyncKindState { Kind = "tags", HighestVersionSeen = 5 });
        await state.SaveItemsAsync("tags", [new SyncItemState { ItemId = "a" }]);
        await state.SetHeldAccountAsync(new SyncHeldAccount { Email = "a@x.com" });
        await state.SetPendingTagAsync(new SyncPendingTag { AccountKey = "a", MessageId = "m", TagId = "t" });

        await state.ResetAsync();
        Assert.Null(await state.GetOwnerAsync());
        Assert.Equal(0, (await state.GetKindAsync("tags")).HighestVersionSeen);
        Assert.Empty(await state.GetItemsAsync("tags"));
        Assert.Empty(await state.GetHeldAccountsAsync());
        Assert.Empty(await state.GetPendingTagsAsync());
    }

    [Fact]
    public async Task SyncState_IsInNoBackup_AndEraseDeletesIt()
    {
        Assert.DoesNotContain(StoreFiles.SyncState, StoreFiles.Databases);
        var stores = await OpenAsync();
        await stores.SyncState.SetOwnerAsync("uid1");
        stores.Flush();
        Assert.True(File.Exists(Path.Combine(_dir, StoreFiles.SyncState)));

        Assert.Empty(await stores.WipeAllAsync());
        _stores = null;
        Assert.Empty(Directory.EnumerateFiles(_dir, StoreFiles.SyncState + "*"));
    }

    [Fact]
    public async Task SecureMessageKeys_ReachDisk_WhenTheStoresClose()
    {
        var stores = await OpenAsync();
        await stores.SecureMessageKeys.AddAsync(new SecureMessageKeyRecord
        {
            KeyId = "0123456789abcdef0123456789abcdef",
            SenderAddress = "a@x.com",
            KeyPackage = [1, 2, 3],
            CreatedUtc = DateTime.UtcNow,
        });
        Close();
        Assert.NotNull(await (await OpenAsync()).SecureMessageKeys.GetAsync("0123456789abcdef0123456789abcdef"));
    }
}

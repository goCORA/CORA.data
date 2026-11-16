using CORA.Data.Contacts;
using CORA.Data.Email;
using CORA.Core.Security;

namespace CORA.Data.Tests.Email;

public class SecureDataStoresTests : IDisposable
{
   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
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

   private sealed class InMemorySecureKeyStorage : ISecureKeyStorage
   {
      private readonly Dictionary<string, string> _values = [];

      public Task<string?> GetAsync(string key) =>
         Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

      public Task SetAsync(string key, string value)
      {
         _values[key] = value;
         return Task.CompletedTask;
      }

      public Task RemoveAsync(string key)
      {
         _values.Remove(key);
         return Task.CompletedTask;
      }
   }

   private sealed class InMemoryPreferences : IPreferenceStore
   {
      private readonly Dictionary<string, string> _values = [];

      public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
      public void Set(string key, string value) => _values[key] = value;
      public void Remove(string key) => _values.Remove(key);
   }

   private async Task<IDataStores> CreateStoresAsync()
   {
      Directory.CreateDirectory(_dir);
      _stores = await SecureDataStores.CreateAsync(
         new TempAppData(_dir), new InMemorySecureKeyStorage(), new InMemoryPreferences());
      return _stores;
   }

   [Fact]
   public async Task CreateAsync_MigratesLegacyPlaintextContacts_Automatically()
   {
      Directory.CreateDirectory(_dir);
      var contactsFile = Path.Combine(_dir, "contacts.litedb");
      using (var legacy = new LiteDB.LiteDatabase(contactsFile))
      {
         legacy.GetCollection<Contact>("Contacts").Insert(
         [
            new Contact { FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com" },
            new Contact { FirstName = "Grace", LastName = "Hopper", Email = "grace@example.com" },
         ]);
      }

      var stores = await CreateStoresAsync();

      var contacts = await stores.Contacts.GetAllAsync();
      Assert.Equal(["Grace", "Ada"], contacts.Select(c => c.FirstName)); // ordered by last name
      Assert.False(File.Exists(contactsFile + ".legacy-plaintext"));
   }

   [Fact]
   public async Task Contacts_AreEncryptedOnDisk_AfterFlush()
   {
      var stores = await CreateStoresAsync();
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com" });

      stores.Flush();

      var raw = File.ReadAllBytes(Path.Combine(_dir, "contacts.litedb"));
      Assert.NotEmpty(raw);
      Assert.DoesNotContain("Lovelace", System.Text.Encoding.Latin1.GetString(raw));
   }

   [Fact]
   public async Task Contacts_AreWrittenShortlyAfterAChange_WithoutAnExplicitFlush()
   {
      var stores = await CreateStoresAsync();
      var contactsFile = Path.Combine(_dir, "contacts.litedb");

      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace" });

      // Well inside the 27s periodic interval, so only the change-triggered flush can do this.
      Assert.True(await WaitForAsync(() => File.Exists(contactsFile), TimeSpan.FromSeconds(10)));
   }

   [Fact]
   public async Task Contacts_AreNotWritten_AfterFlushIsSuppressed()
   {
      var stores = await CreateStoresAsync();
      var contactsFile = Path.Combine(_dir, "contacts.litedb");
      stores.SuppressFlush(); // what a restore does, so it isn't overwritten by stale memory

      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace" });

      Assert.False(await WaitForAsync(() => File.Exists(contactsFile), TimeSpan.FromSeconds(3)));
   }

   [Fact]
   public async Task WipeAllAsync_DeletesDatabasesLeftoversAndAttachments_AndNeverWritesThemBack()
   {
      var stores = await CreateStoresAsync();
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace" });
      await stores.Tags.AddTagAsync("work", "#ff0000");
      stores.Flush();
      var contactsFile = Path.Combine(_dir, "contacts.litedb");
      Assert.True(File.Exists(contactsFile));

      var attachments = Path.Combine(_dir, "mail_attachments", "acct");
      Directory.CreateDirectory(attachments);
      File.WriteAllText(Path.Combine(attachments, "a.pdf"), "x");
      File.WriteAllText(contactsFile + ".unrecovered-20260101000000.bak", "x");
      File.WriteAllText(Path.Combine(_dir, "tags.litedb.tmp"), "x");
      var unrelated = Path.Combine(_dir, "keep.txt");
      File.WriteAllText(unrelated, "x");

      // A change made just before the wipe must not survive via the debounced/periodic flush.
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Grace", LastName = "Hopper" });
      var failed = await stores.WipeAllAsync();

      Assert.Empty(failed);
      Assert.Empty(Directory.EnumerateFiles(_dir, "*.litedb*"));
      Assert.False(Directory.Exists(Path.Combine(_dir, "mail_attachments")));
      Assert.True(File.Exists(unrelated));
      Assert.False(await WaitForAsync(() => File.Exists(contactsFile), TimeSpan.FromSeconds(3)));
   }

   [Fact]
   public async Task WipeAllAsync_ReportsFilesThatCouldNotBeDeleted()
   {
      if (!OperatingSystem.IsWindows())
         return; // deleting an open file only fails on Windows

      var stores = await CreateStoresAsync();
      var locked = Path.Combine(_dir, "blacklist.litedb.bak");
      File.WriteAllText(locked, "x");
      using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

      var failed = await stores.WipeAllAsync();

      Assert.Equal([locked], failed);
   }

   private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
   {
      var deadline = DateTime.UtcNow + timeout;
      while (DateTime.UtcNow < deadline)
      {
         if (condition())
            return true;
         await Task.Delay(50);
      }
      return condition();
   }

   [Fact]
   public async Task RunAfterFlush_FlushesToDiskBeforeRunningAction()
   {
      var stores = await CreateStoresAsync();
      await stores.Tags.AddTagAsync("work", "#ff0000");
      var tagsFile = Path.Combine(_dir, "tags.litedb");
      Assert.False(File.Exists(tagsFile)); // nothing flushed yet (the periodic flush is 27s away)

      var existedInsideAction = false;
      stores.RunAfterFlush(() => existedInsideAction = File.Exists(tagsFile));

      Assert.True(existedInsideAction);
   }

   [Fact]
   public async Task RunAfterFlush_BlocksFlushesUntilActionCompletes()
   {
      var stores = await CreateStoresAsync();
      var insideAction = new ManualResetEventSlim();
      var releaseAction = new ManualResetEventSlim();

      var snapshot = Task.Run(() => stores.RunAfterFlush(() =>
      {
         insideAction.Set();
         Assert.True(releaseAction.Wait(TimeSpan.FromSeconds(10)));
      }));
      Assert.True(insideAction.Wait(TimeSpan.FromSeconds(10)));

      var flush = Task.Run(stores.Flush);
      await Task.Delay(300);
      Assert.False(flush.IsCompleted); // waiting on the lock the action holds

      releaseAction.Set();
      await Task.WhenAll(snapshot, flush).WaitAsync(TimeSpan.FromSeconds(10));
   }

   // ---- Per-account sets (claude/design-per-account-ownership.md), stage 1 ----

   [Fact]
   public async Task OwnerSet_OpensInItsOwnFolder_AndStage1StillUsesTheOwnerlessSet()
   {
      var stores = await CreateStoresAsync();
      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");

      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", Email = "ada@example.com" });
      stores.Flush();

      Assert.Equal(Path.Combine(_dir, "owners", "uidA1"), owner.Directory);
      Assert.True(Directory.Exists(owner.Directory));
      Assert.Single(await stores.Contacts.GetAllAsync());
      Assert.Empty(await owner.Contacts.GetAllAsync());
      Assert.True(File.Exists(Path.Combine(_dir, "contacts.litedb")));
      Assert.Same(owner, ((SecureDataStores)stores).OpenOwnerSet("uidA1"));

      // A set's files are written once something is saved in it (an empty store writes nothing).
      await owner.Contacts.SaveAsync(new Contact { FirstName = "Grace", Email = "grace@example.com" });
      stores.Flush();
      Assert.True(File.Exists(Path.Combine(owner.Directory, "contacts.litedb")));
      Assert.Single(await stores.Contacts.GetAllAsync());   // still only the ownerless contact
   }

   [Theory]
   [InlineData("")]
   [InlineData("../x")]
   [InlineData("a/b")]
   [InlineData("a\\b")]
   [InlineData("..")]
   public void OwnerDirectory_RefusesAnythingButAnAccountId(string uid)
   {
      Assert.Throws<ArgumentException>(() => OwnerStoreSet.OwnerDirectory(_dir, uid));
   }

   [Fact]
   public async Task WipeAllAsync_AlsoDeletesEveryOwnerSet()
   {
      var stores = await CreateStoresAsync();
      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      await owner.Contacts.SaveAsync(new Contact { FirstName = "Ada", Email = "ada@example.com" });
      stores.Flush();

      var failed = await stores.WipeAllAsync();
      _stores = null;

      Assert.Empty(failed);
      Assert.False(Directory.Exists(Path.Combine(_dir, "owners")));
   }

   // ---- Per-account sets, stage 2: whose items the stores show ----

   [Fact]
   public async Task SignedIn_NewItemsBelongToTheAccount_AndOwnerlessItemsStillShow()
   {
      var stores = await CreateStoresAsync();
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Old", LastName = "Ownerless", Email = "old@example.com" });
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "old@example.com" });

      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      await stores.Contacts.SaveAsync(new Contact { FirstName = "New", LastName = "Mine", Email = "new@example.com" });
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "new@example.com" });

      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      Assert.Equal("uidA1", stores.CurrentOwnerUid);
      Assert.Equal(["new@example.com"], (await owner.Accounts.LoadAllAsync()).Select(a => a.EmailAddress));
      Assert.Equal(["New"], (await owner.Contacts.GetAllAsync()).Select(c => c.FirstName));
      Assert.Equal(2, (await stores.Accounts.LoadAllAsync()).Count);
      Assert.Equal(2, (await stores.Contacts.GetAllAsync()).Count);
   }

   [Fact]
   public async Task SignedIn_EditingAnOwnerlessContact_ChangesItWhereItIs()
   {
      var stores = await CreateStoresAsync();
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", Email = "ada@example.com" });
      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);

      var shown = Assert.Single(await stores.Contacts.GetAllAsync());
      Assert.True(shown.Id < 0);   // the other set's ids are shown negative
      shown.LastName = "Lovelace";
      await stores.Contacts.SaveAsync(shown);

      Assert.True(shown.Id < 0);
      Assert.Equal("Lovelace", (await stores.Contacts.GetAsync(shown.Id))?.LastName);
      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      Assert.Empty(await owner.Contacts.GetAllAsync());

      stores.UseOwner(null, signedIn: false, showOwnerWhenSignedOut: false);
      Assert.Equal("Lovelace", Assert.Single(await stores.Contacts.GetAllAsync()).LastName);
   }

   [Fact]
   public async Task SignedOut_ShowsTheLastAccount_UnlessItHidesItsMailboxes()
   {
      var stores = await CreateStoresAsync();
      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "mine@example.com" });

      stores.UseOwner("uidA1", signedIn: false, showOwnerWhenSignedOut: true);
      Assert.Null(stores.CurrentOwnerUid);
      Assert.Single(await stores.Accounts.LoadAllAsync());

      stores.UseOwner("uidA1", signedIn: false, showOwnerWhenSignedOut: false);
      Assert.Empty(await stores.Accounts.LoadAllAsync());

      // Another account never sees it.
      stores.UseOwner("uidB2", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.Empty(await stores.Accounts.LoadAllAsync());
   }

   [Fact]
   public async Task MailboxData_FollowsTheSetThatHoldsTheMailbox()
   {
      var stores = await CreateStoresAsync();
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "old@example.com" });
      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "new@example.com" });

      stores.HiddenFolders.Hide("old@example.com", "Spam");
      stores.HiddenFolders.Hide("new@example.com", "Junk");

      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      Assert.Equal(["Junk"], owner.HiddenFolders.GetHidden("new@example.com"));
      Assert.Empty(owner.HiddenFolders.GetHidden("old@example.com"));
      Assert.True(stores.HiddenFolders.IsHidden("old@example.com", "Spam"));
      Assert.Equal(2, stores.HiddenFolders.GetAll().Count);

      stores.UseOwner(null, signedIn: false, showOwnerWhenSignedOut: false);
      Assert.Equal([("old@example.com", "Spam")], stores.HiddenFolders.GetAll());
   }

   [Fact]
   public async Task Shutdown_SavesOnceMore_AndTheStoresStayUsable()
   {
      var stores = await CreateStoresAsync();
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Ada", Email = "ada@example.com" });
      var file = Path.Combine(_dir, "contacts.litedb");

      stores.Shutdown();
      var saved = File.ReadAllBytes(file);

      // Late background work: no crash, and nothing more is written.
      await stores.Contacts.SaveAsync(new Contact { FirstName = "Grace", Email = "grace@example.com" });
      Assert.Equal(2, (await stores.Contacts.GetAllAsync()).Count);
      stores.Flush();
      Assert.Equal(saved, File.ReadAllBytes(file));
   }

   // ---- Per-account sets, stage 4: "Keep these with …" ----

   [Fact]
   public async Task GiveOwnerlessItems_MovesAMailboxWithItsOwnData_AndOtherAccountsNoLongerSeeIt()
   {
      var stores = await CreateStoresAsync();
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "Mine@Example.com" });
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "left@example.com" });
      stores.HiddenFolders.Hide("mine@example.com", "Spam");
      stores.FolderOrder.SetOrder("mine@example.com", ["Inbox", "Sent"]);
      var tag = await stores.Tags.AddTagAsync("Work", "#123456");
      await stores.Tags.SetMessageTagAsync("mine@example.com", "INBOX", 7, tag.Id);
      await stores.TrustedImageSenders.SetMessageAllowedAsync("mine@example.com", "INBOX", 7, true);

      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      var items = await stores.GetOwnerlessItemsAsync();
      Assert.Equal(2, items.Mailboxes.Count);
      Assert.Equal(1, items.Tags);

      var choice = new OwnerlessChoice();
      choice.Mailboxes.Add("Mine@Example.com");
      stores.GiveOwnerlessItems("uidA1", choice);

      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      Assert.Equal(["Mine@Example.com"], (await owner.Accounts.LoadAllAsync()).Select(a => a.EmailAddress));
      Assert.Equal(["Spam"], owner.HiddenFolders.GetHidden("mine@example.com"));
      Assert.Equal(["Inbox", "Sent"], owner.FolderOrder.GetOrder("mine@example.com"));
      Assert.True(await owner.TrustedImageSenders.IsMessageAllowedAsync("mine@example.com", "INBOX", 7));
      Assert.Equal(tag.Id, (await stores.Tags.GetMessageTagAsync("mine@example.com", "INBOX", 7))?.Id);   // the tag itself stayed ownerless
      Assert.Equal(["left@example.com"], (await stores.GetOwnerlessItemsAsync()).Mailboxes.Select(a => a.EmailAddress));

      stores.UseOwner("uidB2", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.Equal(["left@example.com"], (await stores.Accounts.LoadAllAsync()).Select(a => a.EmailAddress));
      Assert.False(stores.HiddenFolders.IsHidden("mine@example.com", "Spam"));
   }

   [Fact]
   public async Task GiveOwnerlessItems_MovesTheTickedContacts_KeepingTheirSyncIds()
   {
      var stores = await CreateStoresAsync();
      var ada = new Contact { FirstName = "Ada", Email = "ada@example.com" };
      var grace = new Contact { FirstName = "Grace", Email = "grace@example.com" };
      await stores.Contacts.SaveAsync(ada);
      await stores.Contacts.SaveAsync(grace);

      var choice = new OwnerlessChoice();
      choice.ContactIds.Add(ada.Id);
      stores.GiveOwnerlessItems("uidA1", choice);

      var owner = ((SecureDataStores)stores).OpenOwnerSet("uidA1");
      var moved = Assert.Single(await owner.Contacts.GetAllAsync());
      Assert.Equal("Ada", moved.FirstName);
      Assert.Equal(ada.SyncId, moved.SyncId);
      Assert.Equal(["Grace"], (await stores.Contacts.GetAllAsync()).Select(c => c.FirstName));   // signed out view: ownerless only

      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.Equal(2, (await stores.Contacts.GetAllAsync()).Count);
   }

   [Fact]
   public async Task GiveOwnerlessItems_MovesWholeGroups()
   {
      var stores = await CreateStoresAsync();
      await stores.Tags.AddTagAsync("Work", "#123456");
      await stores.Blacklist.BlacklistSenderAsync("spam@example.com");
      await stores.Blacklist.BlacklistDomainAsync("spam.example");
      await stores.TrustedImageSenders.SetTrustedAsync("friend@example.com", true);
      await stores.RecentRecipients.RecordSentAsync(["bob@example.com"]);

      var before = await stores.GetOwnerlessItemsAsync();
      Assert.Equal((1, 2, 1, 1), (before.Tags, before.BlockedSendersAndDomains, before.TrustedSenders, before.RecentRecipients));

      var choice = new OwnerlessChoice { Tags = true, BlockedSendersAndDomains = true, TrustedSenders = true, RecentRecipients = true };
      stores.GiveOwnerlessItems("uidA1", choice);

      Assert.True((await stores.GetOwnerlessItemsAsync()).IsEmpty);
      Assert.False(await stores.Blacklist.IsSenderBlacklistedAsync("spam@example.com"));   // signed out: not shown any more

      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.True(await stores.Blacklist.IsSenderBlacklistedAsync("spam@example.com"));
      Assert.True(await stores.Blacklist.IsDomainBlacklistedAsync("spam.example"));
      Assert.True(await stores.TrustedImageSenders.IsTrustedAsync("friend@example.com"));
      Assert.Single(await stores.Tags.GetTagsAsync());
      Assert.Single(await stores.RecentRecipients.GetAllAsync());
   }

   // ---- Per-account sets, stage 6: what sync sees ----

   [Fact]
   public async Task Views_SeeOnlyTheAccountsItems_OrOnlyTheOwnerlessOnes()
   {
      var stores = await CreateStoresAsync();
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "old@example.com" });
      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      await stores.Accounts.SaveAccountAsync(new MailAccount { EmailAddress = "new@example.com" });

      Assert.Equal(2, (await stores.Accounts.LoadAllAsync()).Count);
      Assert.Equal(["new@example.com"], (await stores.OwnedOnly.Accounts.LoadAllAsync()).Select(a => a.EmailAddress));
      Assert.Equal(["old@example.com"], (await stores.OwnerlessOnly.Accounts.LoadAllAsync()).Select(a => a.EmailAddress));

      stores.UseOwner("uidB2", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.Empty(await stores.OwnedOnly.Accounts.LoadAllAsync());
   }

   [Fact]
   public async Task AdoptOwnerlessSyncState_MovesIt_OnlyToTheAccountItBelongedTo_AndOnlyOnce()
   {
      var stores = await CreateStoresAsync();
      await stores.SyncState.SetOwnerAsync("uidA1");   // signed out: the shared (ownerless) sync state
      await stores.SyncState.SetValueAsync("passwords", "off");

      Assert.False(await stores.AdoptOwnerlessSyncStateAsync("uidB2", "uidB2"));   // not B's
      Assert.True(await stores.AdoptOwnerlessSyncStateAsync("uidA1", "uidA1"));
      Assert.False(await stores.AdoptOwnerlessSyncStateAsync("uidA1", "uidA1"));  // already moved

      Assert.Null(await stores.SyncState.GetOwnerAsync());   // still signed out: the shared one is empty now
      stores.UseOwner("uidA1", signedIn: true, showOwnerWhenSignedOut: false);
      Assert.Equal("uidA1", await stores.SyncState.GetOwnerAsync());
      Assert.Equal("off", await stores.SyncState.GetValueAsync("passwords"));
   }
}

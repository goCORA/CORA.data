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
}

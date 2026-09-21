using CORA.Data.Contacts;
using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Tests.Contacts;

public class LegacyContactMigrationTests : IDisposable
{
   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
   private readonly string _path;
   private string LegacyPath => _path + ".legacy-plaintext";

   public LegacyContactMigrationTests()
   {
      Directory.CreateDirectory(_dir);
      _path = Path.Combine(_dir, "contacts.litedb");
      Cora.InitializeAsync(new InMemorySecureKeyStorage()).GetAwaiter().GetResult();
   }

   public void Dispose()
   {
      try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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

   private static Contact[] SampleContacts() =>
   [
      new Contact { FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com;ada@work.example", Phone = "555-0100", Tags = "vip", IsGoCORA = true },
      new Contact { FirstName = "Grace", LastName = "Hopper", Email = "grace@example.com", PhotoPath = "grace.jpg" },
      new Contact { FirstName = "Alan", LastName = "Turing", Email = "alan@example.com" },
   ];

   /// <summary>Writes a plaintext LiteDB file the way the old app-level ContactDatabase did.</summary>
   private static void WritePlaintext(string path, params Contact[] contacts)
   {
      using var db = new LiteDatabase(path);
      db.GetCollection<Contact>("Contacts").Insert(contacts);
   }

   private static async Task<List<Contact>> ReadAllAsync(string path)
   {
      using var store = new ContactDatabase(path);
      return await store.GetAllAsync();
   }

   private bool FileIsPlaintextLiteDb(string path)
   {
      try
      {
         using var db = new LiteDatabase(new MemoryStream(File.ReadAllBytes(path)));
         _ = db.GetCollectionNames().ToList();
         return true;
      }
      catch (Exception)
      {
         return false;
      }
   }

   [Fact]
   public async Task PlaintextFile_IsConvertedToEncrypted_WithAllContactsAndIdsPreserved()
   {
      var contacts = SampleContacts();
      WritePlaintext(_path, contacts);
      Assert.True(FileIsPlaintextLiteDb(_path));

      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.False(FileIsPlaintextLiteDb(_path));
      Assert.False(File.Exists(LegacyPath));
      var migrated = await ReadAllAsync(_path);
      // LiteDB's mapper stores empty strings as null (EmptyStringToNull), in the old file too.
      static (int, string, string, string, string, string, bool, string) Fields(Contact c) =>
         (c.Id, c.FirstName ?? "", c.LastName ?? "", c.Email ?? "", c.Phone ?? "", c.PhotoPath ?? "", c.IsGoCORA, c.Tags ?? "");
      Assert.Equal(contacts.OrderBy(c => c.LastName).Select(Fields), migrated.Select(Fields));
   }

   [Fact]
   public async Task MigratedStore_KeepsGeneratingUniqueIds()
   {
      WritePlaintext(_path, SampleContacts());
      LegacyContactMigration.MigrateIfNeeded(_path);

      using (var store = new ContactDatabase(_path))
      {
         await store.SaveAsync(new Contact { FirstName = "New", LastName = "Person" });
         Assert.Equal(4, (await store.GetAllAsync()).Select(c => c.Id).Distinct().Count());
      }
   }

   [Fact]
   public async Task AlreadyEncryptedFile_IsLeftUntouched()
   {
      using (var store = new ContactDatabase(_path))
      {
         await store.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace" });
         ((CORA.Data.Email.IFlushableStore)store).Flush();
      }
      var before = File.ReadAllBytes(_path);

      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.Equal(before, File.ReadAllBytes(_path));
      Assert.False(File.Exists(LegacyPath));
      Assert.Single(await ReadAllAsync(_path));
   }

   [Fact]
   public async Task Flush_OnlyWritesWhenSomethingChanged()
   {
      using var store = new ContactDatabase(_path);
      var flushable = (CORA.Data.Email.IFlushableStore)store;

      flushable.Flush();
      Assert.False(File.Exists(_path)); // nothing has changed yet, so nothing is written

      await store.SaveAsync(new Contact { FirstName = "Ada", LastName = "Lovelace" });
      flushable.Flush();
      var afterSave = File.ReadAllBytes(_path);

      flushable.Flush(); // clean: a rewrite would produce different ciphertext (random nonce)
      Assert.Equal(afterSave, File.ReadAllBytes(_path));

      await store.DeleteAsync((await store.GetAllAsync()).Single());
      flushable.Flush();
      Assert.NotEqual(afterSave, File.ReadAllBytes(_path));
   }

   [Fact]
   public void NoFile_IsANoOp()
   {
      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.Empty(Directory.GetFileSystemEntries(_dir));
   }

   [Fact]
   public void UnrecognizedFile_IsNotTouched()
   {
      var garbage = new byte[2048];
      new Random(1).NextBytes(garbage);
      File.WriteAllBytes(_path, garbage);

      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.Equal(garbage, File.ReadAllBytes(_path));
      Assert.False(File.Exists(LegacyPath));
   }

   [Fact]
   public async Task InterruptedMigration_LegacyFileOnly_IsResumed()
   {
      // State after a crash between "move plaintext aside" and "write encrypted file".
      WritePlaintext(LegacyPath, SampleContacts());

      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.False(File.Exists(LegacyPath));
      Assert.Equal(3, (await ReadAllAsync(_path)).Count);
   }

   [Fact]
   public async Task InterruptedMigration_PartialEncryptedFile_IsCompletedWithoutDuplicates()
   {
      var contacts = SampleContacts();
      WritePlaintext(LegacyPath, contacts);
      // State after a crash mid-import: the encrypted file already holds some of the contacts.
      using (var partial = EncryptedLiteDbFile.Open(_path))
      {
         partial.Database.GetCollection<Contact>("Contacts").Insert(new Contact { Id = 1, FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com;ada@work.example" });
         partial.Flush();
      }

      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.False(File.Exists(LegacyPath));
      var all = await ReadAllAsync(_path);
      Assert.Equal(3, all.Count);
      Assert.Equal(3, all.Select(c => c.Id).Distinct().Count());
   }

   [Fact]
   public async Task RunningTwice_IsIdempotent()
   {
      WritePlaintext(_path, SampleContacts());

      LegacyContactMigration.MigrateIfNeeded(_path);
      LegacyContactMigration.MigrateIfNeeded(_path);

      Assert.Equal(3, (await ReadAllAsync(_path)).Count);
   }

   [Fact]
   public async Task PlaintextRestoredOverEncryptedFile_IsMigratedAgain()
   {
      WritePlaintext(_path, SampleContacts());
      LegacyContactMigration.MigrateIfNeeded(_path);

      // Restoring an old backup puts a plaintext file back where the encrypted one was.
      File.Delete(_path);
      WritePlaintext(_path, new Contact { FirstName = "Old", LastName = "Backup" });
      LegacyContactMigration.MigrateIfNeeded(_path);

      var all = await ReadAllAsync(_path);
      Assert.Equal("Old", Assert.Single(all).FirstName);
   }
}

using System.IO.Compression;
using CORA.Data.Backup;
using CORA.Data.Email;
using CORA.Core.Security;

namespace CORA.Data.Tests.Backup;

public class DatabaseBackupServiceTests : IDisposable
{
   private static readonly string[] AllStoreFiles =
   [
      "tags.litedb", "mailsync.litedb", "accounts.litedb", "contacts.litedb",
      "trustedImageSenders.litedb", "blacklist.litedb", "ai_autonomy.litedb", "hidden_folders.litedb",
      "ai_result_cache.litedb", "folder_order.litedb", "secure_message_keys.litedb",
   ];

   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
   private readonly DatabaseBackupService _service;

   public DatabaseBackupServiceTests()
   {
      Directory.CreateDirectory(_dir);
      _service = new DatabaseBackupService(new TempAppData(_dir), new NoKeyStorage());
   }

   public void Dispose()
   {
      try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
   }

   private sealed class TempAppData(string dir) : IAppDataLocation
   {
      public string AppDataDirectory { get; } = dir;
   }

   private sealed class NoKeyStorage : ISecureKeyStorage
   {
      public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
      public Task SetAsync(string key, string value) => Task.CompletedTask;
      public Task RemoveAsync(string key) => Task.CompletedTask;
   }

   private void WriteAllStoreFiles(string content)
   {
      foreach (var name in AllStoreFiles)
         File.WriteAllText(Path.Combine(_dir, name), $"{content}:{name}");
   }

   private MemoryStream BackupOfCurrentFiles()
   {
      var stream = new MemoryStream();
      _service.CreateBackupArchive(stream);
      stream.Position = 0;
      return stream;
   }

   [Fact]
   public void CreateBackupArchive_IncludesEveryStoreFile_IncludingBlacklistAndAiAutonomy()
   {
      WriteAllStoreFiles("original");

      using var backup = BackupOfCurrentFiles();

      using var zip = new ZipArchive(backup, ZipArchiveMode.Read);
      Assert.Equal(AllStoreFiles.Order(), zip.Entries.Select(e => e.FullName).Order());
   }

   [Fact]
   public void RestoreBackupArchive_RestoresHiddenFoldersAndFolderOrder_WithTheAccountsToggle_AndNotWithout()
   {
      WriteAllStoreFiles("original");
      using var backup = BackupOfCurrentFiles();
      WriteAllStoreFiles("changed-since-backup");

      _service.RestoreBackupArchive(
         backup, restoreAccounts: true, restoreTags: false, restoreContacts: false,
         restoreMailboxes: false, restoreTrustedImageSenders: false,
         restoreBlacklist: false, restoreAiAutonomy: false, restoreAiResultCache: false);

      foreach (var name in AllStoreFiles)
      {
         var expected = name is "accounts.litedb" or "hidden_folders.litedb" or "folder_order.litedb" ? "original" : "changed-since-backup";
         Assert.Equal($"{expected}:{name}", File.ReadAllText(Path.Combine(_dir, name)));
      }
   }

   [Theory]
   [InlineData(true, false, false, "blacklist.litedb")]
   [InlineData(false, true, false, "ai_autonomy.litedb")]
   [InlineData(false, false, true, "ai_result_cache.litedb")]
   [InlineData(true, true, true, "blacklist.litedb", "ai_autonomy.litedb", "ai_result_cache.litedb")]
   [InlineData(false, false, false)]
   public void RestoreBackupArchive_RestoresOnlyTheSelectedStores(
      bool blacklist, bool aiAutonomy, bool aiResultCache, params string[] expectedRestored)
   {
      WriteAllStoreFiles("original");
      using var backup = BackupOfCurrentFiles();
      WriteAllStoreFiles("changed-since-backup");

      _service.RestoreBackupArchive(
         backup, restoreAccounts: false, restoreTags: false, restoreContacts: false,
         restoreMailboxes: false, restoreTrustedImageSenders: false,
         restoreBlacklist: blacklist, restoreAiAutonomy: aiAutonomy, restoreAiResultCache: aiResultCache);

      foreach (var name in AllStoreFiles)
      {
         var expected = expectedRestored.Contains(name) ? "original" : "changed-since-backup";
         Assert.Equal($"{expected}:{name}", File.ReadAllText(Path.Combine(_dir, name)));
      }
   }

   [Fact]
   public void ContactPhotos_AreBackedUpWithSlashNames_AndRestoreWithTheContactsToggleOnly()
   {
      WriteAllStoreFiles("original");
      Directory.CreateDirectory(Path.Combine(_dir, "contact_photos"));
      File.WriteAllText(Path.Combine(_dir, "contact_photos", "a.jpg"), "photo");
      Directory.CreateDirectory(Path.Combine(_dir, "mail_attachments", "m1"));
      File.WriteAllText(Path.Combine(_dir, "mail_attachments", "m1", "x.pdf"), "pdf");

      using var backup = BackupOfCurrentFiles();
      using (var zip = new ZipArchive(backup, ZipArchiveMode.Read, leaveOpen: true))
      {
         Assert.Contains(zip.Entries, e => e.FullName == "contact_photos/a.jpg");
         Assert.Contains(zip.Entries, e => e.FullName == "mail_attachments/m1/x.pdf");
         Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains('\\'));
      }

      Directory.Delete(Path.Combine(_dir, "contact_photos"), recursive: true);
      Directory.Delete(Path.Combine(_dir, "mail_attachments"), recursive: true);
      backup.Position = 0;
      _service.RestoreBackupArchive(backup, restoreAccounts: false, restoreTags: false, restoreContacts: false, restoreMailboxes: false,
         restoreTrustedImageSenders: false, restoreBlacklist: false, restoreAiAutonomy: false, restoreAiResultCache: false);
      Assert.False(File.Exists(Path.Combine(_dir, "contact_photos", "a.jpg")));

      backup.Position = 0;
      _service.RestoreBackupArchive(backup, restoreAccounts: false, restoreTags: false, restoreContacts: true, restoreMailboxes: false,
         restoreTrustedImageSenders: false, restoreBlacklist: false, restoreAiAutonomy: false, restoreAiResultCache: false);
      Assert.Equal("photo", File.ReadAllText(Path.Combine(_dir, "contact_photos", "a.jpg")));
      Assert.False(File.Exists(Path.Combine(_dir, "mail_attachments", "m1", "x.pdf")));
   }

   [Fact]
   public void Restore_ReadsOldBackslashNames_AndSkipsNamesThatLeaveTheDataFolder()
   {
      var stream = new MemoryStream();
      using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
      {
         void Add(string name, string text)
         {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
         }
         Add("mail_attachments\\m2\\y.txt", "old windows name");
         Add("../escaped.txt", "outside");
         Add("contact_photos/../../escaped2.txt", "outside");
      }

      stream.Position = 0;
      _service.RestoreBackupArchive(stream);
      Assert.Equal("old windows name", File.ReadAllText(Path.Combine(_dir, "mail_attachments", "m2", "y.txt")));
      Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped.txt")));
      Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped2.txt")));

      stream.Position = 0;
      _service.RestoreBackupArchive(stream, restoreAccounts: false, restoreTags: false, restoreContacts: true, restoreMailboxes: true,
         restoreTrustedImageSenders: false, restoreBlacklist: false, restoreAiAutonomy: false, restoreAiResultCache: false);
      Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped2.txt")));
   }
}

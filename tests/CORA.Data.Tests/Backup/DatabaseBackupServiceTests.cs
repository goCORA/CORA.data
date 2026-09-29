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
      "ai_result_cache.litedb",
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
   public void RestoreBackupArchive_RestoresHiddenFolders_WithTheAccountsToggle_AndNotWithout()
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
         var expected = name is "accounts.litedb" or "hidden_folders.litedb" ? "original" : "changed-since-backup";
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
}

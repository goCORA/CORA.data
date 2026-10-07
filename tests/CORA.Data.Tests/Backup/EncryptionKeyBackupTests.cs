using System.Collections.Concurrent;
using System.Security.Cryptography;
using CORA.Core.Security;
using CORA.Data.Backup;
using CORA.Data.Email;

namespace CORA.Data.Tests.Backup;

/// <summary>Step 4b: the encryption-key backup through the key-free methods (no key handed out).</summary>
public class EncryptionKeyBackupTests : IDisposable
{
   private sealed class MemoryStorage : ISecureKeyStorage
   {
      public ConcurrentDictionary<string, string> Values { get; } = new();
      public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
      public Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
      public Task RemoveAsync(string key) { Values.TryRemove(key, out _); return Task.CompletedTask; }
   }

   private sealed class TempAppData(string dir) : IAppDataLocation
   {
      public string AppDataDirectory { get; } = dir;
   }

   private const string MasterStorageKey = "cora.litedb.key.master";
   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-keybackup-tests-" + Guid.NewGuid().ToString("N"));

   public EncryptionKeyBackupTests() => Directory.CreateDirectory(_dir);

   public void Dispose()
   {
      try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
   }

   [Fact]
   public async Task KeyBackup_RestoresTheSameMasterKeyOnAnotherDevice()
   {
      var storage = new MemoryStorage();
      await Cora.InitializeAsync(storage);
      var service = new DatabaseBackupService(new TempAppData(_dir), storage);

      byte[] backup;
      using (var key = AccountBackupKey.Create("Joe@Example.com", "1234"))
         backup = await service.CreateEncryptionKeyBackupAsync(key);

      var otherStorage = new MemoryStorage();
      var otherDevice = new DatabaseBackupService(new TempAppData(_dir), otherStorage);
      await otherDevice.RestoreEncryptionKeyBackupAsync("joe@example.com", "1234", backup);

      Assert.Equal(storage.Values[MasterStorageKey], otherStorage.Values[MasterStorageKey]);
   }

   [Fact]
   public async Task KeyBackup_WithWrongPassphrase_FailsAndChangesNothing()
   {
      var storage = new MemoryStorage();
      await Cora.InitializeAsync(storage);
      var service = new DatabaseBackupService(new TempAppData(_dir), storage);

      byte[] backup;
      using (var key = AccountBackupKey.Create("joe@example.com", "1234"))
         backup = await service.CreateEncryptionKeyBackupAsync(key);

      var otherStorage = new MemoryStorage();
      var otherDevice = new DatabaseBackupService(new TempAppData(_dir), otherStorage);
      await Assert.ThrowsAnyAsync<CryptographicException>(
         () => otherDevice.RestoreEncryptionKeyBackupAsync("joe@example.com", "0000", backup));

      Assert.False(otherStorage.Values.ContainsKey(MasterStorageKey));
   }
}

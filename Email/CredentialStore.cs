using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="ICredentialStore"/> implementation, following the same
/// pattern as <see cref="MailSyncDatabase"/>.
/// Singleton — the database file/collection is created on first use.
/// Migrates any accounts previously saved via secure storage on first load.
/// The backing file is encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class AccountCredentialDatabase : ICredentialStore, IFlushableStore, IDisposable
{
    // Legacy secure-storage keys written by earlier versions of the app.
    private const string LegacySecureStorageKey = "cora.mail.accounts";
    private const string LegacySecureStorageSingleKey = "cora.mail.account";
    private const string ActiveAccountPreferenceKey = "cora.active.account.email";

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<MailAccount> _accounts;
    private readonly ISecureKeyStorage _legacySecureStorage;
    private readonly IPreferenceStore _preferences;

    static AccountCredentialDatabase()
    {
        // MailAccount has no dedicated Id property; use EmailAddress as the LiteDB key.
        BsonMapper.Global.Entity<MailAccount>().Id(a => a.EmailAddress, autoId: false);
    }

    public AccountCredentialDatabase(string path, ISecureKeyStorage legacySecureStorage, IPreferenceStore preferences)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _accounts = _file.Database.GetCollection<MailAccount>("Accounts");
        _legacySecureStorage = legacySecureStorage;
        _preferences = preferences;
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    public async Task<List<MailAccount>> LoadAllAsync()
    {
        var accounts = await Task.Run(() => _accounts.FindAll().ToList());
        if (accounts.Count > 0)
            return accounts;

        // Migrate any accounts saved by the previous secure-storage-backed store.
        var migrated = await MigrateFromSecureStorageAsync();
        return migrated ?? [];
    }

    public Task SaveAccountAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Task.Run(() =>
        {
            _accounts.Upsert(account);
        });
    }

    public Task DeleteAccountAsync(string email)
    {
        if (string.IsNullOrEmpty(email))
            return Task.CompletedTask;
        return Task.Run(() =>
        {
            _accounts.Delete(email);
        });
    }

    public void Clear() { }

    public string? GetActiveAccountEmail() => _preferences.Get(ActiveAccountPreferenceKey);

    public void SetActiveAccountEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
            _preferences.Remove(ActiveAccountPreferenceKey);
        else
            _preferences.Set(ActiveAccountPreferenceKey, email);
    }

    private async Task<List<MailAccount>?> MigrateFromSecureStorageAsync()
    {
        try
        {
            var json = await _legacySecureStorage.GetAsync(LegacySecureStorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var accounts = System.Text.Json.JsonSerializer.Deserialize<List<MailAccount>>(json) ?? [];
                if (accounts.Count > 0)
                {
                    foreach (var account in accounts)
                        await SaveAccountAsync(account);
                }
                await _legacySecureStorage.RemoveAsync(LegacySecureStorageKey);
                return accounts;
            }

            var legacySingleJson = await _legacySecureStorage.GetAsync(LegacySecureStorageSingleKey);
            if (!string.IsNullOrEmpty(legacySingleJson))
            {
                var single = System.Text.Json.JsonSerializer.Deserialize<MailAccount>(legacySingleJson);
                if (single is not null)
                {
                    var migrated = new List<MailAccount> { single };
                    await SaveAccountAsync(single);
                    await _legacySecureStorage.RemoveAsync(LegacySecureStorageSingleKey);
                    return migrated;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

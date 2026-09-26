using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IHiddenFolderStore"/>, following the same pattern as
/// <see cref="BlacklistDatabase"/>. The backing file is encrypted at rest via
/// <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class HiddenFolderDatabase : IHiddenFolderStore, IFlushableStore, IDisposable
{
    // Older versions kept this list as one JSON preference (account -> folder names), in plain
    // text. It is imported once by ImportLegacyPreference and then removed.
    internal const string LegacyPreferenceKey = "cora.flyout.hiddenFolders";

    private sealed class HiddenFolderDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string Account { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<HiddenFolderDoc> _folders;

    public HiddenFolderDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _folders = _file.Database.GetCollection<HiddenFolderDoc>("HiddenFolders");
        _folders.EnsureIndex(d => d.Account);
    }

    public event EventHandler? Changed;

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    // Addresses are lowercased. Folder names keep their case, but LiteDB compares string ids
    // (and so the ids built below) case-insensitively, so "Notes" and "notes" are the same folder here.
    private static string NormalizeAccount(string accountAddress) => accountAddress.Trim().ToLowerInvariant();

    private static string MakeId(string account, string folder) => account + "|" + folder;

    public bool IsHidden(string accountAddress, string folderFullName) =>
        _folders.FindById(MakeId(NormalizeAccount(accountAddress), folderFullName)) is not null;

    public IReadOnlyCollection<string> GetHidden(string accountAddress)
    {
        var account = NormalizeAccount(accountAddress);
        return _folders.Find(d => d.Account == account).Select(d => d.Folder).ToList();
    }

    public IReadOnlyList<(string Account, string Folder)> GetAll() =>
        _folders.FindAll()
            .OrderBy(d => d.Account, StringComparer.Ordinal)
            .ThenBy(d => d.Folder, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => (d.Account, d.Folder))
            .ToList();

    public void Hide(string accountAddress, string folderFullName)
    {
        var account = NormalizeAccount(accountAddress);
        var id = MakeId(account, folderFullName);
        if (_folders.FindById(id) is not null)
            return;

        _folders.Insert(new HiddenFolderDoc { Id = id, Account = account, Folder = folderFullName });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unhide(string accountAddress, string folderFullName)
    {
        if (_folders.Delete(MakeId(NormalizeAccount(accountAddress), folderFullName)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UnhideAll(string accountAddress)
    {
        var account = NormalizeAccount(accountAddress);
        if (_folders.DeleteMany(d => d.Account == account) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One-time move of the old plaintext preference into this encrypted store. The preference is
    /// only removed after the imported rows have been flushed to disk, so a crash in between
    /// leaves it in place for the next launch. An unreadable value is dropped (nothing hidden).
    /// </summary>
    internal void ImportLegacyPreference(IPreferenceStore preferences)
    {
        var raw = preferences.Get(LegacyPreferenceKey);
        if (string.IsNullOrEmpty(raw))
            return;

        try
        {
            var legacy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw);
            if (legacy is not null)
            {
                foreach (var (account, folders) in legacy)
                {
                    foreach (var folder in folders ?? [])
                        Hide(account, folder);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable: treat as nothing hidden rather than losing folders from the menu.
        }

        _file.Flush();
        preferences.Remove(LegacyPreferenceKey);
    }
}

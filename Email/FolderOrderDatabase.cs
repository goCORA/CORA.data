using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IFolderOrderStore"/>, following the same pattern as
/// <see cref="HiddenFolderDatabase"/>: one document per account. The backing file is encrypted
/// at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class FolderOrderDatabase : IFolderOrderStore, IFlushableStore, IDisposable
{
    private sealed class FolderOrderDoc
    {
        [BsonId]
        public string Account { get; set; } = string.Empty;
        public List<string> Folders { get; set; } = [];
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<FolderOrderDoc> _orders;

    public FolderOrderDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _orders = _file.Database.GetCollection<FolderOrderDoc>("FolderOrder");
    }

    public event EventHandler? Changed;

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static string NormalizeAccount(string accountAddress) => accountAddress.Trim().ToLowerInvariant();

    public IReadOnlyList<string> GetOrder(string accountAddress) =>
        _orders.FindById(NormalizeAccount(accountAddress))?.Folders ?? [];

    public void SetOrder(string accountAddress, IReadOnlyList<string> folderFullNames)
    {
        ArgumentNullException.ThrowIfNull(folderFullNames);

        var account = NormalizeAccount(accountAddress);
        var folders = folderFullNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (folders.Count == 0)
        {
            Clear(accountAddress);
            return;
        }

        var existing = _orders.FindById(account);
        if (existing is not null && existing.Folders.SequenceEqual(folders, StringComparer.Ordinal))
            return;

        _orders.Upsert(new FolderOrderDoc { Account = account, Folders = folders });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string accountAddress)
    {
        if (_orders.Delete(NormalizeAccount(accountAddress)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string accountAddress, string oldFolderFullName, string newFolderFullName)
    {
        var doc = _orders.FindById(NormalizeAccount(accountAddress));
        if (doc is null)
            return;

        var index = doc.Folders.FindIndex(f => f.Equals(oldFolderFullName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        doc.Folders[index] = newFolderFullName;
        _orders.Update(doc);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

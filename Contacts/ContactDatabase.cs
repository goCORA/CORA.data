using CORA.Data.Email;
using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Contacts;

/// <summary>
/// LiteDB-backed <see cref="IContactStore"/> implementation, following the same pattern as
/// <see cref="TagDatabase"/>. The backing file is encrypted at rest via
/// <see cref="EncryptedLiteDbFile"/>. Existing plaintext files from older versions are
/// converted by <see cref="LegacyContactMigration"/> before this store is opened.
/// </summary>
public class ContactDatabase : IContactStore, IFlushableStore, IDisposable
{
    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<Contact> _contacts;
    private int _dirty; // 1 = changes not yet written to disk

    /// <summary>
    /// Raised after every change. <see cref="SecureDataStores"/> uses it to schedule a flush
    /// shortly afterwards, so a contact edit reaches disk without waiting for the periodic one.
    /// </summary>
    internal Action? Changed { get; set; }

    public ContactDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _contacts = _file.Database.GetCollection<Contact>(LegacyContactMigration.CollectionName);
        _contacts.EnsureIndex(c => c.FirstName);
        _contacts.EnsureIndex(c => c.LastName);
        _contacts.EnsureIndex(c => c.Email);
    }

    public void Dispose() => _file.Dispose();

    /// <summary>
    /// Writes to disk only if something changed since the last write. Contacts change rarely, so
    /// the periodic, lifecycle and backup flushes are no-ops for them unless there is something
    /// to save. The flag is cleared first, so a change made while the write is running marks
    /// the store dirty again, and it is restored if the write fails so the next flush retries.
    /// </summary>
    void IFlushableStore.Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        try
        {
            _file.Flush();
        }
        catch
        {
            Volatile.Write(ref _dirty, 1);
            throw;
        }
    }

    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private void MarkChanged()
    {
        Volatile.Write(ref _dirty, 1);
        Changed?.Invoke();
    }

    public Task<List<Contact>> GetAllAsync() =>
        Task.Run(() => _contacts.FindAll()
            .OrderBy(c => c.LastName)
            .ThenBy(c => c.FirstName)
            .ToList());

    public Task<Contact?> GetAsync(int id) =>
        Task.Run<Contact?>(() => _contacts.FindById(id));

    public Task<int> SaveAsync(Contact contact) =>
        Task.Run(() =>
        {
            if (contact.Id == 0)
            {
                var newId = _contacts.Insert(contact);
                contact.Id = newId.AsInt32;
            }
            else if (!_contacts.Update(contact))
            {
                // No such id: insert using the specified id so undo can restore original ids.
                var newId = _contacts.Insert(contact);
                contact.Id = newId?.AsInt32 ?? contact.Id;
            }

            MarkChanged();
            return 1;
        });

    public Task<int> DeleteAsync(Contact contact) =>
        Task.Run(() =>
        {
            var deleted = _contacts.Delete(contact.Id);
            if (deleted)
                MarkChanged();
            return deleted ? 1 : 0;
        });

    public Task<int> DeleteManyAsync(IEnumerable<Contact> contacts) =>
        Task.Run(() =>
        {
            var count = contacts.Count(c => _contacts.Delete(c.Id));
            if (count > 0)
                MarkChanged();
            return count;
        });

    public Task<List<Contact>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllAsync();

        return Task.Run(() =>
        {
            var q = query.Trim();
            return _contacts.FindAll()
                .Where(c =>
                    (c.FirstName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                    (c.LastName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                    (c.Email?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
                .OrderBy(c => c.LastName ?? string.Empty)
                .ThenBy(c => c.FirstName ?? string.Empty)
                .ToList();
        });
    }
}

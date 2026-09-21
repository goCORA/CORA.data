namespace CORA.Data.Contacts;

/// <summary>
/// Persistent store for the user's contacts. Implemented by <see cref="ContactDatabase"/>,
/// which keeps the backing file encrypted at rest; consumers only see this interface via
/// <see cref="Email.IDataStores.Contacts"/>.
/// </summary>
public interface IContactStore
{
    /// <summary>Returns every contact, ordered by last name then first name.</summary>
    Task<List<Contact>> GetAllAsync();

    /// <summary>Returns the contact with the given id, or null if there is none.</summary>
    Task<Contact?> GetAsync(int id);

    /// <summary>
    /// Inserts the contact (Id == 0, an Id is generated and assigned) or updates it; if the
    /// Id doesn't exist yet it is inserted, so undo can restore a deleted contact's original Id.
    /// </summary>
    Task<int> SaveAsync(Contact contact);

    /// <summary>Deletes the contact, returning 1 if it existed and 0 otherwise.</summary>
    Task<int> DeleteAsync(Contact contact);

    /// <summary>Deletes multiple contacts in a single batch, returning the number actually removed.</summary>
    Task<int> DeleteManyAsync(IEnumerable<Contact> contacts);

    /// <summary>Case-insensitive match on first name, last name or email; an empty query returns everything.</summary>
    Task<List<Contact>> SearchAsync(string query);
}

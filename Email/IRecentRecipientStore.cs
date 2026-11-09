namespace CORA.Data.Email;

/// <summary>An address the user sent to that wasn't in Contacts at the time.</summary>
public sealed record RecentRecipient(string Address, int UseCount, DateTime LastUsedUtc, bool DontAskToSave);

/// <summary>
/// Addresses the user typed and sent to that weren't contacts (Joe 2026-11-09). Compose offers them
/// as "Used before" suggestions, and the second time one is used goCORA offers to save it to
/// Contacts. Local and encrypted like every store; subscribers' cloud sync carries it inside the
/// "lists" kind. Deliberately not in backups.
/// </summary>
public interface IRecentRecipientStore
{
    /// <summary>
    /// Records one successful send to each address (normalized; duplicates count once) and returns
    /// those used at least twice that the user hasn't said "Don't ask again" for. The caller leaves
    /// out addresses that are already contacts.
    /// </summary>
    Task<List<string>> RecordSentAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default);

    /// <summary>Addresses containing <paramref name="text"/>, most recently used first.</summary>
    Task<List<RecentRecipient>> SearchAsync(string text, int max, CancellationToken cancellationToken = default);

    /// <summary>Every remembered address.</summary>
    Task<List<RecentRecipient>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops offering to save these addresses to Contacts (they stay as suggestions).</summary>
    Task SetDontAskToSaveAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default);

    /// <summary>Forgets an address: it is no longer suggested.</summary>
    Task RemoveAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// From sync: the address exists, used at least <paramref name="minUseCount"/> times, with the
    /// given "Don't ask again" choice. A local use count that is higher is kept.
    /// </summary>
    Task MergeFromSyncAsync(string address, int minUseCount, bool dontAskToSave, CancellationToken cancellationToken = default);
}

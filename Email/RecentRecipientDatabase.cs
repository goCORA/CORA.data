using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IRecentRecipientStore"/>, encrypted at rest via
/// <see cref="EncryptedLiteDbFile"/> like the other stores.
/// </summary>
public class RecentRecipientDatabase : IRecentRecipientStore, IFlushableStore, IDisposable
{
    private sealed class RecipientDoc
    {
        [BsonId]
        public string Address { get; set; } = string.Empty;
        public int UseCount { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public bool DontAskToSave { get; set; }
    }

    /// <summary>Kept small: the oldest addresses are forgotten past this many.</summary>
    private const int MaxEntries = 500;

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<RecipientDoc> _recipients;

    /// <summary>
    /// Raised after a change that sync carries to other devices: a new or forgotten address, the
    /// second use, or a "Don't ask again". Not for every send, so sends don't each start an upload.
    /// </summary>
    internal Action? Changed { get; set; }

    public RecentRecipientDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _recipients = _file.Database.GetCollection<RecipientDoc>("RecentRecipients");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static RecentRecipient ToRecord(RecipientDoc d) => new(d.Address, d.UseCount, d.LastUsedUtc, d.DontAskToSave);

    private static IEnumerable<string> Normalize(IEnumerable<string> addresses) =>
        addresses.Select(SecureMessageFormat.NormalizeAddress).Where(a => a.Length > 0).Distinct();

    public Task<List<string>> RecordSentAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var now = DateTime.UtcNow;
            var repeats = new List<string>();
            var syncable = false;
            foreach (var address in Normalize(addresses))
            {
                var doc = _recipients.FindById(address) ?? new RecipientDoc { Address = address };
                doc.UseCount++;
                doc.LastUsedUtc = now;
                _recipients.Upsert(doc);
                syncable |= doc.UseCount <= 2;   // new, or used again for the first time
                if (doc.UseCount >= 2 && !doc.DontAskToSave)
                    repeats.Add(address);
            }
            syncable |= Trim();
            if (syncable)
                Changed?.Invoke();
            return repeats;
        }, cancellationToken);

    // Forgets the least recently used addresses past MaxEntries. Returns whether any went.
    private bool Trim()
    {
        var extra = _recipients.Count() - MaxEntries;
        if (extra <= 0)
            return false;
        foreach (var old in _recipients.FindAll().OrderBy(d => d.LastUsedUtc).Take(extra).ToList())
            _recipients.Delete(old.Address);
        return true;
    }

    public Task<List<RecentRecipient>> SearchAsync(string text, int max, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var token = text.Trim();
            if (token.Length == 0 || max <= 0)
                return [];
            return _recipients.FindAll()
                .Where(d => d.Address.Contains(token, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastUsedUtc)
                .Take(max)
                .Select(ToRecord)
                .ToList();
        }, cancellationToken);

    public Task<List<RecentRecipient>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _recipients.FindAll().Select(ToRecord).ToList(), cancellationToken);

    public Task SetDontAskToSaveAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var changed = false;
            foreach (var address in Normalize(addresses))
            {
                if (_recipients.FindById(address) is not { DontAskToSave: false } doc)
                    continue;
                doc.DontAskToSave = true;
                _recipients.Update(doc);
                changed = true;
            }
            if (changed)
                Changed?.Invoke();
        }, cancellationToken);

    public Task RemoveAsync(string address, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (_recipients.Delete(SecureMessageFormat.NormalizeAddress(address)))
                Changed?.Invoke();
        }, cancellationToken);

    public Task MergeFromSyncAsync(string address, int minUseCount, bool dontAskToSave, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var key = SecureMessageFormat.NormalizeAddress(address);
            if (key.Length == 0)
                return;
            // An address that only came from sync sorts after the ones used on this device.
            var doc = _recipients.FindById(key) ?? new RecipientDoc { Address = key, LastUsedUtc = DateTime.MinValue };
            doc.UseCount = Math.Max(doc.UseCount, minUseCount);
            doc.DontAskToSave = dontAskToSave;
            _recipients.Upsert(doc);
        }, cancellationToken);
}

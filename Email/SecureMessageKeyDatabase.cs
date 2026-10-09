using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="ISecureMessageKeyStore"/>, following the same pattern as
/// <see cref="BlacklistDatabase"/>. The backing file is encrypted at rest with the master key via
/// <see cref="EncryptedLiteDbFile"/>, like every other store.
/// </summary>
public class SecureMessageKeyDatabase : ISecureMessageKeyStore, IFlushableStore, IDisposable
{
    private sealed class SecureMessageKeyDoc
    {
        [BsonId]
        public string KeyId { get; set; } = string.Empty;
        public string SenderAddress { get; set; } = string.Empty;
        public List<string> Recipients { get; set; } = new();
        public byte[] KeyPackage { get; set; } = [];
        public DateTime CreatedUtc { get; set; }
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<SecureMessageKeyDoc> _keys;

    public SecureMessageKeyDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _keys = _file.Database.GetCollection<SecureMessageKeyDoc>("SecureMessageKeys");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static SecureMessageKeyRecord ToRecord(SecureMessageKeyDoc d) => new()
    {
        KeyId = d.KeyId,
        SenderAddress = d.SenderAddress,
        Recipients = d.Recipients.ToList(),
        KeyPackage = d.KeyPackage.ToArray(),
        CreatedUtc = d.CreatedUtc,
    };

    public Task AddAsync(SecureMessageKeyRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.KeyId);
        if (record.KeyPackage.Length == 0)
            throw new ArgumentException("A key package is required.", nameof(record));

        return Task.Run(() =>
        {
            _keys.Upsert(new SecureMessageKeyDoc
            {
                KeyId = record.KeyId,
                SenderAddress = SecureMessageFormat.NormalizeAddress(record.SenderAddress),
                Recipients = record.Recipients.Select(SecureMessageFormat.NormalizeAddress)
                    .Where(a => a.Length > 0).Distinct().ToList(),
                KeyPackage = record.KeyPackage.ToArray(),
                CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc,
            });
        }, cancellationToken);
    }

    public Task<SecureMessageKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.Run<SecureMessageKeyRecord?>(() => _keys.FindById(keyId) is { } d ? ToRecord(d) : null, cancellationToken);

    public Task<IReadOnlyList<SecureMessageKeyRecord>> FindByRecipientAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var address = SecureMessageFormat.NormalizeAddress(emailAddress);
        return Task.Run(IReadOnlyList<SecureMessageKeyRecord> () => _keys.FindAll()
            .Where(d => d.Recipients.Contains(address))
            .OrderByDescending(d => d.CreatedUtc)
            .Select(ToRecord)
            .ToList(), cancellationToken);
    }

    public Task RemoveAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.Run(() => _keys.Delete(keyId), cancellationToken);
}

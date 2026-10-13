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
        public string Subject { get; set; } = string.Empty;
        public byte[] KeyPackage { get; set; } = [];
        public DateTime CreatedUtc { get; set; }
        public bool Received { get; set; }
    }

    private sealed class KeyRequestDoc
    {
        [BsonId]
        public string KeyId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string SenderAddress { get; set; } = string.Empty;
        public byte[] ProtectedPrivate { get; set; } = [];
        public string Status { get; set; } = "pending";
        public byte[]? SealedKey { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class KeyShareDoc
    {
        [BsonId]
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string RequestId { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public List<string> RequesterAddresses { get; set; } = new();
        public bool Shared { get; set; }
        public DateTime AtUtc { get; set; }
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<SecureMessageKeyDoc> _keys;
    private readonly ILiteCollection<KeyRequestDoc> _requests;
    private readonly ILiteCollection<KeyShareDoc> _shares;

    public SecureMessageKeyDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _keys = _file.Database.GetCollection<SecureMessageKeyDoc>("SecureMessageKeys");
        _requests = _file.Database.GetCollection<KeyRequestDoc>("KeyRequests");
        _shares = _file.Database.GetCollection<KeyShareDoc>("KeysShared");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static SecureMessageKeyRecord ToRecord(SecureMessageKeyDoc d) => new()
    {
        KeyId = d.KeyId,
        SenderAddress = d.SenderAddress,
        Recipients = d.Recipients.ToList(),
        Subject = d.Subject ?? string.Empty,
        KeyPackage = d.KeyPackage.ToArray(),
        CreatedUtc = d.CreatedUtc,
        Received = d.Received,
    };

    private static KeyRequestRecord ToRequest(KeyRequestDoc d) => new()
    {
        KeyId = d.KeyId,
        RequestId = d.RequestId,
        SenderId = d.SenderId,
        SenderAddress = d.SenderAddress,
        ProtectedPrivate = d.ProtectedPrivate.ToArray(),
        Status = d.Status,
        SealedKey = d.SealedKey?.ToArray(),
        CreatedUtc = d.CreatedUtc,
        ExpiresUtc = d.ExpiresUtc,
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
                Subject = record.Subject ?? string.Empty,
                KeyPackage = record.KeyPackage.ToArray(),
                CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc,
                Received = record.Received,
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

    public Task<IReadOnlyList<SecureMessageKeyRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run(IReadOnlyList<SecureMessageKeyRecord> () => _keys.FindAll()
            .OrderByDescending(d => d.CreatedUtc)
            .Select(ToRecord)
            .ToList(), cancellationToken);

    public Task<bool> UpdateKeyPackageAsync(string keyId, byte[] keyPackage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(keyPackage);
        if (keyPackage.Length == 0)
            throw new ArgumentException("A key package is required.", nameof(keyPackage));

        return Task.Run(() =>
        {
            if (_keys.FindById(keyId) is not { } doc)
                return false;
            doc.KeyPackage = keyPackage.ToArray();
            return _keys.Update(doc);
        }, cancellationToken);
    }

    public Task RemoveAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.Run(() => _keys.Delete(keyId), cancellationToken);

    public Task SaveRequestAsync(KeyRequestRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.KeyId);
        return Task.Run(() =>
        {
            _requests.Upsert(new KeyRequestDoc
            {
                KeyId = request.KeyId,
                RequestId = request.RequestId,
                SenderId = request.SenderId,
                SenderAddress = SecureMessageFormat.NormalizeAddress(request.SenderAddress),
                ProtectedPrivate = request.ProtectedPrivate.ToArray(),
                Status = request.Status,
                SealedKey = request.SealedKey?.ToArray(),
                CreatedUtc = request.CreatedUtc == default ? DateTime.UtcNow : request.CreatedUtc,
                ExpiresUtc = request.ExpiresUtc,
            });
        }, cancellationToken);
    }

    public Task<KeyRequestRecord?> GetRequestAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.Run<KeyRequestRecord?>(() => _requests.FindById(keyId) is { } d ? ToRequest(d) : null, cancellationToken);

    public Task<IReadOnlyList<KeyRequestRecord>> GetRequestsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(IReadOnlyList<KeyRequestRecord> () => _requests.FindAll()
            .OrderByDescending(d => d.CreatedUtc)
            .Select(ToRequest)
            .ToList(), cancellationToken);

    public Task RemoveRequestAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.Run(() => _requests.Delete(keyId), cancellationToken);

    public Task AddShareAsync(KeyShareRecord share, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(share);
        return Task.Run(() =>
        {
            _shares.Insert(new KeyShareDoc
            {
                RequestId = share.RequestId,
                KeyId = share.KeyId,
                Subject = share.Subject,
                RequesterAddresses = share.RequesterAddresses.ToList(),
                Shared = share.Shared,
                AtUtc = share.AtUtc == default ? DateTime.UtcNow : share.AtUtc,
            });
        }, cancellationToken);
    }

    public Task<IReadOnlyList<KeyShareRecord>> GetSharesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(IReadOnlyList<KeyShareRecord> () => _shares.FindAll()
            .OrderByDescending(d => d.AtUtc)
            .Select(d => new KeyShareRecord
            {
                RequestId = d.RequestId,
                KeyId = d.KeyId,
                Subject = d.Subject,
                RequesterAddresses = d.RequesterAddresses.ToList(),
                Shared = d.Shared,
                AtUtc = d.AtUtc,
            })
            .ToList(), cancellationToken);

    public Task<int> RemoveSharesOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
        Task.Run(() => _shares.DeleteMany(d => d.AtUtc < cutoffUtc), cancellationToken);

    public Task ClearSharesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _shares.DeleteAll(), cancellationToken);
}

using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IAiAutonomyStore"/> implementation, following the same pattern as
/// <see cref="BlacklistDatabase"/>/<see cref="TrustedImageSenderDatabase"/>. The backing file
/// is encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class AiAutonomyDatabase : IAiAutonomyStore, IFlushableStore, IDisposable
{
    private sealed class AccountAutonomyDoc
    {
        [BsonId]
        public string AccountKey { get; set; } = string.Empty;
        public AutonomyTier Tier { get; set; } = AutonomyTier.ScopedAutonomy;
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<AccountAutonomyDoc> _accounts;

    public AiAutonomyDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _accounts = _file.Database.GetCollection<AccountAutonomyDoc>("AccountAutonomy");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    public Task<AutonomyTier> GetTierAsync(string accountKey, CancellationToken cancellationToken = default) =>
        Task.Run(() => _accounts.FindById(accountKey)?.Tier ?? AutonomyTier.ScopedAutonomy, cancellationToken);

    public Task SetTierAsync(string accountKey, AutonomyTier tier, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _accounts.Upsert(new AccountAutonomyDoc { AccountKey = accountKey, Tier = tier });
        }, cancellationToken);
}

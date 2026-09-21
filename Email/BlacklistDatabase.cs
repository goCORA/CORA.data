using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="IBlacklistStore"/> implementation, following the same pattern as
/// <see cref="TrustedImageSenderDatabase"/>. Singleton — the database file/collection is
/// created on first use. The backing file is encrypted at rest via
/// <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class BlacklistDatabase : IBlacklistStore, IFlushableStore, IDisposable
{
    private sealed class BlacklistedSenderDoc
    {
        [BsonId]
        public string SenderEmail { get; set; } = string.Empty;
    }

    private sealed class BlacklistedDomainDoc
    {
        [BsonId]
        public string Domain { get; set; } = string.Empty;
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<BlacklistedSenderDoc> _senders;
    private readonly ILiteCollection<BlacklistedDomainDoc> _domains;

    public BlacklistDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _senders = _file.Database.GetCollection<BlacklistedSenderDoc>("BlacklistedSenders");
        _domains = _file.Database.GetCollection<BlacklistedDomainDoc>("BlacklistedDomains");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static string NormalizeSender(string senderEmail)
    {
        // From headers are often "Display Name <address@example.com>"; key strictly off
        // the address itself so different display names for the same sender still match.
        var match = System.Text.RegularExpressions.Regex.Match(senderEmail, "<([^>]+)>");
        var address = match.Success ? match.Groups[1].Value : senderEmail;
        return address.Trim().ToLowerInvariant();
    }

    private static string NormalizeDomain(string domain) => domain.Trim().ToLowerInvariant();

    /// <summary>Extracts the domain (portion after '@') from a raw sender/email string, or empty if none.</summary>
    public static string ExtractDomain(string senderEmailOrAddress)
    {
        var address = NormalizeSender(senderEmailOrAddress);
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : string.Empty;
    }

    public Task<bool> IsSenderBlacklistedAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        Task.Run(() => _senders.FindById(NormalizeSender(senderEmail)) is not null, cancellationToken);

    public Task<bool> IsDomainBlacklistedAsync(string domain, CancellationToken cancellationToken = default) =>
        Task.Run(() => _domains.FindById(NormalizeDomain(domain)) is not null, cancellationToken);

    public Task BlacklistSenderAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _senders.Upsert(new BlacklistedSenderDoc { SenderEmail = NormalizeSender(senderEmail) });
        }, cancellationToken);

    public Task BlacklistDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _domains.Upsert(new BlacklistedDomainDoc { Domain = NormalizeDomain(domain) });
        }, cancellationToken);

    public Task<IReadOnlyList<string>> GetBlacklistedSendersAsync(CancellationToken cancellationToken = default) =>
        Task.Run(IReadOnlyList<string> () => _senders.FindAll().Select(d => d.SenderEmail).ToList(), cancellationToken);

    public Task<IReadOnlyList<string>> GetBlacklistedDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(IReadOnlyList<string> () => _domains.FindAll().Select(d => d.Domain).ToList(), cancellationToken);

    public Task RemoveSenderAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        Task.Run(() => _senders.Delete(NormalizeSender(senderEmail)), cancellationToken);

    public Task RemoveDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        Task.Run(() => _domains.Delete(NormalizeDomain(domain)), cancellationToken);
}

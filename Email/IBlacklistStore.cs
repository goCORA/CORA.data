namespace CORA.Data.Email;

/// <summary>
/// Local persistence for sender/domain "blacklist" entries. When a sender email or domain
/// is blacklisted, newly synced mail from that sender/domain is automatically routed to
/// Junk (see the sync-time enforcement in <see cref="MailKitEmailService"/>). Purely local
/// state, global across accounts, following the same pattern as
/// <see cref="ITrustedImageSenderStore"/>.
/// </summary>
public interface IBlacklistStore
{
    /// <summary>Returns whether the given sender email address is blacklisted.</summary>
    Task<bool> IsSenderBlacklistedAsync(string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>Returns whether the given domain (e.g. "example.com") is blacklisted.</summary>
    Task<bool> IsDomainBlacklistedAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Adds (or re-affirms) the given sender email address to the blacklist.</summary>
    Task BlacklistSenderAsync(string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>Adds (or re-affirms) the given domain to the blacklist.</summary>
    Task BlacklistDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Returns every currently blacklisted sender email address.</summary>
    Task<IReadOnlyList<string>> GetBlacklistedSendersAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every currently blacklisted domain.</summary>
    Task<IReadOnlyList<string>> GetBlacklistedDomainsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the given sender email address from the blacklist, if present.</summary>
    Task RemoveSenderAsync(string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>Removes the given domain from the blacklist, if present.</summary>
    Task RemoveDomainAsync(string domain, CancellationToken cancellationToken = default);
}

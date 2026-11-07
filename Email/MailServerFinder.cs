using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace CORA.Data.Email;

/// <summary>Providers goCORA has its own sign-in for (the Add account picker pre-selects them).</summary>
public enum MailProviderKind
{
    Unknown,
    Gmail,              // app password
    MicrosoftPersonal,  // Hotmail / Outlook.com: Sign in with Microsoft (consumers)
    ICloud,             // app-specific password
    Yahoo,              // app password
}

/// <summary>Recognizes the big providers from an address, on the device only: nothing is looked up online.</summary>
public static class MailProviders
{
    private static readonly Dictionary<string, MailProviderKind> KnownDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = MailProviderKind.Gmail,
        ["googlemail.com"] = MailProviderKind.Gmail,

        ["outlook.com"] = MailProviderKind.MicrosoftPersonal,
        ["hotmail.com"] = MailProviderKind.MicrosoftPersonal,
        ["live.com"] = MailProviderKind.MicrosoftPersonal,
        ["msn.com"] = MailProviderKind.MicrosoftPersonal,
        ["passport.com"] = MailProviderKind.MicrosoftPersonal,
        ["outlook.fr"] = MailProviderKind.MicrosoftPersonal,
        ["hotmail.fr"] = MailProviderKind.MicrosoftPersonal,
        ["live.fr"] = MailProviderKind.MicrosoftPersonal,
        ["live.ca"] = MailProviderKind.MicrosoftPersonal,
        ["hotmail.ca"] = MailProviderKind.MicrosoftPersonal,
        ["hotmail.co.uk"] = MailProviderKind.MicrosoftPersonal,
        ["live.co.uk"] = MailProviderKind.MicrosoftPersonal,
        ["outlook.es"] = MailProviderKind.MicrosoftPersonal,
        ["hotmail.es"] = MailProviderKind.MicrosoftPersonal,

        ["icloud.com"] = MailProviderKind.ICloud,
        ["me.com"] = MailProviderKind.ICloud,
        ["mac.com"] = MailProviderKind.ICloud,

        ["yahoo.com"] = MailProviderKind.Yahoo,
        ["ymail.com"] = MailProviderKind.Yahoo,
        ["rocketmail.com"] = MailProviderKind.Yahoo,
        ["yahoo.ca"] = MailProviderKind.Yahoo,
        ["yahoo.fr"] = MailProviderKind.Yahoo,
        ["yahoo.es"] = MailProviderKind.Yahoo,
        ["yahoo.co.uk"] = MailProviderKind.Yahoo,
    };

    /// <summary>The provider of a well-known address (gmail.com, hotmail.com, icloud.com, yahoo.com, …); Unknown otherwise.</summary>
    public static MailProviderKind FromAddress(string? emailAddress) =>
        DomainOf(emailAddress) is { } domain && KnownDomains.TryGetValue(domain, out var provider)
            ? provider
            : MailProviderKind.Unknown;

    /// <summary>The address's domain in lower-case ASCII (IDN converted), or null when it isn't an address.</summary>
    public static string? DomainOf(string? emailAddress)
    {
        var address = emailAddress?.Trim() ?? string.Empty;
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1 || address.Contains(' '))
            return null;
        try
        {
            var domain = new IdnMapping().GetAscii(address[(at + 1)..].TrimEnd('.')).ToLowerInvariant();
            return IsHostName(domain) && domain.Contains('.') ? domain : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A server name the user typed, cleaned up (trimmed, lower-case, trailing dot removed), or null when it isn't one.</summary>
    public static string? NormalizeHost(string? host)
    {
        var value = host?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        return IsHostName(value) && value.Contains('.') ? value : null;
    }

    internal static bool IsHostName(string host) =>
        host.Length is > 0 and <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns;
}

/// <summary>A mail server goCORA can reach. <see cref="MailSecurity.None"/> is never used here.</summary>
public sealed record MailServerEndpoint(string Host, int Port, MailSecurity Security);

/// <summary>Checks that a server answers with an encrypted connection and a valid certificate. Never signs in.</summary>
public interface IMailServerProbe
{
    Task<bool> CanConnectImapAsync(MailServerEndpoint server, CancellationToken cancellationToken);

    /// <param name="localDomain">The sender's domain for EHLO, so the probe never reveals the device name.</param>
    Task<bool> CanConnectSmtpAsync(MailServerEndpoint server, string localDomain, CancellationToken cancellationToken);
}

/// <summary>
/// Add account › IMAP server: the user types one server name; this finds which port and security it
/// answers on (IMAP 993 SSL, then 143 STARTTLS; SMTP 465 SSL, then 587 STARTTLS). Only the server the
/// user typed is contacted, always encrypted with the strict certificate check, and without signing in,
/// so no password is sent before the real sign-in.
/// </summary>
public sealed class MailServerFinder
{
    private readonly IMailServerProbe _probe;

    public MailServerFinder(IMailServerProbe probe) => _probe = probe;

    public Task<MailServerEndpoint?> FindImapAsync(string host, CancellationToken cancellationToken) =>
        FirstReachableAsync(
            [new(host, 993, MailSecurity.Ssl), new(host, 143, MailSecurity.Tls)],
            s => _probe.CanConnectImapAsync(s, cancellationToken), cancellationToken);

    public Task<MailServerEndpoint?> FindSmtpAsync(string host, string localDomain, CancellationToken cancellationToken) =>
        FirstReachableAsync(
            [new(host, 465, MailSecurity.Ssl), new(host, 587, MailSecurity.Tls)],
            s => _probe.CanConnectSmtpAsync(s, localDomain, cancellationToken), cancellationToken);

    // Both ports at once (an unanswered port can take seconds), keeping the preferred order.
    private static async Task<MailServerEndpoint?> FirstReachableAsync(
        IReadOnlyList<MailServerEndpoint> candidates, Func<MailServerEndpoint, Task<bool>> probe, CancellationToken cancellationToken)
    {
        var tasks = candidates.Select(c => SafeAsync(() => probe(c), cancellationToken)).ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (await tasks[i].ConfigureAwait(false))
                return candidates[i];
        }
        return null;
    }

    private static async Task<bool> SafeAsync(Func<Task<bool>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MailServerFinder] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Connects with MailKit (encrypted, strict certificate check), then disconnects. Never sends a user name or password.</summary>
public sealed class MailKitServerProbe : IMailServerProbe
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);

    public async Task<bool> CanConnectImapAsync(MailServerEndpoint server, CancellationToken cancellationToken)
    {
        using var imap = new ImapClient();
        return await TryConnectAsync(imap, server, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CanConnectSmtpAsync(MailServerEndpoint server, string localDomain, CancellationToken cancellationToken)
    {
        // EHLO with the sender's domain, not the device name (see MailKitEmailService.SenderDomain).
        using var smtp = new SmtpClient { LocalDomain = localDomain };
        return await TryConnectAsync(smtp, server, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryConnectAsync(MailService client, MailServerEndpoint server, CancellationToken cancellationToken)
    {
        var certificateCheck = new MailCertificateCheck();
        client.CheckCertificateRevocation = false;
        client.ServerCertificateValidationCallback = certificateCheck.Validate;
        client.Timeout = (int)Timeout.TotalMilliseconds;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var options = server.Security == MailSecurity.Ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(server.Host, server.Port, options, timeout.Token).ConfigureAwait(false);
            var secure = client.IsSecure;
            try
            {
                await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Connected is all that was needed.
            }
            return secure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;   // timed out
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MailServerFinder] probe {server.Host}:{server.Port} {server.Security}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

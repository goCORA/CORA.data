using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace CORA.Data.Email;

/// <summary>Why a mail server's TLS certificate was refused.</summary>
public enum MailCertificateProblem
{
    /// <summary>The certificate is valid but issued for different host name(s) than the one configured.</summary>
    NameMismatch,

    /// <summary>The certificate has expired (or is not yet valid).</summary>
    Expired,

    /// <summary>The certificate is not issued by a trusted authority (e.g. self-signed) or its chain is broken.</summary>
    NotTrusted,

    /// <summary>The server presented no certificate, or the failure could not be classified.</summary>
    Other,
}

/// <summary>
/// Thrown when goCORA refuses to connect to an IMAP, POP3 or SMTP server because its TLS
/// certificate does not check out. goCORA never connects over an unverified connection (a
/// wrong or forged certificate would let someone on the network read the password and mail),
/// so this carries what the UI needs to tell the user how to fix it: the host they configured,
/// the names the certificate is actually issued for (usually the provider's "secure server"
/// name, e.g. *.site4now.net), and the account to open for editing.
/// <see cref="Exception.Message"/> is a complete English explanation; CORA.App shows a
/// localized version built from the properties.
/// </summary>
public sealed class MailServerCertificateException : Exception
{
    public MailServerCertificateException(
        string protocol, string host, int port, string accountEmail,
        MailCertificateProblem problem, IReadOnlyList<string> certificateNames,
        DateTime? expiresUtc, Exception? innerException)
        : base(BuildMessage(protocol, host, problem, certificateNames, expiresUtc), innerException)
    {
        Protocol = protocol;
        Host = host;
        Port = port;
        AccountEmail = accountEmail;
        Problem = problem;
        CertificateNames = certificateNames;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>"IMAP", "POP3" or "SMTP".</summary>
    public string Protocol { get; }

    /// <summary>The server name as configured in the account.</summary>
    public string Host { get; }

    public int Port { get; }

    /// <summary>The account's email address, so the UI can open that account for editing.</summary>
    public string AccountEmail { get; }

    public MailCertificateProblem Problem { get; }

    /// <summary>Host names the certificate is issued for (subject alternative names, else the common name).</summary>
    public IReadOnlyList<string> CertificateNames { get; }

    /// <summary>The certificate's expiry, when known.</summary>
    public DateTime? ExpiresUtc { get; }

    /// <summary>The certificate names joined for display, e.g. "*.site4now.net, site4now.net".</summary>
    public string CertificateNamesText =>
        CertificateNames.Count == 0 ? "(unknown)" : string.Join(", ", CertificateNames);

    private static string BuildMessage(
        string protocol, string host, MailCertificateProblem problem,
        IReadOnlyList<string> names, DateTime? expiresUtc) => problem switch
    {
        MailCertificateProblem.NameMismatch =>
            $"Can't connect securely to {host} ({protocol}): the server's security certificate is issued for " +
            $"{(names.Count == 0 ? "a different name" : string.Join(", ", names))}, not for {host}. " +
            "Use the secure server name your email provider lists for this account.",
        MailCertificateProblem.Expired =>
            $"Can't connect securely to {host} ({protocol}): the server's security certificate " +
            $"{(expiresUtc is { } d ? $"expired on {d:yyyy-MM-dd}" : "has expired")}. Contact your email provider.",
        MailCertificateProblem.NotTrusted =>
            $"Can't connect securely to {host} ({protocol}): the server's security certificate is not issued by a " +
            "trusted authority (it may be self-signed). goCORA only connects to servers with a valid certificate.",
        _ =>
            $"Can't connect securely to {host} ({protocol}): the server's security certificate could not be verified.",
    };
}

/// <summary>
/// Strict TLS certificate validation for one connection attempt that also remembers what the
/// server presented, so a refusal can be turned into a <see cref="MailServerCertificateException"/>
/// with a useful explanation. Only a certificate with no policy errors is accepted - exactly the
/// platform's normal check, with no "accept anyway" path.
/// </summary>
internal sealed class MailCertificateCheck
{
    public X509Certificate2? Certificate { get; private set; }
    public SslPolicyErrors Errors { get; private set; }
    public bool WasInvoked { get; private set; }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        WasInvoked = true;
        Errors = errors;
        try
        {
            Certificate = certificate switch
            {
                null => null,
                X509Certificate2 c2 => c2,
                _ => X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()),
            };
        }
        catch
        {
            Certificate = null;
        }

        return errors == SslPolicyErrors.None;
    }

    /// <summary>
    /// If this check refused the server's certificate, returns the exception to throw in place
    /// of MailKit's technical SslHandshakeException; otherwise null.
    /// </summary>
    public MailServerCertificateException? ToException(
        string protocol, string host, int port, string accountEmail, Exception inner)
    {
        if (!WasInvoked || Errors == SslPolicyErrors.None)
            return null;

        var names = GetNames(Certificate);
        var expires = Certificate?.NotAfter.ToUniversalTime();
        var problem =
            Errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ? MailCertificateProblem.Other
            : Certificate is not null && (Certificate.NotAfter < DateTime.Now || Certificate.NotBefore > DateTime.Now)
                ? MailCertificateProblem.Expired
            : Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? MailCertificateProblem.NotTrusted
            : Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? MailCertificateProblem.NameMismatch
            : MailCertificateProblem.Other;

        return new MailServerCertificateException(protocol, host, port, accountEmail, problem, names, expires, inner);
    }

    private static IReadOnlyList<string> GetNames(X509Certificate2? certificate)
    {
        if (certificate is null)
            return Array.Empty<string>();

        var names = new List<string>();
        try
        {
            foreach (var extension in certificate.Extensions)
            {
                if (extension is X509SubjectAlternativeNameExtension san)
                    names.AddRange(san.EnumerateDnsNames());
            }
        }
        catch
        {
            // Fall back to the common name below.
        }

        if (names.Count == 0)
        {
            var cn = certificate.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrWhiteSpace(cn))
                names.Add(cn);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }
}

namespace CORA.Data.Email;

/// <summary>How the account authenticates to the mail servers.</summary>
public enum AuthMethod
{
    Password,
    OAuth,
}

/// <summary>Incoming mail protocol for reading messages.</summary>
public enum IncomingProtocol
{
    Imap,
    Pop3,
}

/// <summary>Encryption/security mode for a mail server connection.</summary>
public enum MailSecurity
{
    /// <summary>SSL/TLS on connect (implicit TLS, e.g. port 993/995/465).</summary>
    Ssl,
    /// <summary>STARTTLS upgrade after connecting (explicit TLS, e.g. port 587/143/110).</summary>
    Tls,
    /// <summary>No encryption. Use only on trusted private networks.</summary>
    None,
}

/// <summary>
/// Connection settings and credentials for a single email account.
/// </summary>
public class MailAccount
{
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Protocol used to read incoming mail (IMAP or POP3).</summary>
    public IncomingProtocol IncomingProtocol { get; set; } = IncomingProtocol.Imap;

    // Incoming server — used for both IMAP and POP3.
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public MailSecurity ImapSecurity { get; set; } = MailSecurity.Ssl;

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public MailSecurity SmtpSecurity { get; set; } = MailSecurity.Tls;

    /// <summary>Login user name. Falls back to <see cref="EmailAddress"/> when empty.</summary>
    public string UserName { get; set; } = string.Empty;

    public string EffectiveUserName =>
        string.IsNullOrWhiteSpace(UserName) ? EmailAddress : UserName;

    // --- OAuth ---

    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>Friendly provider identifier (e.g. "Google", "Microsoft").</summary>
    public string OAuthProvider { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresUtc { get; set; }

    /// <summary>Token endpoint used to refresh the access token.</summary>
    public string OAuthTokenEndpoint { get; set; } = string.Empty;
    public string OAuthClientId { get; set; } = string.Empty;

    /// <summary>Optional; only web-type client registrations require a secret.</summary>
    public string OAuthClientSecret { get; set; } = string.Empty;

    public bool IsOAuth => AuthMethod == AuthMethod.OAuth;
    public bool IsPop3  => IncomingProtocol == IncomingProtocol.Pop3;
}

using MailKit;
using MailKit.Security;

namespace CORA.Data.Email;

/// <summary>
/// App-wide policy for mail server connections. Set by CORA.App from the Settings switch
/// "Refuse unencrypted connections" (on by default) at startup and whenever it changes.
/// </summary>
public static class MailConnectionPolicy
{
    /// <summary>
    /// When true (the default), goCORA never talks to a mail server over an unencrypted
    /// connection: "no security" and "encrypt if available" are upgraded to "encryption
    /// required", and a connection that still ends up unencrypted is refused with a
    /// <see cref="MailUnencryptedConnectionException"/>. When false, connections behave as
    /// configured (including plain text), which exposes the password and mail on the network.
    /// </summary>
    public static bool RequireEncryption { get; set; } = true;

    /// <summary>
    /// The socket options to actually use: unchanged when encryption isn't required; otherwise
    /// any option that could end up unencrypted becomes STARTTLS-required (implicit SSL stays).
    /// </summary>
    internal static SecureSocketOptions Apply(SecureSocketOptions requested) =>
        !RequireEncryption
            ? requested
            : requested switch
            {
                SecureSocketOptions.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SecureSocketOptions.StartTls => SecureSocketOptions.StartTls,
                // None, Auto and StartTlsWhenAvailable can all end up in plain text (Auto and
                // WhenAvailable silently fall back if the server - or someone in the middle -
                // doesn't offer STARTTLS). Require it instead.
                _ => SecureSocketOptions.StartTls,
            };

    /// <summary>
    /// If encryption is required, <paramref name="exception"/> shows the server doesn't offer
    /// encryption (MailKit reports a missing STARTTLS as NotSupportedException), returns the
    /// exception to throw instead; otherwise null.
    /// </summary>
    internal static MailUnencryptedConnectionException? ToException(
        Exception exception, string protocol, string host, int port, string accountEmail) =>
        RequireEncryption && exception is NotSupportedException
            ? new MailUnencryptedConnectionException(protocol, host, port, accountEmail, exception)
            : null;

    /// <summary>
    /// Final safety net after connecting: when encryption is required and the connection is
    /// not secure, closes it and throws before any password is sent.
    /// </summary>
    internal static void EnsureSecure(
        IMailService client, string protocol, string host, int port, string accountEmail)
    {
        if (!RequireEncryption || client.IsSecure)
            return;

        try
        {
            client.Disconnect(false);
        }
        catch
        {
            // Closing is best-effort; the important part is not authenticating.
        }

        throw new MailUnencryptedConnectionException(protocol, host, port, accountEmail, null);
    }
}

/// <summary>
/// Thrown when "Refuse unencrypted connections" is on and a mail server can't be reached over
/// an encrypted connection. <see cref="Exception.Message"/> is a complete English explanation;
/// CORA.App shows a localized version (with an "Edit account" button) built from the properties.
/// </summary>
public sealed class MailUnencryptedConnectionException : Exception
{
    public MailUnencryptedConnectionException(
        string protocol, string host, int port, string accountEmail, Exception? innerException)
        : base($"Can't connect to {host} ({protocol}, port {port}) securely: the server didn't offer an " +
               "encrypted connection, and goCORA's \"Refuse unencrypted connections\" setting is on. " +
               "Check the account's port and security settings (for example SSL on 993/995/465, or " +
               "STARTTLS on 143/110/587).", innerException)
    {
        Protocol = protocol;
        Host = host;
        Port = port;
        AccountEmail = accountEmail;
    }

    /// <summary>"IMAP", "POP3" or "SMTP".</summary>
    public string Protocol { get; }

    public string Host { get; }

    public int Port { get; }

    /// <summary>The account's email address, so the UI can open that account for editing.</summary>
    public string AccountEmail { get; }
}

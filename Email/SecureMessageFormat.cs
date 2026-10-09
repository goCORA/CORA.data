using System.Text;
using MimeKit;

namespace CORA.Data.Email;

/// <summary>
/// The text layout of a secure (CORA-encrypted) email body, as agreed for goCORA:
/// <code>
/// This is an encrypted email that requires the goCORA app to decrypt.   (notice, readable anywhere)
/// -----------------------------------------------------------------------------
///
/// -----BEGIN CORA MESSAGE-----
/// Key-Id: 3f2a...
///
/// (encrypted body, Base64, 76 characters per line)
/// -----END CORA MESSAGE-----
/// </code>
/// goCORA finds the encrypted part by the BEGIN/END markers only, so the notice and separator
/// lines can be reworded later without breaking messages already sent.
/// </summary>
public static class SecureMessageFormat
{
    // Wording to be finalized (Joe); only the markers below must never change.
    public const string NoticeLine = "This is an encrypted email that requires the goCORA app to decrypt.";
    public const string SeparatorLine = "-----------------------------------------------------------------------------";

    public const string BeginMarker = "-----BEGIN CORA MESSAGE-----";
    public const string EndMarker = "-----END CORA MESSAGE-----";
    public const string KeyIdPrefix = "Key-Id: ";
    public const int LineLength = 76;

    /// <summary>A new random key id (32 hex characters).</summary>
    public static string NewKeyId() => Guid.NewGuid().ToString("N");

    /// <summary>Builds the plain-text email body for an encrypted payload.</summary>
    public static string Build(string keyId, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var base64 = Convert.ToBase64String(payload);

        var sb = new StringBuilder(base64.Length + base64.Length / LineLength * 2 + 512);
        sb.Append(NoticeLine).Append("\r\n");
        sb.Append(SeparatorLine).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(BeginMarker).Append("\r\n");
        sb.Append(KeyIdPrefix).Append(keyId).Append("\r\n");
        sb.Append("\r\n");
        for (var i = 0; i < base64.Length; i += LineLength)
            sb.Append(base64, i, Math.Min(LineLength, base64.Length - i)).Append("\r\n");
        sb.Append(EndMarker).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Finds the encrypted part of a received/sent body. Returns false if the body has no complete
    /// BEGIN/END block, no Key-Id line, or the Base64 is damaged.
    /// </summary>
    public static bool TryParse(string? body, out string keyId, out byte[] payload)
    {
        keyId = string.Empty;
        payload = [];
        if (string.IsNullOrEmpty(body))
            return false;

        var begin = body.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
            return false;
        var end = body.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);
        if (end < 0)
            return false;

        var inner = body.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length);
        var base64 = new StringBuilder(inner.Length);
        foreach (var raw in inner.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith(KeyIdPrefix, StringComparison.Ordinal))
                keyId = line[KeyIdPrefix.Length..].Trim();
            else
                base64.Append(line);
        }

        if (keyId.Length == 0 || base64.Length == 0)
            return false;
        try
        {
            payload = Convert.FromBase64String(base64.ToString());
            return true;
        }
        catch (FormatException)
        {
            payload = [];
            return false;
        }
    }

    /// <summary>"Anna &lt;Anna@Example.com&gt;" or " anna@example.com " becomes "anna@example.com".</summary>
    public static string NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;
        var text = address.Trim();
        if (MailboxAddress.TryParse(text, out var mailbox))
            text = mailbox.Address;
        return text.Trim().ToLowerInvariant();
    }

    /// <summary>Splits a comma/semicolon separated recipient list into normalized addresses (no duplicates).</summary>
    public static List<string> SplitAddresses(string? recipients) =>
        (recipients ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeAddress)
            .Where(a => a.Length > 0)
            .Distinct()
            .ToList();
}

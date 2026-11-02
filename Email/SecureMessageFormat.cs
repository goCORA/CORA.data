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
/// (Base64, 76 characters per line, of: "CK" 0x01 | Key-Id (16 bytes) | encrypted body)
/// -----END CORA MESSAGE-----
/// </code>
/// Since 2026-11-02 (claude/design-secure-email-key-routes.md) the email names no sending device:
/// goCORA's key service finds it from the Key-Id. The Key-Id travels inside the Base64 block (not
/// encrypted: the recipient needs it before having any key), so a mail program shows only the notice
/// and one block. Older emails had readable <c>Key-Id:</c> / <c>Sender-Id:</c> lines; they are still read.
/// goCORA finds the encrypted part by the BEGIN/END markers only, so the notice and separator lines
/// can be reworded later without breaking messages already sent.
/// </summary>
public static class SecureMessageFormat
{
    // Wording to be finalized (Joe); only the markers below must never change.
    public const string NoticeLine = "This is an encrypted email that requires the goCORA app to decrypt.";
    public const string SeparatorLine = "-----------------------------------------------------------------------------";

    public const string BeginMarker = "-----BEGIN CORA MESSAGE-----";
    public const string EndMarker = "-----END CORA MESSAGE-----";
    public const int LineLength = 76;

    // Older emails (before 2026-11-02): readable lines inside the block.
    public const string KeyIdPrefix = "Key-Id: ";
    public const string SenderIdPrefix = "Sender-Id: ";

    // The block's binary header: "CK", version 1, then the 16-byte Key-Id.
    private static readonly byte[] BlockMagic = [(byte)'C', (byte)'K', 1];
    private const int KeyIdBytes = 16;

    /// <summary>A new random key id (32 hex characters).</summary>
    public static string NewKeyId() => Guid.NewGuid().ToString("N");

    /// <summary>Builds the plain-text email body for an encrypted payload and its Key-Id (32 hex characters).</summary>
    public static string Build(string keyId, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length != KeyIdBytes * 2)
            throw new ArgumentException("A Key-Id is 32 hex characters.", nameof(keyId));
        var keyBytes = Convert.FromHexString(keyId);

        var block = new byte[BlockMagic.Length + KeyIdBytes + payload.Length];
        BlockMagic.CopyTo(block, 0);
        keyBytes.CopyTo(block, BlockMagic.Length);
        payload.CopyTo(block.AsSpan(BlockMagic.Length + KeyIdBytes));
        var base64 = Convert.ToBase64String(block);

        var sb = new StringBuilder(base64.Length + base64.Length / LineLength * 2 + 512);
        sb.Append(NoticeLine).Append("\r\n");
        sb.Append(SeparatorLine).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(BeginMarker).Append("\r\n");
        for (var i = 0; i < base64.Length; i += LineLength)
            sb.Append(base64, i, Math.Min(LineLength, base64.Length - i)).Append("\r\n");
        sb.Append(EndMarker).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Finds the encrypted part of a received/sent body. Returns false if the body has no complete
    /// BEGIN/END block, no Key-Id, or the Base64 is damaged.
    /// </summary>
    public static bool TryParse(string? body, out string keyId, out byte[] payload) =>
        TryParse(body, out keyId, out _, out payload);

    /// <summary>
    /// As above, also returning the sending device's Sender-Id when the email names one (only
    /// emails written before 2026-11-02; empty otherwise — the key service routes by Key-Id).
    /// </summary>
    public static bool TryParse(string? body, out string keyId, out string senderId, out byte[] payload)
    {
        keyId = string.Empty;
        senderId = string.Empty;
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
            else if (line.StartsWith(SenderIdPrefix, StringComparison.Ordinal))
                senderId = line[SenderIdPrefix.Length..].Trim();
            else
                base64.Append(line);
        }
        if (base64.Length == 0)
            return false;

        byte[] block;
        try
        {
            block = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException)
        {
            keyId = senderId = string.Empty;
            return false;
        }

        if (keyId.Length > 0)
        {
            // An older email: the Key-Id was a readable line and the block is the payload alone.
            payload = block;
            return true;
        }

        if (block.Length <= BlockMagic.Length + KeyIdBytes || !block.AsSpan(0, BlockMagic.Length).SequenceEqual(BlockMagic))
            return false;
        keyId = Convert.ToHexString(block, BlockMagic.Length, KeyIdBytes).ToLowerInvariant();
        payload = block[(BlockMagic.Length + KeyIdBytes)..];
        return true;
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

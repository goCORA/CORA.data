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
/// (Base64, 76 characters per line, of: "CK" version | Key-Id (16 bytes) | encrypted body)
/// -----END CORA MESSAGE-----
/// </code>
/// Since 2026-11-02 (claude/design-secure-email-key-routes.md) the email names no sending device:
/// goCORA's key service finds it from the Key-Id. The Key-Id travels inside the Base64 block (not
/// encrypted: the recipient needs it before having any key), so a mail program shows only the notice
/// and one block. Older emails had readable <c>Key-Id:</c> / <c>Sender-Id:</c> lines; they are still read.
/// Block version 2 (Rust core 0.19.0): the body of a 64-byte message key (key package version 3), i.e.
/// [2 mup length][mup][wrapped commitment + CORA4]; version 1: a plain CORA4 body (32-byte keys, so also
/// replies in conversations started before 0.19.0). Use <see cref="BlockVersionFor"/> to pick it.
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

    // The block's binary header: "CK", the block version (1 or 2), then the 16-byte Key-Id.
    private const int BlockHeaderBytes = 3;
    private const int KeyIdBytes = 16;

    /// <summary>Plain CORA4 body (32-byte message keys).</summary>
    public const byte BlockVersion1 = 1;
    /// <summary>The Rust 0.19.0 body (64-byte message keys: mup, mupActual wrap, key commitment).</summary>
    public const byte BlockVersion2 = 2;

    /// <summary>The block version for a body encrypted with <paramref name="keyPackage"/>: 2 for a
    /// version-3 key package (64-byte key), 1 for older ones.</summary>
    public static byte BlockVersionFor(ReadOnlySpan<byte> keyPackage) =>
        keyPackage.Length > 0 && keyPackage[0] >= 3 ? BlockVersion2 : BlockVersion1;

    /// <summary>A new random key id (32 hex characters).</summary>
    public static string NewKeyId() => Guid.NewGuid().ToString("N");

    /// <summary>Builds the plain-text email body for an encrypted payload and its Key-Id (32 hex characters).
    /// <paramref name="blockVersion"/>: <see cref="BlockVersionFor"/> of the key package the payload was
    /// encrypted with.</summary>
    public static string Build(string keyId, ReadOnlySpan<byte> payload, byte blockVersion = BlockVersion1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length != KeyIdBytes * 2)
            throw new ArgumentException("A Key-Id is 32 hex characters.", nameof(keyId));
        if (blockVersion is not (BlockVersion1 or BlockVersion2))
            throw new ArgumentOutOfRangeException(nameof(blockVersion), "The block version is 1 or 2.");
        var keyBytes = Convert.FromHexString(keyId);

        var block = new byte[BlockHeaderBytes + KeyIdBytes + payload.Length];
        block[0] = (byte)'C';
        block[1] = (byte)'K';
        block[2] = blockVersion;
        keyBytes.CopyTo(block, BlockHeaderBytes);
        payload.CopyTo(block.AsSpan(BlockHeaderBytes + KeyIdBytes));
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

        if (block.Length <= BlockHeaderBytes + KeyIdBytes || block[0] != (byte)'C' || block[1] != (byte)'K'
            || block[2] is not (BlockVersion1 or BlockVersion2))
            return false;
        keyId = Convert.ToHexString(block, BlockHeaderBytes, KeyIdBytes).ToLowerInvariant();
        payload = block[(BlockHeaderBytes + KeyIdBytes)..];
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

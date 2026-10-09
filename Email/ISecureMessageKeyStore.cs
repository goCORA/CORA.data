namespace CORA.Data.Email;

/// <summary>
/// One secure (CORA-encrypted) email sent from this device: who it went to, and its message key
/// protected with the sender's ck1 (the key package). Nothing here is readable without both
/// halves of CORA's split key: the store file is under this device's master key, and the key
/// package needs the account's ck1.
/// </summary>
public sealed class SecureMessageKeyRecord
{
    /// <summary>Random id, also written into the sent email (the "Key-Id:" line) to find this record again.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The sending account's address (lower case).</summary>
    public string SenderAddress { get; set; } = string.Empty;

    /// <summary>Every To and Bcc address the message was sent to (lower case, no display names).</summary>
    public List<string> Recipients { get; set; } = new();

    /// <summary>The message key protected with ck1 (see <c>Cora.EncryptSecureMessage</c>).</summary>
    public byte[] KeyPackage { get; set; } = [];

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Local, encrypted record of the message keys of secure emails sent from this device (see
/// <see cref="SecureMessageKeyRecord"/>). Global across mail accounts, like the blacklist.
/// </summary>
public interface ISecureMessageKeyStore
{
    /// <summary>Adds (or replaces) a record.</summary>
    Task AddAsync(SecureMessageKeyRecord record, CancellationToken cancellationToken = default);

    /// <summary>The record with this key id, or null.</summary>
    Task<SecureMessageKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Every record whose recipients include <paramref name="emailAddress"/>, newest first.</summary>
    Task<IReadOnlyList<SecureMessageKeyRecord>> FindByRecipientAsync(string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes the record with this key id, if present.</summary>
    Task RemoveAsync(string keyId, CancellationToken cancellationToken = default);
}

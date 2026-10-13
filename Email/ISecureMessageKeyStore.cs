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

    /// <summary>
    /// True for a key received from the sender's goCORA (key request), false for a secure email sent
    /// from this device. Only sent keys are ever shared with requesters.
    /// </summary>
    public bool Received { get; set; }
}

/// <summary>
/// A key request this device made (recipient side): the one-time private half waits here, protected
/// by the device's master key, until the answer is opened. One per message (Key-Id).
/// </summary>
public sealed class KeyRequestRecord
{
    public string KeyId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    /// <summary>The sender's address (From of the email), lower case.</summary>
    public string SenderAddress { get; set; } = string.Empty;
    /// <summary>The one-time private key, encrypted with this device's master key.</summary>
    public byte[] ProtectedPrivate { get; set; } = [];
    /// <summary>"pending", "answered" (sealed key collected, not opened yet), "declined" or "expired".</summary>
    public string Status { get; set; } = "pending";
    /// <summary>The collected answer (93 bytes), kept until it has been opened and stored.</summary>
    public byte[]? SealedKey { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>"Keys shared" history (sender side): one entry per answered or declined request.</summary>
public sealed class KeyShareRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>The requester's verified addresses, as the key service passed them on.</summary>
    public List<string> RequesterAddresses { get; set; } = new();
    /// <summary>True: the key was shared; false: the request was declined (no requester address was a recipient).</summary>
    public bool Shared { get; set; }
    public DateTime AtUtc { get; set; }
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

    /// <summary>Every record, newest first (used to re-protect stored keys after a PIN reset).</summary>
    Task<IReadOnlyList<SecureMessageKeyRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the key package of one record (after re-protecting it under a new ck1). False if there is no such record.</summary>
    Task<bool> UpdateKeyPackageAsync(string keyId, byte[] keyPackage, CancellationToken cancellationToken = default);

    /// <summary>Removes the record with this key id, if present.</summary>
    Task RemoveAsync(string keyId, CancellationToken cancellationToken = default);

    // ---- Key requests (recipient) and the keys-shared history (sender) ----

    /// <summary>Adds or replaces the request for <see cref="KeyRequestRecord.KeyId"/>.</summary>
    Task SaveRequestAsync(KeyRequestRecord request, CancellationToken cancellationToken = default);

    /// <summary>The request for this message key, or null.</summary>
    Task<KeyRequestRecord?> GetRequestAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Every request, newest first.</summary>
    Task<IReadOnlyList<KeyRequestRecord>> GetRequestsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the request for this message key (its one-time private half goes with it).</summary>
    Task RemoveRequestAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Adds an entry to the keys-shared history.</summary>
    Task AddShareAsync(KeyShareRecord share, CancellationToken cancellationToken = default);

    /// <summary>The keys-shared history, newest first.</summary>
    Task<IReadOnlyList<KeyShareRecord>> GetSharesAsync(CancellationToken cancellationToken = default);
}

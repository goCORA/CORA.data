namespace CORA.Data.Email;

/// <summary>
/// A single previously-synced POP3 message, keyed by its UIDL, cached locally so
/// its headers do not need to be re-fetched from the server on every sync.
/// </summary>
public sealed class StoredMailSummary
{
    public string Uidl { get; set; } = string.Empty;
    public uint Uid { get; set; }
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }

    /// <summary>True if the message has been locally flagged as important.</summary>
    public bool IsImportant { get; set; }

    /// <summary>
    /// True if this row was placed here by a local move (e.g. Trash back into Inbox)
    /// rather than being synced from the server. Its Uidl may not correspond to any
    /// real server message id, so orphan-pruning during sync must skip it instead of
    /// treating it as a message the server no longer has and moving it back to Trash.
    /// </summary>
    public bool IsLocalOnly { get; set; }

    /// <summary>
    /// RFC 5322 <c>Message-Id</c> of this message, as captured at sync time. Empty for
    /// messages cached before header capture was added, and for some locally-created rows.
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// RFC 5322 <c>In-Reply-To</c> — the <see cref="MessageId"/> of the message this one
    /// directly replies to. Empty if this message starts a conversation.
    /// </summary>
    public string InReplyTo { get; set; } = string.Empty;

    /// <summary>
    /// RFC 5322 <c>References</c> — the chain of ancestor message ids, space-separated in
    /// the order the sending client supplied them. This is the reliable basis for grouping a
    /// conversation: unlike subject matching it survives subject edits and localized reply
    /// prefixes, and will not merge unrelated messages that happen to share a subject.
    /// </summary>
    public string References { get; set; } = string.Empty;
}

/// <summary>
/// Full body content for a cached POP3 message, stored so message detail views
/// never need to reconnect to the server (server contact only happens during
/// <see cref="IMailSyncStore.UpsertBodyAsync"/> at sync time).
/// </summary>
public sealed class StoredMailBody
{
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public string To { get; set; } = string.Empty;
    public List<StoredMailAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// Metadata plus a local cache file path for a single attachment on a cached
/// POP3 message. Bytes are stored on disk (not in SQLite) and streamed from
/// <see cref="CachePath"/> on demand.
/// </summary>
public sealed class StoredMailAttachment
{
    public string PartSpecifier { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string CachePath { get; set; } = string.Empty;
}

/// <summary>
/// Local persistence for POP3 message UIDLs so that repeated syncs only download
/// headers for messages that have not been seen before. POP3 has no server-side
/// concept of "new since last sync" beyond UIDL, and no read/unread flags at all,
/// so this store is also used to remember local read state per message.
/// It also holds full message bodies and cached attachment files so message
/// detail views can be served entirely from the local database.
/// </summary>
public interface IMailSyncStore
{
    /// <summary>Returns the UIDLs already known locally for the given account/folder.</summary>
    Task<HashSet<string>> GetKnownUidlsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of known UIDLs for the given account/folder that do not yet
    /// have a cached body (e.g. summaries synced before body-caching existed, or a
    /// previous sync that was interrupted after saving the summary but before the body).
    /// </summary>
    Task<HashSet<string>> GetUidlsMissingBodyAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates cached summaries for the given account/folder.</summary>
    Task UpsertAsync(
        string accountKey, string folderFullName, IEnumerable<StoredMailSummary> summaries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves a fresh (1-based) uid guaranteed not to already be in use by any cached
    /// summary in the given account/folder. Used for POP3 accounts, where the server has
    /// no persistent per-message id other than UIDL: assigning uids from a positional index
    /// that shifts between syncs would risk a brand-new message colliding with the frozen
    /// uid of an already-cached one.
    /// </summary>
    Task<uint> ReserveUidAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// POP3-only maintenance: repairs a folder whose cached summaries have colliding uids
    /// (e.g. left over from before uids were reserved uniquely) by reassigning fresh ones.
    /// Must never be called for IMAP folders, whose uids are the server's own stable ids.
    /// </summary>
    Task RepairDuplicateUidsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>Returns all cached summaries for the given account/folder, newest first.</summary>
    Task<List<StoredMailSummary>> GetCachedSummariesAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>Persists the local read state for a single cached message.</summary>
    Task SetReadStateAsync(
        string accountKey, string folderFullName, string uidl, bool isRead,
        CancellationToken cancellationToken = default);

    /// <summary>Persists the local "flagged/important" state for a single cached message.</summary>
    Task SetImportantAsync(
        string accountKey, string folderFullName, string uidl, bool isImportant,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up the UIDL for a cached message by its exposed (1-based) POP3 uid.</summary>
    Task<string?> GetUidlForUidAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Removes a cached message, e.g. after it has been deleted from the server.</summary>
    Task DeleteAsync(
        string accountKey, string folderFullName, string uidl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a cached POP3 message out of its current folder and into the local-only
    /// "Trash" virtual folder, preserving its summary/body/attachments. Because a POP3
    /// message that has been deleted from the server (or found orphaned during a refresh)
    /// no longer has a reliable UIDL to key on, the moved copy is stored without one.
    /// A tombstone is still recorded in the source folder so a later sync never re-adds it.
    /// </summary>
    Task MoveToTrashAsync(
        string accountKey, string sourceFolderFullName, string uidl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a cached message (summary, body, and attachment rows) from one local folder
    /// to another without contacting the server, e.g. to move a POP3 message into the
    /// local-only "Junk" virtual folder.
    /// </summary>
    Task MoveLocalMessageAsync(
        string accountKey, string sourceFolderFullName, string destinationFolderFullName, string uidl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers, per account, that the user moved these messages (by Message-Id) into the Inbox
    /// themselves, so auto-junk never moves them out again even if the sender is still blocked.
    /// Message-Ids are used because the move gives the message a new server UID. Blank ids are ignored.
    /// </summary>
    Task MarkRescuedAsync(
        string accountKey, IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>The subset of <paramref name="messageIds"/> that were marked with <see cref="MarkRescuedAsync"/> for this account.</summary>
    Task<HashSet<string>> GetRescuedAsync(
        string accountKey, IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the full body and attachment metadata for a message, fetched once
    /// during sync so later reads never need the server.
    /// </summary>
    Task UpsertBodyAsync(
        string accountKey, string folderFullName, string uidl, StoredMailBody body,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the cached full body/attachments for a message, or null if not cached yet.</summary>
    Task<StoredMailBody?> GetBodyAsync(
        string accountKey, string folderFullName, string uidl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists attachment bytes to local storage (outside SQLite) and returns the path
    /// to use as <see cref="StoredMailAttachment.CachePath"/>. Implemented by the platform
    /// layer, which knows where app data may be written.
    /// </summary>
    Task<string> SaveAttachmentAsync(
        string accountKey, string folderFullName, string uidl, string partSpecifier, string fileName,
        Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens a previously-saved attachment file for reading.</summary>
    Stream OpenAttachment(string cachePath);

    /// <summary>
    /// Persists the last-known folder list (name/totals) for an account so it can be
    /// shown instantly (e.g. expanding an account in the Shell flyout) without waiting
    /// on a live server round-trip. Overwrites any previously cached list for the account.
    /// </summary>
    Task UpsertFoldersAsync(
        string accountKey, IEnumerable<MailFolderInfo> folders, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the last-known folder list cached for an account, or an empty list if
    /// none has been cached yet.
    /// </summary>
    Task<List<MailFolderInfo>> GetCachedFoldersAsync(
        string accountKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes everything cached locally for an account: summaries, bodies, deletion
    /// tombstones, the cached folder list and the attachment files on disk. Used when the
    /// account is deleted from the app. <paramref name="emailAddress"/> is the account's
    /// address; it is normalized to the same key the other methods are given.
    /// </summary>
    Task DeleteAccountDataAsync(string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a user-created, client-only folder for a POP3 account (there is no server
    /// to create it on). Used alongside the always-present virtual folders (Inbox, Junk,
    /// Sent, Trash) so the user can organize cached mail into their own folders.
    /// </summary>
    Task AddCustomFolderAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a previously-created custom local folder's entry. Does not itself touch any
    /// cached messages still filed under it; callers should relocate those first.
    /// </summary>
    Task RemoveCustomFolderAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a previously-created custom local folder, migrating its entry plus every
    /// cached summary, body, and delete tombstone filed under the old folder name over to
    /// the new one (their storage keys embed the folder name).
    /// </summary>
    Task RenameCustomFolderAsync(
        string accountKey, string oldFolderFullName, string newFolderFullName, CancellationToken cancellationToken = default);

    /// <summary>Returns all custom local folder names previously created for the given account.</summary>
    Task<List<string>> GetCustomFoldersAsync(
        string accountKey, CancellationToken cancellationToken = default);
}

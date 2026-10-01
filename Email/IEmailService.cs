namespace CORA.Data.Email;

/// <summary>
/// A live view over a single message that keeps its IMAP connection open so
/// attachments can be streamed on demand. Dispose to release the connection.
/// </summary>
public interface IMessageSession : IAsyncDisposable
{
    /// <summary>The loaded message (headers + body, without attachment bytes).</summary>
    EmailMessage Message { get; }

    /// <summary>Streams the given attachment part into <paramref name="destination"/>.</summary>
    Task DownloadAttachmentAsync(
        string partSpecifier, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction over an email backend (IMAP for reading, SMTP for sending).
/// A single instance holds all signed-in accounts and tracks the active one.
/// </summary>
public interface IEmailService
{
    /// <summary>Raised whenever <see cref="CurrentAccount"/> changes.</summary>
    event EventHandler? CurrentAccountChanged;

    /// <summary>Raised whenever folder metadata (counts/unread) may have changed.</summary>
    event EventHandler? FoldersChanged;

    /// <summary>The account currently active, or null.</summary>
    MailAccount? CurrentAccount { get; }

    /// <summary>All accounts that have been signed in during this session.</summary>
    IReadOnlyList<MailAccount> Accounts { get; }

    bool IsSignedIn { get; }

    /// <summary>Validates the account by connecting to both IMAP and SMTP, then adds it to <see cref="Accounts"/>.</summary>
    Task SignInAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an account without validating server connectivity.
    /// Use when credentials are saved manually and a live connection is not required.
    /// </summary>
    void AddAccount(MailAccount account);

    /// <summary>Removes the current account from <see cref="Accounts"/> and switches to another if available.</summary>
    void SignOut();

    /// <summary>Switches the active account without disconnecting others.</summary>
    void SwitchAccount(MailAccount account);

    /// <summary>Removes the specified account from <see cref="Accounts"/>. If it was the current account, switches to another or clears.</summary>
    void RemoveAccount(MailAccount account);

    /// <summary>Deactivate the given account as the active account without removing its saved credentials.</summary>
    void DeactivateAccount(MailAccount account);

    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GetFoldersAsync(CancellationToken)"/> but fetches folders for the
    /// given account explicitly, without relying on (or mutating) <see cref="CurrentAccount"/>.
    /// Use this when fetching folders for an account that may not be the active one (e.g. to
    /// populate a multi-account flyout), so concurrent fetches for different accounts cannot
    /// race on the shared current-account state.
    /// </summary>
    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new top-level folder on the server for the current (IMAP) account.
    /// Throws <see cref="NotSupportedException"/> for POP3 accounts, which have no
    /// server-side folders.
    /// </summary>
    Task CreateFolderAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the given folder from the server for the current (IMAP) account. Refuses to
    /// delete well-known system/virtual folders (Inbox, Sent, Junk, Trash). Throws
    /// <see cref="NotSupportedException"/> for POP3 accounts, which have no server-side folders.
    /// </summary>
    Task DeleteFolderAsync(string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the given folder. For IMAP accounts this renames the folder on the server.
    /// For POP3 accounts only user-created custom folders may be renamed (there is no
    /// server to rename them on); the app's cached messages under that folder are moved
    /// along with the rename. Refuses to rename well-known system/virtual folders
    /// (Inbox, Sent, Junk, Trash).
    /// </summary>
    Task RenameFolderAsync(string folderFullName, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the last-known folder list for the current account from the local cache,
    /// without contacting the server. Used to populate the Shell flyout instantly when an
    /// account is expanded; callers should follow up with <see cref="GetFoldersAsync"/> to
    /// refresh the list in the background.
    /// </summary>
    Task<IReadOnlyList<MailFolderInfo>> GetCachedFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GetCachedFoldersAsync(CancellationToken)"/> but reads the cache for
    /// the given account explicitly, without relying on <see cref="CurrentAccount"/>.
    /// </summary>
    Task<IReadOnlyList<MailFolderInfo>> GetCachedFoldersAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the message list for the given folder. Both POP3 and IMAP accounts read
    /// straight from the local cache/db (no network round-trip); use
    /// <see cref="SyncFolderAsync"/> to refresh the cache from the server.
    /// </summary>
    /// <param name="folderFullName">The folder to read messages from.</param>
    /// <param name="take">The maximum number of messages to return, starting after <paramref name="skip"/>.</param>
    /// <param name="skip">The number of newest-first cached messages to skip, for paging ("Load More").</param>
    Task<IReadOnlyList<EmailSummary>> GetMessagesAsync(
        string folderFullName, int take = 50, int skip = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of cached messages for the given folder, used to determine
    /// whether a "Load More" action should be offered.
    /// </summary>
    Task<int> GetMessageCountAsync(
        string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects to the server (POP3 via UIDL, IMAP via UID), diffs the server's message
    /// identifiers against the local cache, downloads full messages only for new (or
    /// not-yet-fully-cached) messages, and upserts them into the local store. Returns
    /// true if new messages were found or stale cache entries were pruned.
    /// </summary>
    Task<bool> SyncFolderAsync(
        string folderFullName, CancellationToken cancellationToken = default);

    Task<EmailMessage> GetMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a message and keeps the underlying IMAP connection/folder open for the
    /// lifetime of the returned session, so repeated attachment downloads reuse it.
    /// Dispose the session when done (e.g. when leaving the detail view).
    /// </summary>
    Task<IMessageSession> OpenMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a single attachment's decoded content into <paramref name="destination"/>
    /// without buffering the whole payload in memory.
    /// </summary>
    Task DownloadAttachmentAsync(
        string folderFullName, uint uid, string partSpecifier, Stream destination,
        CancellationToken cancellationToken = default);

    Task DeleteMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple messages using a single connection for the whole batch instead
    /// of reconnecting per message, which is significantly faster for both IMAP (one
    /// UID set expunge) and POP3 (one session, N DELE commands, one QUIT/expunge).
    /// </summary>
    Task DeleteMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a message to Junk/Spam. For IMAP accounts this moves the message on the
    /// server to its junk/spam folder: the one flagged \Junk by the server, else a folder with a
    /// well-known name such as "Junk", "Junk Mail" or "Spam" (see JunkFolderDetection). For
    /// POP3 accounts, which have no server-side folders, the message is moved locally
    /// into a virtual "Junk" folder in the local cache/db (see <see cref="GetFoldersAsync"/>).
    /// </summary>
    Task MoveToJunkAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Moves multiple messages to Junk/Spam using a single connection for the whole batch.</summary>
    Task MoveToJunkMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blacklists the given sender's email address and immediately moves the specified
    /// message to Junk/Spam. Future synced mail from this sender is auto-routed to Junk.
    /// </summary>
    Task BlacklistSenderAsync(
        string folderFullName, uint uid, string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blacklists the domain of the given sender's email address and immediately moves the
    /// specified message to Junk/Spam. Future synced mail from this domain is auto-routed
    /// to Junk.
    /// </summary>
    Task BlacklistDomainAsync(
        string folderFullName, uint uid, string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves multiple messages from one folder to another. For IMAP accounts this
    /// moves the messages on the server; for POP3 accounts this updates the local
    /// cache to place the message under the destination virtual folder.
    /// <para>
    /// A move into the Inbox that the user made themselves (<paramref name="initiatedByAi"/>
    /// false) is remembered, so auto-junk never moves that mail out again even if its sender is
    /// still blocked. Callers must pass true for anything the AI assistant did, and never for a
    /// human tap (same rule as <c>IEmailActionGateway</c>).
    /// </para>
    /// </summary>
    Task MoveMessagesAsync(
        string sourceFolderFullName, IEnumerable<uint> uids, string destinationFolderFullName,
        bool initiatedByAi = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves multiple messages out of Junk/Spam back to the Inbox using a single connection
    /// for the whole batch. For POP3 accounts this moves the cached rows from the local
    /// virtual "Junk" folder back to "INBOX". For IMAP accounts this moves the messages on
    /// the server from the current folder back to the Inbox. Unless <paramref name="initiatedByAi"/>
    /// is true, the user's decision is remembered so auto-junk does not undo it (see
    /// <see cref="MoveMessagesAsync"/>).
    /// </summary>
    Task MoveToInboxMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, bool initiatedByAi = false, CancellationToken cancellationToken = default);

    Task SetReadStateAsync(
        string folderFullName, uint uid, bool isRead, CancellationToken cancellationToken = default);

    /// <summary>Sets the read state for multiple messages using a single connection for the whole batch.</summary>
    Task SetReadStateMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, bool isRead, CancellationToken cancellationToken = default);

    /// <summary>Sets the local "flagged/important" state for a single message.</summary>
    Task SetImportantAsync(
        string folderFullName, uint uid, bool isImportant, CancellationToken cancellationToken = default);

    /// <summary>Sets the local "flagged/important" state for multiple messages.</summary>
    Task SetImportantMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, bool isImportant, CancellationToken cancellationToken = default);

    Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
}

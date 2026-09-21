using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace CORA.Data.Email;

/// <summary>
/// Thrown by <see cref="MailKitEmailService.SignInAsync"/> when the incoming
/// (POP3/IMAP) server, the outgoing (SMTP) server, or both fail validation.
/// </summary>
public sealed class MailSignInException : Exception
{
    public bool IncomingFailed { get; }
    public bool OutgoingFailed { get; }
    public Exception? IncomingException { get; }
    public Exception? OutgoingException { get; }

    public MailSignInException(
        string message, bool incomingFailed, bool outgoingFailed,
        Exception? incomingException, Exception? outgoingException)
        : base(message, incomingException ?? outgoingException)
    {
        IncomingFailed = incomingFailed;
        OutgoingFailed = outgoingFailed;
        IncomingException = incomingException;
        OutgoingException = outgoingException;
    }
}

/// <summary>
/// MailKit-based implementation of <see cref="IEmailService"/> using IMAP for
/// reading/managing mail and SMTP for sending. Connections are opened per
/// operation to keep the implementation simple and robust across mobile
/// networking conditions.
/// </summary>
public class MailKitEmailService : IEmailService
{
    /// <summary>
    /// Local-only virtual folder (like POP3's virtual "Junk") that holds a locally-cached
    /// copy of every message sent from this device, for both IMAP and POP3 accounts.
    /// Sending mail via SMTP does not itself save a copy anywhere, so this is synthesized
    /// entirely client-side and never round-trips to the server.
    /// </summary>
    internal const string SentFolderName = "Sent";

    /// <summary>
    /// Local-only virtual folder (POP3 only) that holds deleted and orphaned messages so
    /// they are never permanently lost. Deleting a POP3 message, or discovering during a
    /// refresh that a locally-cached message no longer exists on the server, moves the
    /// message here instead of removing it outright. Because deleted/orphaned messages no
    /// longer have a reliable server UIDL, entries in this folder are stored without one.
    /// </summary>
    internal const string TrashFolderName = "Trash";

    private readonly List<MailAccount> _accounts = [];
    private readonly IMailSyncStore _syncStore;
    private readonly IBlacklistStore _blacklistStore;

    /// <summary>Generates a UID for a locally-synthesized message (e.g. a just-sent message) that will not collide with real server UIDs within its own virtual folder.</summary>
    private static uint NextLocalUid() => unchecked((uint)DateTimeOffset.UtcNow.Ticks);
    private MailAccount? _currentAccount;

    public MailKitEmailService(IMailSyncStore syncStore, IBlacklistStore blacklistStore)
    {
        _syncStore = syncStore;
        _blacklistStore = blacklistStore;
    }

    /// <summary>Raised whenever folder metadata (counts/unread) may have changed.</summary>
    public event EventHandler? FoldersChanged;

    private void OnFoldersChanged() => FoldersChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CurrentAccountChanged;

    public MailAccount? CurrentAccount
    {
        get => _currentAccount;
        private set
        {
            if (ReferenceEquals(_currentAccount, value))
                return;
            _currentAccount = value;
            CurrentAccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<MailAccount> Accounts => _accounts;

    public bool IsSignedIn => CurrentAccount is not null;

    /// <summary>Stable key used to scope locally-cached POP3 data to this account.</summary>
    private static string AccountKey(MailAccount account) =>
        account.EmailAddress.Trim().ToLowerInvariant();

    public async Task SignInAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Validate incoming and outgoing servers independently so that a failure on one
        // does not prevent us from also reporting a failure on the other.
        Exception? incomingError = null;
        Exception? outgoingError = null;

        try
        {
            if (account.IsPop3)
            {
                using var pop3 = await ConnectPop3Async(account, cancellationToken).ConfigureAwait(false);
                await pop3.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);
                await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            incomingError = ex;
        }

        try
        {
            using var smtp = await ConnectSmtpAsync(account, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outgoingError = ex;
        }

        if (incomingError is not null || outgoingError is not null)
        {
            // Gmail rejects a plain password on both IMAP and SMTP for the same underlying
            // reason; avoid showing the same "needs an App Password" explanation twice.
            if (incomingError is InvalidOperationException incomingIoe &&
                outgoingError is InvalidOperationException outgoingIoe &&
                incomingIoe.Message == outgoingIoe.Message)
            {
                throw new MailSignInException(incomingIoe.Message, true, true, incomingError, outgoingError);
            }

            var incomingLabel = account.IsPop3 ? "POP3" : "IMAP";
            var parts = new List<string>();
            if (incomingError is not null)
                parts.Add($"{incomingLabel} (incoming) failed: {incomingError.Message}");
            if (outgoingError is not null)
                parts.Add($"SMTP (outgoing) failed: {outgoingError.Message}");

            throw new MailSignInException(
                string.Join(" | ", parts),
                incomingError is not null,
                outgoingError is not null,
                incomingError,
                outgoingError);
        }

        // Replace existing entry for the same address or add new.
        var existing = _accounts.FindIndex(
            a => string.Equals(a.EmailAddress, account.EmailAddress, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _accounts[existing] = account;
        else
            _accounts.Add(account);

        CurrentAccount = account;
    }

    public void AddAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (_accounts.Any(a => string.Equals(a.EmailAddress, account.EmailAddress, StringComparison.OrdinalIgnoreCase)))
            return; // already registered
        _accounts.Add(account);
        CurrentAccount ??= account;
    }

    public void SignOut()
    {
        if (CurrentAccount is null)
            return;
        RemoveAccount(CurrentAccount);
    }

    public void SwitchAccount(MailAccount account)
    {
        if (!_accounts.Contains(account))
            throw new InvalidOperationException("Account is not in the signed-in list.");
        CurrentAccount = account;
    }

    public void RemoveAccount(MailAccount account)
    {
        _accounts.Remove(account);
        if (CurrentAccount == account)
            CurrentAccount = _accounts.Count > 0 ? _accounts[0] : null;
    }

    public void DeactivateAccount(MailAccount account)
    {
        if (ReferenceEquals(CurrentAccount, account))
            CurrentAccount = null;
    }

    public async Task<IReadOnlyList<MailFolderInfo>> GetCachedFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();
        var accountKey = AccountKey(account);
        return await _syncStore.GetCachedFoldersAsync(accountKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();

        if (account.IsPop3)
        {
            using var pop3 = await ConnectPop3Async(account, cancellationToken).ConfigureAwait(false);
            var count = await pop3.GetMessageCountAsync(cancellationToken).ConfigureAwait(false);
            await pop3.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            var accountKey = AccountKey(account);
            var junkSummaries = await _syncStore.GetCachedSummariesAsync(accountKey, "Junk", cancellationToken)
                .ConfigureAwait(false);
            var sentSummaries = await _syncStore.GetCachedSummariesAsync(accountKey, SentFolderName, cancellationToken)
                .ConfigureAwait(false);
            var trashSummaries = await _syncStore.GetCachedSummariesAsync(accountKey, TrashFolderName, cancellationToken)
                .ConfigureAwait(false);

            // Read cached summaries for POP3 so we can report a meaningful "Unread" count
            // (POP3 has no server-side read flag). Use the server's total message count
            // for the folder Total but compute Unread from the cached IsRead values.
            var cachedInbox = await _syncStore.GetCachedSummariesAsync(accountKey, "INBOX", cancellationToken).ConfigureAwait(false);
            var pop3Result = new List<MailFolderInfo>
            {
                new() { FullName = "INBOX", Name = "Inbox", Total = count, Unread = cachedInbox.Count(s => !s.IsRead) },
                new()
                {
                    FullName = "Junk",
                    Name = "Junk",
                    Total = junkSummaries.Count,
                    Unread = junkSummaries.Count(s => !s.IsRead),
                },
                new()
                {
                    FullName = SentFolderName,
                    Name = SentFolderName,
                    Total = sentSummaries.Count,
                    Unread = 0,
                },
                new()
                {
                    FullName = TrashFolderName,
                    Name = TrashFolderName,
                    Total = trashSummaries.Count,
                    Unread = trashSummaries.Count(s => !s.IsRead),
                },
            };

            await _syncStore.UpsertFoldersAsync(accountKey, pop3Result, cancellationToken).ConfigureAwait(false);
            return pop3Result;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var result = new List<MailFolderInfo>();
        var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
        var folders = await personal.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);

        // Ensure the Inbox is present and listed first.
        var ordered = new List<IMailFolder> { imap.Inbox };
        ordered.AddRange(folders.Where(f => !f.FullName.Equals(imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase)));

        foreach (var folder in ordered)
        {
            if ((folder.Attributes & FolderAttributes.NonExistent) != 0)
                continue;
            // Skip any server-side folder literally named "Sent" to avoid a duplicate entry
            // alongside the always-present local virtual Sent folder appended below.
            if (folder.Name.Equals(SentFolderName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                result.Add(new MailFolderInfo
                {
                    FullName = folder.FullName,
                    Name = folder.Name,
                    Total = folder.Count,
                    Unread = folder.Unread,
                });
                await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Skip folders that cannot be opened (e.g. \Noselect).
            }
        }

        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // The IMAP server's own Sent-mail folder (name varies by provider and is often not
        // populated at all unless the client explicitly appends sent copies there) is not
        // used; instead, always surface the locally-cached virtual Sent folder so sent mail
        // is reliably visible regardless of provider.
        var imapAccountKey = AccountKey(account);
        var imapSentSummaries = await _syncStore.GetCachedSummariesAsync(imapAccountKey, SentFolderName, cancellationToken)
            .ConfigureAwait(false);
        result.Add(new MailFolderInfo
        {
            FullName = SentFolderName,
            Name = SentFolderName,
            Total = imapSentSummaries.Count,
            Unread = 0,
        });

        await _syncStore.UpsertFoldersAsync(imapAccountKey, result, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<EmailSummary>> GetMessagesAsync(
        string folderFullName, int take = 50, int skip = 0, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();

        // Both POP3 and IMAP mailbox reads come from the local cache; use SyncFolderAsync
        // to refresh it from the server (there is no server round-trip here). This also
        // serves the local-only virtual "Junk" folder for POP3 accounts when requested.
        var cacheFolder = string.IsNullOrWhiteSpace(folderFullName) ? "INBOX" : folderFullName;
        var accountKey = AccountKey(account);

        // POP3 uids are locally-assigned (see SyncFolderAsync) and can end up duplicated
        // within a folder from before uids were reserved uniquely. Repair any such
        // collisions before reading so callers that key off Uid (e.g. building a
        // Dictionary<uint, ...>) never throw on a duplicate key. Never do this for IMAP,
        // whose uids are the server's own stable identifiers and must not be reassigned.
        if (account.IsPop3)
        {
            await _syncStore.RepairDuplicateUidsAsync(accountKey, cacheFolder, cancellationToken)
                .ConfigureAwait(false);
        }

        var cached = await _syncStore.GetCachedSummariesAsync(accountKey, cacheFolder, cancellationToken)
            .ConfigureAwait(false);

            // Before returning, attach any known avatar URLs from the trusted-image store or
            // leave AvatarUrl empty (UI falls back to initials). The MailSyncDatabase stores
            // only the header summary fields; avatar resolution happens here when building
            // EmailSummary objects to avoid scattering contact/Gravatar logic higher in the app.
            var list = cached
            .Skip(skip)
            .Take(take)
            .Select(s => new EmailSummary
            {
                Uid = s.Uid,
                FolderName = cacheFolder,
                From = s.From,
                Subject = s.Subject,
                Date = s.Date,
                IsRead = s.IsRead,
                HasAttachments = s.HasAttachments,
                IsImportant = s.IsImportant,
            })
            .ToList();

            // Resolve avatars: prefer local contacts (app-level ContactDatabase) then Gravatar.
            // Avoid heavy network calls here; callers may update AvatarUrl later. For now, only
            // consult the trusted-image sender store to decide whether to allow message-level
            // remote images (not used as avatar source) and leave AvatarUrl empty. The Contact
            // database and Gravatar lookup are done from the app layer (MailboxPage) to preserve
            // separation of concerns and keep Core project free of UI/data-layer service deps.

            return list;
    }

    public async Task<int> GetMessageCountAsync(
        string folderFullName, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();
        var cacheFolder = string.IsNullOrWhiteSpace(folderFullName) ? "INBOX" : folderFullName;
        var accountKey = AccountKey(account);

        var cached = await _syncStore.GetCachedSummariesAsync(accountKey, cacheFolder, cancellationToken)
            .ConfigureAwait(false);
        return cached.Count;
    }

    public async Task<bool> SyncFolderAsync(
        string folderFullName, CancellationToken cancellationToken = default)
    {
        // The Sent folder is local-only (synthesized in SaveSentCopyAsync); there is no
        // server-side counterpart to sync against.
        if (string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
            return false;

        var account = RequireAccount();

        if (!account.IsPop3)
            return await SyncImapFolderAsync(account, folderFullName, cancellationToken).ConfigureAwait(false);

        const string pop3Folder = "INBOX";
        var accountKey = AccountKey(account);

        using var pop3 = await ConnectPop3Async(account, cancellationToken).ConfigureAwait(false);
        var count = await pop3.GetMessageCountAsync(cancellationToken).ConfigureAwait(false);

        // One-time (per sync, cheap no-op once fixed) repair of any uid collisions left over
        // from before uids were reserved uniquely instead of derived from a positional index.
        await _syncStore.RepairDuplicateUidsAsync(accountKey, pop3Folder, cancellationToken)
            .ConfigureAwait(false);

        // Diff the server's UIDL list against what we already have cached locally so we
        // only download new messages we have never seen before.
        var serverUidls = count > 0
            ? await GetMessageUidsResilientAsync(pop3, cancellationToken).ConfigureAwait(false)
            : [];
        var knownUidls = await _syncStore.GetKnownUidlsAsync(accountKey, pop3Folder, cancellationToken)
            .ConfigureAwait(false);

        // Only summaries actually present locally (not tombstoned deletions) are candidates
        // for orphan detection below — a message already moved to Trash should not be
        // re-evaluated every sync. Keep the full list (not just Uidls) so an already-known
        // message that just needs its body re-fetched (see missingBodyUidls below) keeps its
        // previously-assigned uid instead of getting a new one.
        var cachedSummaries = await _syncStore.GetCachedSummariesAsync(accountKey, pop3Folder, cancellationToken)
            .ConfigureAwait(false);
        var cachedSummaryUidls = cachedSummaries.Select(s => s.Uidl).ToHashSet();
        var cachedUidByUidl = cachedSummaries.ToDictionary(s => s.Uidl, s => s.Uid);

        // A UIDL can be "known" (have a summary) but still be missing its cached body,
        // e.g. if it was synced before body-caching existed or a prior sync was
        // interrupted between saving the summary and saving the body. Re-fetch those too
        // so message detail is never left permanently unable to load from the local cache.
        var missingBodyUidls = await _syncStore.GetUidlsMissingBodyAsync(accountKey, pop3Folder, cancellationToken)
            .ConfigureAwait(false);

        var newSummaries = new List<StoredMailSummary>();
        for (var i = 0; i < serverUidls.Count; i++)
        {
            var uidl = serverUidls[i];
            var isKnown = knownUidls.Contains(uidl);
            if (isKnown && !missingBodyUidls.Contains(uidl))
                continue;

            // Fetch the full message (not just headers) so the local db can fully serve
            // reads afterwards without ever reconnecting to the server.
            var mime = await pop3.GetMessageAsync(i, cancellationToken).ConfigureAwait(false);

            // POP3 has no persistent per-message id other than UIDL, and a message's
            // position on the server can shift between syncs (mail added/removed). Assigning
            // uid from the current positional index would risk a brand-new message colliding
            // with the frozen uid of an already-cached one at the same position in a later
            // sync. Reuse the existing uid for messages we already know about (just missing a
            // body), and reserve a fresh, guaranteed-unique one only for genuinely new mail.
            var uid = cachedUidByUidl.TryGetValue(uidl, out var existingUid)
                ? existingUid
                : await _syncStore.ReserveUidAsync(accountKey, pop3Folder, cancellationToken).ConfigureAwait(false);


            var attachments = mime.Attachments.OfType<MimePart>().ToList();
            var storedAttachments = new List<StoredMailAttachment>();
            for (var a = 0; a < attachments.Count; a++)
            {
                var part = attachments[a];
                var partSpecifier = a.ToString();
                var fileName = part.FileName ?? "attachment";

                await using var partStream = new MemoryStream();
                await part.Content.DecodeToAsync(partStream, cancellationToken).ConfigureAwait(false);
                partStream.Position = 0;

                var cachePath = await _syncStore.SaveAttachmentAsync(
                    accountKey, pop3Folder, uidl, partSpecifier, fileName, partStream, cancellationToken)
                    .ConfigureAwait(false);

                storedAttachments.Add(new StoredMailAttachment
                {
                    PartSpecifier = partSpecifier,
                    FileName = fileName,
                    ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                    Size = partStream.Length,
                    CachePath = cachePath,
                });
            }

            newSummaries.Add(new StoredMailSummary
            {
                Uidl = uidl,
                Uid = uid,
                From = mime.From?.ToString() ?? string.Empty,
                Subject = mime.Subject ?? string.Empty,
                Date = mime.Date,
                IsRead = false,
                HasAttachments = storedAttachments.Count > 0,
                MessageId = mime.MessageId ?? string.Empty,
                InReplyTo = mime.InReplyTo ?? string.Empty,
                References = FormatReferences(mime),
            });

            await _syncStore.UpsertBodyAsync(accountKey, pop3Folder, uidl, new StoredMailBody
            {
                To = mime.To?.ToString() ?? string.Empty,
                TextBody = mime.TextBody,
                HtmlBody = mime.HtmlBody,
                Attachments = storedAttachments,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (newSummaries.Count > 0)
            await _syncStore.UpsertAsync(accountKey, pop3Folder, newSummaries, cancellationToken)
                .ConfigureAwait(false);

        // Auto-route newly-synced mail from blacklisted senders/domains straight to Junk.
        await AutoJunkBlacklistedAsync(accountKey, pop3Folder, newSummaries, cancellationToken)
            .ConfigureAwait(false);

        // Prune local summaries that are orphaned: their body was never (and can never be)
        // cached because the message is no longer on the server to re-fetch, or the message
        // was known locally but the server no longer has it (e.g. deleted from another
        // client). Rather than losing such entries outright, move them into the local
        // Trash folder so nothing the user had is silently lost.
        //
        // Locally-moved rows (e.g. a message moved back from Trash into Inbox) are keyed
        // by a locally-generated id that will never appear in the server's UIDL list, so
        // they must be excluded here - otherwise every sync would immediately treat them
        // as orphaned and move them straight back to Trash.
        var localOnlyUidls = cachedSummaries.Where(s => s.IsLocalOnly).Select(s => s.Uidl).ToHashSet();
        var serverUidlSet = serverUidls.ToHashSet();
        var unrepairable = missingBodyUidls.Where(uidl => !serverUidlSet.Contains(uidl) && !localOnlyUidls.Contains(uidl)).ToList();
        var orphanedKnown = cachedSummaryUidls.Where(uidl => !serverUidlSet.Contains(uidl) && !unrepairable.Contains(uidl) && !localOnlyUidls.Contains(uidl)).ToList();
        foreach (var uidl in unrepairable)
            await _syncStore.MoveToTrashAsync(accountKey, pop3Folder, uidl, cancellationToken).ConfigureAwait(false);
        foreach (var uidl in orphanedKnown)
            await _syncStore.MoveToTrashAsync(accountKey, pop3Folder, uidl, cancellationToken).ConfigureAwait(false);

        await pop3.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        var pop3Changed = newSummaries.Count > 0 || unrepairable.Count > 0 || orphanedKnown.Count > 0;
        if (pop3Changed)
            OnFoldersChanged();

        return pop3Changed;
    }

    /// <summary>
    /// IMAP counterpart to the POP3 sync above: connects to the server, diffs the folder's
    /// current UID list against what is already cached locally (using the IMAP UID as the
    /// stable identifier, analogous to a POP3 UIDL), downloads full messages only for new
    /// (or not-yet-fully-cached) ones, and upserts them into the local store so the rest of
    /// the app can read, list, and delete IMAP mail entirely from the local db.
    /// </summary>
    private async Task<bool> SyncImapFolderAsync(
        MailAccount account, string folderFullName, CancellationToken cancellationToken)
    {
        var imapFolderName = string.IsNullOrWhiteSpace(folderFullName) ? "INBOX" : folderFullName;
        var accountKey = AccountKey(account);

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var folder = await OpenFolderAsync(imap, imapFolderName, FolderAccess.ReadOnly, cancellationToken)
            .ConfigureAwait(false);

        var serverUids = await folder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
        var serverUidls = serverUids.Select(u => u.Id.ToString()).ToList();

        var knownUidls = await _syncStore.GetKnownUidlsAsync(accountKey, imapFolderName, cancellationToken)
            .ConfigureAwait(false);
        var missingBodyUidls = await _syncStore.GetUidlsMissingBodyAsync(accountKey, imapFolderName, cancellationToken)
            .ConfigureAwait(false);

        // Batch-fetch flags for every message currently on the server in one round-trip.
        // This is cheap (no bodies) and lets us both seed the read state for newly-cached
        // messages below and refresh the read state of already-cached messages so changes
        // made elsewhere (webmail, another device) are reflected locally on every sync.
        var flagsByUid = new Dictionary<UniqueId, MessageFlags>();
        if (serverUids.Count > 0)
        {
            var flagItems = await folder.FetchAsync(serverUids, MessageSummaryItems.Flags, cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in flagItems)
                flagsByUid[item.UniqueId] = item.Flags ?? MessageFlags.None;
        }

        // Downloading the full body + attachments for every uncached message in a large,
        // real-world IMAP mailbox in one pass can take an extremely long time (or appear to
        // hang) on first sync. Cap each sync call to the newest N uncached messages; any
        // remainder stays "unknown"/"missing body" and is picked up by later sync calls
        // (e.g. the next pull-to-refresh or folder revisit), giving a fast, incremental fill.
        const int maxMessagesPerSync = 50;
        var uidsToFetch = serverUids
            .Where(u =>
            {
                var uidl = u.Id.ToString();
                return !knownUidls.Contains(uidl) || missingBodyUidls.Contains(uidl);
            })
            .OrderByDescending(u => u.Id)
            .Take(maxMessagesPerSync)
            .ToList();

        var newSummaries = new List<StoredMailSummary>();
        if (uidsToFetch.Count > 0)
        {
            foreach (var uniqueId in uidsToFetch)
            {
                var uidl = uniqueId.Id.ToString();
                var isRead = flagsByUid.TryGetValue(uniqueId, out var flags) && flags.HasFlag(MessageFlags.Seen);

                // Fetch the full message so the local db can fully serve reads afterwards
                // without reconnecting to the server.
                var mime = await folder.GetMessageAsync(uniqueId, cancellationToken).ConfigureAwait(false);
                var uid = uniqueId.Id;

                var attachments = mime.Attachments.OfType<MimePart>().ToList();
                var storedAttachments = new List<StoredMailAttachment>();
                for (var a = 0; a < attachments.Count; a++)
                {
                    var part = attachments[a];
                    var partSpecifier = a.ToString();
                    var fileName = part.FileName ?? "attachment";

                    await using var partStream = new MemoryStream();
                    await part.Content.DecodeToAsync(partStream, cancellationToken).ConfigureAwait(false);
                    partStream.Position = 0;

                    var cachePath = await _syncStore.SaveAttachmentAsync(
                        accountKey, imapFolderName, uidl, partSpecifier, fileName, partStream, cancellationToken)
                        .ConfigureAwait(false);

                    storedAttachments.Add(new StoredMailAttachment
                    {
                        PartSpecifier = partSpecifier,
                        FileName = fileName,
                        ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                        Size = partStream.Length,
                        CachePath = cachePath,
                    });
                }

                newSummaries.Add(new StoredMailSummary
                {
                    Uidl = uidl,
                    Uid = uid,
                    From = mime.From?.ToString() ?? string.Empty,
                    Subject = mime.Subject ?? string.Empty,
                    Date = mime.Date,
                    IsRead = isRead,
                    HasAttachments = storedAttachments.Count > 0,
                    MessageId = mime.MessageId ?? string.Empty,
                    InReplyTo = mime.InReplyTo ?? string.Empty,
                    References = FormatReferences(mime),
                });

                await _syncStore.UpsertBodyAsync(accountKey, imapFolderName, uidl, new StoredMailBody
                {
                    To = mime.To?.ToString() ?? string.Empty,
                    TextBody = mime.TextBody,
                    HtmlBody = mime.HtmlBody,
                    Attachments = storedAttachments,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (newSummaries.Count > 0)
            await _syncStore.UpsertAsync(accountKey, imapFolderName, newSummaries, cancellationToken)
                .ConfigureAwait(false);

        // Auto-route newly-synced mail from blacklisted senders/domains straight to Junk.
        await AutoJunkBlacklistedAsync(accountKey, imapFolderName, newSummaries, cancellationToken)
            .ConfigureAwait(false);

        // Refresh the local read state of every already-cached message from the server's
        // authoritative flags (messages just seeded above already have the right state via
        // newSummaries, but re-applying is harmless and keeps this simple).
        var newlyCachedUidls = newSummaries.Select(s => s.Uidl).ToHashSet();
        var readStateRefreshCount = 0;
        foreach (var uniqueId in serverUids)
        {
            var uidl = uniqueId.Id.ToString();
            if (!knownUidls.Contains(uidl) || newlyCachedUidls.Contains(uidl))
                continue;

            // Only ever let this passive refresh upgrade a message to read, never downgrade
            // it back to unread. The read flag is pushed to the server via a separate,
            // best-effort connection (see MarkReadEverywhereAsync) that can occasionally
            // fail or lag; if it hasn't propagated yet, treating the server's stale
            // "unseen" flag as authoritative here would revert a message the user just
            // read back to unread as soon as they return to the mailbox list.
            if (flagsByUid.TryGetValue(uniqueId, out var flags) && flags.HasFlag(MessageFlags.Seen))
            {
                await _syncStore.SetReadStateAsync(accountKey, imapFolderName, uidl, true, cancellationToken)
                    .ConfigureAwait(false);
                readStateRefreshCount++;
            }
        }

        // Prune local rows for messages that are no longer on the server (e.g. deleted from
        // another client), same reasoning as the POP3 orphan-pruning above.
        var serverUidlSet = serverUidls.ToHashSet();
        var staleUidls = knownUidls.Where(uidl => !serverUidlSet.Contains(uidl)).ToList();
        foreach (var uidl in staleUidls)
            await _syncStore.DeleteAsync(accountKey, imapFolderName, uidl, cancellationToken).ConfigureAwait(false);

        await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        var imapChanged = newSummaries.Count > 0 || staleUidls.Count > 0 || readStateRefreshCount > 0;
        if (imapChanged)
            OnFoldersChanged();

        return imapChanged;
    }


    public async Task<EmailMessage> GetMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();
        var accountKey = AccountKey(account);

        var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message not found in local cache.");

        var body = await _syncStore.GetBodyAsync(accountKey, folderFullName, uidl, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message body not yet synced locally.");

        // Persist the read state locally so it survives a reload of the mailbox list
        // (which rebuilds from the local cache), and for IMAP also mark the message Seen
        // on the server so other clients see it as read too.
        await MarkReadEverywhereAsync(account, accountKey, folderFullName, uid, uidl, cancellationToken)
            .ConfigureAwait(false);

        var summary = await GetCachedSummaryForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false);
        return BuildMessageFromCache(uid, folderFullName, summary, body);
    }

    /// <summary>
    /// Marks a message read in the local cache (awaited, since it's just a fast db write) and,
    /// for IMAP accounts, also sets the \Seen flag on the server so the read state is reflected
    /// on other clients/webmail. POP3 has no server-side flags, so only the local cache is
    /// updated for it.
    /// </summary>
    /// <remarks>
    /// The server-side IMAP flag update is intentionally fire-and-forget (not awaited): it
    /// requires its own connect/open-folder/disconnect round-trip, which would otherwise block
    /// message display for however long that takes even though the message body itself already
    /// came from the local cache. It's best-effort anyway (see catch below and the next sync's
    /// flag refresh), so there is nothing useful gained by making the caller wait for it.
    /// </remarks>
    private async Task MarkReadEverywhereAsync(
        MailAccount account, string accountKey, string folderFullName, uint uid, string uidl,
        CancellationToken cancellationToken)
    {
        await _syncStore.SetReadStateAsync(accountKey, folderFullName, uidl, true, cancellationToken)
            .ConfigureAwait(false);

        if (account.IsPop3 || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
            return;

        // Deliberately not awaited by the caller — see remarks above. Uses CancellationToken.None
        // since this outlives the caller's request (e.g. a page navigating away shouldn't cancel
        // a flag update that's already in flight).
        _ = Task.Run(async () =>
        {
            try
            {
                using var imap = await ConnectImapAsync(account, CancellationToken.None).ConfigureAwait(false);
                var folder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, CancellationToken.None)
                    .ConfigureAwait(false);
                await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, CancellationToken.None)
                    .ConfigureAwait(false);
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
                await imap.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the local cache is already updated, so reading the message still
                // works offline; the next sync's flag refresh will reconcile if this failed.
            }
        });
    }

    /// <summary>Looks up a single cached POP3 summary by its exposed uid, used to fill From/Subject/Date on message detail.</summary>
    private async Task<StoredMailSummary?> GetCachedSummaryForUidAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken)
    {
        var summaries = await _syncStore.GetCachedSummariesAsync(accountKey, folderFullName, cancellationToken)
            .ConfigureAwait(false);
        return summaries.FirstOrDefault(s => s.Uid == uid);
    }

    /// <summary>
    /// Opens a long-lived session that keeps the IMAP connection and folder open
    /// for the lifetime of the returned object, so multiple attachment downloads
    /// reuse the same connection. Dispose the session to release it.
    /// </summary>
    public async Task<IMessageSession> OpenMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();
        var accountKey = AccountKey(account);

        var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message not found in local cache.");

        var body = await _syncStore.GetBodyAsync(accountKey, folderFullName, uidl, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message body not yet synced locally.");

        // Persist the read state locally so it survives a reload of the mailbox list
        // (which rebuilds from the local cache), and for IMAP also mark the message Seen
        // on the server so other clients see it as read too.
        await MarkReadEverywhereAsync(account, accountKey, folderFullName, uid, uidl, cancellationToken)
            .ConfigureAwait(false);

        var summary = await GetCachedSummaryForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false);
        var msg = BuildMessageFromCache(uid, folderFullName, summary, body);
        return new CachedMessageSession(msg, _syncStore);
    }

    /// <summary>
    /// POP3 has no stable per-message identifier other than UIDL, and message positions
    /// on the server can shift between connections (new mail arriving, other messages
    /// deleted, etc.). This resolves the correct current server index for a message we
    /// previously cached by looking up its UIDL against the live server UIDL list, so
    /// callers never act on the wrong message due to a stale positional index.
    /// </summary>
    private static async Task<int?> ResolvePop3IndexAsync(
        Pop3Client pop3, string? uidl, int fallbackIndex, CancellationToken cancellationToken)
    {
        var serverUidls = await GetMessageUidsResilientAsync(pop3, cancellationToken).ConfigureAwait(false);
        return ResolvePop3Index(serverUidls, uidl, fallbackIndex);
    }

    /// <summary>
    /// Same resolution as <see cref="ResolvePop3IndexAsync"/> but against an already-fetched
    /// UIDL snapshot, for batch operations that must not re-issue UIDL per message.
    /// Returns null when the message can no longer be resolved to a valid position on the
    /// server (e.g. it was already deleted elsewhere, so neither the UIDL lookup nor the
    /// stale fallback index fall within the server's current message count) — callers
    /// should treat this as "already gone from the server" rather than crash.
    /// </summary>
    private static int? ResolvePop3Index(IList<string> serverUidls, string? uidl, int fallbackIndex)
    {
        if (uidl is not null)
        {
            var index = serverUidls.IndexOf(uidl);
            if (index >= 0)
                return index;
        }

        // Only trust the positional fallback if it actually falls within the server's
        // current message count; otherwise the message is no longer there to delete.
        if (fallbackIndex >= 0 && fallbackIndex < serverUidls.Count)
            return fallbackIndex;

        return null;
    }

    /// <summary>
    /// Fetches the full UIDL snapshot for a session using the bulk "UIDL" (no argument) command,
    /// falling back to per-message "UIDL n" calls if the server rejects the bulk form (some POP3
    /// servers - notably several shared-hosting/cPanel style servers - only implement the
    /// single-message form and return an error response to the bulk list command).
    /// </summary>
    private static async Task<IList<string>> GetMessageUidsResilientAsync(
        Pop3Client pop3, CancellationToken cancellationToken)
    {
        try
        {
            return await pop3.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Pop3ProtocolException)
        {
            var count = await pop3.GetMessageCountAsync(cancellationToken).ConfigureAwait(false);
            var uidls = new List<string>(count);
            for (var i = 0; i < count; i++)
                uidls.Add(await pop3.GetMessageUidAsync(i, cancellationToken).ConfigureAwait(false));

            return uidls;
        }
    }

    /// <summary>
    /// Fetches envelope + structure, marks the message read, pulls text/html body
    /// parts individually, and records attachment metadata plus their body parts.
    /// Attachment bytes are never downloaded here.
    /// </summary>
    internal static async Task<(EmailMessage Message, Dictionary<string, BodyPart> Parts)> BuildMessageAsync(
        IMailFolder folder, uint uid, CancellationToken cancellationToken)
    {
        var uniqueId = new UniqueId(uid);

        var items = await folder.FetchAsync(new[] { uniqueId },
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure |
            MessageSummaryItems.References,
            cancellationToken).ConfigureAwait(false);
        var summary = items.FirstOrDefault();

        // Mark as read on open.
        await folder.AddFlagsAsync(uniqueId, MessageFlags.Seen, true, cancellationToken)
            .ConfigureAwait(false);

        var envelope = summary?.Envelope;
        var message = new EmailMessage
        {
            Uid = uid,
            FolderName = folder.FullName,
            From = envelope?.From?.ToString() ?? string.Empty,
            To = envelope?.To?.ToString() ?? string.Empty,
            Subject = envelope?.Subject ?? string.Empty,
            Date = envelope?.Date ?? summary?.Date ?? DateTimeOffset.MinValue,
            MessageId = envelope?.MessageId ?? string.Empty,
            References = summary?.References is { Count: > 0 } refs
                ? string.Join(' ', refs)
                : string.Empty,
        };

        if (summary?.TextBody is not null)
            message.TextBody = await FetchTextAsync(folder, uniqueId, summary.TextBody, cancellationToken)
                .ConfigureAwait(false);

        if (summary?.HtmlBody is not null)
            message.HtmlBody = await FetchTextAsync(folder, uniqueId, summary.HtmlBody, cancellationToken)
                .ConfigureAwait(false);

        var parts = new Dictionary<string, BodyPart>();
        if (summary is not null)
        {
            foreach (var part in summary.Attachments)
            {
                message.Attachments.Add(new AttachmentInfo
                {
                    FolderName = folder.FullName,
                    Uid = uid,
                    PartSpecifier = part.PartSpecifier,
                    FileName = part.ContentDisposition?.FileName
                        ?? part.ContentType?.Name
                        ?? "attachment",
                    ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                    Size = (long)part.Octets,
                });
                parts[part.PartSpecifier] = part;
            }
        }

        return (message, parts);
    }

    public async Task DownloadAttachmentAsync(
        string folderFullName, uint uid, string partSpecifier, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var account = RequireAccount();
        var accountKey = AccountKey(account);

        var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message not found in local cache.");

        var body = await _syncStore.GetBodyAsync(accountKey, folderFullName, uidl, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message body not yet synced locally.");

        var attachment = body.Attachments.FirstOrDefault(a => a.PartSpecifier == partSpecifier);
        if (attachment is not null)
        {
            await using var source = _syncStore.OpenAttachment(attachment.CachePath);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }


    public async Task DeleteMessageAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();

        if (string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var sentAccountKey = AccountKey(account);
            var sentUidl = await _syncStore.GetUidlForUidAsync(sentAccountKey, folderFullName, uid, cancellationToken)
                .ConfigureAwait(false);
            if (sentUidl is not null)
                await _syncStore.DeleteAsync(sentAccountKey, folderFullName, sentUidl, cancellationToken)
                    .ConfigureAwait(false);
            return;
        }

        if (account.IsPop3)
        {
            var accountKey = AccountKey(account);

            // Repair any duplicate Uids in this folder before resolving uid -> uidl below.
            // Without this, a stale duplicate Uid could make GetUidlForUidAsync resolve to
            // the wrong (or an already-processed) cached row, silently deleting the wrong
            // message or leaving the intended one behind.
            await _syncStore.RepairDuplicateUidsAsync(accountKey, folderFullName, cancellationToken)
                .ConfigureAwait(false);

            var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                .ConfigureAwait(false);

            // Trash is local-only for POP3: every message in it was already DELE'd from the
            // server when it was originally trashed (or was an orphan that never had a live
            // server copy), and its "uidl" is a locally-generated id, not a real server UIDL.
            // Resolving it against the server here would, at best, find nothing and at worst
            // match an unrelated real message at the same positional index and delete it by
            // mistake. So a delete from Trash never touches the server — just purge locally.
            if (string.Equals(folderFullName, TrashFolderName, StringComparison.OrdinalIgnoreCase))
            {
                if (uidl is not null)
                    await _syncStore.DeleteAsync(accountKey, folderFullName, uidl, cancellationToken)
                        .ConfigureAwait(false);
                OnFoldersChanged();
                return;
            }

            using var pop3 = await ConnectPop3Async(account, cancellationToken).ConfigureAwait(false);
            // Do not trust the positional fallback index when resolving a POP3 message
            // for deletion: if the UIDL lookup fails, skip server-side DELE to avoid
            // accidentally deleting the wrong message. Use -1 so ResolvePop3IndexAsync
            // will not accept a positional fallback.
            var index = await ResolvePop3IndexAsync(pop3, uidl, -1, cancellationToken)
                .ConfigureAwait(false);

            // A null index means the message is no longer resolvable on the server (already
            // deleted elsewhere, or the cached positional index no longer fits the current
            // mailbox) — nothing to DELE, just clean up the local cache below.
            if (index is not null)
                await pop3.DeleteMessageAsync(index.Value, cancellationToken).ConfigureAwait(false);
            await pop3.DisconnectAsync(true, cancellationToken).ConfigureAwait(false); // QUIT expunges

            if (uidl is not null)
                await _syncStore.MoveToTrashAsync(accountKey, folderFullName, uidl, cancellationToken)
                    .ConfigureAwait(false);
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var folder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Deleted, true, cancellationToken)
            .ConfigureAwait(false);
        await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);

        await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // Keep the local cache/db in sync so the mailbox list (which reads from the
        // cache) no longer shows the deleted message.
        await _syncStore.DeleteAsync(AccountKey(account), folderFullName, uid.ToString(), cancellationToken)
            .ConfigureAwait(false);
        OnFoldersChanged();
    }


    public async Task DeleteMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();

        if (string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var sentAccountKey = AccountKey(account);
            foreach (var uid in uidList)
            {
                var sentUidl = await _syncStore.GetUidlForUidAsync(sentAccountKey, folderFullName, uid, cancellationToken)
                    .ConfigureAwait(false);
                if (sentUidl is not null)
                    await _syncStore.DeleteAsync(sentAccountKey, folderFullName, sentUidl, cancellationToken)
                        .ConfigureAwait(false);
            }
            return;
        }

        if (account.IsPop3)
        {
            var accountKey = AccountKey(account);

            // Repair any duplicate Uids in this folder before resolving uid -> uidl below.
            // Without this, multiple selected messages sharing a stale duplicate Uid would
            // all resolve to the same cached row's Uidl, so only one unique message would
            // actually be removed from the folder's cache even though the UI optimistically
            // removes every selected row from the list.
            await _syncStore.RepairDuplicateUidsAsync(accountKey, folderFullName, cancellationToken)
                .ConfigureAwait(false);

            // Resolve all UIDLs up front, then use a single POP3 session for every
            // DELE command instead of reconnecting once per message.
            var uidls = new Dictionary<uint, string?>();
            foreach (var uid in uidList)
                uidls[uid] = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                    .ConfigureAwait(false);

            // Trash is local-only for POP3 (see DeleteMessageAsync for the full explanation):
            // its "uidls" are locally-generated ids, not real server UIDLs, so resolving them
            // against the server could match and delete an unrelated real message. Deleting
            // from Trash never touches the server — just purge the local rows.
            if (string.Equals(folderFullName, TrashFolderName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var uid in uidList)
                {
                    var uidl = uidls[uid];
                    if (uidl is not null)
                        await _syncStore.DeleteAsync(accountKey, folderFullName, uidl, cancellationToken)
                            .ConfigureAwait(false);
                }
                OnFoldersChanged();
                return;
            }

            using (var pop3 = await ConnectPop3Async(account, cancellationToken).ConfigureAwait(false))
            {
                // Fetch the UIDL list once for the whole session. Message numbers stay stable
                // for the lifetime of a POP3 session even after DELE, so there is no need (and
                // some servers actively reject the need) to re-issue UIDL per message.
                var serverUidls = await GetMessageUidsResilientAsync(pop3, cancellationToken).ConfigureAwait(false);

                foreach (var uid in uidList)
                {
                    // Do not fall back to the positional uid index here. If the UIDL cannot
                    // be resolved against the server snapshot, skip issuing DELE for that
                    // message to avoid removing the wrong server message.
                    var index = ResolvePop3Index(serverUidls, uidls[uid], -1);

                    // Null means this message can no longer be resolved on the server (already
                    // deleted elsewhere, or the cached positional index no longer fits the
                    // current mailbox) — skip the DELE and just clean up the local cache below.
                    if (index is not null)
                        await pop3.DeleteMessageAsync(index.Value, cancellationToken).ConfigureAwait(false);
                }

                await pop3.DisconnectAsync(true, cancellationToken).ConfigureAwait(false); // QUIT expunges
            }

            foreach (var uid in uidList)
            {
                var uidl = uidls[uid];
                if (uidl is not null)
                    await _syncStore.MoveToTrashAsync(accountKey, folderFullName, uidl, cancellationToken)
                        .ConfigureAwait(false);
            }
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var folderImap = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        var uniqueIds = new UniqueIdSet(uidList.Select(uid => new UniqueId(uid)));
        await folderImap.AddFlagsAsync(uniqueIds, MessageFlags.Deleted, true, cancellationToken)
            .ConfigureAwait(false);
        await folderImap.ExpungeAsync(cancellationToken).ConfigureAwait(false);

        await folderImap.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // Keep the local cache/db in sync so the mailbox list (which reads from the
        // cache) no longer shows the deleted messages.
        var imapAccountKey = AccountKey(account);
        foreach (var uid in uidList)
            await _syncStore.DeleteAsync(imapAccountKey, folderFullName, uid.ToString(), cancellationToken)
                .ConfigureAwait(false);
        OnFoldersChanged();
    }

    private static readonly string[] JunkFolderNames = ["Junk", "Junk E-Mail", "Spam", "Bulk Mail"];

    /// <summary>
    /// Flattens a message's RFC 5322 <c>References</c> chain into a single space-separated
    /// string for storage, preserving the sender's original ordering (oldest ancestor first).
    /// </summary>
    private static string FormatReferences(MimeMessage mime) =>
        mime.References is { Count: > 0 } ? string.Join(' ', mime.References) : string.Empty;

    /// <summary>
    /// Splits a stored space-separated <c>References</c> chain back into individual message ids,
    /// trimming the angle brackets MailKit's <see cref="MessageIdList"/> does not expect.
    /// </summary>
    private static IEnumerable<string> SplitReferences(string? references)
    {
        if (string.IsNullOrWhiteSpace(references))
            yield break;

        foreach (var part in references.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part.Trim('<', '>');
    }



    public async Task MoveToJunkAsync(
        string folderFullName, uint uid, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();

        if (account.IsPop3 || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            const string junkFolder = "Junk";
            var accountKey = AccountKey(account);
            var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Message not found in local cache.");

            await _syncStore.MoveLocalMessageAsync(accountKey, folderFullName, junkFolder, uidl, cancellationToken)
                .ConfigureAwait(false);
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var sourceFolder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
        var subfolders = await personal.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);

        IMailFolder? destinationFolder = null;
        foreach (var candidateName in JunkFolderNames)
        {
            destinationFolder = subfolders.FirstOrDefault(
                f => f.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase));
            if (destinationFolder is not null)
                break;
        }

        if (destinationFolder is null)
        {
            await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
            await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("No Junk/Spam folder was found on the server.");
        }

        await sourceFolder.MoveToAsync(new UniqueId(uid), destinationFolder, cancellationToken)
            .ConfigureAwait(false);

        await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // The message now lives under a new UID in the destination folder; drop the stale
        // local cache row here and let the destination folder's next sync pick it up fresh.
        await _syncStore.DeleteAsync(AccountKey(account), folderFullName, uid.ToString(), cancellationToken)
            .ConfigureAwait(false);
        OnFoldersChanged();
    }

    public async Task MoveToJunkMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();

        if (account.IsPop3 || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            const string junkFolder = "Junk";
            var accountKey = AccountKey(account);

            foreach (var uid in uidList)
            {
                var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Message not found in local cache.");

                await _syncStore.MoveLocalMessageAsync(accountKey, folderFullName, junkFolder, uidl, cancellationToken)
                    .ConfigureAwait(false);
            }
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var sourceFolder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
        var subfolders = await personal.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);

        IMailFolder? destinationFolder = null;
        foreach (var candidateName in JunkFolderNames)
        {
            destinationFolder = subfolders.FirstOrDefault(
                f => f.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase));
            if (destinationFolder is not null)
                break;
        }

        if (destinationFolder is null)
        {
            await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
            await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("No Junk/Spam folder was found on the server.");
        }

        var uniqueIds = new UniqueIdSet(uidList.Select(uid => new UniqueId(uid)));
        await sourceFolder.MoveToAsync(uniqueIds, destinationFolder, cancellationToken)
            .ConfigureAwait(false);

        await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        var junkAccountKey = AccountKey(account);
        foreach (var uid in uidList)
            await _syncStore.DeleteAsync(junkAccountKey, folderFullName, uid.ToString(), cancellationToken)
                .ConfigureAwait(false);
        OnFoldersChanged();
    }

    public async Task BlacklistSenderAsync(
        string folderFullName, uint uid, string senderEmail, CancellationToken cancellationToken = default)
    {
        await _blacklistStore.BlacklistSenderAsync(senderEmail, cancellationToken).ConfigureAwait(false);
        await MoveToJunkAsync(folderFullName, uid, cancellationToken).ConfigureAwait(false);
    }

    public async Task BlacklistDomainAsync(
        string folderFullName, uint uid, string senderEmail, CancellationToken cancellationToken = default)
    {
        var domain = BlacklistDatabase.ExtractDomain(senderEmail);
        if (!string.IsNullOrEmpty(domain))
            await _blacklistStore.BlacklistDomainAsync(domain, cancellationToken).ConfigureAwait(false);
        await MoveToJunkAsync(folderFullName, uid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the given sender should be auto-routed to Junk, based on the local
    /// blacklist (either the exact sender address or its domain is blacklisted).
    /// </summary>
    private async Task<bool> IsBlacklistedAsync(string from, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(from))
            return false;

        if (await _blacklistStore.IsSenderBlacklistedAsync(from, cancellationToken).ConfigureAwait(false))
            return true;

        var domain = BlacklistDatabase.ExtractDomain(from);
        return !string.IsNullOrEmpty(domain)
            && await _blacklistStore.IsDomainBlacklistedAsync(domain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// After a sync caches a batch of newly-seen messages, moves any of them that are from a
    /// blacklisted sender/domain into the local "Junk" folder. Skipped for folders that are
    /// already Junk, Trash, or Sent, to avoid pointless moves/loops.
    /// </summary>
    private async Task AutoJunkBlacklistedAsync(
        string accountKey, string folderFullName, IReadOnlyList<StoredMailSummary> newSummaries,
        CancellationToken cancellationToken)
    {
        if (newSummaries.Count == 0)
            return;

        if (string.Equals(folderFullName, "Junk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderFullName, TrashFolderName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
            return;

        const string junkFolder = "Junk";
        foreach (var summary in newSummaries)
        {
            if (!await IsBlacklistedAsync(summary.From, cancellationToken).ConfigureAwait(false))
                continue;

            await _syncStore.MoveLocalMessageAsync(accountKey, folderFullName, junkFolder, summary.Uidl, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Move messages from one folder to another. This is a generic implementation used by the UI
    /// when the user selects an arbitrary destination folder.
    /// </summary>
    public async Task MoveMessagesAsync(
        string sourceFolderFullName, IEnumerable<uint> uids, string destinationFolderFullName, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();

        if (account.IsPop3 || string.Equals(sourceFolderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var acctKey = AccountKey(account);
            foreach (var uid in uidList)
            {
                var uidl = await _syncStore.GetUidlForUidAsync(acctKey, sourceFolderFullName, uid, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Message not found in local cache.");

                await _syncStore.MoveLocalMessageAsync(acctKey, sourceFolderFullName, destinationFolderFullName, uidl, cancellationToken)
                    .ConfigureAwait(false);
            }
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);
        var sourceFolder = await OpenFolderAsync(imap, sourceFolderFullName, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

        IMailFolder? destinationFolder = null;
        var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
        var subfolders = await personal.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);
        // Try to find a subfolder matching the display name; fall back to exact name match.
        destinationFolder = subfolders.FirstOrDefault(f => f.Name.Equals(destinationFolderFullName, StringComparison.OrdinalIgnoreCase))
            ?? subfolders.FirstOrDefault(f => f.FullName.Equals(destinationFolderFullName, StringComparison.OrdinalIgnoreCase));

        if (destinationFolder is null)
        {
            await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
            await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Destination folder not found on server.");
        }

        var uniqueIds = new UniqueIdSet(uidList.Select(uid => new UniqueId(uid)));
        await sourceFolder.MoveToAsync(uniqueIds, destinationFolder, cancellationToken).ConfigureAwait(false);

        await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        var syncAccountKey = AccountKey(account);
        foreach (var uid in uidList)
            await _syncStore.DeleteAsync(syncAccountKey, sourceFolderFullName, uid.ToString(), cancellationToken).ConfigureAwait(false);
        OnFoldersChanged();
    }

    public async Task MoveToInboxMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();

        if (account.IsPop3)
        {
            const string inboxFolder = "INBOX";
            var accountKey = AccountKey(account);

            foreach (var uid in uidList)
            {
                var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Message not found in local cache.");

                await _syncStore.MoveLocalMessageAsync(accountKey, folderFullName, inboxFolder, uidl, cancellationToken)
                    .ConfigureAwait(false);
            }
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var sourceFolder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        var uniqueIds = new UniqueIdSet(uidList.Select(uid => new UniqueId(uid)));
        await sourceFolder.MoveToAsync(uniqueIds, imap.Inbox, cancellationToken)
            .ConfigureAwait(false);

        await sourceFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        var inboxAccountKey = AccountKey(account);
        foreach (var uid in uidList)
            await _syncStore.DeleteAsync(inboxAccountKey, folderFullName, uid.ToString(), cancellationToken)
                .ConfigureAwait(false);
        OnFoldersChanged();
    }

    public async Task SetReadStateAsync(
        string folderFullName, uint uid, bool isRead, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();

        // POP3 has no server-side flags, and the Sent folder is local-only for every
        // account; persist the read state in the local cache instead in both cases.
        if (account.IsPop3 || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var accountKey = AccountKey(account);
            var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                .ConfigureAwait(false);
            if (uidl is not null)
                await _syncStore.SetReadStateAsync(accountKey, folderFullName, uidl, isRead, cancellationToken)
                    .ConfigureAwait(false);
            OnFoldersChanged();
            return;
        }

        var imapAccountKey = AccountKey(account);

        // Persist the read state locally first and unconditionally, so it is never lost
        // even if the subsequent server round-trip below fails or throws (e.g. a
        // transient network issue) — the local cache is the source of truth for the
        // mailbox list, and the server flag push below is best-effort on top of it.
        await _syncStore.SetReadStateAsync(imapAccountKey, folderFullName, uid.ToString(), isRead, cancellationToken)
            .ConfigureAwait(false);
        OnFoldersChanged();

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var folder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        if (isRead)
            await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, cancellationToken)
                .ConfigureAwait(false);
        else
            await folder.RemoveFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, cancellationToken)
                .ConfigureAwait(false);

        await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetReadStateMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, bool isRead, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();

        // POP3 has no server-side flags, and the Sent folder is local-only for every
        // account; persist the read state in the local cache instead in both cases.
        if (account.IsPop3 || string.Equals(folderFullName, SentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var accountKey = AccountKey(account);
            foreach (var uid in uidList)
            {
                var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                    .ConfigureAwait(false);
                if (uidl is not null)
                    await _syncStore.SetReadStateAsync(accountKey, folderFullName, uidl, isRead, cancellationToken)
                        .ConfigureAwait(false);
            }
            OnFoldersChanged();
            return;
        }

        using var imap = await ConnectImapAsync(account, cancellationToken).ConfigureAwait(false);

        var folder = await OpenFolderAsync(imap, folderFullName, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        var uniqueIds = new UniqueIdSet(uidList.Select(uid => new UniqueId(uid)));

        // Persist locally first and unconditionally, so it is never lost even if the
        // server round-trip below fails or throws.
        var readStateAccountKey = AccountKey(account);
        foreach (var uid in uidList)
            await _syncStore.SetReadStateAsync(readStateAccountKey, folderFullName, uid.ToString(), isRead, cancellationToken)
                .ConfigureAwait(false);
        OnFoldersChanged();

        if (isRead)
            await folder.AddFlagsAsync(uniqueIds, MessageFlags.Seen, true, cancellationToken)
                .ConfigureAwait(false);
        else
            await folder.RemoveFlagsAsync(uniqueIds, MessageFlags.Seen, true, cancellationToken)
                .ConfigureAwait(false);

        await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetImportantAsync(
        string folderFullName, uint uid, bool isImportant, CancellationToken cancellationToken = default)
    {
        var account = RequireAccount();
        var accountKey = AccountKey(account);

        var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
            .ConfigureAwait(false)
            ?? uid.ToString();

        await _syncStore.SetImportantAsync(accountKey, folderFullName, uidl, isImportant, cancellationToken)
            .ConfigureAwait(false);
        OnFoldersChanged();
    }

    public async Task SetImportantMessagesAsync(
        string folderFullName, IEnumerable<uint> uids, bool isImportant, CancellationToken cancellationToken = default)
    {
        var uidList = uids as IReadOnlyCollection<uint> ?? uids.ToList();
        if (uidList.Count == 0)
            return;

        var account = RequireAccount();
        var accountKey = AccountKey(account);

        foreach (var uid in uidList)
        {
            var uidl = await _syncStore.GetUidlForUidAsync(accountKey, folderFullName, uid, cancellationToken)
                .ConfigureAwait(false)
                ?? uid.ToString();

            await _syncStore.SetImportantAsync(accountKey, folderFullName, uidl, isImportant, cancellationToken)
                .ConfigureAwait(false);
        }

        OnFoldersChanged();
    }

    public async Task SendMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var account = message.SenderAccount ?? RequireAccount();

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
        foreach (var recipient in SplitAddresses(message.To))
            mime.To.Add(MailboxAddress.Parse(recipient));
        // Include Bcc recipients so messages with only Bcc are valid for SMTP servers
        foreach (var recipient in SplitAddresses(message.Bcc))
            mime.Bcc.Add(MailboxAddress.Parse(recipient));
        mime.Subject = message.Subject;

        // RFC 5322 section 3.6.4 threading. Without these a reply arrives at the recipient as a
        // brand-new conversation rather than threading under the message it answers, which is a
        // correctness problem in what we emit - not merely a local display issue.
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            mime.InReplyTo = message.InReplyTo;

        foreach (var reference in SplitReferences(message.References))
            mime.References.Add(reference);

        var builder = new BodyBuilder { TextBody = message.Body };
        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            builder.HtmlBody = $"<div dir=\"auto\">{System.Net.WebUtility.HtmlEncode(message.Body).Replace("\r\n", "<br>").Replace("\n", "<br>")}</div>";
        }

        // Open the attachment files as streams so large payloads are read from disk
        // during transmission instead of being buffered on the managed heap.
        var streams = new List<Stream>();
        try
        {
            foreach (var attachment in message.Attachments)
            {
                var stream = File.OpenRead(attachment.FilePath);
                streams.Add(stream);
                builder.Attachments.Add(
                    attachment.FileName,
                    stream,
                    ContentType.Parse(attachment.ContentType));
            }
            mime.Body = builder.ToMessageBody();

            using var smtp = await ConnectSmtpAsync(account, cancellationToken).ConfigureAwait(false);

            var sendTask = smtp.SendAsync(mime, cancellationToken);
            using var sendDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sendTimeout = Task.Delay(Timeout.Infinite, sendDeadline.Token);

            if (await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false) != sendTask)
            {
                throw new TimeoutException(
                    $"SMTP send to {account.SmtpHost}:{account.SmtpPort} timed out after 30 s.");
            }

            await sendTask.ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            await SaveSentCopyAsync(account, message, mime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Caches a locally-synthesized copy of a just-sent message into the virtual
    /// <see cref="SentFolderName"/> folder so it shows up in <see cref="GetMessagesAsync"/>
    /// without ever needing a server round-trip (SMTP submission does not itself save a
    /// copy anywhere). Failures here are swallowed since the message was already sent
    /// successfully; not being able to cache a local copy should not surface as a send error.
    /// </summary>
    private async Task SaveSentCopyAsync(
        MailAccount account, OutgoingMessage message, MimeMessage mime, CancellationToken cancellationToken)
    {
        try
        {
            var accountKey = AccountKey(account);
            var uid = NextLocalUid();
            var uidl = uid.ToString();
            var now = DateTimeOffset.UtcNow;

            var storedAttachments = new List<StoredMailAttachment>();
            foreach (var attachment in message.Attachments)
            {
                using var stream = File.OpenRead(attachment.FilePath);
                var cachePath = await _syncStore.SaveAttachmentAsync(
                    accountKey, SentFolderName, uidl, storedAttachments.Count.ToString(),
                    attachment.FileName, stream, cancellationToken).ConfigureAwait(false);

                storedAttachments.Add(new StoredMailAttachment
                {
                    PartSpecifier = storedAttachments.Count.ToString(),
                    FileName = attachment.FileName,
                    ContentType = attachment.ContentType,
                    Size = stream.Length,
                    CachePath = cachePath,
                });
            }

            var fromDisplay = string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.EmailAddress
                : $"{account.DisplayName} <{account.EmailAddress}>";

            await _syncStore.UpsertAsync(accountKey, SentFolderName,
                new[]
                {
                    new StoredMailSummary
                    {
                        Uidl = uidl,
                        Uid = uid,
                        From = fromDisplay,
                        Subject = message.Subject,
                        Date = now,
                        IsRead = true,
                        HasAttachments = storedAttachments.Count > 0,
                        IsLocalOnly = true,

                        // Storing our own Message-Id lets an incoming reply - whose References
                        // will name it - group with this sent copy, and echoing the reply's own
                        // threading chain puts this copy in the right conversation locally too.
                        MessageId = mime.MessageId ?? string.Empty,
                        InReplyTo = message.InReplyTo,
                        References = FormatReferences(mime),
                    },
                }, cancellationToken).ConfigureAwait(false);

            await _syncStore.UpsertBodyAsync(accountKey, SentFolderName, uidl, new StoredMailBody
            {
                TextBody = message.Body,
                HtmlBody = !string.IsNullOrWhiteSpace(message.Body)
                    ? $"<div dir=\"auto\">{System.Net.WebUtility.HtmlEncode(message.Body).Replace("\r\n", "<br>").Replace("\n", "<br>")}</div>"
                    : null,
                To = message.To,
                Attachments = storedAttachments,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort local caching only; the message has already been sent.
        }
    }

    private MailAccount RequireAccount() =>
        CurrentAccount ?? throw new InvalidOperationException("No account is signed in.");

    private static async Task<string?> FetchTextAsync(
        IMailFolder folder, UniqueId uid, BodyPart bodyPart, CancellationToken cancellationToken)
    {
        var entity = await folder.GetBodyPartAsync(uid, bodyPart, cancellationToken).ConfigureAwait(false);
        return entity is TextPart textPart ? textPart.Text : null;
    }

    internal static async Task DecodeEntityAsync(
        MimeEntity entity, Stream destination, CancellationToken cancellationToken)
    {
        if (entity is MimePart part)
            await part.Content.DecodeToAsync(destination, cancellationToken).ConfigureAwait(false);
        else if (entity is MessagePart messagePart)
            await messagePart.Message.WriteToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ImapClient> ConnectImapAsync(
        MailAccount account, CancellationToken cancellationToken)
    {
        var imap = new ImapClient { CheckCertificateRevocation = false };
        await imap.ConnectAsync(account.ImapHost, account.ImapPort,
            ToSecureSocketOptions(account.ImapSecurity),
            cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(imap, account, cancellationToken).ConfigureAwait(false);
        return imap;
    }

    /// <summary>Writes MailKit's raw protocol traffic to the Debug output window.</summary>
    private sealed class DebugProtocolLogger : IProtocolLogger
    {
        private sealed class NoSecrets : IAuthenticationSecretDetector
        {
            public IList<AuthenticationSecret> DetectSecrets(byte[] buffer, int offset, int count) =>
                Array.Empty<AuthenticationSecret>();
        }

        public IAuthenticationSecretDetector AuthenticationSecretDetector { get; set; } = new NoSecrets();

        private readonly string _tag;

        public DebugProtocolLogger(string tag) => _tag = tag;

        public void LogConnect(Uri uri) =>
            System.Diagnostics.Debug.WriteLine($"[{_tag}-WIRE] Connect: {uri}");

        public void LogClient(byte[] buffer, int offset, int count) =>
            System.Diagnostics.Debug.WriteLine($"[{_tag}-WIRE] C: {System.Text.Encoding.ASCII.GetString(buffer, offset, count).TrimEnd()}");

        public void LogServer(byte[] buffer, int offset, int count) =>
            System.Diagnostics.Debug.WriteLine($"[{_tag}-WIRE] S: {System.Text.Encoding.ASCII.GetString(buffer, offset, count).TrimEnd()}");

        public void Dispose() { }
    }

    internal static async Task<Pop3Client> ConnectPop3Async(
        MailAccount account, CancellationToken cancellationToken)
    {
        var pop3 = new Pop3Client(new DebugProtocolLogger("POP3"))
        {

            SslProtocols = System.Security.Authentication.SslProtocols.Tls
                         | System.Security.Authentication.SslProtocols.Tls11
                         | System.Security.Authentication.SslProtocols.Tls12
                         | System.Security.Authentication.SslProtocols.Tls13,
            ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || certificate is not null,
        };

        // Derive the correct security option from port: 995 = implicit SSL, 110 = STARTTLS.
        // Ignore the user's ImapSecurity setting for POP3 — a StartTls/SSL mismatch is the
        // most common cause of handshake hangs and cannot be fixed from the UI reliably.
        var secureOpts = account.ImapPort == 995
            ? SecureSocketOptions.SslOnConnect
            : account.ImapPort == 110
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

        System.Diagnostics.Debug.WriteLine(
            $"[POP3] Connecting to {account.ImapHost}:{account.ImapPort} security={secureOpts}");

        var connectTask = pop3.ConnectAsync(
            account.ImapHost, account.ImapPort, secureOpts, cancellationToken);

        // Use an independent (non-linked) CancellationTokenSource for the deadline so the
        // delay task always fires after exactly 20 s regardless of what connectTask does with
        // the shared cancellationToken.  When both race to complete at the same time the
        // linked-token approach lets connectTask win and bypass the timeout branch.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var timeoutTask = Task.Delay(Timeout.Infinite, deadline.Token);

        if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask)
        {
            deadline.Cancel(); // clean up
            throw new TimeoutException(
                $"POP3 connection to {account.ImapHost}:{account.ImapPort} " +
                $"(security={secureOpts}) timed out after 20 s.");
        }

        deadline.Cancel(); // stop the deadline timer

        await connectTask.ConfigureAwait(false);

        // Basic shared-hosting POP3 servers only support USER/PASS. MailKit will try SASL
        // mechanisms (GSSAPI, NTLM, ...) first if the server advertises them, and those
        // negotiations can stall indefinitely on servers that partially implement them.
        // Clearing the set forces MailKit to use the plain USER/PASS sequence instead.
        var mechanisms = string.Join(", ", pop3.AuthenticationMechanisms);
        System.Diagnostics.Debug.WriteLine($"[POP3] Connected. SASL mechanisms: {(mechanisms.Length > 0 ? mechanisms : "(none)")}");
        pop3.AuthenticationMechanisms.Clear();

        System.Diagnostics.Debug.WriteLine("[POP3] Authenticating with USER/PASS");

        using var authDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // authTask uses the outer cancellationToken — NOT authDeadline.Token — so the
        // deadline timer and the auth operation are fully independent.  If authDeadline
        // fires first, Task.WhenAny returns the timeout task unconditionally.
        var authTask    = AuthenticateAsync(pop3, account, cancellationToken);
        var authTimeout = Task.Delay(Timeout.Infinite, authDeadline.Token);

        if (await Task.WhenAny(authTask, authTimeout).ConfigureAwait(false) != authTask)
        {
            throw new TimeoutException(
                $"POP3 authentication with {account.ImapHost} timed out after 20 s. " +
                "Check username and password, or try a different authentication method.");
        }

        await authTask.ConfigureAwait(false);

        System.Diagnostics.Debug.WriteLine("[POP3] Authenticated successfully");
        return pop3;
    }

    internal static async Task<SmtpClient> ConnectSmtpAsync(
        MailAccount account, CancellationToken cancellationToken)
    {
        var smtp = new SmtpClient(new DebugProtocolLogger("SMTP"))
        {
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || certificate is not null,
        };

        // Derive the correct security option from port, same as POP3: 465 = implicit SSL
        // (SMTPS), 587/25 = STARTTLS. A StartTls setting on port 465 stalls the handshake
        // because the server sends TLS immediately instead of a plain-text greeting.
        var secureOpts = account.SmtpPort == 465
            ? SecureSocketOptions.SslOnConnect
            : account.SmtpPort is 587 or 25
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

        System.Diagnostics.Debug.WriteLine(
            $"[SMTP] Connecting to {account.SmtpHost}:{account.SmtpPort} security={secureOpts}");

        var connectTask = smtp.ConnectAsync(
            account.SmtpHost, account.SmtpPort, secureOpts, cancellationToken);

        using var connectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connectTimeout = Task.Delay(Timeout.Infinite, connectDeadline.Token);

        if (await Task.WhenAny(connectTask, connectTimeout).ConfigureAwait(false) != connectTask)
        {
            throw new TimeoutException(
                $"SMTP connection to {account.SmtpHost}:{account.SmtpPort} " +
                $"(security={secureOpts}) timed out after 20 s.");
        }

        await connectTask.ConfigureAwait(false);

        System.Diagnostics.Debug.WriteLine("[SMTP] Connected, authenticating");

        var authTask = AuthenticateAsync(smtp, account, cancellationToken);

        using var authDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var authTimeout = Task.Delay(Timeout.Infinite, authDeadline.Token);

        if (await Task.WhenAny(authTask, authTimeout).ConfigureAwait(false) != authTask)
        {
            throw new TimeoutException(
                $"SMTP authentication with {account.SmtpHost} timed out after 20 s.");
        }

        await authTask.ConfigureAwait(false);

        System.Diagnostics.Debug.WriteLine("[SMTP] Authenticated successfully");
        return smtp;
    }

    /// <summary>Builds an <see cref="EmailMessage"/> entirely from cached data.</summary>
    private static EmailMessage BuildMessageFromCache(
        uint uid, string folderName, StoredMailSummary? summary, StoredMailBody body)
    {
        var message = new EmailMessage
        {
            Uid = uid,
            FolderName = folderName,
            From = summary?.From ?? string.Empty,
            To = body.To,
            Subject = summary?.Subject ?? string.Empty,
            Date = summary?.Date ?? DateTimeOffset.MinValue,
            TextBody = body.TextBody,
            HtmlBody = body.HtmlBody,
            MessageId = summary?.MessageId ?? string.Empty,
            References = summary?.References ?? string.Empty,
        };

        foreach (var attachment in body.Attachments)
        {
            message.Attachments.Add(new AttachmentInfo
            {
                FolderName = folderName,
                Uid = uid,
                PartSpecifier = attachment.PartSpecifier,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                Size = attachment.Size,
                CachePath = attachment.CachePath,
            });
        }

        return message;
    }

    /// <summary>
    /// Lightweight session for a POP3 message where all data (including attachments)
    /// is already served from the local cache/db; never reconnects to the server.
    /// </summary>
    private sealed class CachedMessageSession : IMessageSession
    {
        private readonly IMailSyncStore _syncStore;

        public CachedMessageSession(EmailMessage message, IMailSyncStore syncStore)
        {
            Message = message;
            _syncStore = syncStore;
        }

        public EmailMessage Message { get; }

        public async Task DownloadAttachmentAsync(
            string partSpecifier, Stream destination, CancellationToken cancellationToken = default)
        {
            var attachment = Message.Attachments.FirstOrDefault(a => a.PartSpecifier == partSpecifier);
            if (attachment is null || string.IsNullOrEmpty(attachment.CachePath))
                return;

            await using var source = _syncStore.OpenAttachment(attachment.CachePath);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Authenticates a connected MailKit client using either a password or an
    /// OAuth 2.0 (XOAUTH2) access token, refreshing the token first when needed.
    /// </summary>
    internal static async Task AuthenticateAsync(
        MailKit.MailService client, MailAccount account, CancellationToken cancellationToken)
    {
        if (account.IsOAuth)
        {
            await EnsureAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);
            var oauth2 = new SaslMechanismOAuth2(account.EffectiveUserName, account.AccessToken);
            await client.AuthenticateAsync(oauth2, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await client.AuthenticateAsync(account.EffectiveUserName, account.Password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsGmailAppPasswordFailure(account.EffectiveUserName, ex))
            {
                throw new InvalidOperationException(
                    "Google rejected the password because this account requires an App Password " +
                    "(or sign-in with Google/OAuth). Generate one at " +
                    "https://myaccount.google.com/apppasswords and use it here instead of your " +
                    "regular Gmail password, or use \"Sign in with Google\" instead.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Detects a Gmail account authentication failure that is most likely caused by using a
    /// regular account password instead of an App Password or OAuth 2.0 token. Gmail reports
    /// this clearly as "5.7.9 Application-specific password required" on SMTP, but on IMAP it
    /// often just closes the connection abruptly (surfacing as an IOException/"unexpectedly
    /// disconnected" from MailKit) instead of returning readable text, so for Gmail accounts
    /// any auth-adjacent failure while using a plain password is treated as this cause.
    /// </summary>
    private static bool IsGmailAppPasswordFailure(string userName, Exception ex)
    {
        if (!userName.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) &&
            !userName.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase))
            return false;

        return ex is MailKit.Security.AuthenticationException
            || ex is IOException
            || (ex.Message?.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ?? false)
            || (ex.Message?.Contains("5.7.9", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Refreshes the OAuth access token when it is missing, expired, or within a
    /// two-minute window of expiring, using the account's refresh token.
    /// </summary>
    internal static async Task EnsureAccessTokenAsync(
        MailAccount account, CancellationToken cancellationToken)
    {
        var needsRefresh =
            string.IsNullOrEmpty(account.AccessToken) ||
            account.AccessTokenExpiresUtc <= DateTimeOffset.UtcNow.AddMinutes(2);

        if (!needsRefresh || string.IsNullOrEmpty(account.RefreshToken) ||
            string.IsNullOrEmpty(account.OAuthTokenEndpoint))
        {
            return;
        }

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = account.OAuthClientId,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token",
        };
        if (!string.IsNullOrEmpty(account.OAuthClientSecret))
            parameters["client_secret"] = account.OAuthClientSecret;

        using var http = new HttpClient();
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await http.PostAsync(account.OAuthTokenEndpoint, content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var token = await response.Content
            .ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty token response from the OAuth server.");

        if (!string.IsNullOrEmpty(token.AccessToken))
            account.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken))
            account.RefreshToken = token.RefreshToken;
        if (token.ExpiresIn > 0)
            account.AccessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    internal static async Task<IMailFolder> OpenFolderAsync(
        ImapClient imap, string folderFullName, FolderAccess access, CancellationToken cancellationToken)
    {
        IMailFolder folder;
        if (string.IsNullOrWhiteSpace(folderFullName) ||
            folderFullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            folder = imap.Inbox;
        }
        else
        {
            folder = await imap.GetFolderAsync(folderFullName, cancellationToken).ConfigureAwait(false);
        }

        await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    private static IEnumerable<string> SplitAddresses(string addresses) =>
        (addresses ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static SecureSocketOptions ToSecureSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.Ssl  => SecureSocketOptions.SslOnConnect,
        MailSecurity.Tls  => SecureSocketOptions.StartTls,
        _                 => SecureSocketOptions.None,
    };
}

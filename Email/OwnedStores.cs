using CORA.Data.Contacts;

namespace CORA.Data.Email;

/// <summary>
/// Decides which <see cref="OwnerStoreSet"/>s the app sees (claude/design-per-account-ownership.md).
/// <list type="bullet">
/// <item><b>Write set</b>: where new items go — the signed-in account's set, or the ownerless set
/// when no one is signed in.</item>
/// <item><b>Other set</b>: shown alongside — the ownerless set when signed in; the last signed-in
/// account's set when signed out (unless it hides its mailboxes when signed out); else none.</item>
/// </list>
/// An item that already exists is changed in the set that holds it. Mailbox-scoped data (hidden
/// folders, folder order, tag assignments, AI results, AI autonomy, image approvals) follows the
/// set that holds that mailbox, so each owner keeps its own copy for a mailbox they share.
/// The store interfaces handed to the app are thin layers that ask this router on every call,
/// so switching owner never leaves a service holding a stale store.
/// </summary>
internal sealed class OwnerStoreRouter
{
    private readonly object _gate = new();
    private readonly Func<(OwnerStoreSet Write, OwnerStoreSet? Other)>? _view;
    private OwnerStoreSet _write;
    private OwnerStoreSet? _other;

    public OwnerStoreRouter(OwnerStoreSet ownerless)
    {
        Ownerless = ownerless;
        _write = ownerless;
    }

    private OwnerStoreRouter(OwnerStoreRouter main, bool ownerlessOnly)
    {
        Ownerless = main.Ownerless;
        _write = main.Ownerless;
        _view = ownerlessOnly ? () => (main.Ownerless, null) : () => (main.Current, null);
        main.CurrentChanged += () => CurrentChanged?.Invoke();
        main.SetOpened += set => SetOpened?.Invoke(set);
    }

    /// <summary>
    /// A read-only view of <paramref name="main"/> that sees one set: the current owner's
    /// (<paramref name="ownerlessOnly"/> false) or the ownerless set (true). Sync uses it so an
    /// account's cloud only ever holds what that account owns (decision 10).
    /// </summary>
    public static OwnerStoreRouter ViewOf(OwnerStoreRouter main, bool ownerlessOnly) => new(main, ownerlessOnly);

    /// <summary>Items without an owner: today's files in the app data folder.</summary>
    public OwnerStoreSet Ownerless { get; }

    /// <summary>The set new items are written to (sync state too).</summary>
    public OwnerStoreSet Current => Snapshot().Write;

    /// <summary>The write set, then the other set if any, as one consistent snapshot.</summary>
    public (OwnerStoreSet Write, OwnerStoreSet? Other) Snapshot()
    {
        if (_view is not null)
            return _view();
        lock (_gate)
            return (_write, _other);
    }

    public IReadOnlyList<OwnerStoreSet> Reads
    {
        get
        {
            var (write, other) = Snapshot();
            return other is null ? [write] : [write, other];
        }
    }

    /// <summary>Raised after the sets in use changed. May come from any thread.</summary>
    public event Action? CurrentChanged;

    /// <summary>Raised for each set as it opens (including the ownerless one, via <see cref="Announce"/>), so layers can listen to its stores.</summary>
    public event Action<OwnerStoreSet>? SetOpened;

    public void Announce(OwnerStoreSet set) => SetOpened?.Invoke(set);

    public void Use(OwnerStoreSet write, OwnerStoreSet? other)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_view is not null)
            throw new InvalidOperationException("A view follows its main router.");
        if (ReferenceEquals(write, other))
            other = null;
        lock (_gate)
        {
            if (ReferenceEquals(_write, write) && ReferenceEquals(_other, other))
                return;
            _write = write;
            _other = other;
        }
        CurrentChanged?.Invoke();
    }

    public bool IsCurrent(OwnerStoreSet set) => ReferenceEquals(Current, set);

    /// <summary>Items moved between the shown sets: tell listeners (hidden folders, folder order) to read again.</summary>
    public void Refresh() => CurrentChanged?.Invoke();

    public bool IsShown(OwnerStoreSet set)
    {
        var (write, other) = Snapshot();
        return ReferenceEquals(write, set) || ReferenceEquals(other, set);
    }

    /// <summary>The shown set that holds this mailbox (the write set first), else the write set.</summary>
    public OwnerStoreSet ForMailbox(string? address)
    {
        var (write, other) = Snapshot();
        if (string.IsNullOrWhiteSpace(address) || write.Accounts.ContainsAccount(address))
            return write;
        return other is not null && other.Accounts.ContainsAccount(address) ? other : write;
    }
}

internal static class OwnedStoreExtensions
{
    public static async Task<T?> FirstFoundAsync<T>(this IReadOnlyList<OwnerStoreSet> sets, Func<OwnerStoreSet, Task<T?>> get) where T : class
    {
        foreach (var set in sets)
        {
            if (await get(set).ConfigureAwait(false) is { } found)
                return found;
        }
        return null;
    }

    public static async Task<bool> AnyAsync(this IReadOnlyList<OwnerStoreSet> sets, Func<OwnerStoreSet, Task<bool>> test)
    {
        foreach (var set in sets)
        {
            if (await test(set).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    public static async Task ForEachAsync(this IReadOnlyList<OwnerStoreSet> sets, Func<OwnerStoreSet, Task> action)
    {
        foreach (var set in sets)
            await action(set).ConfigureAwait(false);
    }
}

internal sealed class OwnedCredentialStore(OwnerStoreRouter router) : ICredentialStore
{
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The write set's mailboxes, then the other set's that the write set doesn't also have.</summary>
    public async Task<List<MailAccount>> LoadAllAsync()
    {
        var (write, other) = router.Snapshot();
        var all = await write.Accounts.LoadAllAsync().ConfigureAwait(false);
        if (other is not null)
        {
            foreach (var account in await other.Accounts.LoadAllAsync().ConfigureAwait(false))
            {
                if (!all.Any(a => Same(a.EmailAddress, account.EmailAddress)))
                    all.Add(account);
            }
        }
        return all;
    }

    public Task SaveAccountAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return router.ForMailbox(account.EmailAddress).Accounts.SaveAccountAsync(account);
    }

    public Task AddAccountAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return router.Current.Accounts.SaveAccountAsync(account);
    }

    public Task DeleteAccountAsync(string email) => router.ForMailbox(email).Accounts.DeleteAccountAsync(email);

    public void Clear() { }

    // The open mailbox is a device preference for now (per-account settings come in stage 3).
    public string? GetActiveAccountEmail() => router.Ownerless.Accounts.GetActiveAccountEmail();
    public void SetActiveAccountEmail(string? email) => router.Ownerless.Accounts.SetActiveAccountEmail(email);
}

/// <summary>
/// Contacts from both shown sets. Contact ids are numbers per file, so the other set's contacts
/// are shown with negative ids and mapped back on every call; the write set's keep their own.
/// </summary>
internal sealed class OwnedContactStore(OwnerStoreRouter router) : IContactStore
{
    private static Contact Shown(Contact c)
    {
        c.Id = -c.Id;
        return c;
    }

    private static List<Contact> Sorted(IEnumerable<Contact> contacts) =>
        contacts.OrderBy(c => c.LastName ?? string.Empty).ThenBy(c => c.FirstName ?? string.Empty).ToList();

    private async Task<List<Contact>> BothAsync(Func<IContactStore, Task<List<Contact>>> get)
    {
        var (write, other) = router.Snapshot();
        var mine = await get(write.Contacts).ConfigureAwait(false);
        if (other is null)
            return mine;
        var theirs = await get(other.Contacts).ConfigureAwait(false);
        return Sorted(mine.Concat(theirs.Select(Shown)));
    }

    public Task<List<Contact>> GetAllAsync() => BothAsync(s => s.GetAllAsync());

    public Task<List<Contact>> SearchAsync(string query) => BothAsync(s => s.SearchAsync(query));

    public async Task<Contact?> GetAsync(int id)
    {
        var (write, other) = router.Snapshot();
        if (id >= 0)
            return await write.Contacts.GetAsync(id).ConfigureAwait(false);
        if (other is null)
            return null;
        var found = await other.Contacts.GetAsync(-id).ConfigureAwait(false);
        return found is null ? null : Shown(found);
    }

    public async Task<int> SaveAsync(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var (write, other) = router.Snapshot();
        if (contact.Id >= 0 || other is null)
        {
            if (contact.Id < 0)
                contact.Id = 0;   // its set is no longer shown: save it as new here
            return await write.Contacts.SaveAsync(contact).ConfigureAwait(false);
        }
        contact.Id = -contact.Id;
        try
        {
            return await other.Contacts.SaveAsync(contact).ConfigureAwait(false);
        }
        finally
        {
            contact.Id = -contact.Id;
        }
    }

    public async Task<int> DeleteAsync(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var (write, other) = router.Snapshot();
        if (contact.Id >= 0)
            return await write.Contacts.DeleteAsync(contact).ConfigureAwait(false);
        if (other is null)
            return 0;
        contact.Id = -contact.Id;
        try
        {
            return await other.Contacts.DeleteAsync(contact).ConfigureAwait(false);
        }
        finally
        {
            contact.Id = -contact.Id;
        }
    }

    public async Task<int> DeleteManyAsync(IEnumerable<Contact> contacts)
    {
        var (write, other) = router.Snapshot();
        var list = contacts.ToList();
        var count = await write.Contacts.DeleteManyAsync(list.Where(c => c.Id >= 0).ToList()).ConfigureAwait(false);
        var theirs = list.Where(c => c.Id < 0).ToList();
        if (other is null || theirs.Count == 0)
            return count;
        foreach (var c in theirs)
            c.Id = -c.Id;
        try
        {
            return count + await other.Contacts.DeleteManyAsync(theirs).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in theirs)
                c.Id = -c.Id;
        }
    }
}

internal sealed class OwnedTagStore(OwnerStoreRouter router) : ITagStore
{
    // Tag ids are GUIDs, so the same id never names two tags across sets.
    private async Task<OwnerStoreSet> SetOfTagAsync(string tagId, CancellationToken cancellationToken)
    {
        foreach (var set in router.Reads)
        {
            if ((await set.Tags.GetTagsAsync(cancellationToken).ConfigureAwait(false)).Any(t => t.Id == tagId))
                return set;
        }
        return router.Current;
    }

    public async Task<List<TagDefinition>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<TagDefinition>();
        foreach (var set in router.Reads)
        {
            foreach (var tag in await set.Tags.GetTagsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!all.Any(t => t.Id == tag.Id))
                    all.Add(tag);
            }
        }
        return all;
    }

    public Task<TagDefinition> AddTagAsync(string name, string colorHex, CancellationToken cancellationToken = default) =>
        router.Current.Tags.AddTagAsync(name, colorHex, cancellationToken);

    public async Task UpdateTagAsync(string tagId, string name, string colorHex, CancellationToken cancellationToken = default) =>
        await (await SetOfTagAsync(tagId, cancellationToken).ConfigureAwait(false)).Tags.UpdateTagAsync(tagId, name, colorHex, cancellationToken).ConfigureAwait(false);

    public async Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default) =>
        await (await SetOfTagAsync(tagId, cancellationToken).ConfigureAwait(false)).Tags.DeleteTagAsync(tagId, cancellationToken).ConfigureAwait(false);

    // Assignments follow the mailbox; the tag itself may be in either shown set.
    public async Task<TagDefinition?> GetMessageTagAsync(string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default)
    {
        var ids = await router.ForMailbox(accountKey).Tags.GetMessageTagIdsAsync(accountKey, folderFullName, cancellationToken).ConfigureAwait(false);
        if (!ids.TryGetValue(uid, out var tagId))
            return null;
        return (await GetTagsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.Id == tagId);
    }

    public Task<Dictionary<uint, string>> GetMessageTagIdsAsync(string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).Tags.GetMessageTagIdsAsync(accountKey, folderFullName, cancellationToken);

    public Task SetMessageTagAsync(string accountKey, string folderFullName, uint uid, string? tagId, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).Tags.SetMessageTagAsync(accountKey, folderFullName, uid, tagId, cancellationToken);

    public Task DeleteAccountAssignmentsAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        router.ForMailbox(emailAddress).Tags.DeleteAccountAssignmentsAsync(emailAddress, cancellationToken);

    public async Task<List<MessageTagAssignment>> GetAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        // Each shown set's assignments for the mailboxes it holds.
        var all = new List<MessageTagAssignment>();
        foreach (var set in router.Reads)
        {
            foreach (var a in await set.Tags.GetAssignmentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ReferenceEquals(router.ForMailbox(a.AccountKey), set))
                    all.Add(a);
            }
        }
        return all;
    }

    public Task SetAssignmentMessageIdAsync(string accountKey, string folderFullName, uint uid, string messageId, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).Tags.SetAssignmentMessageIdAsync(accountKey, folderFullName, uid, messageId, cancellationToken);
}

internal sealed class OwnedBlacklistStore(OwnerStoreRouter router) : IBlacklistStore
{
    public Task<bool> IsSenderBlacklistedAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        router.Reads.AnyAsync(s => s.Blacklist.IsSenderBlacklistedAsync(senderEmail, cancellationToken));

    public Task<bool> IsDomainBlacklistedAsync(string domain, CancellationToken cancellationToken = default) =>
        router.Reads.AnyAsync(s => s.Blacklist.IsDomainBlacklistedAsync(domain, cancellationToken));

    public Task BlacklistSenderAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        router.Current.Blacklist.BlacklistSenderAsync(senderEmail, cancellationToken);

    public Task BlacklistDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        router.Current.Blacklist.BlacklistDomainAsync(domain, cancellationToken);

    public async Task<IReadOnlyList<string>> GetBlacklistedSendersAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<string>();
        foreach (var set in router.Reads)
            all.AddRange(await set.Blacklist.GetBlacklistedSendersAsync(cancellationToken).ConfigureAwait(false));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<string>> GetBlacklistedDomainsAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<string>();
        foreach (var set in router.Reads)
            all.AddRange(await set.Blacklist.GetBlacklistedDomainsAsync(cancellationToken).ConfigureAwait(false));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Unblocking removes it wherever it shows, so it really stops being blocked.
    public Task RemoveSenderAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.Blacklist.RemoveSenderAsync(senderEmail, cancellationToken));

    public Task RemoveDomainAsync(string domain, CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.Blacklist.RemoveDomainAsync(domain, cancellationToken));
}

internal sealed class OwnedTrustedImageSenderStore(OwnerStoreRouter router) : ITrustedImageSenderStore
{
    public Task<bool> IsTrustedAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        router.Reads.AnyAsync(s => s.TrustedImageSenders.IsTrustedAsync(senderEmail, cancellationToken));

    public async Task<List<string>> GetTrustedSendersAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<string>();
        foreach (var set in router.Reads)
            all.AddRange(await set.TrustedImageSenders.GetTrustedSendersAsync(cancellationToken).ConfigureAwait(false));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SetTrustedAsync(string senderEmail, bool trusted, CancellationToken cancellationToken = default)
    {
        if (trusted)
        {
            if (!await IsTrustedAsync(senderEmail, cancellationToken).ConfigureAwait(false))
                await router.Current.TrustedImageSenders.SetTrustedAsync(senderEmail, true, cancellationToken).ConfigureAwait(false);
            return;
        }
        await router.Reads.ForEachAsync(s => s.TrustedImageSenders.SetTrustedAsync(senderEmail, false, cancellationToken)).ConfigureAwait(false);
    }

    public Task<bool> IsMessageAllowedAsync(string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).TrustedImageSenders.IsMessageAllowedAsync(accountKey, folderFullName, uid, cancellationToken);

    public Task SetMessageAllowedAsync(string accountKey, string folderFullName, uint uid, bool allowed, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).TrustedImageSenders.SetMessageAllowedAsync(accountKey, folderFullName, uid, allowed, cancellationToken);
}

/// <summary>Forwards a set's Changed event while that set is shown, and raises it when the shown sets change.</summary>
internal abstract class OwnedNotifyingStore
{
    protected OwnedNotifyingStore(OwnerStoreRouter router, Func<OwnerStoreSet, Action<EventHandler>> subscribe)
    {
        Router = router;
        router.SetOpened += set => subscribe(set)((_, _) =>
        {
            if (router.IsShown(set))
                Changed?.Invoke(this, EventArgs.Empty);
        });
        router.CurrentChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    protected OwnerStoreRouter Router { get; }

    public event EventHandler? Changed;
}

internal sealed class OwnedHiddenFolderStore(OwnerStoreRouter router)
    : OwnedNotifyingStore(router, set => handler => set.HiddenFolders.Changed += handler), IHiddenFolderStore
{
    private IHiddenFolderStore For(string accountAddress) => Router.ForMailbox(accountAddress).HiddenFolders;
    public bool IsHidden(string accountAddress, string folderFullName) => For(accountAddress).IsHidden(accountAddress, folderFullName);
    public IReadOnlyCollection<string> GetHidden(string accountAddress) => For(accountAddress).GetHidden(accountAddress);

    public IReadOnlyList<(string Account, string Folder)> GetAll() =>
        Router.Reads.SelectMany(set => set.HiddenFolders.GetAll().Where(h => ReferenceEquals(Router.ForMailbox(h.Account), set))).ToList();

    public void Hide(string accountAddress, string folderFullName) => For(accountAddress).Hide(accountAddress, folderFullName);
    public void Unhide(string accountAddress, string folderFullName) => For(accountAddress).Unhide(accountAddress, folderFullName);
    public void UnhideAll(string accountAddress) => For(accountAddress).UnhideAll(accountAddress);
}

internal sealed class OwnedFolderOrderStore(OwnerStoreRouter router)
    : OwnedNotifyingStore(router, set => handler => set.FolderOrder.Changed += handler), IFolderOrderStore
{
    private IFolderOrderStore For(string accountAddress) => Router.ForMailbox(accountAddress).FolderOrder;
    public IReadOnlyList<string> GetOrder(string accountAddress) => For(accountAddress).GetOrder(accountAddress);
    public void SetOrder(string accountAddress, IReadOnlyList<string> folderFullNames) => For(accountAddress).SetOrder(accountAddress, folderFullNames);
    public void Clear(string accountAddress) => For(accountAddress).Clear(accountAddress);
    public void Rename(string accountAddress, string oldFolderFullName, string newFolderFullName) => For(accountAddress).Rename(accountAddress, oldFolderFullName, newFolderFullName);
}

internal sealed class OwnedAiAutonomyStore(OwnerStoreRouter router) : IAiAutonomyStore
{
    public Task<AutonomyTier> GetTierAsync(string accountKey, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).AiAutonomy.GetTierAsync(accountKey, cancellationToken);

    public Task SetTierAsync(string accountKey, AutonomyTier tier, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).AiAutonomy.SetTierAsync(accountKey, tier, cancellationToken);
}

internal sealed class OwnedAiResultCacheStore(OwnerStoreRouter router) : IAiResultCacheStore
{
    public Task<string?> GetAsync(string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).AiResultCache.GetAsync(accountKey, folderName, uid, kind, schemaVersion, attachmentPartSpecifier, cancellationToken);

    public Task SetAsync(string accountKey, string folderName, uint uid, AiResultCacheKind kind, int schemaVersion,
        string rawResult, string? attachmentPartSpecifier = null, CancellationToken cancellationToken = default) =>
        router.ForMailbox(accountKey).AiResultCache.SetAsync(accountKey, folderName, uid, kind, schemaVersion, rawResult, attachmentPartSpecifier, cancellationToken);
}

internal sealed class OwnedSecureMessageKeyStore(OwnerStoreRouter router) : ISecureMessageKeyStore
{
    private ISecureMessageKeyStore W => router.Current.SecureMessageKeys;

    private async Task<IReadOnlyList<T>> AllAsync<T>(Func<ISecureMessageKeyStore, Task<IReadOnlyList<T>>> get)
    {
        var all = new List<T>();
        foreach (var set in router.Reads)
            all.AddRange(await get(set.SecureMessageKeys).ConfigureAwait(false));
        return all;
    }

    public Task AddAsync(SecureMessageKeyRecord record, CancellationToken cancellationToken = default) => W.AddAsync(record, cancellationToken);

    public Task<SecureMessageKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken = default) =>
        router.Reads.FirstFoundAsync(s => s.SecureMessageKeys.GetAsync(keyId, cancellationToken));

    public async Task<IReadOnlyList<SecureMessageKeyRecord>> FindByRecipientAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        (await AllAsync(s => s.FindByRecipientAsync(emailAddress, cancellationToken)).ConfigureAwait(false)).DistinctBy(r => r.KeyId).ToList();

    public async Task<IReadOnlyList<SecureMessageKeyRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await AllAsync(s => s.GetAllAsync(cancellationToken)).ConfigureAwait(false)).DistinctBy(r => r.KeyId).ToList();

    public async Task<bool> UpdateKeyPackageAsync(string keyId, byte[] keyPackage, CancellationToken cancellationToken = default)
    {
        foreach (var set in router.Reads)
        {
            if (await set.SecureMessageKeys.UpdateKeyPackageAsync(keyId, keyPackage, cancellationToken).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    public Task RemoveAsync(string keyId, CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.SecureMessageKeys.RemoveAsync(keyId, cancellationToken));

    public async Task SaveRequestAsync(KeyRequestRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var set in router.Reads)
        {
            if (await set.SecureMessageKeys.GetRequestAsync(request.KeyId, cancellationToken).ConfigureAwait(false) is not null)
            {
                await set.SecureMessageKeys.SaveRequestAsync(request, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        await W.SaveRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<KeyRequestRecord?> GetRequestAsync(string keyId, CancellationToken cancellationToken = default) =>
        router.Reads.FirstFoundAsync(s => s.SecureMessageKeys.GetRequestAsync(keyId, cancellationToken));

    public async Task<IReadOnlyList<KeyRequestRecord>> GetRequestsAsync(CancellationToken cancellationToken = default) =>
        (await AllAsync(s => s.GetRequestsAsync(cancellationToken)).ConfigureAwait(false)).DistinctBy(r => r.KeyId).ToList();

    public Task RemoveRequestAsync(string keyId, CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.SecureMessageKeys.RemoveRequestAsync(keyId, cancellationToken));

    public Task AddShareAsync(KeyShareRecord share, CancellationToken cancellationToken = default) => W.AddShareAsync(share, cancellationToken);

    public async Task<IReadOnlyList<KeyShareRecord>> GetSharesAsync(CancellationToken cancellationToken = default) =>
        (await AllAsync(s => s.GetSharesAsync(cancellationToken)).ConfigureAwait(false)).OrderByDescending(s => s.AtUtc).ToList();

    public async Task<int> RemoveSharesOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var set in router.Reads)
            removed += await set.SecureMessageKeys.RemoveSharesOlderThanAsync(cutoffUtc, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public Task ClearSharesAsync(CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.SecureMessageKeys.ClearSharesAsync(cancellationToken));

    public Task AddCorrespondentsAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default) =>
        W.AddCorrespondentsAsync(addresses, cancellationToken);

    public Task<bool> IsKnownCorrespondentAsync(string address, CancellationToken cancellationToken = default) =>
        router.Reads.AnyAsync(s => s.SecureMessageKeys.IsKnownCorrespondentAsync(address, cancellationToken));
}

internal sealed class OwnedRecentRecipientStore(OwnerStoreRouter router) : IRecentRecipientStore
{
    private IRecentRecipientStore W => router.Current.RecentRecipients;

    // One line per address: the highest count, the latest use, and "don't ask" if either says so.
    private static List<RecentRecipient> Merge(IEnumerable<RecentRecipient> all) =>
        all.GroupBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecentRecipient(g.Key, g.Max(r => r.UseCount), g.Max(r => r.LastUsedUtc), g.Any(r => r.DontAskToSave)))
            .ToList();

    public Task<List<string>> RecordSentAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default) =>
        W.RecordSentAsync(addresses, cancellationToken);

    public async Task<List<RecentRecipient>> SearchAsync(string text, int max, CancellationToken cancellationToken = default)
    {
        var all = new List<RecentRecipient>();
        foreach (var set in router.Reads)
            all.AddRange(await set.RecentRecipients.SearchAsync(text, max, cancellationToken).ConfigureAwait(false));
        return Merge(all).OrderByDescending(r => r.LastUsedUtc).Take(max).ToList();
    }

    public async Task<List<RecentRecipient>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<RecentRecipient>();
        foreach (var set in router.Reads)
            all.AddRange(await set.RecentRecipients.GetAllAsync(cancellationToken).ConfigureAwait(false));
        return Merge(all);
    }

    public Task SetDontAskToSaveAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        var list = addresses.ToList();
        return router.Reads.ForEachAsync(s => s.RecentRecipients.SetDontAskToSaveAsync(list, cancellationToken));
    }

    public Task RemoveAsync(string address, CancellationToken cancellationToken = default) =>
        router.Reads.ForEachAsync(s => s.RecentRecipients.RemoveAsync(address, cancellationToken));

    public Task MergeFromSyncAsync(string address, int minUseCount, bool dontAskToSave, CancellationToken cancellationToken = default) =>
        W.MergeFromSyncAsync(address, minUseCount, dontAskToSave, cancellationToken);
}

/// <summary>Sync bookkeeping belongs to the write set only: each goCORA account syncs from its own state.</summary>
internal sealed class OwnedSyncStateStore(OwnerStoreRouter router) : ISyncStateStore
{
    private ISyncStateStore S => router.Current.SyncState;
    public Task<string?> GetOwnerAsync(CancellationToken cancellationToken = default) => S.GetOwnerAsync(cancellationToken);
    public Task SetOwnerAsync(string ownerUid, CancellationToken cancellationToken = default) => S.SetOwnerAsync(ownerUid, cancellationToken);
    public Task<SyncKindState> GetKindAsync(string kind, CancellationToken cancellationToken = default) => S.GetKindAsync(kind, cancellationToken);
    public Task SetKindAsync(SyncKindState state, CancellationToken cancellationToken = default) => S.SetKindAsync(state, cancellationToken);
    public Task<List<SyncItemState>> GetItemsAsync(string kind, CancellationToken cancellationToken = default) => S.GetItemsAsync(kind, cancellationToken);
    public Task SaveItemsAsync(string kind, IEnumerable<SyncItemState> items, CancellationToken cancellationToken = default) => S.SaveItemsAsync(kind, items, cancellationToken);
    public Task<int> PurgeDeletedAsync(DateTime beforeUtc, CancellationToken cancellationToken = default) => S.PurgeDeletedAsync(beforeUtc, cancellationToken);
    public Task<List<SyncHeldAccount>> GetHeldAccountsAsync(CancellationToken cancellationToken = default) => S.GetHeldAccountsAsync(cancellationToken);
    public Task SetHeldAccountAsync(SyncHeldAccount account, CancellationToken cancellationToken = default) => S.SetHeldAccountAsync(account, cancellationToken);
    public Task RemoveHeldAccountAsync(string emailAddress, CancellationToken cancellationToken = default) => S.RemoveHeldAccountAsync(emailAddress, cancellationToken);
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => S.GetValueAsync(key, cancellationToken);
    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => S.SetValueAsync(key, value, cancellationToken);
    public Task<List<SyncPendingTag>> GetPendingTagsAsync(CancellationToken cancellationToken = default) => S.GetPendingTagsAsync(cancellationToken);
    public Task SetPendingTagAsync(SyncPendingTag tag, CancellationToken cancellationToken = default) => S.SetPendingTagAsync(tag, cancellationToken);
    public Task RemovePendingTagAsync(string accountKey, string messageId, CancellationToken cancellationToken = default) => S.RemovePendingTagAsync(accountKey, messageId, cancellationToken);
    public Task AddHistoryAsync(IEnumerable<SyncHistoryEntry> entries, CancellationToken cancellationToken = default) => S.AddHistoryAsync(entries, cancellationToken);
    public Task<List<SyncHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default) => S.GetHistoryAsync(cancellationToken);
    public Task RemoveHistoryAsync(DateTime? beforeUtc, CancellationToken cancellationToken = default) => S.RemoveHistoryAsync(beforeUtc, cancellationToken);
    public Task ResetAsync(CancellationToken cancellationToken = default) => S.ResetAsync(cancellationToken);
}

/// <summary>The stores as one view sees them (<see cref="OwnerStoreRouter.ViewOf"/>).</summary>
internal sealed class StoreView : IStoreView
{
    public StoreView(OwnerStoreRouter router)
    {
        Accounts = new OwnedCredentialStore(router);
        Contacts = new OwnedContactStore(router);
        Tags = new OwnedTagStore(router);
        Blacklist = new OwnedBlacklistStore(router);
        TrustedImageSenders = new OwnedTrustedImageSenderStore(router);
        RecentRecipients = new OwnedRecentRecipientStore(router);
        HiddenFolders = new OwnedHiddenFolderStore(router);
        FolderOrder = new OwnedFolderOrderStore(router);
        SyncState = new OwnedSyncStateStore(router);
    }

    public ICredentialStore Accounts { get; }
    public IContactStore Contacts { get; }
    public ITagStore Tags { get; }
    public IBlacklistStore Blacklist { get; }
    public ITrustedImageSenderStore TrustedImageSenders { get; }
    public IRecentRecipientStore RecentRecipients { get; }
    public IHiddenFolderStore HiddenFolders { get; }
    public IFolderOrderStore FolderOrder { get; }
    public ISyncStateStore SyncState { get; }
}

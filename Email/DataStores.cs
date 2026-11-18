using CORA.Data.Contacts;
using CORA.Core.Security;

namespace CORA.Data.Email;

/// <summary>
/// Aggregates every domain-level local data store the app needs, so consumers depend
/// on a single facade instead of individual store interfaces or any encryption/storage
/// detail. Implemented by <see cref="SecureDataStores"/>, which owns all encryption,
/// key management, and file-path concerns internally.
/// </summary>
/// <summary>
/// The owned stores as one view sees them: only the current goCORA account's items, or only the
/// items without an owner (claude/design-per-account-ownership.md). Cloud sync works on the first.
/// </summary>
public interface IStoreView
{
    ICredentialStore Accounts { get; }
    IContactStore Contacts { get; }
    ITagStore Tags { get; }
    IBlacklistStore Blacklist { get; }
    ITrustedImageSenderStore TrustedImageSenders { get; }
    IRecentRecipientStore RecentRecipients { get; }
    IHiddenFolderStore HiddenFolders { get; }
    IFolderOrderStore FolderOrder { get; }
    ISyncStateStore SyncState { get; }
}

public interface IDataStores : IDisposable
{
    ITagStore Tags { get; }
    IMailSyncStore MailSync { get; }
    ICredentialStore Accounts { get; }
    ITrustedImageSenderStore TrustedImageSenders { get; }
    IBlacklistStore Blacklist { get; }
    IAiAutonomyStore AiAutonomy { get; }
    IContactStore Contacts { get; }
    IHiddenFolderStore HiddenFolders { get; }
    IFolderOrderStore FolderOrder { get; }
    IAiResultCacheStore AiResultCache { get; }
    ISecureMessageKeyStore SecureMessageKeys { get; }
    IRecentRecipientStore RecentRecipients { get; }
    ISyncStateStore SyncState { get; }

    /// <summary>Raised after a change that sync carries to other devices. May come from any thread.</summary>
    event Action? SyncableDataChanged;

    /// <summary>
    /// For the end of the app: one last flush of every store, then no more writes to disk, while
    /// the stores stay open. Work still running in the background (mail checks, timers) can then
    /// finish harmlessly instead of hitting closed LiteDB files ("Cannot access a disposed object
    /// ... ReaderWriterLockSlim"). Anything changed after this is not saved, as with Dispose.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Chooses whose items the stores show (claude/design-per-account-ownership.md).
    /// Signed in (<paramref name="signedIn"/>): that goCORA account's items plus items without an
    /// owner; new items belong to the account. Signed out: items without an owner, plus the last
    /// signed-in account's when <paramref name="showOwnerWhenSignedOut"/>; new items have no owner.
    /// A null or invalid <paramref name="ownerUid"/> shows only items without an owner.
    /// </summary>
    void UseOwner(string? ownerUid, bool signedIn, bool showOwnerWhenSignedOut);

    /// <summary>The goCORA account new items go to, or null when they get no owner.</summary>
    string? CurrentOwnerUid { get; }

    /// <summary>Only the current goCORA account's items (none of the ownerless ones): what its sync carries.</summary>
    IStoreView OwnedOnly { get; }

    /// <summary>Only the items without an owner.</summary>
    IStoreView OwnerlessOnly { get; }

    /// <summary>
    /// Sync state from before per-account data lived in the shared folder, owned by whichever
    /// account synced there. When <paramref name="ownerUid"/>'s own sync state is still empty and the
    /// shared one belongs to <paramref name="syncGroup"/> (that account's), it moves to the account,
    /// so items it no longer owns are taken out of its cloud. Returns whether it moved.
    /// </summary>
    Task<bool> AdoptOwnerlessSyncStateAsync(string ownerUid, string syncGroup);

    /// <summary>How many mail accounts a goCORA account has on this device (0 for an invalid id).</summary>
    int CountMailAccounts(string? ownerUid);

    /// <summary>
    /// The copies of a mail account kept on this device by others than the set in use (another goCORA
    /// account, or without an owner), with their server settings: one address = one mailbox, so adding
    /// it with a different incoming server is refused (stage 5, option C).
    /// </summary>
    Task<IReadOnlyList<MailAccount>> OtherMailAccountCopiesAsync(string address);

    /// <summary>
    /// Removes the shown copy of a mail account (the signed-in account's own first) with its tag
    /// assignments and folder order. Its downloaded mail, shared by every copy, is deleted only when
    /// no copy is left on this device (decision 5). Returns true when the mail was kept.
    /// </summary>
    Task<bool> RemoveMailAccountAsync(string address);

    /// <summary>Every mail account address on this device, whoever keeps it, once each (lowercase): the free mailbox limit counts these (decision 13).</summary>
    IReadOnlySet<string> MailAccountAddressesOnDevice();

    /// <summary>How many mail accounts all goCORA accounts' sets on this device hold, leaving out <paramref name="exceptOwnerUid"/>'s.</summary>
    int CountOwnedMailAccounts(string? exceptOwnerUid);

    /// <summary>What has no owner on this device: for "Keep these with …" (decision 14).</summary>
    Task<OwnerlessItems> GetOwnerlessItemsAsync();

    /// <summary>
    /// Gives the ticked ownerless items to <paramref name="ownerUid"/>: they move into that
    /// account's set (a mailbox with its hidden folders, folder order, tag assignments, image
    /// approvals and AI data) and are saved at once. The mail itself is shared and stays put.
    /// </summary>
    void GiveOwnerlessItems(string ownerUid, OwnerlessChoice choice);

    /// <summary>
    /// What else this device holds besides <paramref name="ownerUid"/>'s items: other goCORA
    /// accounts with items here, and items without an owner. For the Erase screen's caution.
    /// </summary>
    Task<OtherDataOnDevice> DescribeOtherDataAsync(string ownerUid);

    /// <summary>
    /// Takes a goCORA account's items off this device (Delete account, Erase "Only my data";
    /// claude/design-per-account-ownership.md, decision 12). <paramref name="keepWithoutOwner"/>
    /// true: everything moves to the ownerless set (its sync state is dropped). False: deleted,
    /// with the cached mail of its mailboxes and its contacts' photo files, unless another set
    /// on this device still has that mailbox or photo. Other accounts' items and the ownerless
    /// ones are never touched. Afterwards only the ownerless set is in use. Returns what
    /// couldn't be removed.
    /// </summary>
    Task<IReadOnlyList<string>> RemoveOwnerAsync(string ownerUid, bool keepWithoutOwner);

    /// <summary>
    /// Immediately flushes every store to disk without closing anything. Safe to call
    /// any number of times, including while the app keeps running afterward - unlike
    /// <see cref="IDisposable.Dispose"/>, this never tears down the stores. Intended for
    /// platform "about to background" lifecycle hooks (e.g. mobile OnSleep) where a real
    /// teardown signal (e.g. Window.Destroying) is not guaranteed to fire before the OS
    /// suspends or kills the process.
    /// </summary>
    void Flush();

    /// <summary>
    /// Flushes every store to disk, then runs <paramref name="action"/> while holding the same
    /// lock the periodic/lifecycle flushes take, so no flush can rewrite the files while the
    /// action reads them. Intended for a short, synchronous snapshot of the on-disk files
    /// (e.g. building a backup archive); keep it brief, and never put user prompts or awaits
    /// inside it, since flushes wait for it to finish.
    /// </summary>
    void RunAfterFlush(Action action);

    /// <summary>
    /// Permanently suppresses all future flushes (including Dispose) for this session.
    /// Call this immediately after a successful database restore so that the in-memory
    /// LiteDB state (which reflects the old data) is never written back to disk and the
    /// just-restored files on disk are preserved intact until the next cold launch.
    /// </summary>
    void SuppressFlush();

    /// <summary>
    /// Permanently deletes all local data held by these stores: every database file (plus its
    /// leftovers such as atomic-flush temp files and unrecovered-data backups) and the synced
    /// attachments folder. The stores are closed first, without flushing, so the open LiteDB
    /// instances neither hold the files locked nor write stale in-memory data back; this
    /// instance is unusable afterwards and the host should quit. The master encryption key is
    /// not touched. Returns the full paths of any files or folders that could not be deleted
    /// (empty when everything was removed).
    /// </summary>
    Task<IReadOnlyList<string>> WipeAllAsync();
}

/// <summary>
/// Abstraction over where Core-owned encrypted database/attachment files should live
/// on disk. Implemented by the host app (e.g. CORA.App via <c>FileSystem.AppDataDirectory</c>)
/// so CORA.Core has no dependency on any UI/platform framework, while still being the
/// only place that constructs actual file paths for its stores.
/// </summary>
public interface IAppDataLocation
{
    /// <summary>The app's private data directory, used as the base for all store files.</summary>
    string AppDataDirectory { get; }
}

/// <summary>
/// Internal capability implemented by every Core-owned <see cref="EncryptedLiteDbFile"/>-backed
/// store, letting <see cref="SecureDataStores"/> flush each store's encrypted file to disk
/// without needing to know about LiteDB, encryption, or file paths directly. Deliberately
/// internal: only Core code (specifically <see cref="SecureDataStores"/>'s lifecycle/periodic
/// flush) ever calls this - CORA.App only ever sees the domain interfaces in <see cref="IDataStores"/>.
/// </summary>
internal interface IFlushableStore
{
    /// <summary>Encrypts and writes the store's current in-memory contents to disk immediately.</summary>
    void Flush();

    /// <summary>Tells the underlying <see cref="EncryptedLiteDbFile"/> to skip its flush on Dispose.</summary>
    void SuppressFlush();
}

/// <summary>
/// Default <see cref="IDataStores"/> implementation. Owns the entire encryption/storage
/// boundary: loads the single master key at startup, opens each encrypted LiteDB file
/// with that key, and constructs the concrete domain store implementations. Consumers
/// (e.g. CORA.App) only ever see the domain interfaces exposed here — never key names,
/// SecureStorage, LiteDB encryption flags, or file paths.
/// </summary>
public sealed class SecureDataStores : IDataStores, IDisposable
{
    // Option B: safety-net periodic flush interval, in case the app process is killed
    // by the OS before Dispose() (Option A) ever runs.
    private static readonly TimeSpan AutoFlushInterval = TimeSpan.FromSeconds(27);

    // Contacts are written shortly after they change (debounced, so a bulk import produces one
    // write, not one per contact) rather than waiting for the periodic flush.
    private static readonly TimeSpan ContactsFlushDelay = TimeSpan.FromSeconds(1);

    // The mail itself is shared by every owner. Everything else lives in an OwnerStoreSet
    // (claude/design-per-account-ownership.md): the ownerless set (today's files) plus one set per
    // goCORA account opened on this device, reached through the router-backed layers below.
    private readonly MailSyncDatabase _mailSync;
    private readonly OwnerStoreRouter _router;
    private readonly Dictionary<string, OwnerStoreSet> _ownerSets = new(StringComparer.Ordinal);
    private readonly IPreferenceStore _preferences;
    private readonly string _baseDir;

    private readonly ITagStore _tags;
    private readonly ICredentialStore _accounts;
    private readonly ITrustedImageSenderStore _trustedImageSenders;
    private readonly IBlacklistStore _blacklist;
    private readonly IAiAutonomyStore _aiAutonomy;
    private readonly IContactStore _contacts;
    private readonly IHiddenFolderStore _hiddenFolders;
    private readonly IFolderOrderStore _folderOrder;
    private readonly IAiResultCacheStore _aiResultCache;
    private readonly ISecureMessageKeyStore _secureMessageKeys;
    private readonly IRecentRecipientStore _recentRecipients;
    private readonly ISyncStateStore _syncState;
    private readonly StoreView _ownedOnly;
    private readonly StoreView _ownerlessOnly;
    private readonly CancellationTokenSource _autoFlushCts = new();
    private readonly Task _autoFlushTask;
    private readonly Timer _contactsFlushTimer;
    // Serializes every flush with RunAfterFlush snapshots (e.g. backup) of the on-disk files.
    private readonly object _flushLock = new();
    private bool _disposed;
    private bool _flushSuppressed;

    public ITagStore Tags => _tags;
    public IMailSyncStore MailSync => _mailSync;
    public ICredentialStore Accounts => _accounts;
    public ITrustedImageSenderStore TrustedImageSenders => _trustedImageSenders;
    public IBlacklistStore Blacklist => _blacklist;
    public IAiAutonomyStore AiAutonomy => _aiAutonomy;
    public IContactStore Contacts => _contacts;
    public IHiddenFolderStore HiddenFolders => _hiddenFolders;
    public IFolderOrderStore FolderOrder => _folderOrder;
    public IAiResultCacheStore AiResultCache => _aiResultCache;
    public ISecureMessageKeyStore SecureMessageKeys => _secureMessageKeys;
    public IRecentRecipientStore RecentRecipients => _recentRecipients;
    public ISyncStateStore SyncState => _syncState;
    public event Action? SyncableDataChanged;

    private SecureDataStores(string baseDir, MailSyncDatabase mailSync, OwnerStoreSet ownerless, IPreferenceStore preferences)
    {
        _baseDir = baseDir;
        _mailSync = mailSync;
        _preferences = preferences;
        _router = new OwnerStoreRouter(ownerless);

        _tags = new OwnedTagStore(_router);
        _accounts = new OwnedCredentialStore(_router);
        _trustedImageSenders = new OwnedTrustedImageSenderStore(_router);
        _blacklist = new OwnedBlacklistStore(_router);
        _aiAutonomy = new OwnedAiAutonomyStore(_router);
        _contacts = new OwnedContactStore(_router);
        _hiddenFolders = new OwnedHiddenFolderStore(_router);
        _folderOrder = new OwnedFolderOrderStore(_router);
        _aiResultCache = new OwnedAiResultCacheStore(_router);
        _secureMessageKeys = new OwnedSecureMessageKeyStore(_router);
        _recentRecipients = new OwnedRecentRecipientStore(_router);
        _syncState = new OwnedSyncStateStore(_router);
        _ownedOnly = new StoreView(OwnerStoreRouter.ViewOf(_router, ownerlessOnly: false));
        _ownerlessOnly = new StoreView(OwnerStoreRouter.ViewOf(_router, ownerlessOnly: true));

        // Option B: background safety-net flush, so a mid-session OS kill (which never
        // gives Dispose() a chance to run) loses at most a few seconds of writes instead
        // of everything since the last lifecycle flush. CORA.App has no knowledge of this.
        _autoFlushTask = RunAutoFlushLoopAsync(_autoFlushCts.Token);

        _contactsFlushTimer = new Timer(_ => FlushContacts(), null, Timeout.Infinite, Timeout.Infinite);
        _hiddenFolders.Changed += (_, _) => SyncableDataChanged?.Invoke();
        _folderOrder.Changed += (_, _) => SyncableDataChanged?.Invoke();

        WireSet(ownerless);
        _router.Announce(ownerless);
    }

    // Changes in the current set are what sync carries; another owner's set is never synced from here.
    private void WireSet(OwnerStoreSet set)
    {
        void Syncable()
        {
            if (_router.IsCurrent(set))
                SyncableDataChanged?.Invoke();
        }
        set.Contacts.Changed = () =>
        {
            ScheduleContactsFlush();
            Syncable();
        };
        set.Tags.Changed = set.Blacklist.Changed = set.TrustedImageSenders.Changed = set.Accounts.Changed = set.RecentRecipients.Changed = Syncable;
    }

    // Every store with a file: the shared mail plus each open set. Caller must hold _flushLock.
    private IEnumerable<IFlushableStore> FlushablesLocked()
    {
        yield return _mailSync;
        foreach (var store in _router.Ownerless.Flushables)
            yield return store;
        foreach (var set in _ownerSets.Values)
        {
            foreach (var store in set.Flushables)
                yield return store;
        }
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        lock (_flushLock)
        {
            if (_disposed || _flushSuppressed)
                return;
            FlushAllLocked();
            _flushSuppressed = true;   // every later flush, the periodic one included, now does nothing
        }
        _autoFlushCts.Cancel();
    }

    /// <inheritdoc/>
    public void UseOwner(string? ownerUid, bool signedIn, bool showOwnerWhenSignedOut)
    {
        var owner = OwnerStoreSet.IsValidOwnerUid(ownerUid) ? OpenOwnerSet(ownerUid!) : null;
        if (owner is null)
            _router.Use(_router.Ownerless, null);
        else if (signedIn)
            _router.Use(owner, _router.Ownerless);
        else
            _router.Use(_router.Ownerless, showOwnerWhenSignedOut ? owner : null);
    }

    /// <inheritdoc/>
    public string? CurrentOwnerUid => _router.Current.OwnerUid;

    /// <inheritdoc/>
    public IStoreView OwnedOnly => _ownedOnly;

    /// <inheritdoc/>
    public IStoreView OwnerlessOnly => _ownerlessOnly;

    /// <inheritdoc/>
    public async Task<bool> AdoptOwnerlessSyncStateAsync(string ownerUid, string syncGroup)
    {
        if (string.IsNullOrEmpty(syncGroup))
            return false;
        var owner = OpenOwnerSet(ownerUid);
        if (await owner.SyncState.GetOwnerAsync().ConfigureAwait(false) is not null
            || await _router.Ownerless.SyncState.GetOwnerAsync().ConfigureAwait(false) != syncGroup)
            return false;
        lock (_flushLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnerMove.MoveWholeStore(_router.Ownerless.SyncState.Raw, owner.SyncState.Raw);
            FlushAllLocked();
        }
        return true;
    }

    /// <inheritdoc/>
    public int CountMailAccounts(string? ownerUid)
    {
        if (!OwnerStoreSet.IsValidOwnerUid(ownerUid))
            return 0;
        var raw = OpenOwnerSet(ownerUid!).Accounts.Raw;
        return raw.CollectionExists("Accounts") ? raw.GetCollection("Accounts").Count() : 0;
    }

    // Every set on this device: the ownerless one and each account's (opened as needed).
    private List<OwnerStoreSet> AllSets()
    {
        var sets = new List<OwnerStoreSet> { _router.Ownerless };
        sets.AddRange(OwnerUidsOnDisk().Select(OpenOwnerSet));
        return sets;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> MailAccountAddressesOnDevice()
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in AllSets().Select(s => s.Accounts.Raw).Where(r => r.CollectionExists("Accounts")))
        {
            foreach (var doc in raw.GetCollection("Accounts").FindAll())
            {
                if (doc["_id"].IsString)
                    addresses.Add(doc["_id"].AsString.Trim().ToLowerInvariant());
            }
        }
        return addresses;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MailAccount>> OtherMailAccountCopiesAsync(string address)
    {
        var current = _router.Current;
        var copies = new List<MailAccount>();
        foreach (var set in AllSets().Where(s => !ReferenceEquals(s, current) && s.Accounts.ContainsAccount(address)))
        {
            var key = address.Trim();
            copies.AddRange((await set.Accounts.LoadAllAsync().ConfigureAwait(false))
                .Where(a => string.Equals(a.EmailAddress.Trim(), key, StringComparison.OrdinalIgnoreCase)));
        }
        return copies;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveMailAccountAsync(string address)
    {
        // Its own data first, while the copy still decides which set they're in.
        await _tags.DeleteAccountAssignmentsAsync(address).ConfigureAwait(false);
        _folderOrder.Clear(address);
        await _accounts.DeleteAccountAsync(address).ConfigureAwait(false);
        if (AllSets().Any(s => s.Accounts.ContainsAccount(address)))
            return true;
        await _mailSync.DeleteAccountDataAsync(address).ConfigureAwait(false);
        return false;
    }

    /// <inheritdoc/>
    public int CountOwnedMailAccounts(string? exceptOwnerUid) =>
        OwnerUidsOnDisk().Where(u => u != exceptOwnerUid).Sum(u => CountMailAccounts(u));

    /// <inheritdoc/>
    public Task<OwnerlessItems> GetOwnerlessItemsAsync() => OwnerMove.DescribeAsync(_router.Ownerless);

    /// <inheritdoc/>
    public void GiveOwnerlessItems(string ownerUid, OwnerlessChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var owner = OpenOwnerSet(ownerUid);
        lock (_flushLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnerMove.Move(_router.Ownerless, owner, choice);
            FlushAllLocked();   // both sets on disk together, as close to one step as the files allow
        }
        _router.Refresh();
    }

    /// <inheritdoc/>
    public async Task<OtherDataOnDevice> DescribeOtherDataAsync(string ownerUid)
    {
        var others = 0;
        foreach (var uid in OwnerUidsOnDisk())
        {
            if (uid != ownerUid && !(await OwnerMove.DescribeAsync(OpenOwnerSet(uid)).ConfigureAwait(false)).IsEmpty)
                others++;
        }
        var ownerless = await OwnerMove.DescribeAsync(_router.Ownerless).ConfigureAwait(false);
        return new OtherDataOnDevice(others, OwnerMove.Count(ownerless));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RemoveOwnerAsync(string ownerUid, bool keepWithoutOwner)
    {
        if (!OwnerStoreSet.IsValidOwnerUid(ownerUid))
            throw new ArgumentException("Not a goCORA account id.", nameof(ownerUid));
        var owner = OpenOwnerSet(ownerUid);
        var mailboxes = (await owner.Accounts.LoadAllAsync().ConfigureAwait(false)).Select(a => a.EmailAddress).ToList();
        var photos = (await owner.Contacts.GetAllAsync().ConfigureAwait(false))
            .Select(c => ContactPhotoFiles.FileNameOf(c.PhotoPath)).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

        // Nothing reads or writes its set any more, then its items go (to the ownerless set, if kept).
        _router.Use(_router.Ownerless, null);
        lock (_flushLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (keepWithoutOwner)
                OwnerMove.MoveEverything(owner, _router.Ownerless);
            FlushAllLocked();   // with keepWithoutOwner, the ownerless set holds them on disk before the folder goes
            owner.SuppressFlush();
            _ownerSets.Remove(ownerUid);
        }
        owner.Dispose();
        _router.Refresh();

        var problems = new List<string>();
        DeleteOwnerDirectory(owner.Directory, problems);
        if (keepWithoutOwner)
            return problems;

        // The mail and photos are shared: only those no other set on this device still has.
        var others = new List<OwnerStoreSet> { _router.Ownerless };
        others.AddRange(OwnerUidsOnDisk().Where(u => u != ownerUid).Select(OpenOwnerSet));
        foreach (var address in mailboxes)
        {
            if (others.Any(s => s.Accounts.ContainsAccount(address)))
                continue;
            try
            {
                await _mailSync.DeleteAccountDataAsync(address).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                problems.Add($"Mail of {address} ({ex.Message})");
            }
        }
        foreach (var set in others)
        {
            foreach (var contact in await set.Contacts.GetAllAsync().ConfigureAwait(false))
                photos.Remove(ContactPhotoFiles.FileNameOf(contact.PhotoPath));
        }
        foreach (var name in photos)
        {
            var path = Path.Combine(_baseDir, ContactPhotoFiles.DirectoryName, name);
            if (File.Exists(path))
                TryDeleteFile(path, problems);
        }
        return problems;
    }

    // goCORA accounts with a folder of their own here (owners/<uid>/).
    private IEnumerable<string> OwnerUidsOnDisk()
    {
        var owners = Path.Combine(_baseDir, OwnerStoreSet.OwnersDirectoryName);
        if (!Directory.Exists(owners))
            return [];
        return Directory.EnumerateDirectories(owners).Select(Path.GetFileName).OfType<string>()
            .Where(OwnerStoreSet.IsValidOwnerUid).ToList();
    }

    // One account's folder: its store files (and their .tmp / .bak companions), then the folder.
    private static void DeleteOwnerDirectory(string ownerDir, List<string> failed)
    {
        if (!Directory.Exists(ownerDir))
            return;
        var failuresBefore = failed.Count;
        foreach (var name in OwnerStoreSet.FileNames)
        {
            foreach (var file in Directory.EnumerateFiles(ownerDir, name + "*").ToList())
                TryDeleteFile(file, failed);
        }
        if (failed.Count != failuresBefore)
            return;
        try
        {
            Directory.Delete(ownerDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(ownerDir);
        }
    }

    /// <summary>
    /// Opens (creating it empty the first time) a goCORA account's own set of stores in
    /// <c>owners/&lt;uid&gt;/</c>, or returns it if already open. It is flushed and closed with
    /// every other store.
    /// </summary>
    internal OwnerStoreSet OpenOwnerSet(string ownerUid)
    {
        lock (_flushLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownerSets.TryGetValue(ownerUid, out var open))
                return open;
            var set = OwnerStoreSet.OpenOwner(_baseDir, ownerUid, _preferences);
            if (_flushSuppressed)
                set.SuppressFlush();
            WireSet(set);
            _ownerSets[ownerUid] = set;
            _router.Announce(set);
            return set;
        }
    }

    private void ScheduleContactsFlush()
    {
        try
        {
            // Each change restarts the countdown, so the flush runs once things go quiet.
            _contactsFlushTimer.Change(ContactsFlushDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; Dispose does the final flush.
        }
    }

    // Goes through the same lock and suppression flag as FlushAll, so it never overlaps a
    // backup snapshot and never writes over files that a restore has just replaced.
    private void FlushContacts()
    {
        lock (_flushLock)
        {
            if (_disposed || _flushSuppressed)
                return;

            foreach (var set in _ownerSets.Values.Prepend(_router.Ownerless))
            {
                try
                {
                    ((IFlushableStore)set.Contacts).Flush();
                }
                catch
                {
                    // Still marked dirty, so the periodic flush retries it.
                }
            }
        }
    }

    private async Task RunAutoFlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AutoFlushInterval, cancellationToken).ConfigureAwait(false);
                FlushAll();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown (Dispose cancels the token) - not an error.
        }
    }

    private void FlushAll()
    {
        lock (_flushLock)
            FlushAllLocked();
    }

    // Caller must hold _flushLock.
    private void FlushAllLocked()
    {
        if (_flushSuppressed)
            return;

        foreach (var store in FlushablesLocked())
        {
            try
            {
                store.Flush();
            }
            catch
            {
                // Best-effort: a single store's flush failing must not stop the others
                // from being flushed or crash the background loop.
            }
        }
    }

    /// <inheritdoc/>
    public void RunAfterFlush(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_flushLock)
        {
            if (!_disposed)
                FlushAllLocked();
            action();
        }
    }

    /// <inheritdoc/>
    public void SuppressFlush() => _flushSuppressed = true;

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> WipeAllAsync()
    {
        // Same teardown as Dispose (stops the timers, closes every LiteDB instance so Windows
        // releases the file locks), but with flushing suppressed first so the stale in-memory
        // data is never written back over - or recreated in - the files deleted below.
        SuppressFlush();
        Dispose();

        return Task.Run<IReadOnlyList<string>>(() => DeleteStoreFiles(_baseDir));
    }

    private static List<string> DeleteStoreFiles(string baseDir)
    {
        var failed = new List<string>();
        if (!Directory.Exists(baseDir))
            return failed;

        // Each database plus anything named after it: the atomic-flush ".tmp", the
        // ".legacy-plaintext" migration copy and the ".unrecovered-*.bak" safety copies.
        foreach (var name in StoreFiles.Databases.Append(StoreFiles.SyncState).Append(StoreFiles.RecentRecipients).Append(StoreFiles.PendingLocalMail))
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, name + "*").ToList())
                TryDeleteFile(file, failed);
        }

        // Each goCORA account's own set (owners/<uid>/): same file names, so the same rules.
        var owners = Path.Combine(baseDir, OwnerStoreSet.OwnersDirectoryName);
        if (Directory.Exists(owners))
        {
            foreach (var ownerDir in Directory.EnumerateDirectories(owners).ToList())
                DeleteOwnerDirectory(ownerDir, failed);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(owners).Any())
                    Directory.Delete(owners);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(owners);
            }
        }

        var pendingAttachments = Path.Combine(baseDir, StoreFiles.PendingMailAttachments);
        if (Directory.Exists(pendingAttachments))
        {
            try
            {
                Directory.Delete(pendingAttachments, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(pendingAttachments);
            }
        }

        var attachments = Path.Combine(baseDir, StoreFiles.AttachmentsDirectory);
        if (Directory.Exists(attachments))
        {
            var failuresBefore = failed.Count;
            foreach (var file in Directory.EnumerateFiles(attachments, "*", SearchOption.AllDirectories).ToList())
                TryDeleteFile(file, failed);

            // Only report the folder itself when its files were all removed, so a locked file
            // isn't listed twice (once as the file, once as the folder that still contains it).
            if (failed.Count == failuresBefore)
            {
                try
                {
                    Directory.Delete(attachments, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(attachments);
                }
            }
        }

        return failed;
    }

    // Antivirus/indexers can briefly hold a just-closed file on Windows, so retry a couple of
    // times before reporting it (same approach as EncryptedLiteDbFile's atomic file replace).
    private static void TryDeleteFile(string path, List<string> failed)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                {
                    failed.Add(path);
                    return;
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Public, non-destructive flush for platform "about to background" hooks (see
    /// <see cref="IDataStores.Flush"/>). On mobile, Window.Destroying/app-teardown signals
    /// are not guaranteed to fire before the OS suspends or kills the process, so callers
    /// should invoke this from a reliably-called hook like OnSleep instead.
    /// </summary>
    public void Flush()
    {
        if (_disposed)
            return;
        FlushAll();
    }

    /// <summary>
    /// Starts CORA (the Rust core loads the master encryption key, creating it on first run, and
    /// holds it), opens every encrypted LiteDB-backed store, and returns the fully constructed
    /// facade. Must be called once, during app startup, before any store is used. Throws
    /// <see cref="CoraCoreUnavailableException"/> if the Rust core cannot be used on this device.
    /// </summary>
    public static async Task<IDataStores> CreateAsync(
        IAppDataLocation dataLocation, ISecureKeyStorage secureKeyStorage, IPreferenceStore preferences)
    {
        ArgumentNullException.ThrowIfNull(dataLocation);
        ArgumentNullException.ThrowIfNull(secureKeyStorage);
        ArgumentNullException.ThrowIfNull(preferences);

        // The Rust core loads (or creates) the master key and keeps it. Every encrypted LiteDB
        // file below is decrypted and saved by the Rust core - neither C# nor CORA.App sees the key.
        await Cora.InitializeAsync(secureKeyStorage).ConfigureAwait(false);

        var baseDir = dataLocation.AppDataDirectory;

        var mailSync = new MailSyncDatabase(
            Path.Combine(baseDir, StoreFiles.MailSync),
            Path.Combine(baseDir, StoreFiles.AttachmentsDirectory));
        OwnerStoreSet ownerless;
        try
        {
            ownerless = OwnerStoreSet.OpenOwnerless(baseDir, secureKeyStorage, preferences);
        }
        catch
        {
            ((IFlushableStore)mailSync).SuppressFlush();
            mailSync.Dispose();
            throw;
        }

        return new SecureDataStores(baseDir, mailSync, ownerless, preferences);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Option A: stop the Option B safety-net loop and perform one last, deterministic
        // flush+close of every store as part of the normal app shutdown lifecycle.
        _autoFlushCts.Cancel();
        try
        {
            _autoFlushTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        _autoFlushCts.Dispose();

        // Stop the contacts timer, then wait out any flush already running: the lock is held
        // for its duration, and any later one sees _disposed and does nothing.
        _contactsFlushTimer.Dispose();
        lock (_flushLock) { }

        // If a restore was performed this session, propagate the suppression flag down to
        // every EncryptedLiteDbFile before calling Dispose() on them, so their Dispose()
        // skips the Flush() that would overwrite the just-restored files on disk.
        if (_flushSuppressed)
        {
            foreach (var store in FlushablesLocked())
                store.SuppressFlush();
        }

        // Best-effort per store: a failed final flush in one (e.g. the atomic file replace
        // still being blocked after its retries) must not stop the others from flushing.
        try
        {
            _mailSync.Dispose();
        }
        catch
        {
        }
        _router.Ownerless.Dispose();
        foreach (var set in _ownerSets.Values)
            set.Dispose();
    }
}

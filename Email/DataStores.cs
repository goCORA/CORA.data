using CORA.Data.Contacts;
using CORA.Core.Security;

namespace CORA.Data.Email;

/// <summary>
/// Aggregates every domain-level local data store the app needs, so consumers depend
/// on a single facade instead of individual store interfaces or any encryption/storage
/// detail. Implemented by <see cref="SecureDataStores"/>, which owns all encryption,
/// key management, and file-path concerns internally.
/// </summary>
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

    private readonly TagDatabase _tags;
    private readonly MailSyncDatabase _mailSync;
    private readonly AccountCredentialDatabase _accounts;
    private readonly TrustedImageSenderDatabase _trustedImageSenders;
    private readonly BlacklistDatabase _blacklist;
    private readonly AiAutonomyDatabase _aiAutonomy;
    private readonly ContactDatabase _contacts;
    private readonly HiddenFolderDatabase _hiddenFolders;
    private readonly FolderOrderDatabase _folderOrder;
    private readonly AiResultCacheDatabase _aiResultCache;
    private readonly IFlushableStore[] _flushableStores;
    private readonly string _baseDir;
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

    private SecureDataStores(
        string baseDir,
        TagDatabase tags, MailSyncDatabase mailSync, AccountCredentialDatabase accounts,
        TrustedImageSenderDatabase trustedImageSenders, BlacklistDatabase blacklist,
        AiAutonomyDatabase aiAutonomy, ContactDatabase contacts, HiddenFolderDatabase hiddenFolders,
        FolderOrderDatabase folderOrder, AiResultCacheDatabase aiResultCache)
    {
        _baseDir = baseDir;
        _tags = tags;
        _mailSync = mailSync;
        _accounts = accounts;
        _trustedImageSenders = trustedImageSenders;
        _blacklist = blacklist;
        _aiAutonomy = aiAutonomy;
        _contacts = contacts;
        _hiddenFolders = hiddenFolders;
        _folderOrder = folderOrder;
        _aiResultCache = aiResultCache;
        _flushableStores = [_tags, _mailSync, _accounts, _trustedImageSenders, _blacklist, _aiAutonomy, _contacts, _hiddenFolders, _folderOrder, _aiResultCache];

        // Option B: background safety-net flush, so a mid-session OS kill (which never
        // gives Dispose() a chance to run) loses at most a few seconds of writes instead
        // of everything since the last lifecycle flush. CORA.App has no knowledge of this.
        _autoFlushTask = RunAutoFlushLoopAsync(_autoFlushCts.Token);

        _contactsFlushTimer = new Timer(_ => FlushContacts(), null, Timeout.Infinite, Timeout.Infinite);
        _contacts.Changed = ScheduleContactsFlush;
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

            try
            {
                ((IFlushableStore)_contacts).Flush();
            }
            catch
            {
                // Still marked dirty, so the periodic flush retries it.
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

        foreach (var store in _flushableStores)
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
        foreach (var name in StoreFiles.Databases)
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, name + "*").ToList())
                TryDeleteFile(file, failed);
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
    /// Loads the single master encryption key (creating it on first run), opens every
    /// encrypted LiteDB-backed store with it, and returns the fully constructed facade.
    /// Must be called once, during app startup, before any store is used.
    /// </summary>
    public static async Task<IDataStores> CreateAsync(
        IAppDataLocation dataLocation, ISecureKeyStorage secureKeyStorage, IPreferenceStore preferences)
    {
        ArgumentNullException.ThrowIfNull(dataLocation);
        ArgumentNullException.ThrowIfNull(secureKeyStorage);
        ArgumentNullException.ThrowIfNull(preferences);

        // Loads (or creates) the single master key into memory once. Every encrypted
        // LiteDB file below is opened using this in-memory key — CORA.App never sees it.
        await Cora.InitializeAsync(secureKeyStorage).ConfigureAwait(false);

        var baseDir = dataLocation.AppDataDirectory;

        var tags = new TagDatabase(Path.Combine(baseDir, StoreFiles.Tags));
        var mailSync = new MailSyncDatabase(
            Path.Combine(baseDir, StoreFiles.MailSync),
            Path.Combine(baseDir, StoreFiles.AttachmentsDirectory));
        var accounts = new AccountCredentialDatabase(
            Path.Combine(baseDir, StoreFiles.Accounts), secureKeyStorage, preferences);
        var trustedImageSenders = new TrustedImageSenderDatabase(
            Path.Combine(baseDir, StoreFiles.TrustedImageSenders));
        var blacklist = new BlacklistDatabase(
            Path.Combine(baseDir, StoreFiles.Blacklist));
        var aiAutonomy = new AiAutonomyDatabase(
            Path.Combine(baseDir, StoreFiles.AiAutonomy));
        var aiResultCache = new AiResultCacheDatabase(
            Path.Combine(baseDir, StoreFiles.AiResultCache));

        // Older versions kept contacts.litedb as a plaintext LiteDB file. Convert it before the
        // encrypted store opens that path (Open() would treat the plaintext file as unreadable
        // and start empty). A failure here must not block startup, and no data is deleted:
        // if it fails after moving the plaintext aside, the next launch resumes it; if it fails
        // earlier, Open() keeps the unreadable file as contacts.litedb.unrecovered-*.bak.
        var contactsPath = Path.Combine(baseDir, StoreFiles.Contacts);
        try
        {
            LegacyContactMigration.MigrateIfNeeded(contactsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy contacts migration failed; will retry next launch: {ex}");
        }
        var contacts = new ContactDatabase(contactsPath);

        // The hidden-folder list used to live in a plaintext preference; move it into the encrypted
        // store. Best-effort: on failure the preference stays and the import is retried next launch.
        var hiddenFolders = new HiddenFolderDatabase(Path.Combine(baseDir, StoreFiles.HiddenFolders));
        try
        {
            hiddenFolders.ImportLegacyPreference(preferences);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hidden-folder preference import failed; will retry next launch: {ex}");
        }

        var folderOrder = new FolderOrderDatabase(Path.Combine(baseDir, StoreFiles.FolderOrder));

        return new SecureDataStores(baseDir, tags, mailSync, accounts, trustedImageSenders, blacklist, aiAutonomy, contacts, hiddenFolders, folderOrder, aiResultCache);
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
            foreach (var store in _flushableStores)
                store.SuppressFlush();
        }

        // Best-effort per store: a failed final flush in one (e.g. the atomic file replace
        // still being blocked after its retries) must not stop the others from flushing.
        foreach (var store in (IDisposable[])[_tags, _mailSync, _accounts, _trustedImageSenders, _blacklist, _aiAutonomy, _contacts, _hiddenFolders, _folderOrder, _aiResultCache])
        {
            try
            {
                store.Dispose();
            }
            catch
            {
            }
        }
    }
}

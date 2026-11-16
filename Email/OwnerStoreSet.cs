using CORA.Core.Security;
using CORA.Data.Contacts;

namespace CORA.Data.Email;

/// <summary>
/// One goCORA account's own stores (claude/design-per-account-ownership.md): where an item is
/// stored is who owns it. The ownerless set is the app data folder itself, exactly as before
/// per-account data existed, so existing installs keep their files untouched and they simply
/// have no owner. Each goCORA account's set lives in <c>owners/&lt;uid&gt;/</c> with the same file
/// names. The mail itself (<see cref="StoreFiles.MailSync"/> and its attachments) is shared and
/// is not part of any set.
/// </summary>
internal sealed class OwnerStoreSet : IDisposable
{
    /// <summary>Folder under the app data folder that holds one subfolder per goCORA account.</summary>
    public const string OwnersDirectoryName = "owners";

    private OwnerStoreSet(
        string? ownerUid, string directory,
        AccountCredentialDatabase accounts, TagDatabase tags, TrustedImageSenderDatabase trustedImageSenders,
        BlacklistDatabase blacklist, AiAutonomyDatabase aiAutonomy, ContactDatabase contacts,
        HiddenFolderDatabase hiddenFolders, FolderOrderDatabase folderOrder, AiResultCacheDatabase aiResultCache,
        SecureMessageKeyDatabase secureMessageKeys, RecentRecipientDatabase recentRecipients, SyncStateDatabase syncState)
    {
        OwnerUid = ownerUid;
        Directory = directory;
        Accounts = accounts;
        Tags = tags;
        TrustedImageSenders = trustedImageSenders;
        Blacklist = blacklist;
        AiAutonomy = aiAutonomy;
        Contacts = contacts;
        HiddenFolders = hiddenFolders;
        FolderOrder = folderOrder;
        AiResultCache = aiResultCache;
        SecureMessageKeys = secureMessageKeys;
        RecentRecipients = recentRecipients;
        SyncState = syncState;
        Flushables = [Tags, Accounts, TrustedImageSenders, Blacklist, AiAutonomy, Contacts, HiddenFolders, FolderOrder, AiResultCache, SecureMessageKeys, RecentRecipients, SyncState];
    }

    /// <summary>The goCORA account (Firebase uid) these stores belong to; null for the ownerless set.</summary>
    public string? OwnerUid { get; }

    public bool IsOwnerless => OwnerUid is null;

    /// <summary>The folder holding this set's files.</summary>
    public string Directory { get; }

    public AccountCredentialDatabase Accounts { get; }
    public TagDatabase Tags { get; }
    public TrustedImageSenderDatabase TrustedImageSenders { get; }
    public BlacklistDatabase Blacklist { get; }
    public AiAutonomyDatabase AiAutonomy { get; }
    public ContactDatabase Contacts { get; }
    public HiddenFolderDatabase HiddenFolders { get; }
    public FolderOrderDatabase FolderOrder { get; }
    public AiResultCacheDatabase AiResultCache { get; }
    public SecureMessageKeyDatabase SecureMessageKeys { get; }
    public RecentRecipientDatabase RecentRecipients { get; }
    public SyncStateDatabase SyncState { get; }

    public IFlushableStore[] Flushables { get; }

    /// <summary>
    /// The file names a set holds (every database except the shared mail), so backups, wiping
    /// and future owner moves agree on what belongs to an owner.
    /// </summary>
    public static readonly string[] FileNames =
    [
        StoreFiles.Tags,
        StoreFiles.Accounts,
        StoreFiles.Contacts,
        StoreFiles.TrustedImageSenders,
        StoreFiles.Blacklist,
        StoreFiles.AiAutonomy,
        StoreFiles.HiddenFolders,
        StoreFiles.AiResultCache,
        StoreFiles.FolderOrder,
        StoreFiles.SecureMessageKeys,
        StoreFiles.RecentRecipients,
        StoreFiles.SyncState,
    ];

    /// <summary>
    /// The folder of a goCORA account's set. Firebase uids are letters and digits; anything else
    /// is refused so a uid can never point outside <c>owners/</c>.
    /// </summary>
    public static string OwnerDirectory(string baseDir, string ownerUid)
    {
        if (!IsValidOwnerUid(ownerUid))
            throw new ArgumentException("Not a goCORA account id.", nameof(ownerUid));
        return Path.Combine(baseDir, OwnersDirectoryName, ownerUid);
    }

    public static bool IsValidOwnerUid(string? ownerUid) =>
        !string.IsNullOrEmpty(ownerUid) && ownerUid.Length <= 128
        && ownerUid.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// The ownerless set: today's files in the app data folder, with the one-time imports from
    /// older versions (plaintext contacts, the hidden-folder preference, accounts in secure storage).
    /// </summary>
    public static OwnerStoreSet OpenOwnerless(string baseDir, ISecureKeyStorage legacySecureStorage, IPreferenceStore preferences)
    {
        // Older versions kept contacts.litedb as a plaintext LiteDB file. Convert it before the
        // encrypted store opens that path (Open() would treat the plaintext file as unreadable
        // and start empty). A failure here must not block startup, and no data is deleted:
        // if it fails after moving the plaintext aside, the next launch resumes it; if it fails
        // earlier, Open() keeps the unreadable file as contacts.litedb.unrecovered-*.bak.
        try
        {
            LegacyContactMigration.MigrateIfNeeded(Path.Combine(baseDir, StoreFiles.Contacts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy contacts migration failed; will retry next launch: {ex}");
        }

        var set = Open(null, baseDir, legacySecureStorage, preferences);

        // The hidden-folder list used to live in a plaintext preference; move it into the encrypted
        // store. Best-effort: on failure the preference stays and the import is retried next launch.
        try
        {
            set.HiddenFolders.ImportLegacyPreference(preferences);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hidden-folder preference import failed; will retry next launch: {ex}");
        }
        return set;
    }

    /// <summary>
    /// One goCORA account's set, created empty the first time. No imports from older versions:
    /// those belong to the ownerless set only.
    /// </summary>
    public static OwnerStoreSet OpenOwner(string baseDir, string ownerUid, IPreferenceStore preferences)
    {
        var directory = OwnerDirectory(baseDir, ownerUid);
        System.IO.Directory.CreateDirectory(directory);
        return Open(ownerUid, directory, NoLegacyStorage.Instance, preferences);
    }

    private static OwnerStoreSet Open(string? ownerUid, string directory, ISecureKeyStorage legacySecureStorage, IPreferenceStore preferences)
    {
        var opened = new List<IDisposable>();
        T Track<T>(T store) where T : IDisposable
        {
            opened.Add(store);
            return store;
        }

        try
        {
            string P(string name) => Path.Combine(directory, name);
            return new OwnerStoreSet(
                ownerUid, directory,
                Track(new AccountCredentialDatabase(P(StoreFiles.Accounts), legacySecureStorage, preferences)),
                Track(new TagDatabase(P(StoreFiles.Tags))),
                Track(new TrustedImageSenderDatabase(P(StoreFiles.TrustedImageSenders))),
                Track(new BlacklistDatabase(P(StoreFiles.Blacklist))),
                Track(new AiAutonomyDatabase(P(StoreFiles.AiAutonomy))),
                Track(new ContactDatabase(P(StoreFiles.Contacts))),
                Track(new HiddenFolderDatabase(P(StoreFiles.HiddenFolders))),
                Track(new FolderOrderDatabase(P(StoreFiles.FolderOrder))),
                Track(new AiResultCacheDatabase(P(StoreFiles.AiResultCache))),
                Track(new SecureMessageKeyDatabase(P(StoreFiles.SecureMessageKeys))),
                Track(new RecentRecipientDatabase(P(StoreFiles.RecentRecipients))),
                Track(new SyncStateDatabase(P(StoreFiles.SyncState))));
        }
        catch
        {
            // Nothing was written yet: close what opened without flushing over the files.
            foreach (var store in opened)
            {
                (store as IFlushableStore)?.SuppressFlush();
                try { store.Dispose(); } catch { }
            }
            throw;
        }
    }

    public void SuppressFlush()
    {
        foreach (var store in Flushables)
            store.SuppressFlush();
    }

    /// <summary>Flushes and closes every store. Best-effort per store, like the shutdown before sets existed.</summary>
    public void Dispose()
    {
        foreach (var store in (IDisposable[])[Tags, Accounts, TrustedImageSenders, Blacklist, AiAutonomy, Contacts, HiddenFolders, FolderOrder, AiResultCache, SecureMessageKeys, RecentRecipients, SyncState])
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

    // A goCORA account's set never imports the old secure-storage accounts.
    private sealed class NoLegacyStorage : ISecureKeyStorage
    {
        public static readonly NoLegacyStorage Instance = new();
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value) => Task.CompletedTask;
        public Task RemoveAsync(string key) => Task.CompletedTask;
    }
}

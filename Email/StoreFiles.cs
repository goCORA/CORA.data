namespace CORA.Data.Email;

/// <summary>
/// The on-disk layout of CORA's local stores, kept in one place: <see cref="SecureDataStores"/>
/// opens these files, <see cref="Backup.DatabaseBackupService"/> archives/restores them, and
/// <see cref="IDataStores.WipeAllAsync"/> deletes them. Adding a store means adding it here.
/// </summary>
internal static class StoreFiles
{
    public const string Tags = "tags.litedb";
    public const string MailSync = "mailsync.litedb";
    public const string Accounts = "accounts.litedb";
    public const string Contacts = "contacts.litedb";
    public const string TrustedImageSenders = "trustedImageSenders.litedb";
    public const string Blacklist = "blacklist.litedb";
    public const string AiAutonomy = "ai_autonomy.litedb";
    public const string HiddenFolders = "hidden_folders.litedb";
    public const string AiResultCache = "ai_result_cache.litedb";

    /// <summary>Subdirectory of the app data directory holding synced (POP3) attachments.</summary>
    public const string AttachmentsDirectory = "mail_attachments";

    /// <summary>Every encrypted database file, in the order they are opened/archived.</summary>
    public static readonly string[] Databases =
    [
        Tags,
        MailSync,
        Accounts,
        Contacts,
        TrustedImageSenders,
        Blacklist,
        AiAutonomy,
        HiddenFolders,
        AiResultCache,
    ];
}

using System.IO.Compression;
using CORA.Core.Security;
using CORA.Data.Contacts;
using CORA.Data.Email;

namespace CORA.Data.Backup;

/// <summary>
/// Routines for backing up/restoring CORA's local encrypted database files (the backup file
/// itself, protected through the CORA key code, also carries the master key). This class only deals with the file system (via
/// <see cref="IAppDataLocation"/>) and key storage (via <see cref="ISecureKeyStorage"/>) -
/// it has no knowledge of any host-app UI/platform concern (file pickers, save dialogs,
/// etc.), which callers (e.g. CORA.App) are responsible for wiring up. Database files
/// are zipped/unzipped as-is (they stay encrypted at rest under the master key, which the backup
/// file carries, sealed under the CORA key code). Restoring requires an app restart, since the
/// encrypted stores are kept open for the lifetime of the process.
/// </summary>
public sealed class DatabaseBackupService
{
    // The file layout lives in StoreFiles, shared with SecureDataStores (open/wipe).
    private static readonly string[] DatabaseFileNames = StoreFiles.Databases;

    private const string AttachmentsDirectoryName = StoreFiles.AttachmentsDirectory;
    private const string ContactPhotosDirectoryName = ContactPhotoFiles.DirectoryName;

    private readonly IAppDataLocation _dataLocation;
    private readonly ISecureKeyStorage _secureKeyStorage;

    public DatabaseBackupService(IAppDataLocation dataLocation, ISecureKeyStorage secureKeyStorage)
    {
        ArgumentNullException.ThrowIfNull(dataLocation);
        ArgumentNullException.ThrowIfNull(secureKeyStorage);

        _dataLocation = dataLocation;
        _secureKeyStorage = secureKeyStorage;
    }

    /// <summary>
    /// Zips the local database files (with mail attachments and contact photos) into <paramref name="destination"/>.
    /// The caller owns <paramref name="destination"/> (e.g. a temp file stream or a
    /// stream backed by a platform save-file dialog).
    /// </summary>
    /// <summary>Marks a backup made per goCORA account (claude/design-per-account-ownership.md, decision 11).</summary>
    public const string LayoutEntryName = "layout.txt";
    private const string LayoutPerAccount = "per-account 1";

    /// <summary>Folder in a per-account backup holding the backed-up account's store files.</summary>
    public const string OwnerFolder = "owner";

    /// <summary>
    /// What a per-account local backup holds: the account's own store files (<paramref name="OwnerUid"/>,
    /// null for none), the stores without an owner when <paramref name="IncludeOwnerless"/>, the mail
    /// of those mail accounts (exported to <paramref name="LocalMailPath"/> with its
    /// <paramref name="MailAttachments"/>), and the photos of those contacts.
    /// </summary>
    public sealed record BackupContent(
        string? OwnerUid, bool IncludeOwnerless, string LocalMailPath,
        IReadOnlyCollection<string> MailAttachments, IReadOnlySet<string> ContactPhotos);

    /// <summary>
    /// A local backup, per goCORA account: <c>owner/</c> holds the account's store files; the
    /// stores without an owner are at the top only if the user chose to include them. The mail is
    /// that of the included mail accounts only (never another account's), as a mail file that is
    /// added on restore, like an online backup's. Contact photos: only those of included contacts.
    /// </summary>
    public void CreateBackupArchive(Stream destination, BackupContent content)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(content);

        var baseDir = _dataLocation.AppDataDirectory;
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        using (var writer = new StreamWriter(zip.CreateEntry(LayoutEntryName).Open()))
            writer.Write(LayoutPerAccount);

        var storeFiles = DatabaseFileNames.Where(n => n != StoreFiles.MailSync).ToList();
        if (OwnerStoreSet.IsValidOwnerUid(content.OwnerUid))
        {
            var ownerDir = OwnerStoreSet.OwnerDirectory(baseDir, content.OwnerUid!);
            foreach (var name in storeFiles)
                AddFile(zip, Path.Combine(ownerDir, name), OwnerFolder + "/" + name);
        }
        if (content.IncludeOwnerless)
        {
            foreach (var name in storeFiles)
                AddFile(zip, Path.Combine(baseDir, name), name);
        }

        zip.CreateEntryFromFile(content.LocalMailPath, StoreFiles.PendingLocalMail, CompressionLevel.Optimal);
        var attachments = Path.Combine(baseDir, AttachmentsDirectoryName);
        foreach (var relative in content.MailAttachments.Distinct())
        {
            var path = Path.Combine(attachments, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                zip.CreateEntryFromFile(path, $"{StoreFiles.PendingMailAttachments}/{relative}", CompressionLevel.Optimal);
        }

        var photos = Path.Combine(baseDir, ContactPhotosDirectoryName);
        foreach (var name in content.ContactPhotos)
        {
            if (name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && File.Exists(Path.Combine(photos, name)))
                zip.CreateEntryFromFile(Path.Combine(photos, name), ContactPhotosDirectoryName + "/" + name, CompressionLevel.Optimal);
        }
    }

    // A store file read while it may be open elsewhere; skipped (not fatal) if it can't be read.
    private static void AddFile(ZipArchive zip, string fullPath, string entryName)
    {
        if (!File.Exists(fullPath))
            return;
        try
        {
            using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var entryStream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            fileStream.CopyTo(entryStream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CreateBackupArchive: could not include {fullPath}: {ex}");
        }
    }

    /// <summary>The backup layout from before per-account data (everything at the top, the whole mail file). Kept for older callers and tests.</summary>
    public void CreateBackupArchive(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var baseDir = _dataLocation.AppDataDirectory;

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var fileName in DatabaseFileNames)
        {
            var fullPath = Path.Combine(baseDir, fileName);
            if (!File.Exists(fullPath))
                continue;

            // Try to open the file for reading allowing shared read/write access.
            // Some platforms/databases keep the file open and deny exclusive read access;
            // opening with FileShare.ReadWrite increases the chance we can snapshot it.
            try
            {
                using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                fileStream.CopyTo(entryStream);
            }
            catch (Exception ex)
            {
                // If opening the file fails (locked exclusively), skip it rather than failing the whole backup.
                // Log via Debug only — callers can still create backups that include other files.
                System.Diagnostics.Debug.WriteLine($"CreateBackupArchive: could not include {fullPath}: {ex}");
            }
        }

        AddDirectory(zip, baseDir, AttachmentsDirectoryName);
        AddDirectory(zip, baseDir, ContactPhotosDirectoryName);
    }

    // Entry names always use '/', so a backup made on Windows restores into folders on Android/iOS
    // (with '\' the files landed in the data folder under names containing backslashes).
    private static void AddDirectory(ZipArchive zip, string baseDir, string directoryName)
    {
        var dir = Path.Combine(baseDir, directoryName);
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var relative = directoryName + "/" + Path.GetRelativePath(dir, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }
    }

    // An entry's name with '/' separators (older Windows backups used '\').
    private static string EntryName(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    private static bool IsIn(string entryName, string directoryName) =>
        entryName.StartsWith(directoryName + "/", StringComparison.OrdinalIgnoreCase);

    // Where an entry is extracted, or null when its name would leave the data folder ("../").
    private static string? Destination(string baseDir, string entryName)
    {
        var root = Path.GetFullPath(baseDir);
        var path = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path : null;
    }

    private static void Extract(ZipArchiveEntry entry, string baseDir, string entryName)
    {
        var destinationPath = Destination(baseDir, entryName);
        if (destinationPath is null)
            return;
        var destinationDir = Path.GetDirectoryName(destinationPath);
        // Only create a subdirectory when the entry lives in one (mail_attachments/…, contact_photos/…).
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    /// <summary>
    /// Extracts a previously-created backup archive (see <see cref="CreateBackupArchive"/>)
    /// over the local database files, overwriting any existing ones.
    /// </summary>
    public void RestoreBackupArchive(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var baseDir = _dataLocation.AppDataDirectory;

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            var name = EntryName(entry);
            if (name.Length == 0 || name.EndsWith('/'))
                continue; // directory entry
            Extract(entry, baseDir, name);
        }
    }

    /// <summary>
    /// Extracts only selected parts of a previously-created backup archive over the local
    /// database files, overwriting any existing ones. Callers can choose which logical
    /// stores to restore.
    /// </summary>
    /// <summary>
    /// Restores the selected parts. A per-account backup's <c>owner/</c> files go to
    /// <paramref name="ownerUid"/>'s own stores (the restoring account, decision 11); its other store
    /// files stay without an owner; its mail is added at the next start (never replacing mail).
    /// A backup from before per-account data restores as it always did, without an owner.
    /// </summary>
    public void RestoreBackupArchive(Stream source, bool restoreAccounts, bool restoreTags, bool restoreContacts, bool restoreMailboxes, bool restoreTrustedImageSenders, bool restoreBlacklist, bool restoreAiAutonomy, bool restoreAiResultCache, string? ownerUid = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var baseDir = _dataLocation.AppDataDirectory;

        // Map logical selections to the filenames used in backups.
        var allowFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (restoreTags) allowFiles.Add(StoreFiles.Tags);
        // Which folders an account hides from the menu, and in what order it lists them, are
        // per-account settings, so they travel with the accounts toggle rather than getting a
        // switch of their own.
        if (restoreAccounts)
        {
            allowFiles.Add(StoreFiles.Accounts);
            allowFiles.Add(StoreFiles.HiddenFolders);
            allowFiles.Add(StoreFiles.FolderOrder);
        }
        if (restoreContacts) allowFiles.Add(StoreFiles.Contacts);
        // The keys of secure emails sent from this device belong with the mail itself.
        if (restoreMailboxes)
        {
            allowFiles.Add(StoreFiles.MailSync);
            allowFiles.Add(StoreFiles.SecureMessageKeys);
        }
        if (restoreTrustedImageSenders) allowFiles.Add(StoreFiles.TrustedImageSenders);
        if (restoreBlacklist) allowFiles.Add(StoreFiles.Blacklist);
        if (restoreAiAutonomy) allowFiles.Add(StoreFiles.AiAutonomy);
        if (restoreAiResultCache) allowFiles.Add(StoreFiles.AiResultCache);

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            var name = EntryName(entry);
            if (name.Length == 0 || name.EndsWith('/'))
                continue; // directory entry

            // Per-account backups: the account's own files, and the mail to add at the next start.
            if (IsIn(name, OwnerFolder))
            {
                var inner = name[(OwnerFolder.Length + 1)..];
                if (inner.Contains('/') || !allowFiles.Contains(inner) || inner == StoreFiles.MailSync)
                    continue;
                Extract(entry, baseDir, OwnerStoreSet.IsValidOwnerUid(ownerUid)
                    ? $"{OwnerStoreSet.OwnersDirectoryName}/{ownerUid}/{inner}"
                    : inner);
                continue;
            }
            if (name == StoreFiles.PendingLocalMail || IsIn(name, StoreFiles.PendingMailAttachments))
            {
                if (restoreMailboxes)
                    Extract(entry, baseDir, name);
                continue;
            }

            // Mail attachments come back with the mailboxes, contact photos with the contacts;
            // everything else must be an allowed top-level database file.
            if (IsIn(name, AttachmentsDirectoryName))
            {
                if (!restoreMailboxes)
                    continue;
            }
            else if (IsIn(name, ContactPhotosDirectoryName))
            {
                if (!restoreContacts)
                    continue;
            }
            else if (name.Contains('/') || !allowFiles.Contains(name))
            {
                continue;
            }

            Extract(entry, baseDir, name);
        }
    }

    /// <summary>
    /// Zips an online backup into <paramref name="destination"/>: the exported local-only mail and
    /// its attachments, and the AI autonomy settings. Sync and goCORA's cloud key records hold the
    /// rest, and other IMAP mail downloads again. <paramref name="attachments"/> are relative to the
    /// attachments folder, as <see cref="LocalMailExport"/> gives them.
    /// </summary>
    public void CreateOnlineBackupArchive(Stream destination, string localMailPath, IEnumerable<string> attachments, string? ownerUid = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(attachments);

        var baseDir = _dataLocation.AppDataDirectory;
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        zip.CreateEntryFromFile(localMailPath, StoreFiles.PendingLocalMail, CompressionLevel.Optimal);

        // The account's own AI settings (an online backup never holds items without an owner).
        var aiAutonomy = OwnerStoreSet.IsValidOwnerUid(ownerUid)
            ? Path.Combine(OwnerStoreSet.OwnerDirectory(baseDir, ownerUid!), StoreFiles.AiAutonomy)
            : Path.Combine(baseDir, StoreFiles.AiAutonomy);
        if (File.Exists(aiAutonomy))
        {
            using var fileStream = new FileStream(aiAutonomy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var entryStream = zip.CreateEntry(StoreFiles.AiAutonomy, CompressionLevel.Optimal).Open();
            fileStream.CopyTo(entryStream);
        }

        var root = Path.Combine(baseDir, AttachmentsDirectoryName);
        foreach (var relative in attachments.Distinct())
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                zip.CreateEntryFromFile(path, $"{StoreFiles.PendingMailAttachments}/{relative}", CompressionLevel.Optimal);
        }
    }

    /// <summary>
    /// Extracts an online backup: its mail stays beside the mailbox and merges at the next start
    /// (<see cref="ILocalMailArchive"/>). Anything else in the archive is ignored.
    /// </summary>
    public void RestoreOnlineBackupArchive(Stream source, bool restoreMail, bool restoreAiAutonomy, string? ownerUid = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var baseDir = _dataLocation.AppDataDirectory;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            var name = EntryName(entry);
            var wanted = name.EndsWith('/') ? false
                : IsIn(name, StoreFiles.PendingMailAttachments) || name == StoreFiles.PendingLocalMail ? restoreMail
                : name == StoreFiles.AiAutonomy && restoreAiAutonomy;
            if (!wanted)
                continue;
            // AI settings go to the restoring account's own stores.
            Extract(entry, baseDir, name == StoreFiles.AiAutonomy && OwnerStoreSet.IsValidOwnerUid(ownerUid)
                ? $"{OwnerStoreSet.OwnersDirectoryName}/{ownerUid}/{name}"
                : name);
        }
    }

    /// <summary>
    /// Restores a goCORA backup file (made through the CORA key code) into <paramref name="destZipPath"/>,
    /// and adds the master key it carries to this device's retired keys, so the restored database
    /// files open at the next start. Needs secure email open (see <see cref="AccountBackup.RestoreFileAsync"/>).
    /// </summary>
    public Task RestoreBackupFileAsync(string projectId, string idToken, string sourcePath, string destZipPath, CancellationToken cancellationToken = default) =>
        AccountBackup.RestoreFileAsync(_secureKeyStorage, projectId, idToken, sourcePath, destZipPath, cancellationToken);
}

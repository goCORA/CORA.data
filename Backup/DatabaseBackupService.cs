using System.IO.Compression;
using CORA.Core.Security;
using CORA.Data.Email;

namespace CORA.Data.Backup;

/// <summary>
/// Routines for backing up/restoring CORA's local encrypted database files
/// and the master encryption key. This class only deals with the file system (via
/// <see cref="IAppDataLocation"/>) and key storage (via <see cref="ISecureKeyStorage"/>) -
/// it has no knowledge of any host-app UI/platform concern (file pickers, save dialogs,
/// etc.), which callers (e.g. CORA.App) are responsible for wiring up. Database files
/// are zipped/unzipped as-is (they stay encrypted at rest, so the zip itself needs no
/// extra protection); the master key is exported/imported as a small base64 string.
/// Restoring either requires an app restart, since the encrypted stores are kept open
/// for the lifetime of the process.
/// </summary>
public sealed class DatabaseBackupService
{
    // The file layout lives in StoreFiles, shared with SecureDataStores (open/wipe).
    private static readonly string[] DatabaseFileNames = StoreFiles.Databases;

    private const string AttachmentsDirectoryName = StoreFiles.AttachmentsDirectory;

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
    /// Zips the local database files (and attachments) into <paramref name="destination"/>.
    /// The caller owns <paramref name="destination"/> (e.g. a temp file stream or a
    /// stream backed by a platform save-file dialog).
    /// </summary>
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

        var attachmentsDir = Path.Combine(baseDir, AttachmentsDirectoryName);
        if (Directory.Exists(attachmentsDir))
        {
            foreach (var file in Directory.EnumerateFiles(attachmentsDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.Combine(AttachmentsDirectoryName, Path.GetRelativePath(attachmentsDir, file));
                zip.CreateEntryFromFile(file, relative);
            }
        }
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
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry

            var destinationPath = Path.Combine(baseDir, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);

            // Only create a subdirectory when the entry actually lives in one
            // (e.g. mail_attachments/…). Root-level .litedb files already sit in baseDir,
            // which is guaranteed to exist; calling CreateDirectory on it every iteration
            // was redundant and confusing.
            if (!string.IsNullOrEmpty(destinationDir) && destinationDir != baseDir)
                Directory.CreateDirectory(destinationDir);

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// Extracts only selected parts of a previously-created backup archive over the local
    /// database files, overwriting any existing ones. Callers can choose which logical
    /// stores to restore.
    /// </summary>
    public void RestoreBackupArchive(Stream source, bool restoreAccounts, bool restoreTags, bool restoreContacts, bool restoreMailboxes, bool restoreTrustedImageSenders, bool restoreBlacklist, bool restoreAiAutonomy, bool restoreAiResultCache)
    {
        ArgumentNullException.ThrowIfNull(source);

        var baseDir = _dataLocation.AppDataDirectory;

        // Map logical selections to the filenames used in backups.
        var allowFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (restoreTags) allowFiles.Add(StoreFiles.Tags);
        // Which folders an account hides from the menu is a per-account setting, so it travels
        // with the accounts toggle rather than getting a switch of its own.
        if (restoreAccounts) { allowFiles.Add(StoreFiles.Accounts); allowFiles.Add(StoreFiles.HiddenFolders); }
        if (restoreContacts) allowFiles.Add(StoreFiles.Contacts);
        if (restoreMailboxes) allowFiles.Add(StoreFiles.MailSync);
        if (restoreTrustedImageSenders) allowFiles.Add(StoreFiles.TrustedImageSenders);
        if (restoreBlacklist) allowFiles.Add(StoreFiles.Blacklist);
        if (restoreAiAutonomy) allowFiles.Add(StoreFiles.AiAutonomy);
        if (restoreAiResultCache) allowFiles.Add(StoreFiles.AiResultCache);

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry

            // Handle attachments: only restore when mailboxes are selected. Attachments
            // are stored under the AttachmentsDirectoryName subdirectory in the zip.
            var isAttachment = entry.FullName.StartsWith(AttachmentsDirectoryName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith(AttachmentsDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (isAttachment && !restoreMailboxes)
                continue;

            // Only extract entries that correspond to an allowed top-level file, or
            // attachments (handled above). Entries inside subdirectories (attachments)
            // will have their FullName preserved.
            var topLevelName = Path.GetFileName(entry.FullName);
            if (!isAttachment && !allowFiles.Contains(topLevelName))
                continue;

            var destinationPath = Path.Combine(baseDir, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationDir) && destinationDir != baseDir)
                Directory.CreateDirectory(destinationDir);

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    /// <summary>Returns the current master encryption key (base64), or null if none is set.</summary>
    public Task<string?> ExportEncryptionKeyAsync() => Cora.ExportMasterKeyAsync(_secureKeyStorage);

    /// <summary>Replaces the current master encryption key (base64) with <paramref name="key"/>.</summary>
    public Task ImportEncryptionKeyAsync(string key) => Cora.ImportMasterKeyAsync(_secureKeyStorage, key);
}

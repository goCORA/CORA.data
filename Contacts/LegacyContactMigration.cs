using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Contacts;

/// <summary>
/// One-time conversion of the plaintext <c>contacts.litedb</c> written by older app versions
/// into the encrypted store <see cref="ContactDatabase"/> expects at the same path. Must run
/// before <see cref="ContactDatabase"/> opens that path: <see cref="EncryptedLiteDbFile.Open"/>
/// treats a file it can't decrypt as unrecoverable and starts an empty database instead.
///
/// It runs on every launch (a no-op once the file is encrypted) because restoring an old
/// backup writes a plaintext contacts.litedb back into place.
///
/// Steps, ordered so that an interruption at any point loses nothing:
/// 1. If the file at the path is plaintext LiteDB, let LiteDB replay any pending journal
///    (so un-checkpointed writes are included), then move the file aside to
///    <c>contacts.litedb.legacy-plaintext</c>.
/// 2. If that legacy file exists, read its contacts, upsert them by id into the encrypted
///    store, flush, and check the encrypted file on disk holds them all.
/// 3. Only then overwrite the legacy file with zeros and delete it.
/// A crash before step 3 leaves the legacy file in place; the next launch repeats step 2,
/// which is idempotent because the upsert is keyed by the contact's original id.
///
/// Wiping is best-effort: on flash storage, overwriting a file doesn't guarantee the old
/// blocks are erased.
/// </summary>
internal static class LegacyContactMigration
{
    public const string CollectionName = "Contacts";
    private const string LegacySuffix = ".legacy-plaintext";

    /// <summary>Converts a legacy plaintext contacts file at <paramref name="path"/>, if there is one.</summary>
    public static void MigrateIfNeeded(string path)
    {
        var legacyPath = path + LegacySuffix;

        if (File.Exists(path) && IsPlaintextLiteDb(path))
        {
            // Opening by path makes LiteDB replay the "-log" journal next to the file and
            // fold it into the main file when it closes, so nothing unflushed is missed.
            using (new LiteDatabase(path)) { }

            // Only possible if an earlier migration was interrupted and an old backup was
            // restored before the next launch. Keep both rather than pick one.
            if (File.Exists(legacyPath))
                File.Move(legacyPath, $"{legacyPath}.conflict-{DateTime.UtcNow:yyyyMMddHHmmssfff}");

            File.Move(path, legacyPath);
        }

        if (!File.Exists(legacyPath))
            return;

        var docs = ReadContacts(legacyPath);

        using (var file = EncryptedLiteDbFile.Open(path))
        {
            var collection = file.Database.GetCollection(CollectionName);
            foreach (var doc in docs)
                collection.Upsert(doc);
            file.Flush();
        }

        var stored = CountContactsOnDisk(path);
        if (stored < docs.Count)
            throw new InvalidOperationException(
                $"Contact migration verification failed: read {docs.Count} contacts but the encrypted file holds {stored}. The plaintext copy was kept.");

        WipeAndDelete(legacyPath);
        DeleteJournal(path);
    }

    private static bool IsPlaintextLiteDb(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (CanDecrypt(bytes))
            return false;

        try
        {
            // LiteDB rejects anything without its file header, so random or corrupt bytes throw here.
            using var db = new LiteDatabase(new MemoryStream(bytes));
            _ = db.GetCollectionNames().ToList();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanDecrypt(byte[] payload)
    {
        if (TryDecrypt(payload, Cora.GetMasterKey()))
            return true;

        foreach (var oldKey in Cora.GetPreviousMasterKeys())
        {
            if (TryDecrypt(payload, oldKey))
                return true;
        }

        return false;
    }

    private static bool TryDecrypt(byte[] payload, byte[] key)
    {
        try
        {
            Cora.Decrypt(payload, key);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<BsonDocument> ReadContacts(string legacyPath)
    {
        // Read from bytes, not by path: LiteDB would otherwise create a journal file next to it.
        using var db = new LiteDatabase(new MemoryStream(File.ReadAllBytes(legacyPath)));
        return db.GetCollection(CollectionName).FindAll().ToList();
    }

    private static int CountContactsOnDisk(string path)
    {
        var plaintext = Cora.Decrypt(File.ReadAllBytes(path), Cora.GetMasterKey());
        using var db = new LiteDatabase(new MemoryStream(plaintext));
        return db.GetCollection(CollectionName).Count();
    }

    private static void WipeAndDelete(string path)
    {
        try
        {
            var remaining = new FileInfo(path).Length;
            var zeros = new byte[64 * 1024];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            // Best-effort; still delete below.
        }

        File.Delete(path);
    }

    private static void DeleteJournal(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var journal = Path.Combine(
                directory, Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));
            File.Delete(journal);
        }
        catch (Exception)
        {
        }
    }
}

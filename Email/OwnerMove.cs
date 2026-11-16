using CORA.Data.Contacts;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>What the ownerless set holds, for "Keep these with …" (claude/design-per-account-ownership.md, decision 14).</summary>
public sealed class OwnerlessItems
{
    public IReadOnlyList<MailAccount> Mailboxes { get; init; } = [];

    /// <summary>The ownerless set's contacts, with that set's own ids (as <see cref="OwnerlessChoice.ContactIds"/> expects).</summary>
    public IReadOnlyList<Contact> Contacts { get; init; } = [];

    public int Tags { get; init; }
    public int BlockedSendersAndDomains { get; init; }
    public int TrustedSenders { get; init; }
    public int RecentRecipients { get; init; }
    public int SecureEmailKeys { get; init; }

    public bool IsEmpty =>
        Mailboxes.Count == 0 && Contacts.Count == 0 && Tags == 0 && BlockedSendersAndDomains == 0
        && TrustedSenders == 0 && RecentRecipients == 0 && SecureEmailKeys == 0;
}

/// <summary>What else a device holds besides one account's items (the Erase screen's caution).</summary>
/// <param name="OtherAccounts">Other goCORA accounts with at least one item here.</param>
/// <param name="ItemsWithoutOwner">Items without an owner: mail accounts, contacts, tags, lists and keys.</param>
public sealed record OtherDataOnDevice(int OtherAccounts, int ItemsWithoutOwner)
{
    public bool IsEmpty => OtherAccounts == 0 && ItemsWithoutOwner == 0;
}

/// <summary>What the user ticked: these move from the ownerless set to the account's set.</summary>
public sealed class OwnerlessChoice
{
    public HashSet<string> Mailboxes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> ContactIds { get; } = [];
    public bool Tags { get; set; }
    public bool BlockedSendersAndDomains { get; set; }
    public bool TrustedSenders { get; set; }
    public bool RecentRecipients { get; set; }
    public bool SecureEmailKeys { get; set; }
}

/// <summary>
/// Moves items from one <see cref="OwnerStoreSet"/> to another, document by document, so nothing
/// is lost or changed on the way (ids, dates, flags). Each item is written to its new set before
/// it is deleted from the old one: interrupted half-way, an item can at worst show twice, never vanish.
/// A mailbox takes its own data along: hidden folders, folder order, tag assignments, image
/// approvals, AI autonomy and AI results. The mail itself is shared and never moves.
/// Caller holds the stores' flush lock and flushes both sets afterwards.
/// </summary>
internal static class OwnerMove
{
    private static string Key(string email) => email.Trim().ToLowerInvariant();

    public static void Move(OwnerStoreSet from, OwnerStoreSet to, OwnerlessChoice choice)
    {
        foreach (var email in choice.Mailboxes)
            MoveMailbox(from, to, email);

        if (choice.ContactIds.Count > 0)
        {
            MoveContacts(from.Contacts.Raw, to.Contacts.Raw, choice.ContactIds);
            from.Contacts.MarkDirty();
            to.Contacts.MarkDirty();
        }
        if (choice.Tags)
            MoveAll(from.Tags.Raw, to.Tags.Raw, "Tags");
        if (choice.BlockedSendersAndDomains)
        {
            MoveAll(from.Blacklist.Raw, to.Blacklist.Raw, "BlacklistedSenders");
            MoveAll(from.Blacklist.Raw, to.Blacklist.Raw, "BlacklistedDomains");
        }
        if (choice.TrustedSenders)
            MoveAll(from.TrustedImageSenders.Raw, to.TrustedImageSenders.Raw, "TrustedSenders");
        if (choice.RecentRecipients)
            MoveAll(from.RecentRecipients.Raw, to.RecentRecipients.Raw, "RecentRecipients");
        if (choice.SecureEmailKeys)
        {
            foreach (var name in (string[])["SecureMessageKeys", "KeyRequests", "KeysShared", "Correspondents"])
                MoveAll(from.SecureMessageKeys.Raw, to.SecureMessageKeys.Raw, name);
        }
    }

    private static void MoveMailbox(OwnerStoreSet from, OwnerStoreSet to, string email)
    {
        var key = Key(email);
        bool IdIs(BsonDocument d) => Key(d["_id"].AsString ?? string.Empty) == key;
        bool IdStartsWith(BsonDocument d) => (d["_id"].AsString ?? string.Empty).StartsWith(key + "|", StringComparison.Ordinal);
        bool Field(BsonDocument d, string name) => d.TryGetValue(name, out var v) && v.IsString && Key(v.AsString) == key;

        // The mailbox's own data first, the mailbox itself last: interrupted, it stays where it was.
        Move(from.HiddenFolders.Raw, to.HiddenFolders.Raw, "HiddenFolders", d => Field(d, "Account"));
        Move(from.FolderOrder.Raw, to.FolderOrder.Raw, "FolderOrder", IdIs);
        Move(from.Tags.Raw, to.Tags.Raw, "MessageTags", d => Field(d, "AccountKey"));
        Move(from.TrustedImageSenders.Raw, to.TrustedImageSenders.Raw, "MessageImagesAllowed", IdStartsWith);
        Move(from.AiAutonomy.Raw, to.AiAutonomy.Raw, "AccountAutonomy", IdIs);
        Move(from.AiResultCache.Raw, to.AiResultCache.Raw, "AiResultCache", IdStartsWith);
        Move(from.Accounts.Raw, to.Accounts.Raw, "Accounts", IdIs);
    }

    /// <summary>
    /// Every item of one set into another (an account's items staying on the device without an
    /// owner): all stores but the sync state, which belongs to the account's cloud and is dropped.
    /// </summary>
    public static void MoveEverything(OwnerStoreSet from, OwnerStoreSet to)
    {
        foreach (var (source, target) in ((LiteDatabase, LiteDatabase)[])
        [
            (from.HiddenFolders.Raw, to.HiddenFolders.Raw), (from.FolderOrder.Raw, to.FolderOrder.Raw),
            (from.Tags.Raw, to.Tags.Raw), (from.TrustedImageSenders.Raw, to.TrustedImageSenders.Raw),
            (from.Blacklist.Raw, to.Blacklist.Raw), (from.AiAutonomy.Raw, to.AiAutonomy.Raw),
            (from.AiResultCache.Raw, to.AiResultCache.Raw), (from.SecureMessageKeys.Raw, to.SecureMessageKeys.Raw),
            (from.RecentRecipients.Raw, to.RecentRecipients.Raw), (from.Accounts.Raw, to.Accounts.Raw),
        ])
            MoveWholeStore(source, target);

        if (from.Contacts.Raw.CollectionExists(LegacyContactMigration.CollectionName))
        {
            var ids = from.Contacts.Raw.GetCollection(LegacyContactMigration.CollectionName, BsonAutoId.Int32).FindAll()
                .Where(d => d["_id"].IsInt32).Select(d => d["_id"].AsInt32).ToHashSet();
            MoveContacts(from.Contacts.Raw, to.Contacts.Raw, ids);
            from.Contacts.MarkDirty();
            to.Contacts.MarkDirty();
        }
    }

    /// <summary>How many items a set holds, as one number (mail accounts, contacts and every list).</summary>
    public static int Count(OwnerlessItems items) =>
        items.Mailboxes.Count + items.Contacts.Count + items.Tags + items.BlockedSendersAndDomains
        + items.TrustedSenders + items.RecentRecipients + items.SecureEmailKeys;

    private static int MoveAll(LiteDatabase from, LiteDatabase to, string collection) => Move(from, to, collection, _ => true);

    /// <summary>Every collection of one store file into another (e.g. a whole sync state).</summary>
    public static void MoveWholeStore(LiteDatabase from, LiteDatabase to)
    {
        foreach (var name in from.GetCollectionNames().ToList())
            MoveAll(from, to, name);
    }

    private static int Move(LiteDatabase from, LiteDatabase to, string collection, Func<BsonDocument, bool> match)
    {
        if (!from.CollectionExists(collection))
            return 0;
        var source = from.GetCollection(collection);
        var target = to.GetCollection(collection);
        var docs = source.FindAll().Where(match).ToList();
        foreach (var doc in docs)
            target.Upsert(doc);
        foreach (var doc in docs)
            source.Delete(doc["_id"]);
        return docs.Count;
    }

    // Contact ids are numbers per file: a moved contact gets a new number; everything else is kept.
    private static void MoveContacts(LiteDatabase from, LiteDatabase to, IReadOnlySet<int> ids)
    {
        if (!from.CollectionExists(LegacyContactMigration.CollectionName))
            return;
        var source = from.GetCollection(LegacyContactMigration.CollectionName, BsonAutoId.Int32);
        var target = to.GetCollection(LegacyContactMigration.CollectionName, BsonAutoId.Int32);
        var docs = source.FindAll().Where(d => d["_id"].IsInt32 && ids.Contains(d["_id"].AsInt32)).ToList();
        foreach (var doc in docs)
        {
            var copy = new BsonDocument(doc);
            copy.Remove("_id");
            target.Insert(copy);
        }
        foreach (var doc in docs)
            source.Delete(doc["_id"]);
    }

    public static async Task<OwnerlessItems> DescribeAsync(OwnerStoreSet ownerless)
    {
        int Count(LiteDatabase db, params string[] collections) =>
            collections.Where(db.CollectionExists).Sum(c => db.GetCollection(c).Count());

        return new OwnerlessItems
        {
            Mailboxes = await ownerless.Accounts.LoadAllAsync().ConfigureAwait(false),
            Contacts = await ownerless.Contacts.GetAllAsync().ConfigureAwait(false),
            Tags = Count(ownerless.Tags.Raw, "Tags"),
            BlockedSendersAndDomains = Count(ownerless.Blacklist.Raw, "BlacklistedSenders", "BlacklistedDomains"),
            TrustedSenders = Count(ownerless.TrustedImageSenders.Raw, "TrustedSenders"),
            RecentRecipients = Count(ownerless.RecentRecipients.Raw, "RecentRecipients"),
            SecureEmailKeys = Count(ownerless.SecureMessageKeys.Raw, "SecureMessageKeys"),
        };
    }
}

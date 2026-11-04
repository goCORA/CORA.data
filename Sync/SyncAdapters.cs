using System.Text.Json;
using CORA.Data.Contacts;
using CORA.Data.Email;

namespace CORA.Data.Sync;

/// <summary>Contacts, by <see cref="Contact.SyncId"/>. Photo files travel separately.</summary>
public sealed class ContactsSyncAdapter(IContactStore contacts) : ISyncAdapter
{
    private sealed record Data(string First, string Last, string Email, string Phone, string Photo, bool IsGoCORA, string Tags);

    public string Kind => "contacts";

    private static string ToText(Contact c) => JsonSerializer.Serialize(new Data(
        c.FirstName ?? string.Empty, c.LastName ?? string.Empty, c.Email ?? string.Empty, c.Phone ?? string.Empty,
        ContactPhotoFiles.FileNameOf(c.PhotoPath), c.IsGoCORA, c.Tags ?? string.Empty));

    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken) =>
        (await contacts.GetAllAsync().ConfigureAwait(false)).ToDictionary(c => c.SyncId, ToText);

    public async Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken)
    {
        var all = await contacts.GetAllAsync().ConfigureAwait(false);
        var taken = all.Select(c => c.SyncId).ToHashSet();
        var unmatched = all.Where(c => !cloudItems.ContainsKey(c.SyncId) && c.Emails.Count > 0).ToList();
        foreach (var (id, text) in cloudItems)
        {
            if (taken.Contains(id) || JsonSerializer.Deserialize<Data>(text) is not { } data)
                continue;
            var emails = data.Email.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var same = unmatched.FirstOrDefault(c => c.Emails.Any(emails.Contains));
            if (same is null)
                continue;
            unmatched.Remove(same);
            same.SyncId = id;
            await contacts.SaveAsync(same).ConfigureAwait(false);
        }
    }

    public async Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
    {
        var all = (await contacts.GetAllAsync().ConfigureAwait(false)).ToDictionary(c => c.SyncId);
        foreach (var (id, text) in upserts)
        {
            if (JsonSerializer.Deserialize<Data>(text) is not { } data)
                continue;
            var contact = all.GetValueOrDefault(id) ?? new Contact { SyncId = id };
            contact.FirstName = data.First;
            contact.LastName = data.Last;
            contact.Email = data.Email;
            contact.Phone = data.Phone;
            contact.PhotoPath = data.Photo;
            contact.IsGoCORA = data.IsGoCORA;
            contact.Tags = data.Tags;
            await contacts.SaveAsync(contact).ConfigureAwait(false);
        }
        var gone = deletes.Where(all.ContainsKey).Select(id => all[id]).ToList();
        if (gone.Count > 0)
            await contacts.DeleteManyAsync(gone).ConfigureAwait(false);
    }
}

/// <summary>Blocked senders, blocked domains and trusted senders.</summary>
public sealed class ListsSyncAdapter(IBlacklistStore blacklist, ITrustedImageSenderStore trusted) : ISyncAdapter
{
    private const string Sender = "sender:";
    private const string Domain = "domain:";
    private const string Trusted = "trusted:";

    public string Kind => "lists";

    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, string>();
        foreach (var s in await blacklist.GetBlacklistedSendersAsync(cancellationToken).ConfigureAwait(false))
            items[Sender + s] = "1";
        foreach (var d in await blacklist.GetBlacklistedDomainsAsync(cancellationToken).ConfigureAwait(false))
            items[Domain + d] = "1";
        foreach (var t in await trusted.GetTrustedSendersAsync(cancellationToken).ConfigureAwait(false))
            items[Trusted + t] = "1";
        return items;
    }

    public Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
    {
        foreach (var id in upserts.Keys)
        {
            if (id.StartsWith(Sender, StringComparison.Ordinal))
                await blacklist.BlacklistSenderAsync(id[Sender.Length..], cancellationToken).ConfigureAwait(false);
            else if (id.StartsWith(Domain, StringComparison.Ordinal))
                await blacklist.BlacklistDomainAsync(id[Domain.Length..], cancellationToken).ConfigureAwait(false);
            else if (id.StartsWith(Trusted, StringComparison.Ordinal))
                await trusted.SetTrustedAsync(id[Trusted.Length..], true, cancellationToken).ConfigureAwait(false);
        }
        foreach (var id in deletes)
        {
            if (id.StartsWith(Sender, StringComparison.Ordinal))
                await blacklist.RemoveSenderAsync(id[Sender.Length..], cancellationToken).ConfigureAwait(false);
            else if (id.StartsWith(Domain, StringComparison.Ordinal))
                await blacklist.RemoveDomainAsync(id[Domain.Length..], cancellationToken).ConfigureAwait(false);
            else if (id.StartsWith(Trusted, StringComparison.Ordinal))
                await trusted.SetTrustedAsync(id[Trusted.Length..], false, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Tag definitions (<c>tag:&lt;id&gt;</c>) and assignments (<c>msg:&lt;account&gt;|&lt;Message-Id&gt;</c>).
/// An assignment for an email not on this device waits in the sync state, and counts as being
/// here, so waiting for the email never reads as a deletion.
/// </summary>
public sealed class TagsSyncAdapter(ITagStore tags, IMailSyncStore mail, ISyncStateStore state) : ISyncAdapter
{
    private const string TagPrefix = "tag:";
    private const string MessagePrefix = "msg:";

    private sealed record TagData(string Name, string Color);

    public string Kind => "tags";

    private static string MessageItem(string accountKey, string messageId) => $"{MessagePrefix}{accountKey}|{messageId}";

    private static (string AccountKey, string MessageId) ParseMessageItem(string id)
    {
        var bar = id.IndexOf('|', MessagePrefix.Length);
        return (id[MessagePrefix.Length..bar], id[(bar + 1)..]);
    }

    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        await TagMessageIds.BackfillAsync(tags, mail, cancellationToken).ConfigureAwait(false);
        await PlaceWaitingTagsAsync(cancellationToken).ConfigureAwait(false);

        var items = new Dictionary<string, string>();
        foreach (var t in await tags.GetTagsAsync(cancellationToken).ConfigureAwait(false))
            items[TagPrefix + t.Id] = JsonSerializer.Serialize(new TagData(t.Name, t.ColorHex));
        foreach (var a in await tags.GetAssignmentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (a.MessageId.Length > 0)
                items[MessageItem(a.AccountKey, a.MessageId)] = a.TagId;
        }
        foreach (var p in await state.GetPendingTagsAsync(cancellationToken).ConfigureAwait(false))
            items[MessageItem(p.AccountKey, p.MessageId)] = p.TagId;
        return items;
    }

    // Emails that arrived since the last run get the tags that were waiting for them.
    private async Task PlaceWaitingTagsAsync(CancellationToken cancellationToken)
    {
        foreach (var group in (await state.GetPendingTagsAsync(cancellationToken).ConfigureAwait(false)).GroupBy(p => p.AccountKey))
        {
            var messages = await mail.GetMessageIdentitiesAsync(group.Key, cancellationToken).ConfigureAwait(false);
            foreach (var pending in group)
            {
                var found = messages.Where(m => m.MessageId == pending.MessageId).ToList();
                foreach (var m in found)
                {
                    await tags.SetMessageTagAsync(group.Key, m.FolderName, m.Uid, pending.TagId, cancellationToken).ConfigureAwait(false);
                    await tags.SetAssignmentMessageIdAsync(group.Key, m.FolderName, m.Uid, m.MessageId, cancellationToken).ConfigureAwait(false);
                }
                if (found.Count > 0)
                    await state.RemovePendingTagAsync(group.Key, pending.MessageId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken)
    {
        var mine = await tags.GetTagsAsync(cancellationToken).ConfigureAwait(false);
        var assignments = await tags.GetAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (id, text) in cloudItems)
        {
            if (!id.StartsWith(TagPrefix, StringComparison.Ordinal) || JsonSerializer.Deserialize<TagData>(text) is not { } data)
                continue;
            var cloudId = id[TagPrefix.Length..];
            var same = mine.FirstOrDefault(t => t.Id != cloudId && string.Equals(t.Name, data.Name, StringComparison.OrdinalIgnoreCase));
            if (same is null || cloudItems.ContainsKey(TagPrefix + same.Id))
                continue;
            await tags.UpdateTagAsync(cloudId, data.Name, data.Color, cancellationToken).ConfigureAwait(false);
            foreach (var a in assignments.Where(a => a.TagId == same.Id))
                await tags.SetMessageTagAsync(a.AccountKey, a.FolderName, a.Uid, cloudId, cancellationToken).ConfigureAwait(false);
            await tags.DeleteTagAsync(same.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
    {
        foreach (var (id, text) in upserts.Where(u => u.Key.StartsWith(TagPrefix, StringComparison.Ordinal)))
        {
            if (JsonSerializer.Deserialize<TagData>(text) is { } data)
                await tags.UpdateTagAsync(id[TagPrefix.Length..], data.Name, data.Color, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (id, tagId) in upserts.Where(u => u.Key.StartsWith(MessagePrefix, StringComparison.Ordinal)))
        {
            var (accountKey, messageId) = ParseMessageItem(id);
            await state.SetPendingTagAsync(new SyncPendingTag { AccountKey = accountKey, MessageId = messageId, TagId = tagId }, cancellationToken).ConfigureAwait(false);
        }
        await PlaceWaitingTagsAsync(cancellationToken).ConfigureAwait(false);

        var assignments = await tags.GetAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in deletes.Where(d => d.StartsWith(MessagePrefix, StringComparison.Ordinal)))
        {
            var (accountKey, messageId) = ParseMessageItem(id);
            await state.RemovePendingTagAsync(accountKey, messageId, cancellationToken).ConfigureAwait(false);
            foreach (var a in assignments.Where(a => a.AccountKey == accountKey && a.MessageId == messageId))
                await tags.SetMessageTagAsync(a.AccountKey, a.FolderName, a.Uid, null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in deletes.Where(d => d.StartsWith(TagPrefix, StringComparison.Ordinal)))
            await tags.DeleteTagAsync(id[TagPrefix.Length..], cancellationToken).ConfigureAwait(false);
    }
}

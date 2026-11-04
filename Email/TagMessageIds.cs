namespace CORA.Data.Email;

/// <summary>
/// Fills in the <c>Message-Id</c> of tag assignments that don't have one yet, from the mail cached
/// on this device. Sync matches tags to emails by <c>Message-Id</c>, because uids differ between
/// devices (claude/design-cloud-backup-sync.md). The sync run calls this first, so assignments made
/// before sync existed, and new ones, need no change where tags are set.
/// </summary>
public static class TagMessageIds
{
    /// <summary>
    /// Returns how many assignments were filled in. An email no longer cached, or without a
    /// <c>Message-Id</c>, keeps its tag on this device only.
    /// </summary>
    public static async Task<int> BackfillAsync(ITagStore tags, IMailSyncStore mail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(mail);

        var assignments = await tags.GetAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        var filled = 0;
        foreach (var account in assignments.Where(a => a.MessageId.Length == 0).GroupBy(a => a.AccountKey))
        {
            var messages = await mail.GetMessageIdentitiesAsync(account.Key, cancellationToken).ConfigureAwait(false);
            var byLocation = new Dictionary<(string Folder, uint Uid), string>();
            foreach (var m in messages)
                byLocation[(m.FolderName, m.Uid)] = m.MessageId;

            foreach (var assignment in account)
            {
                if (!byLocation.TryGetValue((assignment.FolderName, assignment.Uid), out var messageId))
                    continue;
                await tags.SetAssignmentMessageIdAsync(assignment.AccountKey, assignment.FolderName, assignment.Uid, messageId, cancellationToken).ConfigureAwait(false);
                filled++;
            }
        }
        return filled;
    }
}

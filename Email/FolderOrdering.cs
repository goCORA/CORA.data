namespace CORA.Data.Email;

/// <summary>
/// Pure ordering rules for an account's folder list in the flyout. The app-required folders
/// (Inbox, Sent, Junk, Trash) are pinned at the top in that fixed order; every other folder
/// follows the user's saved order, with folders the saved order doesn't know about (new ones)
/// added at the end, A-Z.
/// </summary>
public static class FolderOrdering
{
    // Fixed display order of the pinned folders. These are the folders the app refuses to
    // rename or delete (see MailKitEmailService.IsReservedFolderName).
    private const int InboxSlot = 0;
    private const int SentSlot = 1;
    private const int JunkSlot = 2;
    private const int TrashSlot = 3;

    /// <summary>Whether this folder is one of the pinned, non-movable folders of its account's list.</summary>
    public static bool IsPinned(MailFolderInfo folder, IEnumerable<MailFolderInfo> allFolders) =>
        PinnedSlot(folder, AnyJunkFlagged(allFolders)) >= 0;

    private static bool AnyJunkFlagged(IEnumerable<MailFolderInfo> folders) => folders.Any(f => f.IsJunk);

    // The junk slot belongs to the folder detected as the account's junk folder (whatever it is
    // called, e.g. "[Gmail]/Spam"). Only when none is flagged (folder lists cached before the
    // flag existed) does a folder literally named "Junk" take it, so a plain folder with that
    // name never shares the slot with the real one.
    private static int PinnedSlot(MailFolderInfo folder, bool anyJunkFlagged)
    {
        if (folder.IsJunk)
            return JunkSlot;
        if (!anyJunkFlagged && folder.FullName.Equals("Junk", StringComparison.OrdinalIgnoreCase))
            return JunkSlot;
        if (folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return InboxSlot;
        if (folder.FullName.Equals("Sent", StringComparison.OrdinalIgnoreCase))
            return SentSlot;
        if (folder.FullName.Equals("Trash", StringComparison.OrdinalIgnoreCase))
            return TrashSlot;
        return -1;
    }

    /// <summary>
    /// Returns <paramref name="folders"/> in display order. With no saved order the list is
    /// returned unchanged (the server's order). Saved names that no longer exist are ignored.
    /// </summary>
    public static IReadOnlyList<MailFolderInfo> Apply(
        IReadOnlyList<MailFolderInfo> folders, IReadOnlyList<string> savedOrder)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(savedOrder);

        if (savedOrder.Count == 0)
            return folders;

        var anyJunkFlagged = AnyJunkFlagged(folders);
        var result = new List<MailFolderInfo>(folders.Count);

        result.AddRange(folders
            .Where(f => PinnedSlot(f, anyJunkFlagged) >= 0)
            .OrderBy(f => PinnedSlot(f, anyJunkFlagged)));

        var movable = folders.Where(f => PinnedSlot(f, anyJunkFlagged) < 0).ToList();
        var placed = new HashSet<MailFolderInfo>();

        foreach (var name in savedOrder)
        {
            var folder = movable.FirstOrDefault(f =>
                !placed.Contains(f) && f.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (folder is not null)
            {
                result.Add(folder);
                placed.Add(folder);
            }
        }

        result.AddRange(movable
            .Where(f => !placed.Contains(f))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>
    /// The full names of the non-pinned folders, A-Z by display name: the order a "Sort A-Z"
    /// action saves.
    /// </summary>
    public static IReadOnlyList<string> SortAlphabetical(IEnumerable<MailFolderInfo> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var all = folders.ToList();
        var anyJunkFlagged = AnyJunkFlagged(all);

        return all
            .Where(f => PinnedSlot(f, anyJunkFlagged) < 0)
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => f.FullName)
            .ToList();
    }
}

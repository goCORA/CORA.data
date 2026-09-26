namespace CORA.Data.Email;

/// <summary>
/// A user-defined tag that can be applied to messages in the mailbox, e.g. "Follow up"
/// or "Personal". Created and managed from goCORA Settings.
/// </summary>
public sealed class TagDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex color (e.g. "#F0C419") used for the tag's swatch/chip.</summary>
    public string ColorHex { get; set; } = "#F0C419";
}

/// <summary>
/// Local persistence for user-defined tags and per-message tag assignments. Tags are
/// global (not per-account) and assignments are keyed by account + folder + uid, so
/// they are independent of server-side state entirely — a purely local organizational
/// feature layered on top of the synced mailbox.
/// </summary>
public interface ITagStore
{
    /// <summary>Returns all defined tags.</summary>
    Task<List<TagDefinition>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a new tag and returns it (with a generated Id).</summary>
    Task<TagDefinition> AddTagAsync(string name, string colorHex, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing tag's name and color.</summary>
    Task UpdateTagAsync(string tagId, string name, string colorHex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tag definition and clears its assignment from any messages currently
    /// carrying it.
    /// </summary>
    Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns the tag currently assigned to a message, or null if none.</summary>
    Task<TagDefinition?> GetMessageTagAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the assigned tag id for every message uid in the given folder that has
    /// one, keyed by uid, so the mailbox list can populate tag chips in a single lookup
    /// instead of one query per row.
    /// </summary>
    Task<Dictionary<uint, string>> GetMessageTagIdsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>Assigns (or replaces) the tag on a message. Pass null tagId to clear it.</summary>
    Task SetMessageTagAsync(
        string accountKey, string folderFullName, uint uid, string? tagId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every tag assignment belonging to an account (used when the account is deleted).
    /// Tag definitions are global and are never deleted by this. <paramref name="emailAddress"/>
    /// is normalized to the same key the other methods are given.
    /// </summary>
    Task DeleteAccountAssignmentsAsync(string emailAddress, CancellationToken cancellationToken = default);
}

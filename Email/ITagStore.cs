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

/// <summary>One email's tag, as stored on this device.</summary>
public sealed class MessageTagAssignment
{
    public string AccountKey { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public uint Uid { get; set; }
    public string TagId { get; set; } = string.Empty;

    /// <summary>
    /// The email's <c>Message-Id</c>: how sync finds it on another device, where its uid differs.
    /// Empty until <see cref="TagMessageIds"/> has filled it in, or when the email has none.
    /// </summary>
    public string MessageId { get; set; } = string.Empty;
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

    /// <summary>Returns every tag assignment on this device.</summary>
    Task<List<MessageTagAssignment>> GetAssignmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records the <c>Message-Id</c> of an assignment. Nothing happens if there is no such assignment.</summary>
    Task SetAssignmentMessageIdAsync(
        string accountKey, string folderFullName, uint uid, string messageId, CancellationToken cancellationToken = default);
}

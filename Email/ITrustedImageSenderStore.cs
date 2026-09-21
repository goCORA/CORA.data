namespace CORA.Data.Email;

/// <summary>
/// Local persistence for the "always show images from this sender" choice a user can make
/// from the message detail view when remote images are blocked. Purely local/organizational,
/// independent of server-side state, following the same pattern as <see cref="ITagStore"/>.
/// </summary>
public interface ITrustedImageSenderStore
{
    /// <summary>Returns whether remote images should be auto-displayed for the given sender address.</summary>
    Task<bool> IsTrustedAsync(string senderEmail, CancellationToken cancellationToken = default);

    /// <summary>Marks the given sender address as trusted (or not) for auto-displaying remote images.</summary>
    Task SetTrustedAsync(string senderEmail, bool trusted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether remote images should be auto-displayed for one specific message,
    /// independent of whether its sender is trusted. Set once the user has downloaded images
    /// for that message, so reopening it later keeps showing them even if the user declined
    /// to trust the sender for future emails.
    /// </summary>
    Task<bool> IsMessageAllowedAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Marks (or clears) whether the given message should always auto-display its remote images.</summary>
    Task SetMessageAllowedAsync(
        string accountKey, string folderFullName, uint uid, bool allowed, CancellationToken cancellationToken = default);
}

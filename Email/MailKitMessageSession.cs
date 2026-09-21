using MailKit;
using MailKit.Net.Imap;

namespace CORA.Data.Email;

/// <summary>
/// Keeps an IMAP connection and folder open for the lifetime of a message view,
/// allowing multiple attachment downloads to reuse the same connection instead
/// of reconnecting for each one.
/// </summary>
internal sealed class MailKitMessageSession : IMessageSession
{
    private readonly ImapClient _imap;
    private readonly IMailFolder _folder;
    private readonly UniqueId _uid;
    private readonly Dictionary<string, BodyPart> _parts;
    private bool _disposed;

    public EmailMessage Message { get; }

    public MailKitMessageSession(
        ImapClient imap,
        IMailFolder folder,
        UniqueId uid,
        EmailMessage message,
        Dictionary<string, BodyPart> parts)
    {
        _imap = imap;
        _folder = folder;
        _uid = uid;
        Message = message;
        _parts = parts;
    }

    public async Task DownloadAttachmentAsync(
        string partSpecifier, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_parts.TryGetValue(partSpecifier, out var bodyPart))
            return;

        var entity = await _folder.GetBodyPartAsync(_uid, bodyPart, cancellationToken).ConfigureAwait(false);
        await MailKitEmailService.DecodeEntityAsync(entity, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (_folder.IsOpen)
                await _folder.CloseAsync(false).ConfigureAwait(false);
            if (_imap.IsConnected)
                await _imap.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup; ignore disconnect races.
        }
        finally
        {
            _imap.Dispose();
        }
    }
}

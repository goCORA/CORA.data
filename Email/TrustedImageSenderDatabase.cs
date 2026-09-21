using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="ITrustedImageSenderStore"/> implementation, following the same
/// pattern as <see cref="TagDatabase"/>. Singleton — the database file/collection is created
/// on first use. The backing file is encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class TrustedImageSenderDatabase : ITrustedImageSenderStore, IFlushableStore, IDisposable
{
    private sealed class TrustedSenderDoc
    {
        [BsonId]
        public string SenderEmail { get; set; } = string.Empty;
        public bool Trusted { get; set; }
    }

    private sealed class MessageImagesAllowedDoc
    {
        [BsonId]
        public string MessageKey { get; set; } = string.Empty;
        public bool Allowed { get; set; }
    }

    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<TrustedSenderDoc> _senders;
    private readonly ILiteCollection<MessageImagesAllowedDoc> _messages;

    public TrustedImageSenderDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);
        _senders = _file.Database.GetCollection<TrustedSenderDoc>("TrustedSenders");
        _messages = _file.Database.GetCollection<MessageImagesAllowedDoc>("MessageImagesAllowed");
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static string NormalizeSender(string senderEmail)
    {
        // From headers are often "Display Name <address@example.com>"; key strictly off
        // the address itself so different display names for the same sender still match.
        var match = System.Text.RegularExpressions.Regex.Match(senderEmail, "<([^>]+)>");
        var address = match.Success ? match.Groups[1].Value : senderEmail;
        return address.Trim().ToLowerInvariant();
    }

    private static string MakeMessageKey(string accountKey, string folderFullName, uint uid) =>
        string.Join("|", accountKey, folderFullName, uid);

    public Task<bool> IsTrustedAsync(string senderEmail, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = _senders.FindById(NormalizeSender(senderEmail));
            return doc?.Trusted ?? false;
        }, cancellationToken);

    public Task SetTrustedAsync(string senderEmail, bool trusted, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _senders.Upsert(new TrustedSenderDoc
            {
                SenderEmail = NormalizeSender(senderEmail),
                Trusted = trusted,
            });
        }, cancellationToken);

    public Task<bool> IsMessageAllowedAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = _messages.FindById(MakeMessageKey(accountKey, folderFullName, uid));
            return doc?.Allowed ?? false;
        }, cancellationToken);

    public Task SetMessageAllowedAsync(
        string accountKey, string folderFullName, uint uid, bool allowed, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _messages.Upsert(new MessageImagesAllowedDoc
            {
                MessageKey = MakeMessageKey(accountKey, folderFullName, uid),
                Allowed = allowed,
            });
        }, cancellationToken);
}

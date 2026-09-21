using CORA.Core.Security;
using LiteDB;

namespace CORA.Data.Email;

/// <summary>
/// LiteDB-backed <see cref="ITagStore"/> implementation, following the same pattern as
/// <see cref="MailSyncDatabase"/>. Tags are global; assignments are keyed by
/// account + folder + uid. Singleton — the database file/collections are created on
/// first use. The backing file is encrypted at rest via <see cref="EncryptedLiteDbFile"/>.
/// </summary>
public class TagDatabase : ITagStore, IFlushableStore, IDisposable
{
    private readonly EncryptedLiteDbFile _file;
    private readonly ILiteCollection<TagDoc> _tags;
    private readonly ILiteCollection<MessageTagDoc> _messageTags;

    public TagDatabase(string path)
    {
        _file = EncryptedLiteDbFile.Open(path);

        _tags = _file.Database.GetCollection<TagDoc>("Tags");

        _messageTags = _file.Database.GetCollection<MessageTagDoc>("MessageTags");
        _messageTags.EnsureIndex(m => m.TagId);
    }

    public void Dispose() => _file.Dispose();

    void IFlushableStore.Flush() => _file.Flush();
    void IFlushableStore.SuppressFlush() => _file.SuppressFlush();

    private static string MakeMessageKey(string accountKey, string folderFullName, uint uid) =>
        string.Join("|", accountKey, folderFullName, uid);

    // Same as MakeMessageKey but without the uid, so callers can match every assignment
    // belonging to one account+folder by prefix. Kept distinct (rather than comparing the
    // AccountKey/FolderName fields separately) so lookups are scoped by the exact same
    // composite identity that SetMessageTagAsync/GetMessageTagAsync already key rows by,
    // instead of relying on two independent field comparisons that could otherwise match
    // rows from other folders/accounts whose numeric Uid happens to coincide.
    private static string MakeMessagePrefix(string accountKey, string folderFullName) =>
        string.Join("|", accountKey, folderFullName) + "|";

    public Task<List<TagDefinition>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
            _tags.FindAll()
                .Select(t => new TagDefinition { Id = t.Id, Name = t.Name, ColorHex = t.ColorHex })
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(), cancellationToken);

    public Task<TagDefinition> AddTagAsync(
        string name, string colorHex, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var doc = new TagDoc
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                ColorHex = colorHex,
            };
            _tags.Insert(doc);
            return new TagDefinition { Id = doc.Id, Name = doc.Name, ColorHex = doc.ColorHex };
        }, cancellationToken);

    public Task UpdateTagAsync(
        string tagId, string name, string colorHex, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _tags.Upsert(new TagDoc
            {
                Id = tagId,
                Name = name,
                ColorHex = colorHex,
            });
        }, cancellationToken);

    public Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _tags.Delete(tagId);
            _messageTags.DeleteMany(m => m.TagId == tagId);
        }, cancellationToken);

    public Task<TagDefinition?> GetMessageTagAsync(
        string accountKey, string folderFullName, uint uid, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeMessageKey(accountKey, folderFullName, uid);
            var assignment = _messageTags.FindById(id);
            if (assignment is null)
                return null;

            var tag = _tags.FindById(assignment.TagId);
            return tag is null ? null : new TagDefinition { Id = tag.Id, Name = tag.Name, ColorHex = tag.ColorHex };
        }, cancellationToken);

    public Task<Dictionary<uint, string>> GetMessageTagIdsAsync(
        string accountKey, string folderFullName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var prefix = MakeMessagePrefix(accountKey, folderFullName);
            return _messageTags
                .FindAll()
                .Where(m => m.Id.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(m => m.Uid, m => m.TagId);
        }, cancellationToken);

    public Task SetMessageTagAsync(
        string accountKey, string folderFullName, uint uid, string? tagId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var id = MakeMessageKey(accountKey, folderFullName, uid);

            if (string.IsNullOrEmpty(tagId))
            {
                _messageTags.Delete(id);
                return;
            }

            _messageTags.Upsert(new MessageTagDoc
            {
                Id = id,
                AccountKey = accountKey,
                FolderName = folderFullName,
                Uid = uid,
                TagId = tagId,
            });
        }, cancellationToken);

    private sealed class TagDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#F0C419";
    }

    private sealed class MessageTagDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public uint Uid { get; set; }
        public string TagId { get; set; } = string.Empty;
    }
}

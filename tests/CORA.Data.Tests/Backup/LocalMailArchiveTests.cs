using System.IO.Compression;
using CORA.Core.Security;
using CORA.Data.Backup;
using CORA.Data.Email;

namespace CORA.Data.Tests.Backup;

/// <summary>Online backups: local-only mail out to a file, and merged back in (add only).</summary>
public class LocalMailArchiveTests : IDisposable
{
    private const string Pop = "pop@example.com";
    private const string Imap = "imap@example.com";

    private sealed class Storage : ISecureKeyStorage
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class TempAppData(string dir) : IAppDataLocation
    {
        public string AppDataDirectory { get; } = dir;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cora-mail-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _open = [];

    public LocalMailArchiveTests()
    {
        Cora.InitializeAsync(new Storage()).GetAwaiter().GetResult();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _open.ForEach(d => d.Dispose());
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Device(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private MailSyncDatabase Open(string dir)
    {
        var db = new MailSyncDatabase(Path.Combine(dir, StoreFiles.MailSync), Path.Combine(dir, StoreFiles.AttachmentsDirectory));
        _open.Add(db);
        return db;
    }

    private static StoredMailSummary Mail(string uidl, uint uid, string messageId = "", bool localOnly = false) => new()
    {
        Uidl = uidl, Uid = uid, From = "a@b.c", Subject = "Subject " + uidl, Date = DateTimeOffset.UnixEpoch, MessageId = messageId, IsLocalOnly = localOnly,
    };

    private static async Task Add(MailSyncDatabase db, string account, string folder, StoredMailSummary mail, string body = "text")
    {
        await db.UpsertAsync(account, folder, [mail]);
        await db.UpsertBodyAsync(account, folder, mail.Uidl, new StoredMailBody { TextBody = body, To = "x@y.z" });
    }

    private static async Task<List<string>> Uidls(MailSyncDatabase db, string account, string folder) =>
        (await db.GetCachedSummariesAsync(account, folder)).Select(s => s.Uidl).Order().ToList();

    private async Task<int> Move(MailSyncDatabase from, MailSyncDatabase to, string toDir)
    {
        var pending = Path.Combine(toDir, StoreFiles.PendingLocalMail);
        await from.ExportLocalMailAsync(pending, [Pop]);
        return await to.MergeLocalMailAsync(pending, Path.Combine(toDir, StoreFiles.PendingMailAttachments));
    }

    [Fact]
    public async Task Export_TakesPop3AndLocalSent_NotOtherImapMail()
    {
        var dir = Device("a");
        var db = Open(dir);
        await Add(db, Pop, "Inbox", Mail("p1", 1, "<p1@x>"));
        await Add(db, Imap, "Sent", Mail("s1", 2, "<s1@x>", localOnly: true));
        await Add(db, Imap, "Inbox", Mail("i1", 3, "<i1@x>"));

        var export = await db.ExportLocalMailAsync(Path.Combine(dir, "export.litedb"), [Pop]);
        Assert.Equal(2, export.Messages);
    }

    [Fact]
    public async Task Merge_AddsMissingMail_WithBodies_AndRemovesThePendingFile()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await Add(from, Pop, "Inbox", Mail("p1", 1), "hello");
        await Add(to, Pop, "Inbox", Mail("p2", 2), "newer mail");

        Assert.Equal(1, await Move(from, to, b));
        Assert.Equal(new[] { "p1", "p2" }, await Uidls(to, Pop, "Inbox"));
        Assert.Equal("hello", (await to.GetBodyAsync(Pop, "Inbox", "p1"))!.TextBody);
        Assert.Equal("newer mail", (await to.GetBodyAsync(Pop, "Inbox", "p2"))!.TextBody);
        Assert.False(File.Exists(Path.Combine(b, StoreFiles.PendingLocalMail)));
    }

    [Fact]
    public async Task Merge_Twice_AddsNothingMore_AndNoFileMeansNothing()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await Add(from, Pop, "Inbox", Mail("p1", 1));

        Assert.Equal(1, await Move(from, to, b));
        Assert.Equal(0, await Move(from, to, b));
        Assert.Single(await Uidls(to, Pop, "Inbox"));
        Assert.Equal(0, await to.MergeLocalMailAsync(Path.Combine(b, "none.litedb"), Path.Combine(b, "none")));
    }

    [Fact]
    public async Task Merge_LeavesExistingMailUnchanged_AndMovedMailIsOneMessage()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await Add(from, Pop, "Inbox", Mail("p1", 1, "<p1@x>"), "old text");
        await Add(to, Pop, "Archive", Mail("p1", 9, "<p1@x>"), "kept text");

        Assert.Equal(0, await Move(from, to, b));
        Assert.Empty(await Uidls(to, Pop, "Inbox"));
        Assert.Equal("kept text", (await to.GetBodyAsync(Pop, "Archive", "p1"))!.TextBody);
    }

    [Fact]
    public async Task SentMail_FromAnotherDevice_IsMatchedByMessageId_AndKeepsItsOwnUid()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await Add(from, Imap, "Sent", Mail("100", 7, "<same@x>", localOnly: true));
        await Add(from, Imap, "Sent", Mail("200", 8, "<other@x>", localOnly: true));
        await Add(to, Imap, "Sent", Mail("300", 7, "<same@x>", localOnly: true));
        await Add(to, Imap, "Sent", Mail("400", 8, "<fourth@x>", localOnly: true));

        Assert.Equal(1, await Move(from, to, b));
        var sent = await to.GetCachedSummariesAsync(Imap, "Sent");
        Assert.Equal(3, sent.Count);
        Assert.Equal(3, sent.Select(s => s.Uid).Distinct().Count());
    }

    [Fact]
    public async Task MailForAnAccountNotHereYet_IsKept()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await Add(from, Pop, "Inbox", Mail("p1", 1));

        Assert.Equal(1, await Move(from, to, b));
        Assert.Equal(new[] { "p1" }, await Uidls(to, Pop, "Inbox"));
    }

    [Fact]
    public async Task Attachments_TravelThroughTheArchive_ToTheNewDevicesFolder()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await from.UpsertAsync(Pop, "Inbox", [Mail("p1", 1)]);
        var saved = await from.SaveAttachmentAsync(Pop, "Inbox", "p1", "0", "file.txt", new MemoryStream([1, 2, 3]));
        await from.UpsertBodyAsync(Pop, "Inbox", "p1", new StoredMailBody
        {
            Attachments = [new StoredMailAttachment { PartSpecifier = "0", FileName = "file.txt", Size = 3, CachePath = saved }],
        });

        var localMail = Path.Combine(a, "export.litedb");
        var export = await from.ExportLocalMailAsync(localMail, [Pop]);
        Assert.Single(export.Attachments);

        using var zip = new MemoryStream();
        new DatabaseBackupService(new TempAppData(a), new Storage()).CreateOnlineBackupArchive(zip, localMail, export.Attachments);
        zip.Position = 0;
        new DatabaseBackupService(new TempAppData(b), new Storage()).RestoreOnlineBackupArchive(zip, restoreMail: true, restoreAiAutonomy: false);

        Assert.Equal(1, await to.MergeLocalMailAsync(Path.Combine(b, StoreFiles.PendingLocalMail), Path.Combine(b, StoreFiles.PendingMailAttachments)));
        var attachment = Assert.Single((await to.GetBodyAsync(Pop, "Inbox", "p1"))!.Attachments);
        Assert.StartsWith(Path.Combine(b, StoreFiles.AttachmentsDirectory), attachment.CachePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(attachment.CachePath));
        Assert.False(Directory.Exists(Path.Combine(b, StoreFiles.PendingMailAttachments)));
    }

    [Fact]
    public async Task AnAttachmentNameLeavingItsFolder_IsNotFollowed()
    {
        var (a, b) = (Device("a"), Device("b"));
        var (from, to) = (Open(a), Open(b));
        await from.UpsertAsync(Pop, "Inbox", [Mail("p1", 1)]);
        await from.UpsertBodyAsync(Pop, "Inbox", "p1", new StoredMailBody
        {
            Attachments = [new StoredMailAttachment { FileName = "x", CachePath = Path.Combine(a, "outside.txt") }],
        });
        File.WriteAllText(Path.Combine(a, "outside.txt"), "x");

        var export = await from.ExportLocalMailAsync(Path.Combine(a, "export.litedb"), [Pop]);
        Assert.Empty(export.Attachments);
    }
}

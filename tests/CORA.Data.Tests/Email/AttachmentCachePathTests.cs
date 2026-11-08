using CORA.Core.Security;
using CORA.Data.Email;
using CORA.Data.Tests.App;

namespace CORA.Data.Tests.Email;

/// <summary>
/// iOS gives the app a new container folder on reinstall or update and moves the data with it, so a
/// cached attachment's saved full path points into a folder that no longer exists (2026-11-08).
/// </summary>
public class AttachmentCachePathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
    private readonly KeyStorage _keys = new();
    private readonly InMemoryPreferenceStore _prefs = new();
    private IDataStores? _stores;

    public void Dispose()
    {
        _stores?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class TempAppData(string dir) : IAppDataLocation
    {
        public string AppDataDirectory { get; } = dir;
    }

    private sealed class KeyStorage : ISecureKeyStorage
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
    }

    private async Task<IDataStores> OpenAsync(string dir)
    {
        _stores?.Dispose();
        Directory.CreateDirectory(dir);
        _stores = await SecureDataStores.CreateAsync(new TempAppData(dir), _keys, _prefs);
        return _stores;
    }

    [Fact]
    public async Task A_cached_attachment_still_opens_after_the_app_folder_moves()
    {
        var before = Path.Combine(_root, "Application", "AAAA-old", "Library");
        var after = Path.Combine(_root, "Application", "BBBB-new", "Library");
        var content = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();

        var stores = await OpenAsync(before);
        string saved;
        using (var stream = new MemoryStream(content))
            saved = await stores.MailSync.SaveAttachmentAsync("pop|me@example.com", "Inbox", "4", "0", "secure-attachments.cora", stream);
        Assert.StartsWith(before, saved);
        stores.Flush();
        _stores!.Dispose();
        _stores = null;

        // The "update": the whole folder moves to a new name; the saved path still names the old one.
        Directory.CreateDirectory(Path.GetDirectoryName(after)!);
        Directory.Move(before, after);
        Assert.False(File.Exists(saved));

        stores = await OpenAsync(after);
        await using var opened = stores.MailSync.OpenAttachment(saved);
        using var copy = new MemoryStream();
        await opened.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task A_path_that_exists_or_points_nowhere_is_left_as_it_is()
    {
        var stores = await OpenAsync(Path.Combine(_root, "app"));
        var db = Assert.IsType<MailSyncDatabase>(stores.MailSync);
        var outside = Path.Combine(_root, "elsewhere.bin");
        File.WriteAllBytes(outside, [1]);
        Assert.Equal(outside, db.ResolveCachePath(outside));
        var missing = Path.Combine(_root, "old", "mail_attachments", "nothing", "here.bin");
        Assert.Equal(missing, db.ResolveCachePath(missing));
        var escaping = Path.Combine(_root, "old", "mail_attachments", "..", "..", "x.bin");
        Assert.Equal(escaping, db.ResolveCachePath(escaping));
        Assert.Equal(string.Empty, db.ResolveCachePath(string.Empty));
    }
}

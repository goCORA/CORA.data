using System.Text;
using CORA.Core.Security;
using CORA.Data.Email;
using CORA.Data.Tests.App;

namespace CORA.Data.Tests.Email;

public class HiddenFolderStoreTests : IDisposable
{
   private const string LegacyKey = "cora.flyout.hiddenFolders";

   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
   private readonly KeyStorage _keys = new();
   private readonly InMemoryPreferenceStore _prefs = new();
   private IDataStores? _stores;

   public void Dispose()
   {
      _stores?.Dispose();
      try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
   }

   private sealed class TempAppData(string dir) : IAppDataLocation
   {
      public string AppDataDirectory { get; } = dir;
   }

   // Shared between "launches" so a reopened store can decrypt what the first one wrote.
   private sealed class KeyStorage : ISecureKeyStorage
   {
      private readonly Dictionary<string, string> _values = [];
      public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
      public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
      public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
   }

   private async Task<IHiddenFolderStore> OpenAsync()
   {
      Directory.CreateDirectory(_dir);
      _stores = await SecureDataStores.CreateAsync(new TempAppData(_dir), _keys, _prefs);
      return _stores.HiddenFolders;
   }

   private async Task<IHiddenFolderStore> ReopenAsync()
   {
      _stores!.Dispose(); // final flush, like a normal shutdown
      return await OpenAsync();
   }

   [Fact]
   public async Task Nothing_is_hidden_by_default()
   {
      var store = await OpenAsync();

      Assert.False(store.IsHidden("a@x.com", "Cell log"));
      Assert.Empty(store.GetHidden("a@x.com"));
      Assert.Empty(store.GetAll());
   }

   [Fact]
   public async Task Hide_persists_across_launches_and_is_per_account()
   {
      var store = await OpenAsync();
      store.Hide("a@x.com", "Cell log");

      store = await ReopenAsync();

      Assert.True(store.IsHidden("a@x.com", "Cell log"));
      Assert.True(store.IsHidden("A@X.com", "Cell log")); // addresses are case-insensitive
      Assert.True(store.IsHidden("a@x.com", "cell log")); // LiteDB ids are case-insensitive, so folders match that way too
      Assert.False(store.IsHidden("b@x.com", "Cell log"));
      Assert.False(store.IsHidden("a@x.com", "Inbox"));
   }

   [Fact]
   public async Task Unhide_and_UnhideAll_make_folders_visible_again()
   {
      var store = await OpenAsync();
      store.Hide("a@x.com", "Cell log");
      store.Hide("a@x.com", "Notes");
      store.Hide("b@x.com", "Notes");

      store.Unhide("a@x.com", "Cell log");
      Assert.False(store.IsHidden("a@x.com", "Cell log"));
      Assert.True(store.IsHidden("a@x.com", "Notes"));

      store.UnhideAll("a@x.com");
      Assert.Empty(store.GetHidden("a@x.com"));
      Assert.True(store.IsHidden("b@x.com", "Notes"));
   }

   [Fact]
   public async Task GetAll_lists_every_hidden_folder_ordered_by_account_then_folder()
   {
      var store = await OpenAsync();
      store.Hide("b@x.com", "Notes");
      store.Hide("a@x.com", "Zed");
      store.Hide("a@x.com", "Cell log");

      Assert.Equal(
         [("a@x.com", "Cell log"), ("a@x.com", "Zed"), ("b@x.com", "Notes")],
         store.GetAll());
   }

   [Fact]
   public async Task Changed_is_raised_only_when_something_changed()
   {
      var store = await OpenAsync();
      var raised = 0;
      store.Changed += (_, _) => raised++;

      store.Hide("a@x.com", "Cell log");
      store.Hide("a@x.com", "Cell log");
      store.Unhide("a@x.com", "Nope");
      store.UnhideAll("b@x.com");

      Assert.Equal(1, raised);
   }

   [Fact]
   public async Task Hidden_folders_are_encrypted_on_disk()
   {
      var store = await OpenAsync();
      store.Hide("someone@example.com", "Cell log");

      _stores!.Flush();

      var raw = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(_dir, "hidden_folders.litedb")));
      Assert.DoesNotContain("Cell log", raw);
      Assert.DoesNotContain("someone@example.com", raw);
   }

   [Fact]
   public async Task Legacy_plaintext_preference_is_imported_then_removed()
   {
      _prefs.Values[LegacyKey] = """{"a@x.com":["Cell log","Notes"],"b@x.com":["Old"]}""";

      var store = await OpenAsync();

      Assert.Equal(
         [("a@x.com", "Cell log"), ("a@x.com", "Notes"), ("b@x.com", "Old")],
         store.GetAll());
      Assert.False(_prefs.Values.ContainsKey(LegacyKey));
      Assert.True(File.Exists(Path.Combine(_dir, "hidden_folders.litedb"))); // written before the preference went
   }

   [Fact]
   public async Task Malformed_legacy_preference_is_dropped_and_hides_nothing()
   {
      _prefs.Values[LegacyKey] = "{not json";

      var store = await OpenAsync();

      Assert.Empty(store.GetAll());
      Assert.False(_prefs.Values.ContainsKey(LegacyKey));
   }
}

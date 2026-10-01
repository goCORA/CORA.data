using CORA.Core.Security;
using CORA.Data.Email;
using CORA.Data.Tests.App;

namespace CORA.Data.Tests.Email;

public class RescuedMessageStoreTests : IDisposable
{
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

   private sealed class KeyStorage : ISecureKeyStorage
   {
      private readonly Dictionary<string, string> _values = [];
      public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
      public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
      public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
   }

   private async Task<IMailSyncStore> OpenAsync()
   {
      Directory.CreateDirectory(_dir);
      _stores = await SecureDataStores.CreateAsync(new TempAppData(_dir), _keys, _prefs);
      return _stores.MailSync;
   }

   private async Task<IMailSyncStore> ReopenAsync()
   {
      _stores!.Dispose();
      return await OpenAsync();
   }

   [Fact]
   public async Task Nothing_is_rescued_by_default()
   {
      var store = await OpenAsync();

      Assert.Empty(await store.GetRescuedAsync("a@x.com", ["<one@x>"]));
   }

   [Fact]
   public async Task Marked_messages_are_reported_and_unmarked_ones_are_not()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["one@x", "two@x"]);

      var found = await store.GetRescuedAsync("a@x.com", ["one@x", "three@x"]);

      Assert.Equal(["one@x"], found);
   }

   [Fact]
   public async Task Message_ids_match_with_or_without_angle_brackets()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["<one@x>"]);

      var found = await store.GetRescuedAsync("a@x.com", ["one@x", " <one@x> "]);

      Assert.Equal(["one@x"], found);
   }

   [Fact]
   public async Task Blank_message_ids_are_ignored()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["", "  ", "<>"]);

      Assert.Empty(await store.GetRescuedAsync("a@x.com", ["", "<>"]));
   }

   [Fact]
   public async Task Rescued_messages_are_per_account()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["one@x"]);

      Assert.Equal(["one@x"], await store.GetRescuedAsync("a@x.com", ["one@x"]));
      Assert.Empty(await store.GetRescuedAsync("b@x.com", ["one@x"]));
   }

   [Fact]
   public async Task Rescued_messages_persist_across_launches()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["one@x"]);

      store = await ReopenAsync();

      Assert.Equal(["one@x"], await store.GetRescuedAsync("a@x.com", ["one@x"]));
   }

   [Fact]
   public async Task Marking_the_same_message_twice_is_harmless()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["one@x"]);
      await store.MarkRescuedAsync("a@x.com", ["one@x", "<one@x>"]);

      Assert.Equal(["one@x"], await store.GetRescuedAsync("a@x.com", ["one@x"]));
   }

   [Fact]
   public async Task Removing_an_account_clears_only_its_rescued_messages()
   {
      var store = await OpenAsync();
      await store.MarkRescuedAsync("a@x.com", ["one@x"]);
      await store.MarkRescuedAsync("b@x.com", ["one@x"]);

      await store.DeleteAccountDataAsync("A@X.com");

      Assert.Empty(await store.GetRescuedAsync("a@x.com", ["one@x"]));
      Assert.Equal(["one@x"], await store.GetRescuedAsync("b@x.com", ["one@x"]));
   }
}

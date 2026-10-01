using CORA.Core.Security;
using CORA.Data.Email;
using CORA.Data.Tests.App;

namespace CORA.Data.Tests.Email;

public class FolderOrderStoreTests : IDisposable
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

   // Shared between "launches" so a reopened store can decrypt what the first one wrote.
   private sealed class KeyStorage : ISecureKeyStorage
   {
      private readonly Dictionary<string, string> _values = [];
      public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
      public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
      public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
   }

   private async Task<IFolderOrderStore> OpenAsync()
   {
      Directory.CreateDirectory(_dir);
      _stores = await SecureDataStores.CreateAsync(new TempAppData(_dir), _keys, _prefs);
      return _stores.FolderOrder;
   }

   private async Task<IFolderOrderStore> ReopenAsync()
   {
      _stores!.Dispose(); // final flush, like a normal shutdown
      return await OpenAsync();
   }

   [Fact]
   public async Task No_order_is_saved_by_default()
   {
      var store = await OpenAsync();

      Assert.Empty(store.GetOrder("a@x.com"));
   }

   [Fact]
   public async Task SetOrder_persists_across_launches_and_keeps_the_order()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["Work", "Home", "Archive"]);

      store = await ReopenAsync();

      Assert.Equal(["Work", "Home", "Archive"], store.GetOrder("a@x.com"));
   }

   [Fact]
   public async Task Orders_are_per_account_and_addresses_are_case_insensitive()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One", "Two"]);
      store.SetOrder("b@x.com", ["Two", "One"]);

      Assert.Equal(["One", "Two"], store.GetOrder("A@X.com"));
      Assert.Equal(["Two", "One"], store.GetOrder("b@x.com"));
      Assert.Empty(store.GetOrder("c@x.com"));
   }

   [Fact]
   public async Task SetOrder_replaces_the_earlier_order()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One", "Two"]);

      store.SetOrder("a@x.com", ["Two", "One", "Three"]);

      Assert.Equal(["Two", "One", "Three"], store.GetOrder("a@x.com"));
   }

   [Fact]
   public async Task SetOrder_with_an_empty_list_clears_the_order()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One"]);

      store.SetOrder("a@x.com", []);

      Assert.Empty(store.GetOrder("a@x.com"));
   }

   [Fact]
   public async Task Clear_removes_only_that_accounts_order()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One"]);
      store.SetOrder("b@x.com", ["Two"]);

      store.Clear("a@x.com");

      Assert.Empty(store.GetOrder("a@x.com"));
      Assert.Equal(["Two"], store.GetOrder("b@x.com"));
   }

   [Fact]
   public async Task Rename_keeps_the_folder_at_its_position()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One", "Two", "Three"]);

      store.Rename("a@x.com", "two", "Deux");

      Assert.Equal(["One", "Deux", "Three"], store.GetOrder("a@x.com"));
   }

   [Fact]
   public async Task Rename_of_an_unknown_folder_or_account_changes_nothing()
   {
      var store = await OpenAsync();
      store.SetOrder("a@x.com", ["One"]);
      var raised = 0;
      store.Changed += (_, _) => raised++;

      store.Rename("a@x.com", "Missing", "Other");
      store.Rename("nobody@x.com", "One", "Other");

      Assert.Equal(["One"], store.GetOrder("a@x.com"));
      Assert.Equal(0, raised);
   }

   [Fact]
   public async Task Changed_is_raised_only_when_something_actually_changes()
   {
      var store = await OpenAsync();
      var raised = 0;
      store.Changed += (_, _) => raised++;

      store.SetOrder("a@x.com", ["One", "Two"]);
      Assert.Equal(1, raised);

      store.SetOrder("a@x.com", ["One", "Two"]); // same order again
      Assert.Equal(1, raised);

      store.Clear("a@x.com");
      Assert.Equal(2, raised);

      store.Clear("a@x.com"); // nothing left to clear
      Assert.Equal(2, raised);
   }
}

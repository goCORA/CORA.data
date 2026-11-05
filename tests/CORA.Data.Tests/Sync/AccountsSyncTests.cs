using CORA.Core.Security;
using CORA.Data.Email;
using CORA.Data.Sync;
using CORA.Data.Tests.App;

namespace CORA.Data.Tests.Sync;

/// <summary>The rules for mail accounts, on real encrypted stores.</summary>
public class AccountsSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora-tests-" + Guid.NewGuid().ToString("N"));
    private IDataStores? _stores;
    private bool _ask = true;
    private readonly List<string> _added = [];
    private readonly List<(string Email, string Password)> _changed = [];

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

    private async Task<AccountsSyncAdapter> OpenAsync()
    {
        Directory.CreateDirectory(_dir);
        _stores = await SecureDataStores.CreateAsync(new TempAppData(_dir), new KeyStorage(), new InMemoryPreferenceStore());
        return new AccountsSyncAdapter(_stores.Accounts, _stores.HiddenFolders, _stores.FolderOrder, _stores.SyncState, () => _ask,
            a => { _added.Add(a.EmailAddress); return Task.CompletedTask; },
            a => { _changed.Add((a.EmailAddress, a.Password)); return Task.CompletedTask; });
    }

    private static MailAccount Work(string host = "mail.example.com", string name = "Work") => new()
    {
        DisplayName = name, EmailAddress = "Work@Example.com", Password = "secret", UserName = "work",
        IncomingProtocol = IncomingProtocol.Imap, ImapHost = host, ImapPort = 993, SmtpHost = "smtp.example.com", SmtpPort = 587,
    };

    private const string Id = "acct:work@example.com";
    private const string Pwd = "pwd:work@example.com";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly string[] NoDeletes = [];

    private static Dictionary<string, string> One(string id, string text) => new() { [id] = text };

    [Fact]
    public async Task History_NamesEachItem_AndLeavesOutWhatIsHeld()
    {
        var adapter = await OpenAsync();
        Assert.Equal((SyncHistoryItem.Password, "work@example.com"), adapter.Describe(Pwd));
        Assert.Equal((SyncHistoryItem.Account, "work@example.com"), adapter.Describe(Id));
        Assert.Equal((SyncHistoryItem.PasswordsSetting, string.Empty), adapter.Describe("setting:passwords"));

        await adapter.ApplyAsync(new Dictionary<string, string> { [Id] = AccountsSyncAdapter.ToText(Work()), [Pwd] = "secret" }, NoDeletes, None);
        var notApplied = await adapter.GetNotAppliedAsync(None);
        Assert.Contains(Id, notApplied);
        Assert.Contains(Pwd, notApplied);
    }

    [Fact]
    public void AHeldAccount_KeepsTheNameItHasOnTheOtherDevices()
    {
        var held = new SyncHeldAccount { Email = "work@example.com", State = SyncHeldState.Skipped, Data = AccountsSyncAdapter.ToText(Work(name: "My work mail")) };
        Assert.Equal("My work mail", AccountsSyncAdapter.DisplayNameOf(held));
        Assert.Equal(string.Empty, AccountsSyncAdapter.DisplayNameOf(new SyncHeldAccount { Email = "x@y.z", Data = null }));
    }

    [Fact]
    public async Task Reading_GivesSettingsAndPassword_AsSeparateItems_AndNeverTokens()
    {
        var adapter = await OpenAsync();
        var account = Work();
        account.AccessToken = "token";
        await _stores!.Accounts.SaveAccountAsync(account);

        var items = await adapter.ReadAsync(None);
        Assert.Equal("secret", items[Pwd]);
        Assert.DoesNotContain("secret", items[Id]);
        Assert.DoesNotContain("token", items[Id]);
        Assert.Equal("on", items["setting:passwords"]);
    }

    [Fact]
    public async Task OAuthAccounts_SyncOnlyTheProviderAndAddress()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(new MailAccount
        {
            EmailAddress = "me@gmail.com", AuthMethod = AuthMethod.OAuth, OAuthProvider = "Google",
            RefreshToken = "refresh", OAuthClientSecret = "client-secret", OAuthTokenEndpoint = "https://attacker.example/token", ImapHost = "imap.gmail.com",
        });
        var text = (await adapter.ReadAsync(None))["acct:me@gmail.com"];
        Assert.DoesNotContain("refresh", text);
        Assert.DoesNotContain("client-secret", text);
        Assert.DoesNotContain("attacker", text);
        Assert.DoesNotContain("imap.gmail.com", text);
        Assert.Contains("Google", text);
        Assert.DoesNotContain("pwd:me@gmail.com", (await adapter.ReadAsync(None)).Keys);
    }

    [Fact]
    public async Task ANewAccount_IsHeld_WithoutItsPassword_UntilAdded()
    {
        var adapter = await OpenAsync();
        await adapter.ApplyAsync(new Dictionary<string, string> { [Id] = AccountsSyncAdapter.ToText(Work()), [Pwd] = "secret" }, NoDeletes, None);

        Assert.Empty(await _stores!.Accounts.LoadAllAsync());
        var held = Assert.Single(await adapter.GetHeldAsync());
        Assert.Equal(SyncHeldState.WaitingAdd, held.State);
        Assert.DoesNotContain("secret", held.Data);
        Assert.Contains(Pwd, await adapter.GetElsewhereAsync(None));

        await _stores.SyncState.SetKindAsync(new SyncKindState { Kind = "accounts", HighestVersionSeen = 3, AppliedVersion = 3 });
        await adapter.AddHeldAsync("work@example.com");
        var account = Assert.Single(await _stores.Accounts.LoadAllAsync());
        Assert.Equal("mail.example.com", account.ImapHost);
        Assert.True(string.IsNullOrEmpty(account.Password));
        Assert.Equal(new[] { "work@example.com" }, _added);
        Assert.Empty(await adapter.GetHeldAsync());
        Assert.Equal(0, (await _stores.SyncState.GetKindAsync("accounts")).AppliedVersion);
        Assert.Contains(Pwd, await adapter.GetElsewhereAsync(None));

        await adapter.ApplyAsync(One(Pwd, "secret"), NoDeletes, None);
        Assert.Equal("secret", Assert.Single(await _stores.Accounts.LoadAllAsync()).Password);
        Assert.Contains(("work@example.com", "secret"), _changed); // the running app is told too
    }

    [Fact]
    public async Task WithTheConfirmationOff_ANewPasswordAccountArrivesAtOnce()
    {
        var adapter = await OpenAsync();
        _ask = false;
        await adapter.ApplyAsync(new Dictionary<string, string> { [Id] = AccountsSyncAdapter.ToText(Work()), [Pwd] = "secret" }, NoDeletes, None);
        Assert.Equal("secret", Assert.Single(await _stores!.Accounts.LoadAllAsync()).Password);
        Assert.Empty(await adapter.GetHeldAsync());
    }

    [Fact]
    public async Task ASkippedAccount_StaysSkipped_EvenWithTheConfirmationOff()
    {
        var adapter = await OpenAsync();
        await adapter.ApplyAsync(One(Id, AccountsSyncAdapter.ToText(Work())), NoDeletes, None);
        await adapter.SkipHeldAsync("work@example.com");
        _ask = false;

        await adapter.ApplyAsync(One(Id, AccountsSyncAdapter.ToText(Work(name: "Renamed"))), NoDeletes, None);

        Assert.Empty(await _stores!.Accounts.LoadAllAsync());
        var held = Assert.Single(await adapter.GetHeldAsync());
        Assert.Equal(SyncHeldState.Skipped, held.State);
        Assert.Contains("Renamed", held.Data);
    }

    [Fact]
    public async Task AServerChange_WaitsForAccept_AndAttackersCantRedirectTheLogin()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(Work());
        var moved = AccountsSyncAdapter.ToText(Work(host: "evil.example.net"));

        await adapter.ApplyAsync(One(Id, moved), NoDeletes, None);

        Assert.Equal("mail.example.com", Assert.Single(await _stores.Accounts.LoadAllAsync()).ImapHost);
        Assert.Equal(SyncHeldState.WaitingChange, Assert.Single(await adapter.GetHeldAsync()).State);
        Assert.Equal(moved, (await adapter.ReadAsync(None))[Id]); // not this device's edit

        await adapter.RejectAsync("work@example.com");
        Assert.Equal("mail.example.com", Assert.Single(await _stores.Accounts.LoadAllAsync()).ImapHost);
        Assert.Equal(SyncHeldState.RejectedChange, Assert.Single(await adapter.GetHeldAsync()).State);

        await adapter.ApplyAsync(One(Id, moved), NoDeletes, None);
        await adapter.AcceptChangeAsync("work@example.com");
        var account = Assert.Single(await _stores.Accounts.LoadAllAsync());
        Assert.Equal("evil.example.net", account.ImapHost);
        Assert.Equal("secret", account.Password);
        Assert.Empty(await adapter.GetHeldAsync());
    }

    [Fact]
    public async Task ARename_AppliesWithoutAsking()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(Work());
        await adapter.ApplyAsync(One(Id, AccountsSyncAdapter.ToText(Work(name: "Office"))), NoDeletes, None);
        Assert.Equal("Office", Assert.Single(await _stores.Accounts.LoadAllAsync()).DisplayName);
        Assert.Empty(await adapter.GetHeldAsync());
    }

    [Fact]
    public async Task ARemoval_WaitsForAnswer_AndNeverReadsAsAnEditHere()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(Work());
        await adapter.ApplyAsync(new Dictionary<string, string>(), [Id], None);

        Assert.Single(await _stores.Accounts.LoadAllAsync());
        Assert.Equal(SyncHeldState.WaitingRemove, Assert.Single(await adapter.GetHeldAsync()).State);
        Assert.False((await adapter.ReadAsync(None)).ContainsKey(Id));

        await adapter.RejectAsync("work@example.com");
        Assert.Equal(SyncHeldState.RejectedRemove, Assert.Single(await adapter.GetHeldAsync()).State);
        Assert.Single(await _stores.Accounts.LoadAllAsync());
    }

    [Fact]
    public async Task TurningPasswordsOff_LeavesLocalPasswords_AndTakesThemOutOfSync()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(Work());
        await adapter.ApplyAsync(One("setting:passwords", "off"), [Pwd], None);

        Assert.False(await adapter.GetIncludePasswordsAsync());
        Assert.Equal("secret", Assert.Single(await _stores.Accounts.LoadAllAsync()).Password);
        var items = await adapter.ReadAsync(None);
        Assert.False(items.ContainsKey(Pwd));
        Assert.Equal("off", items["setting:passwords"]);
        Assert.Empty(await adapter.GetElsewhereAsync(None));
    }

    [Fact]
    public async Task HiddenFoldersAndOrder_TravelWithTheAccount()
    {
        var adapter = await OpenAsync();
        await _stores!.Accounts.SaveAccountAsync(Work());
        await adapter.ApplyAsync(new Dictionary<string, string>
        {
            ["hidden:work@example.com"] = "[\"Spam\"]",
            ["order:work@example.com"] = "[\"B\",\"A\"]",
            ["hidden:other@example.com"] = "[\"Ignored\"]",
        }, NoDeletes, None);

        Assert.True(_stores.HiddenFolders.IsHidden("work@example.com", "Spam"));
        Assert.Equal(new[] { "B", "A" }, _stores.FolderOrder.GetOrder("work@example.com"));
        Assert.Empty(_stores.HiddenFolders.GetHidden("other@example.com"));
    }

    [Fact]
    public async Task ABlankFieldReadBackFromTheStore_IsNotAProtectedChange()
    {
        await OpenAsync();
        var account = Work();
        account.UserName = string.Empty;
        await _stores!.Accounts.SaveAccountAsync(account);
        var stored = Assert.Single(await _stores.Accounts.LoadAllAsync());

        var edited = Work(name: "Renamed");
        edited.UserName = string.Empty;
        Assert.False(AccountsSyncAdapter.IsProtectedChange(stored, edited));
    }

    [Fact]
    public async Task OnlyServerFields_CountAsAProtectedChange()
    {
        await Task.CompletedTask;
        Assert.False(AccountsSyncAdapter.IsProtectedChange(Work(), Work(name: "Other")));
        Assert.True(AccountsSyncAdapter.IsProtectedChange(Work(), Work(host: "other")));
    }
}

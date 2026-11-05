using System.Text.Json;
using CORA.Data.Email;

namespace CORA.Data.Sync;

/// <summary>
/// Mail accounts, with the rules of claude/design-cloud-backup-sync.md. A device never takes a new
/// account, a server change or a removal silently: those are <b>held</b> until the user answers, and
/// a held account's settings stay in the sync state. Passwords are never held: they are separate
/// items, so they stay in the cloud until the account is really on this device. Tokens never sync:
/// an OAuth account arrives as a provider and an address, and each device signs in once.
/// </summary>
public sealed class AccountsSyncAdapter(
    ICredentialStore accounts,
    IHiddenFolderStore hidden,
    IFolderOrderStore order,
    ISyncStateStore state,
    Func<bool> askBeforeAdding,
    Func<MailAccount, Task> accountAdded,
    Func<MailAccount, Task> accountChanged) : ISyncAdapter
{
    private const string Account = "acct:";
    private const string Password = "pwd:";
    private const string Hidden = "hidden:";
    private const string Order = "order:";
    private const string PasswordsSetting = "setting:passwords";
    private const string PasswordsKey = "passwords";
    private const string On = "on";
    private const string Off = "off";

    private sealed record Data(
        string Name, string Email, string Protocol,
        string ImapHost, int ImapPort, string ImapSecurity,
        string SmtpHost, int SmtpPort, string SmtpSecurity,
        string User, string Auth, string Provider);

    public string Kind => "accounts";

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // LiteDB gives empty strings back as null: a blank field must read the same before and after.
    private static string Text(string? value) => value ?? string.Empty;

    private static Data ToData(MailAccount a) => a.IsOAuth
        ? new Data(Text(a.DisplayName), Normalize(a.EmailAddress), string.Empty, string.Empty, 0, string.Empty, string.Empty, 0, string.Empty, string.Empty, nameof(AuthMethod.OAuth), Text(a.OAuthProvider))
        : new Data(Text(a.DisplayName), Normalize(a.EmailAddress), a.IncomingProtocol.ToString(),
            Text(a.ImapHost), a.ImapPort, a.ImapSecurity.ToString(), Text(a.SmtpHost), a.SmtpPort, a.SmtpSecurity.ToString(),
            Text(a.UserName), nameof(AuthMethod.Password), string.Empty);

    /// <summary>The account as sync sees it: settings only, never a password or token.</summary>
    public static string ToText(MailAccount account) => JsonSerializer.Serialize(ToData(account));

    private static Data Parse(string text) => JsonSerializer.Deserialize<Data>(text) ?? throw new InvalidDataException("An empty mail account.");

    // A change to any of these can redirect a login, so it needs the user's Accept on each device.
    private static bool IsProtectedChange(Data a, Data b) => (a with { Name = string.Empty }) != (b with { Name = string.Empty });

    /// <summary>True when a change from <paramref name="before"/> to <paramref name="after"/> needs the CORA key code.</summary>
    public static bool IsProtectedChange(MailAccount before, MailAccount after) => IsProtectedChange(ToData(before), ToData(after));

    private static void ApplySettings(MailAccount a, Data d)
    {
        a.DisplayName = d.Name;
        if (d.Auth == nameof(AuthMethod.OAuth))
            return;
        a.IncomingProtocol = Enum.Parse<IncomingProtocol>(d.Protocol);
        a.ImapHost = d.ImapHost;
        a.ImapPort = d.ImapPort;
        a.ImapSecurity = Enum.Parse<MailSecurity>(d.ImapSecurity);
        a.SmtpHost = d.SmtpHost;
        a.SmtpPort = d.SmtpPort;
        a.SmtpSecurity = Enum.Parse<MailSecurity>(d.SmtpSecurity);
        a.UserName = d.User;
    }

    private async Task<bool> PasswordsOnAsync(CancellationToken cancellationToken) =>
        (await state.GetValueAsync(PasswordsKey, cancellationToken).ConfigureAwait(false) ?? On) == On;

    /// <summary>"Include mail passwords": one setting for the whole goCORA account, carried by sync.</summary>
    public Task<bool> GetIncludePasswordsAsync(CancellationToken cancellationToken = default) => PasswordsOnAsync(cancellationToken);

    public Task SetIncludePasswordsAsync(bool include, CancellationToken cancellationToken = default) =>
        state.SetValueAsync(PasswordsKey, include ? On : Off, cancellationToken);

    private async Task<Dictionary<string, SyncHeldAccount>> HeldAsync(CancellationToken cancellationToken) =>
        (await state.GetHeldAccountsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.Email);

    private static List<string> Lines(string? json) => JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];

    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        var passwords = await PasswordsOnAsync(cancellationToken).ConfigureAwait(false);
        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        var items = new Dictionary<string, string> { [PasswordsSetting] = passwords ? On : Off };
        foreach (var a in await accounts.LoadAllAsync().ConfigureAwait(false))
        {
            var email = Normalize(a.EmailAddress);
            held.TryGetValue(email, out var h);
            if (h is { State: SyncHeldState.WaitingRemove or SyncHeldState.RejectedRemove })
                continue;   // the cloud says it's gone: this device's copy isn't an edit

            // A change not accepted is still what the cloud has: it mustn't read as an edit here.
            items[Account + email] = h is { State: SyncHeldState.WaitingChange or SyncHeldState.RejectedChange, Data: { } data } ? data : ToText(a);
            if (passwords && !a.IsOAuth && !string.IsNullOrEmpty(a.Password))
                items[Password + email] = a.Password;

            var hiddenFolders = hidden.GetHidden(email).Order(StringComparer.Ordinal).ToList();
            if (hiddenFolders.Count > 0)
                items[Hidden + email] = JsonSerializer.Serialize(hiddenFolders);
            var folderOrder = order.GetOrder(email);
            if (folderOrder.Count > 0)
                items[Order + email] = JsonSerializer.Serialize(folderOrder);
        }
        return items;
    }

    public async Task<IReadOnlySet<string>> GetElsewhereAsync(CancellationToken cancellationToken)
    {
        var passwords = await PasswordsOnAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>();
        foreach (var h in (await HeldAsync(cancellationToken).ConfigureAwait(false)).Values)
        {
            if (h.State is not (SyncHeldState.WaitingAdd or SyncHeldState.Skipped))
                continue;
            ids.UnionWith([Account + h.Email, Password + h.Email, Hidden + h.Email, Order + h.Email]);
        }
        if (passwords)
        {
            foreach (var a in await accounts.LoadAllAsync().ConfigureAwait(false))
            {
                if (!a.IsOAuth && string.IsNullOrEmpty(a.Password))
                    ids.Add(Password + Normalize(a.EmailAddress));   // added here: its password is on its way
            }
        }
        return ids;
    }

    public async Task<IReadOnlySet<string>> GetNotAppliedAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>();
        foreach (var h in (await HeldAsync(cancellationToken).ConfigureAwait(false)).Values)
        {
            ids.Add(Account + h.Email);
            if (h.State is not (SyncHeldState.WaitingChange or SyncHeldState.RejectedChange))
                ids.UnionWith([Password + h.Email, Hidden + h.Email, Order + h.Email]);
        }
        if (!await PasswordsOnAsync(cancellationToken).ConfigureAwait(false))
        {
            // The setting turned off: passwords leave the cloud, and this device keeps its own.
            foreach (var a in await accounts.LoadAllAsync().ConfigureAwait(false))
                ids.Add(Password + Normalize(a.EmailAddress));
        }
        return ids;
    }

    public (string Item, string Detail) Describe(string itemId)
    {
        if (itemId.StartsWith(Account, StringComparison.Ordinal))
            return (SyncHistoryItem.Account, itemId[Account.Length..]);
        if (itemId.StartsWith(Password, StringComparison.Ordinal))
            return (SyncHistoryItem.Password, itemId[Password.Length..]);
        if (itemId.StartsWith(Hidden, StringComparison.Ordinal))
            return (SyncHistoryItem.Folders, itemId[Hidden.Length..]);
        if (itemId.StartsWith(Order, StringComparison.Ordinal))
            return (SyncHistoryItem.Folders, itemId[Order.Length..]);
        return (SyncHistoryItem.PasswordsSetting, string.Empty);
    }

    public Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
    {
        if (upserts.TryGetValue(PasswordsSetting, out var setting))
            await state.SetValueAsync(PasswordsKey, setting, cancellationToken).ConfigureAwait(false);

        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        var local = (await accounts.LoadAllAsync().ConfigureAwait(false)).ToDictionary(a => Normalize(a.EmailAddress));

        foreach (var (id, text) in upserts.Where(u => u.Key.StartsWith(Account, StringComparison.Ordinal)))
        {
            var email = id[Account.Length..];
            var cloud = Parse(text);
            held.TryGetValue(email, out var h);
            if (!local.TryGetValue(email, out var account))
            {
                if (!askBeforeAdding() && cloud.Auth != nameof(AuthMethod.OAuth) && h is not { State: SyncHeldState.Skipped })
                {
                    account = new MailAccount { EmailAddress = email };
                    ApplySettings(account, cloud);
                    await accounts.SaveAccountAsync(account).ConfigureAwait(false);
                    await accountAdded(account).ConfigureAwait(false);
                    local[email] = account;
                    await state.RemoveHeldAccountAsync(email, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await HoldAsync(email, h?.State ?? SyncHeldState.WaitingAdd, text, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (IsProtectedChange(ToData(account), cloud))
            {
                await HoldAsync(email, SyncHeldState.WaitingChange, text, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ApplySettings(account, cloud);
                await accounts.SaveAccountAsync(account).ConfigureAwait(false);
                await accountChanged(account).ConfigureAwait(false);
                if (h is not null)
                    await state.RemoveHeldAccountAsync(email, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (id, value) in upserts)
        {
            if (id.StartsWith(Password, StringComparison.Ordinal) && local.TryGetValue(id[Password.Length..], out var account))
            {
                account.Password = value;
                await accounts.SaveAccountAsync(account).ConfigureAwait(false);
                await accountChanged(account).ConfigureAwait(false);
            }
            else if (id.StartsWith(Hidden, StringComparison.Ordinal) && local.ContainsKey(id[Hidden.Length..]))
            {
                var email = id[Hidden.Length..];
                hidden.UnhideAll(email);
                foreach (var folder in Lines(value))
                    hidden.Hide(email, folder);
            }
            else if (id.StartsWith(Order, StringComparison.Ordinal) && local.ContainsKey(id[Order.Length..]))
            {
                order.SetOrder(id[Order.Length..], Lines(value));
            }
        }

        foreach (var id in deletes)
        {
            if (id.StartsWith(Account, StringComparison.Ordinal))
            {
                var email = id[Account.Length..];
                if (local.ContainsKey(email))
                    await HoldAsync(email, SyncHeldState.WaitingRemove, null, cancellationToken).ConfigureAwait(false);
                else if (held.ContainsKey(email))
                    await state.RemoveHeldAccountAsync(email, cancellationToken).ConfigureAwait(false);
            }
            else if (id.StartsWith(Hidden, StringComparison.Ordinal))
            {
                hidden.UnhideAll(id[Hidden.Length..]);
            }
            else if (id.StartsWith(Order, StringComparison.Ordinal))
            {
                order.Clear(id[Order.Length..]);
            }
            // A password going away (the setting turned off) leaves this device's copy alone.
        }
    }

    private Task HoldAsync(string email, SyncHeldState heldState, string? data, CancellationToken cancellationToken) =>
        state.SetHeldAccountAsync(new SyncHeldAccount { Email = email, State = heldState, Data = data }, cancellationToken);

    // ---- The user's answers ----------------------------------------------------------------------

    /// <summary>What's waiting for an answer, and the skipped accounts for "Synced accounts not on this device".</summary>
    public Task<List<SyncHeldAccount>> GetHeldAsync(CancellationToken cancellationToken = default) =>
        state.GetHeldAccountsAsync(cancellationToken);

    /// <summary>The provider of a held OAuth account (it needs a sign-in), or null for a password account.</summary>
    public static string? OAuthProviderOf(SyncHeldAccount held) =>
        held.Data is { } text && Parse(text) is { Auth: nameof(AuthMethod.OAuth) } data ? data.Provider : null;

    /// <summary>The name the account has on the other devices.</summary>
    public static string DisplayNameOf(SyncHeldAccount held) => held.Data is { } text ? Parse(text).Name : string.Empty;

    /// <summary>The incoming server of a held account, for the Accept or Reject question.</summary>
    public static string ServerOf(SyncHeldAccount held) => held.Data is { } text ? Parse(text).ImapHost : string.Empty;

    /// <summary>
    /// Adds a held password account. Its password comes with the next sync (forced here), when the
    /// account's password is in the cloud; otherwise the user types it once.
    /// </summary>
    public async Task AddHeldAsync(string email, CancellationToken cancellationToken = default)
    {
        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(Normalize(email), out var h) || h.Data is null)
            return;
        var account = new MailAccount { EmailAddress = h.Email };
        ApplySettings(account, Parse(h.Data));
        await accounts.SaveAccountAsync(account).ConfigureAwait(false);
        await accountAdded(account).ConfigureAwait(false);
        await state.RemoveHeldAccountAsync(h.Email, cancellationToken).ConfigureAwait(false);
        await FetchAgainAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SkipHeldAsync(string email, CancellationToken cancellationToken = default)
    {
        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        if (held.TryGetValue(Normalize(email), out var h))
            await HoldAsync(h.Email, SyncHeldState.Skipped, h.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"This device only": call after removing the account here. It stays on the other devices.</summary>
    public Task MarkRemovedHereOnlyAsync(MailAccount account, CancellationToken cancellationToken = default) =>
        HoldAsync(Normalize(account.EmailAddress), SyncHeldState.Skipped, ToText(account), cancellationToken);

    public async Task AcceptChangeAsync(string email, CancellationToken cancellationToken = default)
    {
        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        var local = (await accounts.LoadAllAsync().ConfigureAwait(false)).ToDictionary(a => Normalize(a.EmailAddress));
        if (!held.TryGetValue(Normalize(email), out var h) || h.Data is null || !local.TryGetValue(h.Email, out var account))
            return;
        ApplySettings(account, Parse(h.Data));
        await accounts.SaveAccountAsync(account).ConfigureAwait(false);
        await accountChanged(account).ConfigureAwait(false);
        await state.RemoveHeldAccountAsync(h.Email, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reject a change or a removal: this device stays as it is.</summary>
    public async Task RejectAsync(string email, CancellationToken cancellationToken = default)
    {
        var held = await HeldAsync(cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(Normalize(email), out var h))
            return;
        var rejected = h.State == SyncHeldState.WaitingRemove ? SyncHeldState.RejectedRemove : SyncHeldState.RejectedChange;
        await HoldAsync(h.Email, rejected, h.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accept a removal: call after removing the account here.</summary>
    public Task AcceptRemovalAsync(string email, CancellationToken cancellationToken = default) =>
        state.RemoveHeldAccountAsync(Normalize(email), cancellationToken);

    // The next run downloads the cloud copy even if nothing new is there.
    private async Task FetchAgainAsync(CancellationToken cancellationToken)
    {
        var position = await state.GetKindAsync(Kind, cancellationToken).ConfigureAwait(false);
        position.AppliedVersion = 0;
        await state.SetKindAsync(position, cancellationToken).ConfigureAwait(false);
    }
}

namespace CORA.Data.Email;

/// <summary>
/// Persists all signed-in accounts so users don't re-enter settings.
/// </summary>
public interface ICredentialStore
{
    Task<List<MailAccount>> LoadAllAsync();
    Task SaveAccountAsync(MailAccount account);

    /// <summary>
    /// Adds a mail account (the user added it, not an update of one already shown): with goCORA
    /// accounts, always to the signed-in account's own copy, even when the same address is already
    /// on this device for someone else (claude/design-per-account-ownership.md, decision 4).
    /// </summary>
    Task AddAccountAsync(MailAccount account) => SaveAccountAsync(account);
    Task DeleteAccountAsync(string email);
    void Clear();

    /// <summary>The email address of the account that was active when the app was last used, or null.</summary>
    string? GetActiveAccountEmail();

    /// <summary>Persists which account is active so it can be restored on next launch.</summary>
    void SetActiveAccountEmail(string? email);
}

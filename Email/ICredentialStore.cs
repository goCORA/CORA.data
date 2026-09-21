namespace CORA.Data.Email;

/// <summary>
/// Persists all signed-in accounts so users don't re-enter settings.
/// </summary>
public interface ICredentialStore
{
    Task<List<MailAccount>> LoadAllAsync();
    Task SaveAccountAsync(MailAccount account);
    Task DeleteAccountAsync(string email);
    void Clear();

    /// <summary>The email address of the account that was active when the app was last used, or null.</summary>
    string? GetActiveAccountEmail();

    /// <summary>Persists which account is active so it can be restored on next launch.</summary>
    void SetActiveAccountEmail(string? email);
}

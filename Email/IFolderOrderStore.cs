namespace CORA.Data.Email;

/// <summary>
/// The order in which the user wants an account's folders listed in the app's folder menu, per
/// account. Display-only and purely local, stored encrypted at rest (see
/// <see cref="FolderOrderDatabase"/>) because folder names can say something about the user.
/// The pinned folders are not stored; see <see cref="FolderOrdering"/> for how the saved order
/// is applied.
/// <para>
/// Synchronous on purpose, like <see cref="IHiddenFolderStore"/>: it is read while the menu is
/// rebuilt on the UI thread and the data is a handful of small in-memory rows.
/// </para>
/// </summary>
public interface IFolderOrderStore
{
    /// <summary>Raised after <see cref="SetOrder"/>, <see cref="Rename"/> or <see cref="Clear"/> actually changed something.</summary>
    event EventHandler? Changed;

    /// <summary>The saved folder full names for the account, in order (empty when none is saved).</summary>
    IReadOnlyList<string> GetOrder(string accountAddress);

    /// <summary>Saves the order for the account, replacing any earlier one. An empty list clears it.</summary>
    void SetOrder(string accountAddress, IReadOnlyList<string> folderFullNames);

    /// <summary>Forgets the saved order, so the account's folders are listed in the server's order again.</summary>
    void Clear(string accountAddress);

    /// <summary>Keeps a renamed folder at its saved position. Does nothing if it has none.</summary>
    void Rename(string accountAddress, string oldFolderFullName, string newFolderFullName);
}

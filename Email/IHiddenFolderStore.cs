namespace CORA.Data.Email;

/// <summary>
/// Which mail folders the user has hidden from the app's folder menu, per account. Hiding is
/// display-only: the folder still exists on the server and is still synced. Purely local state,
/// stored encrypted at rest (see <see cref="HiddenFolderDatabase"/>) because folder names can
/// say something about the user.
/// <para>
/// Synchronous on purpose: it is read for every folder while the menu is rebuilt on the UI
/// thread, and the data is a handful of small in-memory rows.
/// </para>
/// </summary>
public interface IHiddenFolderStore
{
    /// <summary>Raised after <see cref="Hide"/>, <see cref="Unhide"/> or <see cref="UnhideAll"/> actually changed something.</summary>
    event EventHandler? Changed;

    /// <summary>Whether <paramref name="folderFullName"/> is hidden for the account with this address.</summary>
    bool IsHidden(string accountAddress, string folderFullName);

    /// <summary>The hidden folders' full names for one account (empty when none).</summary>
    IReadOnlyCollection<string> GetHidden(string accountAddress);

    /// <summary>Every hidden folder across all accounts, ordered by account then folder name.</summary>
    IReadOnlyList<(string Account, string Folder)> GetAll();

    void Hide(string accountAddress, string folderFullName);

    void Unhide(string accountAddress, string folderFullName);

    /// <summary>Makes every hidden folder of one account visible again.</summary>
    void UnhideAll(string accountAddress);
}

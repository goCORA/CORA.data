namespace CORA.Data.Contacts;

/// <summary>
/// Where contact photos live: one small JPEG per photo in <see cref="DirectoryName"/> under the
/// app data directory. <see cref="Contact.PhotoPath"/> holds only the file name (older contacts
/// hold a full path from the device that saved them; <see cref="FileNameOf"/> reads both), so a
/// photo still shows after a backup is restored on another device, or after iOS moves the app's
/// folder. Backups include this folder with the contacts.
/// </summary>
public static class ContactPhotoFiles
{
    public const string DirectoryName = "contact_photos";

    /// <summary>The photo's file name from a stored value: a file name, or an old full path written on any platform.</summary>
    public static string FileNameOf(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return string.Empty;
        var trimmed = stored.Trim();
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>The full path of the photo in <paramref name="appDataDirectory"/>, or "" when there is none.</summary>
    public static string FullPath(string appDataDirectory, string? stored)
    {
        var name = FileNameOf(stored);
        if (name.Length == 0)
            return string.Empty;
        var path = Path.Combine(appDataDirectory, DirectoryName, name);
        return File.Exists(path) ? path : string.Empty;
    }
}

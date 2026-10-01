using System.Globalization;
using System.Text;

namespace CORA.Data.Email;

/// <summary>A folder the server lists that might be its junk/spam folder.</summary>
/// <param name="HasJunkFlag">The server marked it with the \Junk special-use attribute.</param>
/// <param name="IsTopLevel">It sits directly under the personal namespace (not inside a container such as "[Gmail]").</param>
internal sealed record JunkCandidate(string FullName, string Name, bool HasJunkFlag, bool IsTopLevel);

/// <summary>
/// Picks an IMAP account's junk/spam folder from the folders its server lists. The server's own
/// \Junk special-use flag decides when there is one; the name list is only a fallback for servers
/// that don't send it. Pure and MailKit-free so the rules are unit-tested.
/// </summary>
internal static class JunkFolderDetection
{
    // As a user would see them, localized names included. Compared after Normalize, so case,
    // spaces, hyphens, underscores and accents don't matter ("Junk E-Mail" == "junk_email").
    private static readonly string[] KnownNames =
    [
        "Junk", "Junk Mail", "Junk Email", "Junk E-Mail", "Spam", "Bulk", "Bulk Mail",
        "Courrier indésirable", "Pourriel",
        "Correo no deseado", "Correo basura",
    ];

    private static readonly HashSet<string> NormalizedNames = KnownNames.Select(Normalize).ToHashSet();

    /// <summary>Lowercases and drops spaces, hyphens, underscores and accents.</summary>
    internal static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark
                || char.IsWhiteSpace(c) || c is '-' or '_')
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    internal static bool IsKnownName(string name) => NormalizedNames.Contains(Normalize(name));

    // The name exactly as in the list above, apart from case: stronger evidence than a
    // normalized match ("Junk Mail" beats "junk_mail" when both exist).
    private static bool IsExactName(string name) =>
        KnownNames.Any(k => k.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The best junk-folder candidate, or null when none is flagged or has a known name. Among
    /// several: the \Junk flag first, then an exact name match, then a top-level folder, then
    /// the first one listed.
    /// </summary>
    internal static JunkCandidate? Pick(IEnumerable<JunkCandidate> candidates) =>
        candidates
            .Where(c => c.HasJunkFlag || IsKnownName(c.Name))
            .OrderByDescending(c => c.HasJunkFlag)
            .ThenByDescending(c => IsExactName(c.Name))
            .ThenByDescending(c => c.IsTopLevel)
            .FirstOrDefault();
}

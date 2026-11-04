using System.Text.RegularExpressions;

namespace CORA.Data.Sync;

/// <summary>A contact photo in goCORA's cloud, and the key code generation it was sealed under.</summary>
public sealed record SyncCloudPhoto(string Id, long Generation);

/// <summary>
/// What one sync run does with contact photo files. The contacts snapshot says which photos should
/// exist; the files themselves are sealed one by one. A photo file never changes under its name, so
/// comparing names is enough. Each run does a limited amount, so a first sync spreads over several.
/// </summary>
public sealed record SyncPhotoPlan(IReadOnlyList<string> Upload, IReadOnlyList<string> Download, IReadOnlyList<string> Delete)
{
    public const int MaxPerRun = 40;

    // The contacts snapshot names the file, so the name decides a path on this device: only
    // goCORA's own "<guid>.<ext>" is accepted, never "..", separators or anything else.
    private static readonly Regex PhotoName = new("^([0-9a-f]{32})(\\.[A-Za-z0-9]{1,8})?\\z", RegexOptions.CultureInvariant);

    /// <summary>The photo's cloud id, or null when the name isn't one goCORA made.</summary>
    public static string? IdOf(string fileName)
    {
        var match = PhotoName.Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <param name="referenced">The photo file names the contacts use.</param>
    /// <param name="onDevice">The photo file names on this device.</param>
    /// <param name="cloud">The photos in the cloud.</param>
    /// <param name="generation">The key code generation open now: older photos are sealed again.</param>
    public static SyncPhotoPlan Build(
        IEnumerable<string> referenced, IEnumerable<string> onDevice, IEnumerable<SyncCloudPhoto> cloud, long generation)
    {
        var names = referenced.Where(n => IdOf(n) is not null).Distinct().ToList();
        var here = onDevice.ToHashSet();
        var inCloud = cloud.ToDictionary(p => p.Id);
        var wanted = names.Select(n => IdOf(n)!).ToHashSet();

        var upload = names
            .Where(n => here.Contains(n) && (!inCloud.TryGetValue(IdOf(n)!, out var p) || p.Generation != generation))
            .Take(MaxPerRun)
            .ToList();
        var download = names
            .Where(n => !here.Contains(n) && inCloud.ContainsKey(IdOf(n)!))
            .Take(MaxPerRun)
            .ToList();
        var delete = inCloud.Keys.Where(id => !wanted.Contains(id)).Take(100).ToList();
        return new SyncPhotoPlan(upload, download, delete);
    }
}

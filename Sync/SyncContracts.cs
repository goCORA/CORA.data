namespace CORA.Data.Sync;

/// <summary>A kind's current version in goCORA's cloud. <see cref="Parts"/> 0 = deleted from the cloud.</summary>
public sealed record SyncCloudVersion(long Version, long Generation, int Parts, string? UploadId);

/// <summary>Another device won this version: merge what it saved, and try again.</summary>
public sealed class SyncVersionOutdatedException : Exception;

/// <summary>A snapshot from before a Reset: no key this device can reach opens it.</summary>
public sealed class SyncSnapshotUnreadableException : Exception;

/// <summary>The cloud side of sync (goCORA.Keys). Implemented by the app.</summary>
public interface ISyncCloud
{
    Task<IReadOnlyDictionary<string, SyncCloudVersion>> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Fetches every part of a version and joins them.</summary>
    Task<byte[]> DownloadAsync(string kind, SyncCloudVersion version, CancellationToken cancellationToken);

    /// <summary>The daily copy of this version, for undo.</summary>
    Task<SyncCloudVersion> GetDailyCopyAsync(string kind, long version, CancellationToken cancellationToken);

    /// <summary>Stores a sealed blob as <paramref name="version"/>. Throws <see cref="SyncVersionOutdatedException"/>.</summary>
    Task UploadAsync(string kind, long version, long generation, byte[] blob, CancellationToken cancellationToken);
}

/// <summary>Sealing under the CORA key code (Rust sync.rs). Implemented by the app.</summary>
public interface ISyncSealer
{
    /// <summary>The goCORA account the open key code belongs to.</summary>
    string Group { get; }

    /// <summary>The key code generation open now.</summary>
    long Generation { get; }

    byte[] Seal(byte[] snapshot, string kind, long version);

    /// <summary>
    /// Opens a blob, following the key chain when it is from an older generation. Throws
    /// <see cref="SyncSnapshotUnreadableException"/> when it is from before a Reset, and anything
    /// else (a changed byte, another version) for a snapshot that must not be trusted.
    /// </summary>
    Task<byte[]> OpenAsync(byte[] blob, string kind, long version, CancellationToken cancellationToken);
}

/// <summary>
/// One kind of synced data on this device. Items are an id and a text value: the same item must
/// always give the same text, because the engine detects changes by hashing it.
/// </summary>
public interface ISyncAdapter
{
    string Kind { get; }

    /// <summary>Every item on this device.</summary>
    Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// First sync on this device: gives matching local items their cloud identity, so a contact or
    /// tag that is already on two devices becomes one item instead of two.
    /// </summary>
    Task AlignAsync(IReadOnlyDictionary<string, string> cloudItems, CancellationToken cancellationToken);

    Task ApplyAsync(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken);
}

public enum SyncKindOutcome
{
    /// <summary>Nothing to do, or everything merged and saved.</summary>
    Done,

    /// <summary>Another device saved first: merged locally, saves at the next run.</summary>
    Retry,

    /// <summary>The cloud copy would delete many items here: waiting for the user.</summary>
    NeedsConfirmation,

    /// <summary>The cloud offers an older version than this device has seen. Nothing was changed.</summary>
    Rollback,
}

/// <summary>What a kind's sync would delete here, for the "are you sure?" question.</summary>
public sealed record SyncKindResult(string Kind, SyncKindOutcome Outcome, int Deletions = 0);

/// <summary>The user deleted the cloud copy on another device: sync stops here too.</summary>
public sealed class SyncCloudCopyDeletedException : Exception;

/// <summary>A daily copy kept for undo. <see cref="Until"/> is when that version was replaced.</summary>
public sealed record SyncDailyCopy(string Kind, long Version, long Generation, int Parts, string? UploadId, DateTimeOffset Until);

/// <summary>This device holds another goCORA account's sync state: the user must choose.</summary>
public sealed class SyncOwnerMismatchException : Exception;

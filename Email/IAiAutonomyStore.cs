namespace CORA.Data.Email;

/// <summary>
/// Local persistence for each account's <see cref="AutonomyTier"/> — the per-account dial the
/// user controls, consumed by the central action-permission gateway (CORA.App's
/// <c>IEmailActionGateway</c>). One tier per account; the per-action-type granularity comes
/// from <see cref="EmailActionRiskClassifier"/> plus the tier's fixed semantics, not a
/// separate configurable matrix.
/// </summary>
public interface IAiAutonomyStore
{
    /// <summary>
    /// Returns the tier for the given account, defaulting to <see cref="AutonomyTier.ScopedAutonomy"/>
    /// if none has been set yet. This is safe as a default specifically because high-risk
    /// actions (send/delete/forward/move/junk/blacklist) always confirm regardless of tier —
    /// the only thing this default relaxes is auto-running the two low-risk actions
    /// (mark read, flag important), which preserves today's existing no-prompt behavior for
    /// those two rather than introducing a confirm dialog on every read/flag toggle.
    /// </summary>
    Task<AutonomyTier> GetTierAsync(string accountKey, CancellationToken cancellationToken = default);

    /// <summary>Sets the tier for the given account.</summary>
    Task SetTierAsync(string accountKey, AutonomyTier tier, CancellationToken cancellationToken = default);
}

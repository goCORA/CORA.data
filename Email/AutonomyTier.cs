namespace CORA.Data.Email;

/// <summary>
/// How much an AI assistant is allowed to do on a given account without asking first. A dial
/// the user controls per account, not a fixed global mode. Note that the central action
/// gateway also runs the user's own manual actions through its high-risk confirmation rule -
/// but the tier itself constrains the assistant, never the user.
/// </summary>
public enum AutonomyTier
{
    /// <summary>
    /// Summarize/search/suggest only. No AI-initiated mutating action may run, ever - not even
    /// with confirmation. The user's own actions are unaffected and behave as they would with
    /// AI switched off.
    /// </summary>
    ReadOnly,

    /// <summary>Every mutating action, low- or high-risk, requires explicit confirmation.</summary>
    DraftAndApprove,

    /// <summary>
    /// Low-risk actions (see <see cref="EmailActionRisk.LowRisk"/>) auto-run without a
    /// prompt. High-risk actions (send/delete/forward/etc.) still always confirm — the
    /// gateway's high-risk rule is not affected by this tier.
    /// </summary>
    ScopedAutonomy,
}

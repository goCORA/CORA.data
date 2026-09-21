namespace CORA.Data.Email;

/// <summary>
/// Every state-changing action that can be taken against a mailbox. Used by
/// <see cref="EmailActionRisk"/> to classify actions for the central permission gateway
/// (see CORA.App's <c>IEmailActionGateway</c>) — every mutating call site, whether triggered
/// by a human tap or an AI suggestion, goes through that gateway keyed on one of these values.
/// </summary>
public enum EmailActionType
{
    MarkRead,
    MarkImportant,
    MoveFolder,
    MoveToJunk,
    MoveToInbox,
    Blacklist,
    Delete,
    Send,
}

/// <summary>How risky an <see cref="EmailActionType"/> is, for permission-gating purposes.</summary>
public enum EmailActionRisk
{
    /// <summary>
    /// Reversible, non-destructive actions that may be allowed to auto-run under the
    /// <c>ScopedAutonomy</c> tier.
    /// </summary>
    LowRisk,

    /// <summary>
    /// Destructive, hard-to-reverse, or externally-visible actions. Always requires explicit
    /// confirmation regardless of autonomy tier — see the gateway's hard rule. Permanent: no
    /// setting can move an action out of this bucket.
    /// </summary>
    HighRisk,

    /// <summary>
    /// Reversible actions (moving a message, including to/from Junk) whose effective risk
    /// depends on whether an AI provider is actually configured — there is no prompt-injection
    /// attack surface at all until an AI is reading mail content, so this stays low-risk
    /// unconditionally with no AI configured. Once an AI key exists, CORA.App's
    /// EmailActionGateway resolves this to High by default (a per-action-type user setting,
    /// defaulting to "Prompt", can opt it back down to Low). See the gateway for the actual
    /// resolution logic — this floor only says "this action is eligible to be reclassified",
    /// never "this action is currently low- or high-risk".
    /// </summary>
    Reclassifiable,
}

/// <summary>Classifies each <see cref="EmailActionType"/> as <see cref="EmailActionRisk"/>.</summary>
public static class EmailActionRiskClassifier
{
    /// <summary>
    /// Low-risk actions are reversible and non-destructive: marking read state or the
    /// important flag - these stay low-risk unconditionally, AI configured or not. Moving a
    /// message (including to/from Junk) is <see cref="EmailActionRisk.Reclassifiable"/> - see
    /// that value's docs. Delete, Send, and Blacklist stay permanently high-risk: delete/send
    /// are the two actions decision #4 explicitly calls out as always requiring confirmation,
    /// and blacklist has an effect beyond the current message (it changes how future mail from
    /// that sender/domain is routed). Everything else defaults to high-risk, per the product
    /// decision to default toward requiring confirmation for anything not explicitly known to
    /// be safe.
    /// </summary>
    public static EmailActionRisk Of(EmailActionType actionType) => actionType switch
    {
        EmailActionType.MarkRead => EmailActionRisk.LowRisk,
        EmailActionType.MarkImportant => EmailActionRisk.LowRisk,
        EmailActionType.MoveFolder => EmailActionRisk.Reclassifiable,
        EmailActionType.MoveToJunk => EmailActionRisk.Reclassifiable,
        EmailActionType.MoveToInbox => EmailActionRisk.Reclassifiable,
        _ => EmailActionRisk.HighRisk,
    };
}

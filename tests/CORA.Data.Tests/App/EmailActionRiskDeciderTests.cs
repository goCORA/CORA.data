using CORA.App.Services.AI;
using CORA.Data.Email;

namespace CORA.Data.Tests.App;

/// <summary>
/// Covers <see cref="EmailActionRiskDecider"/>, the MAUI-free extraction of the risk decision
/// used by <c>CORA.App</c>'s <c>EmailActionGateway</c> - see Stage 1 task 5 in
/// docs/AI-IMPROVEMENT-PLAN.md. Scenarios named after that task's checklist.
/// </summary>
public class EmailActionRiskDeciderTests
{
    [Fact]
    public void User_move_with_ai_on_is_low_risk_and_does_not_confirm()
    {
        // A human's own move is always low-risk, whatever the AI/auto-run state. Confirmation
        // additionally requires ScopedAutonomy (or ReadOnly) for a LowRisk action to skip the
        // prompt - see LowRisk_actions_under_DraftAndApprove_still_confirm for the other tiers.
        var risk = EmailActionRiskDecider.ResolveEffectiveRisk(
            EmailActionType.MoveFolder,
            tier: AutonomyTier.ScopedAutonomy,
            initiatedByAi: false,
            isAgentActiveWithConsent: true,
            moveToFolderAutoRun: false,
            moveToJunkAutoRun: false);

        Assert.Equal(EmailActionRisk.LowRisk, risk);

        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            risk, AutonomyTier.ScopedAutonomy, EmailActionType.MoveFolder,
            initiatedByAi: false, confirmMessageDelete: true);

        Assert.False(needsConfirmation);
    }

    [Fact]
    public void Ai_move_with_autorun_switch_off_is_high_risk_and_confirms()
    {
        var risk = EmailActionRiskDecider.ResolveEffectiveRisk(
            EmailActionType.MoveFolder,
            tier: AutonomyTier.DraftAndApprove,
            initiatedByAi: true,
            isAgentActiveWithConsent: true,
            moveToFolderAutoRun: false,
            moveToJunkAutoRun: false);

        Assert.Equal(EmailActionRisk.HighRisk, risk);

        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            risk, AutonomyTier.DraftAndApprove, EmailActionType.MoveFolder,
            initiatedByAi: true, confirmMessageDelete: true);

        Assert.True(needsConfirmation);
    }

    [Fact]
    public void Ai_move_with_autorun_switch_on_is_low_risk_under_scoped_autonomy_and_does_not_confirm()
    {
        var risk = EmailActionRiskDecider.ResolveEffectiveRisk(
            EmailActionType.MoveFolder,
            tier: AutonomyTier.ScopedAutonomy,
            initiatedByAi: true,
            isAgentActiveWithConsent: true,
            moveToFolderAutoRun: true,
            moveToJunkAutoRun: false);

        Assert.Equal(EmailActionRisk.LowRisk, risk);

        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            risk, AutonomyTier.ScopedAutonomy, EmailActionType.MoveFolder,
            initiatedByAi: true, confirmMessageDelete: true);

        Assert.False(needsConfirmation);
    }

    [Theory]
    [InlineData(EmailActionType.MoveFolder)]
    [InlineData(EmailActionType.MoveToJunk)]
    [InlineData(EmailActionType.MoveToInbox)]
    public void ReadOnly_denies_reclassifiable_risk_for_ai_moves_regardless_of_autorun(EmailActionType actionType)
    {
        // ResolveReclassifiableRisk itself always folds ReadOnly down to LowRisk (the actual
        // denial for AI-initiated actions under ReadOnly happens earlier in the gateway, before
        // risk is even resolved - this only proves the tier is never allowed to reach the
        // "auto-run gates it" branch).
        var risk = EmailActionRiskDecider.ResolveEffectiveRisk(
            actionType,
            tier: AutonomyTier.ReadOnly,
            initiatedByAi: true,
            isAgentActiveWithConsent: true,
            moveToFolderAutoRun: true,
            moveToJunkAutoRun: true);

        Assert.Equal(EmailActionRisk.LowRisk, risk);
    }

    [Fact]
    public void Ai_move_with_agent_not_active_is_low_risk()
    {
        // No consent or AI switched off is treated the same as "AI cannot act at all".
        var risk = EmailActionRiskDecider.ResolveReclassifiableRisk(
            EmailActionType.MoveToJunk,
            tier: AutonomyTier.DraftAndApprove,
            initiatedByAi: true,
            isAgentActiveWithConsent: false,
            moveToFolderAutoRun: false,
            moveToJunkAutoRun: false);

        Assert.Equal(EmailActionRisk.LowRisk, risk);
    }

    [Theory]
    [InlineData(true, false, false)] // confirm-message-delete on -> always confirm
    [InlineData(false, false, true)] // manual delete, opt-out on -> skip confirm
    [InlineData(false, true, false)] // AI-initiated delete, opt-out never applies -> confirm
    public void CanSkipDeleteConfirmation_only_applies_to_manual_deletes_with_opt_out(
        bool confirmMessageDelete, bool initiatedByAi, bool expectedCanSkip)
    {
        var canSkip = EmailActionRiskDecider.CanSkipDeleteConfirmation(
            EmailActionType.Delete, initiatedByAi, confirmMessageDelete);

        Assert.Equal(expectedCanSkip, canSkip);
    }

    [Fact]
    public void CanSkipDeleteConfirmation_never_applies_to_non_delete_actions()
    {
        var canSkip = EmailActionRiskDecider.CanSkipDeleteConfirmation(
            EmailActionType.Blacklist, initiatedByAi: false, confirmMessageDelete: false);

        Assert.False(canSkip);
    }

    [Fact]
    public void HighRisk_actions_always_confirm_except_the_delete_opt_out()
    {
        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            EmailActionRisk.HighRisk, AutonomyTier.ScopedAutonomy, EmailActionType.Send,
            initiatedByAi: false, confirmMessageDelete: false);

        Assert.True(needsConfirmation);
    }

    [Fact]
    public void LowRisk_manual_actions_do_not_confirm_under_ReadOnly()
    {
        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            EmailActionRisk.LowRisk, AutonomyTier.ReadOnly, EmailActionType.MarkRead,
            initiatedByAi: false, confirmMessageDelete: true);

        Assert.False(needsConfirmation);
    }

    [Fact]
    public void LowRisk_actions_under_DraftAndApprove_still_confirm()
    {
        var needsConfirmation = EmailActionRiskDecider.NeedsConfirmation(
            EmailActionRisk.LowRisk, AutonomyTier.DraftAndApprove, EmailActionType.MoveFolder,
            initiatedByAi: false, confirmMessageDelete: true);

        Assert.True(needsConfirmation);
    }
}

using CORA.App.Services.AI;

namespace CORA.Data.Tests.App;

public class AiConsentTests
{
    // The stored keys are part of the persisted format (existing installs have them), so the
    // malformed-value tests write to them directly.
    private const string ClaudeKey = "cora.ai.consent.claude";
    private const string OpenAiKey = "cora.ai.consent.openai";

    private static (AiConsent Consent, InMemoryPreferenceStore Prefs) Create(int currentVersion = 1)
    {
        var prefs = new InMemoryPreferenceStore();
        return (new AiConsent(prefs, currentVersion), prefs);
    }

    [Theory]
    [InlineData(AiProviderType.Claude)]
    [InlineData(AiProviderType.OpenAI)]
    public void Nothing_is_allowed_by_default(AiProviderType provider)
    {
        var (consent, _) = Create();

        Assert.False(consent.HasConsent(provider));
        Assert.Null(consent.GetConsent(provider));
    }

    [Theory]
    [InlineData(AiProviderType.Claude)]
    [InlineData(AiProviderType.OpenAI)]
    public void Grant_records_the_current_version_and_time(AiProviderType provider)
    {
        var (consent, _) = Create(currentVersion: 3);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        consent.Grant(provider);

        Assert.True(consent.HasConsent(provider));
        var record = consent.GetConsent(provider);
        Assert.NotNull(record);
        Assert.Equal(3, record.Value.Version);
        Assert.InRange(record.Value.GrantedAtUtc, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Consent_is_per_provider()
    {
        var (consent, _) = Create();

        consent.Grant(AiProviderType.Claude);

        Assert.True(consent.HasConsent(AiProviderType.Claude));
        Assert.False(consent.HasConsent(AiProviderType.OpenAI));
    }

    [Fact]
    public void Withdraw_removes_only_that_provider()
    {
        var (consent, _) = Create();
        consent.Grant(AiProviderType.Claude);
        consent.Grant(AiProviderType.OpenAI);

        consent.Withdraw(AiProviderType.Claude);

        Assert.False(consent.HasConsent(AiProviderType.Claude));
        Assert.Null(consent.GetConsent(AiProviderType.Claude));
        Assert.True(consent.HasConsent(AiProviderType.OpenAI));
    }

    [Fact]
    public void Withdraw_without_consent_is_harmless()
    {
        var (consent, _) = Create();

        consent.Withdraw(AiProviderType.Claude);

        Assert.False(consent.HasConsent(AiProviderType.Claude));
    }

    [Fact]
    public void WithdrawAll_clears_every_provider()
    {
        var (consent, prefs) = Create();
        consent.Grant(AiProviderType.Claude);
        consent.Grant(AiProviderType.OpenAI);

        consent.WithdrawAll();

        foreach (var provider in Enum.GetValues<AiProviderType>())
            Assert.False(consent.HasConsent(provider));
        Assert.Empty(prefs.Values);
    }

    [Fact]
    public void Consent_for_an_older_version_no_longer_counts()
    {
        var (v1, prefs) = Create(currentVersion: 1);
        v1.Grant(AiProviderType.Claude);

        var v2 = new AiConsent(prefs, currentVersion: 2);

        Assert.False(v2.HasConsent(AiProviderType.Claude));
        // The old record is still readable (Settings can tell "never allowed" from "needs renewing")...
        Assert.Equal(1, v2.GetConsent(AiProviderType.Claude)!.Value.Version);
        // ...and granting again for the new version counts.
        v2.Grant(AiProviderType.Claude);
        Assert.True(v2.HasConsent(AiProviderType.Claude));
    }

    [Fact]
    public void Consent_from_a_newer_version_does_not_count_either()
    {
        // E.g. after a downgrade: strict equality means "ask again" rather than trusting a
        // disclosure this build has never seen.
        var (v3, prefs) = Create(currentVersion: 3);
        v3.Grant(AiProviderType.OpenAI);

        var v2 = new AiConsent(prefs, currentVersion: 2);

        Assert.False(v2.HasConsent(AiProviderType.OpenAI));
    }

    [Fact]
    public void A_stored_value_is_parsed_back()
    {
        var (consent, prefs) = Create(currentVersion: 1);
        prefs.Set(ClaudeKey, "1|2026-01-02T03:04:05.0000000+00:00");

        var record = consent.GetConsent(AiProviderType.Claude);

        Assert.NotNull(record);
        Assert.Equal(1, record.Value.Version);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), record.Value.GrantedAtUtc);
        Assert.True(consent.HasConsent(AiProviderType.Claude));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1")]
    [InlineData("1|")]
    [InlineData("|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData("1|not a date")]
    [InlineData("x|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData("-1|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData("+1|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData(" 1|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData("1.0|2026-01-02T03:04:05.0000000+00:00")]
    [InlineData("99999999999|2026-01-02T03:04:05.0000000+00:00")]
    public void Malformed_values_count_as_no_consent(string raw)
    {
        var (consent, prefs) = Create(currentVersion: 1);
        prefs.Set(ClaudeKey, raw);

        Assert.Null(consent.GetConsent(AiProviderType.Claude));
        Assert.False(consent.HasConsent(AiProviderType.Claude));
        Assert.Throws<AiConsentRequiredException>(() => consent.EnsureConsent(AiProviderType.Claude));
    }

    [Fact]
    public void A_malformed_value_for_one_provider_does_not_affect_the_other()
    {
        var (consent, prefs) = Create();
        prefs.Set(ClaudeKey, "garbage");
        consent.Grant(AiProviderType.OpenAI);

        Assert.False(consent.HasConsent(AiProviderType.Claude));
        Assert.True(consent.HasConsent(AiProviderType.OpenAI));
        Assert.Contains(OpenAiKey, prefs.Values.Keys);
    }

    [Fact]
    public void EnsureConsent_throws_without_consent_and_names_the_provider()
    {
        var (consent, _) = Create();

        var ex = Assert.Throws<AiConsentRequiredException>(() => consent.EnsureConsent(AiProviderType.OpenAI));

        Assert.Equal(AiProviderType.OpenAI, ex.Provider);
    }

    [Fact]
    public void EnsureConsent_passes_once_granted()
    {
        var (consent, _) = Create();
        consent.Grant(AiProviderType.Claude);

        consent.EnsureConsent(AiProviderType.Claude); // must not throw
    }

    [Fact]
    public void EnsureConsent_only_passes_for_the_provider_that_was_allowed()
    {
        var (consent, _) = Create();
        consent.Grant(AiProviderType.Claude);

        Assert.Throws<AiConsentRequiredException>(() => consent.EnsureConsent(AiProviderType.OpenAI));
    }

    [Fact]
    public void EnsureConsent_throws_again_after_withdrawal()
    {
        var (consent, _) = Create();
        consent.Grant(AiProviderType.Claude);
        consent.EnsureConsent(AiProviderType.Claude);

        consent.Withdraw(AiProviderType.Claude);

        Assert.Throws<AiConsentRequiredException>(() => consent.EnsureConsent(AiProviderType.Claude));
    }

    [Fact]
    public void EnsureConsent_throws_when_the_disclosure_version_changed()
    {
        var (v1, prefs) = Create(currentVersion: 1);
        v1.Grant(AiProviderType.Claude);

        var v2 = new AiConsent(prefs, currentVersion: 2);

        Assert.Throws<AiConsentRequiredException>(() => v2.EnsureConsent(AiProviderType.Claude));
    }

    [Fact]
    public void The_exception_is_an_AiProviderException_so_unaware_callers_still_fail_closed()
    {
        var (consent, _) = Create();

        var ex = Record.Exception(() => consent.EnsureConsent(AiProviderType.Claude));

        Assert.IsAssignableFrom<AiProviderException>(ex);
    }

    [Fact]
    public void The_shipped_default_version_is_used_when_none_is_given()
    {
        var prefs = new InMemoryPreferenceStore();
        var consent = new AiConsent(prefs);

        consent.Grant(AiProviderType.Claude);

        Assert.Equal(AiConsent.CurrentVersion, consent.GetConsent(AiProviderType.Claude)!.Value.Version);
        Assert.True(consent.HasConsent(AiProviderType.Claude));
    }
}

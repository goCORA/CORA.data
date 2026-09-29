using CORA.App.Services.AI;

namespace CORA.Data.Tests.App;

public class StructuredAiResultTests
{
    private const string ValidSummaryJson = """
        {
          "topic": "Renewal of ABC Company's software agreement.",
          "keyPoints": ["Renewal price: $14,500", "Contract expires October 31"],
          "actionRequired": ["Reply to Michael", "Confirm the two-year term"],
          "deadline": "October 4",
          "openQuestions": ["Whether support is included in the quoted price"],
          "participants": [{"name": "Michael", "position": "Requesting confirmation"}]
        }
        """;

    [Fact]
    public void TryParseSummary_ValidJson_PopulatesEveryField()
    {
        var summary = StructuredAiResult.TryParseSummary(ValidSummaryJson);

        Assert.NotNull(summary);
        Assert.Equal("Renewal of ABC Company's software agreement.", summary!.Topic);
        Assert.Equal(2, summary.KeyPoints.Count);
        Assert.Equal(2, summary.ActionRequired.Count);
        Assert.Equal("October 4", summary.Deadline);
        Assert.Single(summary.OpenQuestions);
        Assert.Single(summary.Participants);
        Assert.Equal("Michael", summary.Participants[0].Name);
    }

    [Fact]
    public void TryParseSummary_WrappedInMarkdownCodeFence_StillParses()
    {
        var fenced = $"```json\n{ValidSummaryJson}\n```";

        var summary = StructuredAiResult.TryParseSummary(fenced);

        Assert.NotNull(summary);
        Assert.Equal("October 4", summary!.Deadline);
    }

    [Fact]
    public void TryParseSummary_WrappedInPlainCodeFence_StillParses()
    {
        var fenced = $"```\n{ValidSummaryJson}\n```";

        var summary = StructuredAiResult.TryParseSummary(fenced);

        Assert.NotNull(summary);
    }

    [Fact]
    public void TryParseSummary_Prose_ReturnsNull()
    {
        var summary = StructuredAiResult.TryParseSummary(
            "This email is about renewing ABC Company's software agreement for $14,500.");

        Assert.Null(summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseSummary_NullOrBlank_ReturnsNull(string? raw)
    {
        Assert.Null(StructuredAiResult.TryParseSummary(raw));
    }

    [Fact]
    public void TryParseSummary_EmptyJsonObject_ReturnsNull()
    {
        // Valid JSON, but nothing worth showing - must not surface a card with every section blank.
        Assert.Null(StructuredAiResult.TryParseSummary("{}"));
    }

    [Fact]
    public void TryParseSummary_MalformedJson_ReturnsNull()
    {
        Assert.Null(StructuredAiResult.TryParseSummary("{\"topic\": \"unterminated"));
    }

    [Fact]
    public void TryParseSummary_BlankEntriesInArrays_AreFilteredOut()
    {
        const string json = """
            {"topic": "Something", "keyPoints": ["Real point", "", "   ", null], "actionRequired": [], "openQuestions": [], "participants": []}
            """;

        var summary = StructuredAiResult.TryParseSummary(json);

        Assert.NotNull(summary);
        Assert.Single(summary!.KeyPoints);
        Assert.Equal("Real point", summary.KeyPoints[0]);
    }

    [Fact]
    public void TryParseSummary_NullDeadline_LeavesDeadlineNull()
    {
        const string json = """
            {"topic": "Something", "keyPoints": [], "actionRequired": [], "deadline": null, "openQuestions": [], "participants": []}
            """;

        var summary = StructuredAiResult.TryParseSummary(json);

        Assert.NotNull(summary);
        Assert.Null(summary!.Deadline);
    }

    [Fact]
    public void TryParseSummary_CaseInsensitivePropertyNames_StillParse()
    {
        const string json = """
            {"Topic": "Something", "KeyPoints": ["A point"], "ActionRequired": [], "OpenQuestions": [], "Participants": []}
            """;

        var summary = StructuredAiResult.TryParseSummary(json);

        Assert.NotNull(summary);
        Assert.Equal("Something", summary!.Topic);
        Assert.Single(summary.KeyPoints);
    }

    [Fact]
    public void TryParseExtractedFields_ValidArray_ReturnsAllFields()
    {
        const string json = """
            [{"label": "Invoice number", "value": "INV-1001"}, {"label": "Amount", "value": "$14,500"}]
            """;

        var fields = StructuredAiResult.TryParseExtractedFields(json);

        Assert.NotNull(fields);
        Assert.Equal(2, fields!.Count);
        Assert.Equal("Invoice number", fields[0].Label);
        Assert.Equal("INV-1001", fields[0].Value);
    }

    [Fact]
    public void TryParseExtractedFields_EntriesMissingLabelOrValue_AreDropped()
    {
        const string json = """
            [{"label": "Real", "value": "Kept"}, {"label": "", "value": "Dropped - no label"}, {"label": "Dropped - no value", "value": ""}]
            """;

        var fields = StructuredAiResult.TryParseExtractedFields(json);

        Assert.NotNull(fields);
        Assert.Single(fields!);
        Assert.Equal("Real", fields[0].Label);
    }

    [Fact]
    public void TryParseExtractedFields_AllEntriesInvalid_ReturnsNull()
    {
        const string json = """[{"label": "", "value": ""}]""";

        Assert.Null(StructuredAiResult.TryParseExtractedFields(json));
    }

    [Fact]
    public void TryParseExtractedFields_NotAnArray_ReturnsNull()
    {
        // A JSON object where an array was asked for - the old "Label: value" text format
        // would also fail here, which is the point: never crash, just fall back.
        Assert.Null(StructuredAiResult.TryParseExtractedFields("{\"label\": \"x\", \"value\": \"y\"}"));
    }

    [Fact]
    public void TryParseExtractedFields_EmptyArray_ReturnsNull()
    {
        Assert.Null(StructuredAiResult.TryParseExtractedFields("[]"));
    }
}

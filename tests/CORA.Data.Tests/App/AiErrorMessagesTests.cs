using System.Net;
using CORA.App.Services.AI;

namespace CORA.Data.Tests.App;

public class AiErrorMessagesTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void ForHttpStatus_AuthFailure_MentionsTheApiKey(HttpStatusCode status)
    {
        var message = AiErrorMessages.ForHttpStatus(status, "Claude", "invalid x-api-key");

        Assert.Contains("API key", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Claude", message);
    }

    [Fact]
    public void ForHttpStatus_TooManyRequests_MentionsRateLimit()
    {
        var message = AiErrorMessages.ForHttpStatus((HttpStatusCode)429, "OpenAI", "rate limit exceeded");

        Assert.Contains("rate-limiting", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenAI", message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ForHttpStatus_ServerError_MentionsTryingAgain(HttpStatusCode status)
    {
        var message = AiErrorMessages.ForHttpStatus(status, "Claude", "internal error");

        Assert.Contains("try again", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForHttpStatus_OtherStatus_FallsBackToGenericMessage()
    {
        var message = AiErrorMessages.ForHttpStatus(HttpStatusCode.BadRequest, "Claude", "malformed request");

        Assert.Contains("400", message);
        Assert.Contains("Claude", message);
    }

    [Fact]
    public void ForHttpStatus_AlwaysIncludesTheRawProviderDetail()
    {
        var message = AiErrorMessages.ForHttpStatus(HttpStatusCode.Unauthorized, "Claude", "invalid x-api-key");

        // The plain-language reason never discards the provider's own detail - useful for
        // reporting an issue, or for a failure that doesn't match any of the common cases.
        Assert.Contains("invalid x-api-key", message);
    }

    [Fact]
    public void ForNetworkFailure_MentionsConnection()
    {
        var message = AiErrorMessages.ForNetworkFailure("OpenAI", "No such host is known.");

        Assert.Contains("connection", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenAI", message);
        Assert.Contains("No such host is known.", message);
    }
}

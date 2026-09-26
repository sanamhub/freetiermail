using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Smtp2Go.Tests;

/// <summary>Shapes from SMTP2GO's API reference for POST /v3/email/send, read 2026-09-26.</summary>
public sealed class Smtp2GoProviderTests : ProviderContractTests<Smtp2GoProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("200 with a failure", HttpStatusCode.OK, """{"request_id":"r1","data":{"succeeded":0,"failed":1,"failures":["x"]}}""", ProviderOutcome.RecipientRejected),
        Error(400, "E_ApiResponseCodes.ENDPOINT_PERMISSION_DENIED", ProviderOutcome.ProviderFault),
        Error(400, "E_ApiResponseCodes.NON_VALIDATING_IN_PAYLOAD", ProviderOutcome.RecipientRejected),
        Error(401, "E_ApiResponseCodes.API_KEY_INVALID", ProviderOutcome.ProviderFault),
        Error(403, "E_ApiResponseCodes.FORBIDDEN", ProviderOutcome.ProviderFault),
        Error(429, "E_ApiResponseCodes.RATE_LIMITED", ProviderOutcome.Throttled),
        Error(500, "E_ApiResponseCodes.INTERNAL", ProviderOutcome.Unavailable),
    ];

    protected override Smtp2GoProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new Smtp2GoOptions { ApiKey = apiKey, Monthly = 1000 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"request_id":"r1","data":{"email_id":"1abc-XYZ","succeeded":1,"failed":0,"failures":[]}}""", Encoding.UTF8, "application/json") };

    private static DocumentedResponse Error(int status, string code, ProviderOutcome expected) =>
        new($"{status} {code}", (HttpStatusCode)status, $$$"""{"request_id":"r1","data":{"error_code":"{{{code}}}","error":"documented"}}""", expected);

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Hi")
        {
            TextBody = "Body",
            ReplyTo = new EmailAddress("help@example.org"),
        };

        var result = await Create(handler, "api-test").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.smtp2go.com/v3/email/send", request.Request.RequestUri!.ToString());
        Assert.Equal("api-test", Assert.Single(request.Request.Headers.GetValues("X-Smtp2go-Api-Key")));
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("\"Links\" <links@example.org>", root.GetProperty("sender").GetString());
        Assert.Equal("rider@example.com", root.GetProperty("to")[0].GetString());
        Assert.Equal("Body", root.GetProperty("text_body").GetString());
        Assert.Equal("Reply-To", root.GetProperty("custom_headers")[0].GetProperty("header").GetString());
        Assert.Equal("1abc-XYZ", result.ProviderMessageId);
    }
}

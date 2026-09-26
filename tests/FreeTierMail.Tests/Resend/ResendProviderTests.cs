using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Resend.Tests;

/// <summary>Error names and statuses from Resend's error reference, read 2026-09-26.</summary>
public sealed class ResendProviderTests : ProviderContractTests<ResendProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        Error(429, "daily_quota_exceeded", ProviderOutcome.QuotaExhausted) with { ExpectedPeriod = QuotaPeriod.Daily },
        Error(429, "monthly_quota_exceeded", ProviderOutcome.QuotaExhausted) with { ExpectedPeriod = QuotaPeriod.Monthly },
        Error(429, "rate_limit_exceeded", ProviderOutcome.Throttled) with { Headers = new Dictionary<string, string> { ["retry-after"] = "1" } },
        Error(401, "missing_api_key", ProviderOutcome.ProviderFault),
        Error(401, "restricted_api_key", ProviderOutcome.ProviderFault),
        Error(403, "suspended_api_key", ProviderOutcome.ProviderFault),
        Error(403, "invalid_permission", ProviderOutcome.ProviderFault),
        Error(403, "validation_error", ProviderOutcome.ProviderFault),
        Error(403, "email_above_quota", ProviderOutcome.QuotaExhausted),
        Error(400, "validation_error", ProviderOutcome.RecipientRejected),
        Error(400, "invalid_idempotency_key", ProviderOutcome.RecipientRejected),
        Error(409, "concurrent_idempotent_requests", ProviderOutcome.Unknown),
        Error(409, "invalid_idempotent_request", ProviderOutcome.RecipientRejected),
        Error(422, "missing_required_field", ProviderOutcome.RecipientRejected),
        Error(500, "application_error", ProviderOutcome.Unavailable),
        Error(503, "service_unavailable", ProviderOutcome.Unavailable),
    ];

    protected override ResendProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new ResendOptions { ApiKey = apiKey, Daily = 100, Monthly = 3000 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""", Encoding.UTF8, "application/json") };

    // Resend's documented error body: statusCode, name, message.
    private static DocumentedResponse Error(int status, string name, ProviderOutcome expected) =>
        new($"{status} {name}", (HttpStatusCode)status, $$"""{"statusCode":{{status}},"name":"{{name}}","message":"documented message"}""", expected);

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Batosathi, links"), [new EmailAddress("rider@example.com")], "Hi")
        {
            HtmlBody = "<p>Hi</p>",
            ReplyTo = new EmailAddress("help@example.org"),
            Tags = ["sign in"],
            IdempotencyKey = "sign-in:42",
        };

        var result = await Create(handler, "re_test").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.resend.com/emails", request.Request.RequestUri!.ToString());
        Assert.Equal("Bearer re_test", request.Request.Headers.Authorization!.ToString());
        Assert.Equal("sign-in:42", request.Request.Headers.GetValues("Idempotency-Key").Single());
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("\"Batosathi, links\" <links@example.org>", root.GetProperty("from").GetString());
        Assert.Equal("rider@example.com", root.GetProperty("to")[0].GetString());
        Assert.Equal("help@example.org", root.GetProperty("reply_to")[0].GetString());
        Assert.False(root.TryGetProperty("text", out _));
        Assert.Equal("sign_in", root.GetProperty("tags")[0].GetProperty("name").GetString());
        Assert.Equal("49a3999c-0ce1-4ea6-ab68-afcd6dc2e794", result.ProviderMessageId);
    }

    [Fact]
    public async Task No_idempotency_key_sends_no_header()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());

        await Create(handler, "re_test").SendAsync(CanaryMessage(), TestContext.Current.CancellationToken);

        Assert.False(handler.Requests[0].Request.Headers.Contains("Idempotency-Key"));
    }
}

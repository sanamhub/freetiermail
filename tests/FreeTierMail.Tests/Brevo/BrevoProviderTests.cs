using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Brevo.Tests;

/// <summary>Response shapes from Brevo's API reference for sendTransacEmail, read 2026-09-26.</summary>
public sealed class BrevoProviderTests : ProviderContractTests<BrevoProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("400 not_enough_credits", HttpStatusCode.BadRequest, """{"code":"not_enough_credits","message":"Not enough credits"}""", ProviderOutcome.QuotaExhausted),
        new("400 unauthorized", HttpStatusCode.BadRequest, """{"code":"unauthorized","message":"Key not found"}""", ProviderOutcome.ProviderFault),
        new("400 account_under_validation", HttpStatusCode.BadRequest, """{"code":"account_under_validation","message":"Your account is under validation"}""", ProviderOutcome.ProviderFault),
        new("400 invalid_parameter sender", HttpStatusCode.BadRequest, """{"code":"invalid_parameter","message":"Sender is not valid"}""", ProviderOutcome.ProviderFault),
        new("400 invalid_parameter email", HttpStatusCode.BadRequest, """{"code":"invalid_parameter","message":"email is not valid in to"}""", ProviderOutcome.RecipientRejected),
        new("400 duplicate_request", HttpStatusCode.BadRequest, """{"code":"duplicate_request","message":"Request already processed"}""", ProviderOutcome.Unknown),
        new("400 not json", HttpStatusCode.BadRequest, "<html>bad</html>", ProviderOutcome.RecipientRejected),
        new("401", HttpStatusCode.Unauthorized, """{"code":"unauthorized","message":"Key not found"}""", ProviderOutcome.ProviderFault),
        new("402", HttpStatusCode.PaymentRequired, "{}", ProviderOutcome.QuotaExhausted),
        new("403", HttpStatusCode.Forbidden, "{}", ProviderOutcome.ProviderFault),
        new("429", HttpStatusCode.TooManyRequests, "{}", ProviderOutcome.Throttled) { Headers = new Dictionary<string, string> { ["x-sib-ratelimit-reset"] = "12" } },
        new("500", HttpStatusCode.InternalServerError, "{}", ProviderOutcome.Unavailable),
        new("404 undocumented", HttpStatusCode.NotFound, "{}", ProviderOutcome.Unavailable),
    ];

    protected override BrevoProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new BrevoOptions { ApiKey = apiKey, Daily = 300 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.Created) { Content = new StringContent("""{"messageId":"<202609261234.12345@smtp-relay.mailin.fr>"}""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Hi")
        {
            TextBody = "Line one\nLine <two>",
            Cc = [new EmailAddress("cc@example.com")],
            Tags = ["sign-in"],
        };

        var result = await Create(handler, "key-1").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.Request.RequestUri!.ToString());
        Assert.Equal("key-1", Assert.Single(request.Request.Headers.GetValues("api-key")));
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("links@example.org", root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("Links", root.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("rider@example.com", root.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.False(root.GetProperty("to")[0].TryGetProperty("name", out _));
        Assert.Equal("cc@example.com", root.GetProperty("cc")[0].GetProperty("email").GetString());
        Assert.False(root.TryGetProperty("bcc", out _));
        Assert.Equal("<p>Line one<br>Line &lt;two&gt;</p>", root.GetProperty("htmlContent").GetString());
        Assert.Equal("sign-in", root.GetProperty("tags")[0].GetString());
        Assert.Equal("<202609261234.12345@smtp-relay.mailin.fr>", result.ProviderMessageId);
    }

    [Fact]
    public async Task The_rate_limit_reset_header_becomes_the_wait()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("x-sib-ratelimit-reset", "12");
            return response;
        });

        var result = await Create(handler, "key-1").SendAsync(CanaryMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(System.TimeSpan.FromSeconds(12), result.RetryAfter);
    }

    [Fact]
    public void The_options_never_print_the_key()
    {
        Assert.DoesNotContain("secret-key", new BrevoOptions { ApiKey = "secret-key" }.ToString(), System.StringComparison.Ordinal);
    }
}

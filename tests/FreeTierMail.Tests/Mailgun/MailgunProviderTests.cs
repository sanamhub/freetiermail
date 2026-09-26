using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Mailgun.Tests;

/// <summary>Statuses from Mailgun's API reference for POST /v3/{domain}/messages, read 2026-09-26.</summary>
public sealed class MailgunProviderTests : ProviderContractTests<MailgunProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("400", HttpStatusCode.BadRequest, """{"message":"to parameter is not a valid address"}""", ProviderOutcome.RecipientRejected),
        new("401", HttpStatusCode.Unauthorized, "Forbidden", ProviderOutcome.ProviderFault),
        new("403", HttpStatusCode.Forbidden, """{"message":"Domain is not allowed to send"}""", ProviderOutcome.ProviderFault),
        new("404", HttpStatusCode.NotFound, """{"message":"Domain not found"}""", ProviderOutcome.ProviderFault),
        new("413", HttpStatusCode.RequestEntityTooLarge, "{}", ProviderOutcome.RecipientRejected),
        new("429", HttpStatusCode.TooManyRequests, "{}", ProviderOutcome.Throttled),
        new("500", HttpStatusCode.InternalServerError, "{}", ProviderOutcome.Unavailable),
    ];

    protected override MailgunProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new MailgunOptions { ApiKey = apiKey, Domain = "mg.example.org", Daily = 100 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"<20260926.1@mg.example.org>","message":"Queued. Thank you."}""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Hi")
        {
            HtmlBody = "<p>Hi</p>",
            ReplyTo = new EmailAddress("help@example.org"),
            Tags = ["sign-in"],
        };

        var result = await Create(handler, "key-1").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.mailgun.net/v3/mg.example.org/messages", request.Request.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:key-1")), request.Request.Headers.Authorization!.ToString());
        Assert.Contains("name=from", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"Links\" <links@example.org>", request.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"h:Reply-To\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"o:tag\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=text", request.Body, StringComparison.Ordinal);
        Assert.Equal("<20260926.1@mg.example.org>", result.ProviderMessageId);
    }

    [Fact]
    public async Task An_eu_account_uses_the_eu_host()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var provider = new MailgunProvider(new HttpClient(handler), new MailgunOptions { ApiKey = "k", Domain = "mg.example.org", BaseAddress = new Uri("https://api.eu.mailgun.net/") });

        await provider.SendAsync(CanaryMessage(), TestContext.Current.CancellationToken);

        Assert.Equal("api.eu.mailgun.net", handler.Requests[0].Request.RequestUri!.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a domain")]
    public void A_missing_or_invalid_domain_is_refused(string domain)
    {
        Assert.Throws<ArgumentException>(() => new MailgunProvider(new HttpClient(), new MailgunOptions { ApiKey = "k", Domain = domain }));
    }
}

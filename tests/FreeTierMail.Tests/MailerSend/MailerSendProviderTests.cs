using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.MailerSend.Tests;

/// <summary>Statuses from MailerSend's email API reference, read 2026-09-26. The quota wording is a lead, not verified.</summary>
public sealed class MailerSendProviderTests : ProviderContractTests<MailerSendProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("401", HttpStatusCode.Unauthorized, """{"message":"Unauthenticated."}""", ProviderOutcome.ProviderFault),
        new("403", HttpStatusCode.Forbidden, """{"message":"This action is unauthorized."}""", ProviderOutcome.ProviderFault),
        new("422 domain", HttpStatusCode.UnprocessableEntity, """{"message":"The from.email domain must be verified in your account to send emails.","errors":{"from.email":["x"]}}""", ProviderOutcome.ProviderFault),
        new("422 recipient", HttpStatusCode.UnprocessableEntity, """{"message":"The to.0.email must be a valid email address.","errors":{"to.0.email":["x"]}}""", ProviderOutcome.RecipientRejected),
        new("429 quota", HttpStatusCode.TooManyRequests, """{"message":"Daily request quota exceeded."}""", ProviderOutcome.QuotaExhausted) { ExpectedPeriod = QuotaPeriod.Daily },
        new("429 rate", HttpStatusCode.TooManyRequests, """{"message":"Too Many Attempts."}""", ProviderOutcome.Throttled),
        new("500", HttpStatusCode.InternalServerError, "{}", ProviderOutcome.Unavailable),
    ];

    protected override MailerSendProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new MailerSendOptions { ApiKey = apiKey, Daily = 100, Monthly = 500 });

    protected override HttpResponseMessage Success()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.Add("x-message-id", "5e42957d51f1d94a1070a733");
        return response;
    }

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Hi")
        {
            TextBody = "Body",
            ReplyTo = new EmailAddress("help@example.org"),
        };

        var result = await Create(handler, "mlsn.test").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.mailersend.com/v1/email", request.Request.RequestUri!.ToString());
        Assert.Equal("Bearer mlsn.test", request.Request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("links@example.org", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("help@example.org", root.GetProperty("reply_to").GetProperty("email").GetString());
        Assert.Equal("Body", root.GetProperty("text").GetString());
        Assert.Equal("5e42957d51f1d94a1070a733", result.ProviderMessageId);
    }
}

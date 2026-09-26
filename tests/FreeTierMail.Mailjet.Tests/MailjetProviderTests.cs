using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Mailjet.Tests;

/// <summary>Shapes from Mailjet's "Send API v3.1" guide and "Send API v3.1 Errors" page, read 2026-09-26.</summary>
public sealed class MailjetProviderTests : ProviderContractTests<MailjetProvider>
{
    private const string Secret = "canary-secret-6b2e";

    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        MessageError(400, "mj-0013", ProviderOutcome.RecipientRejected),
        MessageError(400, "send-0003", ProviderOutcome.RecipientRejected),
        MessageError(400, "send-0007", ProviderOutcome.ProviderFault),
        MessageError(400, "send-0008", ProviderOutcome.ProviderFault),
        GlobalError(401, "mj-0001", ProviderOutcome.ProviderFault),
        GlobalError(401, "mj-0015", ProviderOutcome.ProviderFault),
        GlobalError(400, "mj-0002", ProviderOutcome.RecipientRejected),
        GlobalError(429, "mj-rate", ProviderOutcome.Throttled),
        GlobalError(500, "mj-internal", ProviderOutcome.Unavailable),
    ];

    protected override MailjetProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new MailjetOptions { ApiKey = apiKey, SecretKey = Secret, Daily = 200, Monthly = 6000 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Messages":[{"Status":"success","To":[{"Email":"x@example.com","MessageUUID":"123","MessageID":456,"MessageHref":"https://api.mailjet.com/v3/message/456"}]}]}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static DocumentedResponse MessageError(int status, string code, ProviderOutcome expected) =>
        new($"{status} {code}", (HttpStatusCode)status,
            $$"""{"Messages":[{"Status":"error","Errors":[{"ErrorIdentifier":"f987008f-251a-4dff-8ffc-40f1583ad7bc","ErrorCode":"{{code}}","StatusCode":{{status}},"ErrorMessage":"documented","ErrorRelatedTo":["To"]}]}]}""",
            expected);

    private static DocumentedResponse GlobalError(int status, string code, ProviderOutcome expected) =>
        new($"{status} {code}", (HttpStatusCode)status,
            $$"""{"ErrorIdentifier":"f987008f-251a-4dff-8ffc-40f1583ad7bc","ErrorCode":"{{code}}","ErrorMessage":"documented","StatusCode":{{status}}}""",
            expected);

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Hi") { TextBody = "Body" };

        var result = await Create(handler, "public-key").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.mailjet.com/v3.1/send", request.Request.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("public-key:" + Secret)), request.Request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(request.Body);
        var sent = json.RootElement.GetProperty("Messages")[0];
        Assert.Equal("links@example.org", sent.GetProperty("From").GetProperty("Email").GetString());
        Assert.Equal("rider@example.com", sent.GetProperty("To")[0].GetProperty("Email").GetString());
        Assert.Equal("Body", sent.GetProperty("TextPart").GetString());
        Assert.False(sent.TryGetProperty("HTMLPart", out _));
        Assert.Equal("123", result.ProviderMessageId);
    }

    [Fact]
    public async Task A_200_with_an_error_status_is_not_accepted()
    {
        using var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.OK, """{"Messages":[{"Status":"error","Errors":[{"ErrorCode":"mj-0013","StatusCode":400}]}]}""");

        var result = await Create(handler, "public-key").SendAsync(CanaryMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderOutcome.RecipientRejected, result.Outcome);
    }

    [Fact]
    public void A_missing_secret_key_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MailjetProvider(new HttpClient(), new MailjetOptions { ApiKey = "public-key" }));
    }

    [Fact]
    public void The_options_never_print_either_key()
    {
        var text = new MailjetOptions { ApiKey = "public-key", SecretKey = Secret }.ToString();

        Assert.DoesNotContain("public-key", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
    }
}

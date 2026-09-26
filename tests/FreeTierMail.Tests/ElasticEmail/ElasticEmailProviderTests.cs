using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.ElasticEmail.Tests;

/// <summary>Statuses from Elastic Email's REST API reference for emails/transactional, read 2026-09-26.</summary>
public sealed class ElasticEmailProviderTests : ProviderContractTests<ElasticEmailProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("400", HttpStatusCode.BadRequest, """{"Error":"Invalid recipient"}""", ProviderOutcome.RecipientRejected),
        new("401", HttpStatusCode.Unauthorized, """{"Error":"Access Denied."}""", ProviderOutcome.ProviderFault),
        new("403", HttpStatusCode.Forbidden, """{"Error":"Insufficient access level"}""", ProviderOutcome.ProviderFault),
        new("429", HttpStatusCode.TooManyRequests, "{}", ProviderOutcome.Throttled),
        new("500", HttpStatusCode.InternalServerError, "{}", ProviderOutcome.Unavailable),
    ];

    protected override ElasticEmailProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new ElasticEmailOptions { ApiKey = apiKey, Daily = 100, Monthly = 3000 });

    protected override HttpResponseMessage Success() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"TransactionID":"t-1","MessageID":"m-1"}""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());
        var message = new EmailMessage(new EmailAddress("links@example.org"), [new EmailAddress("rider@example.com")], "Hi")
        {
            TextBody = "Body",
            HtmlBody = "<p>Body</p>",
        };

        var result = await Create(handler, "ee-test").SendAsync(message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.elasticemail.com/v4/emails/transactional", request.Request.RequestUri!.ToString());
        Assert.Equal("ee-test", Assert.Single(request.Request.Headers.GetValues("X-ElasticEmail-ApiKey")));
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("rider@example.com", root.GetProperty("Recipients").GetProperty("To")[0].GetString());
        var parts = root.GetProperty("Content").GetProperty("Body");
        Assert.Equal("HTML", parts[0].GetProperty("ContentType").GetString());
        Assert.Equal("PlainText", parts[1].GetProperty("ContentType").GetString());
        Assert.Equal("utf-8", parts[1].GetProperty("Charset").GetString());
        Assert.Equal("m-1", result.ProviderMessageId);
    }
}

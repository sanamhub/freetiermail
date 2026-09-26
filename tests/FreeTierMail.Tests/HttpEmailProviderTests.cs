using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using FreeTierMail.Testing;

namespace FreeTierMail.Tests;

/// <summary>The base class passes the contract with the smallest possible provider on top.</summary>
public sealed class HttpEmailProviderTests : ProviderContractTests<MinimalProvider>
{
    protected override IEnumerable<DocumentedResponse> DocumentedErrors =>
    [
        new("429", HttpStatusCode.TooManyRequests, "{}", ProviderOutcome.Throttled),
        new("500", HttpStatusCode.InternalServerError, "{}", ProviderOutcome.Unavailable),
    ];

    protected override MinimalProvider Create(HttpMessageHandler handler, string apiKey) =>
        new(new HttpClient(handler), new MinimalOptions { ApiKey = apiKey });

    protected override HttpResponseMessage Success() => new(HttpStatusCode.OK);
}

public sealed class MinimalOptions : EmailProviderOptions;

public sealed class MinimalProvider(HttpClient http, MinimalOptions options) : HttpEmailProvider(http, options, "minimal")
{
    private readonly string _key = options.ApiKey;

    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://mail.example.org/send")
        {
            Content = new StringContent(message.To[0].Address + "|" + message.Subject + "|" + message.TextBody, Encoding.UTF8),
        };
        request.Headers.Add("X-Key", _key);
        return request;
    }

    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body) => status switch
    {
        HttpStatusCode.OK => ProviderResult.Accepted(),
        HttpStatusCode.TooManyRequests => ProviderResult.Of(ProviderOutcome.Throttled, "Rate limited.", RetryAfter(headers)),
        _ => Undocumented(status),
    };
}

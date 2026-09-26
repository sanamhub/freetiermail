using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FreeTierMail.Testing;

/// <summary>A response a provider's documentation describes, and the outcome it must map to.</summary>
/// <param name="Name">A short label for failure messages, such as <c>429 daily_quota_exceeded</c>.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body, as documented or recorded.</param>
/// <param name="Expected">The outcome the provider must return.</param>
public sealed record DocumentedResponse(string Name, HttpStatusCode Status, string Body, ProviderOutcome Expected)
{
    /// <summary>Headers to add, such as <c>Retry-After</c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>For <see cref="ProviderOutcome.QuotaExhausted"/>: the period the provider must report, if the response says.</summary>
    public QuotaPeriod? ExpectedPeriod { get; init; }
}

/// <summary>
/// The behaviour every provider must have, as xUnit v3 facts (AC-10). Derive a test class per
/// provider, implement the members, and the facts run in your test project.
/// </summary>
/// <typeparam name="TProvider">The provider under test.</typeparam>
public abstract class ProviderContractTests<TProvider>
    where TProvider : IEmailProvider
{
    /// <summary>The key given to <see cref="Create"/>. Distinctive, so a leak is unmistakable.</summary>
    protected const string CanaryKey = "canary-key-5c1e9b7d-0000";

    private const string CanaryRecipient = "canary-recipient-8d2f@example.com";
    private const string CanarySubject = "canary subject 3a9c";

    /// <summary>Creates the provider over <paramref name="handler"/> with <paramref name="apiKey"/>.</summary>
    /// <param name="handler">Answers every request. Wrap it in an <see cref="HttpClient"/>.</param>
    /// <param name="apiKey">The key to configure.</param>
    /// <returns>The provider.</returns>
    protected abstract TProvider Create(HttpMessageHandler handler, string apiKey);

    /// <summary>The response the provider sends for an accepted message.</summary>
    /// <returns>A new response each call.</returns>
    protected abstract HttpResponseMessage Success();

    /// <summary>Every error response the provider's documentation describes, with its outcome.</summary>
    protected abstract IEnumerable<DocumentedResponse> DocumentedErrors { get; }

    /// <summary>A message with canary values, from an address the test domain owns.</summary>
    /// <returns>The message.</returns>
    protected static EmailMessage CanaryMessage() =>
        new(new EmailAddress("sender@example.org", "Sender"), [new EmailAddress(CanaryRecipient)], CanarySubject)
        {
            TextBody = "canary text body",
            HtmlBody = "<p>canary html body</p>",
        };

    /// <summary>A success is <see cref="ProviderOutcome.Accepted"/>.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Success_is_accepted()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());

        var result = await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        Check(result.Outcome == ProviderOutcome.Accepted, $"a success must be Accepted, got {result.Outcome}.");
    }

    /// <summary>Each documented error maps to its outcome, and none puts the key, an address or the subject in its reason.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Every_documented_error_maps_to_its_outcome()
    {
        foreach (var documented in DocumentedErrors)
        {
            using var handler = new ScriptedHttpHandler().Respond(_ => ToResponse(documented));

            var result = await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

            Check(documented.Expected == result.Outcome, $"{documented.Name}: expected {documented.Expected}, got {result.Outcome}.");
            if (documented.ExpectedPeriod is { } period)
            {
                Check(result.ExhaustedPeriod == period, $"{documented.Name}: expected period {period}, got {result.ExhaustedPeriod}.");
            }

            AssertNoLeak(result, documented.Name);
        }
    }

    /// <summary>A refused connection sent nothing: <see cref="ProviderOutcome.Unavailable"/>.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Connection_refused_is_unavailable()
    {
        using var handler = new ScriptedHttpHandler().Throw(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused."));

        var result = await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        Check(result.Outcome == ProviderOutcome.Unavailable, $"a refused connection must be Unavailable, got {result.Outcome}.");
    }

    /// <summary>A timeout may have sent: <see cref="ProviderOutcome.Unknown"/>.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Timeout_is_unknown()
    {
        using var handler = new ScriptedHttpHandler().Throw(new TaskCanceledException("Timed out.", new TimeoutException()));

        var result = await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        Check(result.Outcome == ProviderOutcome.Unknown, $"a timeout must be Unknown, got {result.Outcome}.");
    }

    /// <summary>A response cut off after the request left may have sent: <see cref="ProviderOutcome.Unknown"/>.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Dropped_response_is_unknown()
    {
        using var handler = new ScriptedHttpHandler().Throw(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        var result = await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        Check(result.Outcome == ProviderOutcome.Unknown, $"a dropped response must be Unknown, got {result.Outcome}.");
    }

    /// <summary>The caller's cancellation throws, instead of becoming an outcome.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Cancellation_throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());

        try
        {
            await Create(handler, CanaryKey).SendAsync(CanaryMessage(), cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Fail("a cancelled token must throw OperationCanceledException, not become an outcome.");
    }

    /// <summary>The key travels in a header, never in the URL, where logs and traces would keep it.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task The_key_is_not_in_the_url()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());

        await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        var requests = handler.Requests;
        Check(requests.Count == 1, $"one send must make one request, made {requests.Count}.");
        Check(!requests[0].Request.RequestUri!.ToString().Contains(CanaryKey, StringComparison.Ordinal), "the API key is in the request URL.");
    }

    /// <summary>The request carries the recipient and subject, so the provider mapping is not empty.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task The_request_carries_the_message()
    {
        using var handler = new ScriptedHttpHandler().Respond(_ => Success());

        await Create(handler, CanaryKey).SendAsync(CanaryMessage(), TestContext.Current.CancellationToken).ConfigureAwait(false);

        var requests = handler.Requests;
        Check(requests.Count == 1, $"one send must make one request, made {requests.Count}.");
        var body = requests[0].Body;
        Check(body.Contains(CanaryRecipient, StringComparison.Ordinal), "the request body does not carry the recipient.");
        Check(body.Contains(CanarySubject, StringComparison.Ordinal), "the request body does not carry the subject.");
        Check(body.Contains("canary text body", StringComparison.Ordinal), "the request body does not carry the text body.");
    }

    /// <summary>Fails when <paramref name="result"/> holds the key, the recipient or the subject.</summary>
    /// <param name="result">The result.</param>
    /// <param name="context">A label for the failure message.</param>
    protected static void AssertNoLeak(ProviderResult result, string context)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = result.ToString();
        foreach (var canary in new[] { CanaryKey, CanaryRecipient, CanarySubject })
        {
            Check(!text.Contains(canary, StringComparison.Ordinal), $"{context}: the result contains a canary value: {canary}");
        }
    }

    /// <summary>Fails the fact with <paramref name="failure"/> when <paramref name="condition"/> is false.</summary>
    /// <param name="condition">What must hold.</param>
    /// <param name="failure">What broke, for the test output.</param>
    /// <exception cref="InvalidOperationException">The condition is false.</exception>
    protected static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            Fail(failure);
        }
    }

    private static void Fail(string failure) => throw new InvalidOperationException("Provider contract violated: " + failure);

    private static HttpResponseMessage ToResponse(DocumentedResponse documented)
    {
        var response = new HttpResponseMessage(documented.Status) { Content = new StringContent(documented.Body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in documented.Headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>
/// A provider over an HTTP API. Derived classes build the request and read the answer; this class
/// turns network failures into the right outcome, so every provider treats them alike:
/// a failure before the request left is <see cref="ProviderOutcome.Unavailable"/>, and a timeout or
/// a dropped response is <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
public abstract class HttpEmailProvider : IEmailProvider
{
    private readonly HttpClient _http;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it; give it a timeout, and no retry handler (the mailer decides retries).</param>
    /// <param name="options">The options.</param>
    /// <param name="defaultName">The name used when <see cref="EmailProviderOptions.Name"/> is not set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    protected HttpEmailProvider(HttpClient http, EmailProviderOptions options, string defaultName)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultName);
        options.Validate(defaultName);

        _http = http;
        Name = string.IsNullOrWhiteSpace(options.Name) ? defaultName : options.Name;
        Quota = options.ToQuotaPlan();
        PreferForCritical = options.PreferForCritical;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public QuotaPlan Quota { get; }

    /// <inheritdoc/>
    public bool PreferForCritical { get; }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public async Task<ProviderResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        using var request = CreateRequest(message);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, response.Headers, body);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException)
        {
            // The caller cancelled; content serialization can wrap that in HttpRequestException.
            throw new OperationCanceledException("The send was cancelled.", ex, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout. The request may have reached the provider.
            return ProviderResult.Of(ProviderOutcome.Unknown, "Timed out waiting for the provider; the message may have been sent.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            return ProviderResult.Of(ProviderOutcome.Unavailable, "Could not connect to the provider (" + ex.HttpRequestError + ").");
        }
        catch (HttpRequestException ex)
        {
            return ProviderResult.Of(ProviderOutcome.Unknown, "The connection failed after the request was sent (" + ex.HttpRequestError + "); the message may have been sent.");
        }
    }

    /// <summary>Builds the request for one message, with the key in a header.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The request. This class disposes it.</returns>
    protected abstract HttpRequestMessage CreateRequest(EmailMessage message);

    /// <summary>Reads the provider's answer. Never put the body into <see cref="ProviderResult.Reason"/>: it can echo addresses.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="headers">The response headers.</param>
    /// <param name="body">The response body.</param>
    /// <returns>The outcome.</returns>
    protected abstract ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body);

    /// <summary>The wait a 429 or 503 asked for, from <c>Retry-After</c>, if any.</summary>
    /// <param name="headers">The response headers.</param>
    /// <returns>The wait, or null.</returns>
    protected static TimeSpan? RetryAfter(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero,
            _ => null,
        };
    }

    /// <summary>The outcome for a status the provider's documentation does not describe.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <returns><see cref="ProviderOutcome.Unavailable"/> for 5xx and unknown codes, since no provider documents sending on them.</returns>
    protected static ProviderResult Undocumented(HttpStatusCode status) =>
        ProviderResult.Of(ProviderOutcome.Unavailable, "The provider answered HTTP " + (int)status + ", which its documentation does not describe.");
}

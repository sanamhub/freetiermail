using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>
/// A webhook call as it arrived: the headers and the raw body. Host-neutral, so an ASP.NET Core
/// endpoint, an Azure Function or a test builds one the same way (see the README).
/// </summary>
public sealed class WebhookRequest
{
    private readonly Dictionary<string, string> _headers;

    /// <summary>Creates a request.</summary>
    /// <param name="headers">The headers; names compared without regard to case.</param>
    /// <param name="body">The raw body, unchanged: signatures cover these exact bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    public WebhookRequest(IEnumerable<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            _headers[header.Key] = header.Value;
        }

        Body = body;
    }

    /// <summary>The raw body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>A header's value, or null.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null.</returns>
    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What <see cref="WebhookReceiver.ReceiveAsync"/> did with a call.</summary>
public enum WebhookResult
{
    /// <summary>Verified and read; any hard bounce or complaint is now on the suppression list. Answer 200.</summary>
    Accepted = 0,

    /// <summary>The signature or shared secret did not match. Answer 401; nothing was stored.</summary>
    Unauthorized = 1,

    /// <summary>No webhook is registered under that name. Answer 404.</summary>
    UnknownProvider = 2,

    /// <summary>Verified, but the body is not the provider's format. Answer 400.</summary>
    Malformed = 3,
}

/// <summary>
/// One provider's webhook: how its calls are verified and which of its events suppress an
/// address. Verification comes first and uses constant-time comparison; an unverified call is
/// never parsed (ADR-0005).
/// </summary>
public abstract class EmailWebhook
{
    /// <summary>Creates a webhook.</summary>
    /// <param name="name">The name in the endpoint path, usually the provider's name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    protected EmailWebhook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The name in the endpoint path.</summary>
    public string Name { get; }

    /// <summary>True when the call provably comes from the provider.</summary>
    /// <param name="request">The call.</param>
    /// <param name="now">The current time, for timestamp checks.</param>
    /// <returns>True if verified.</returns>
    public abstract bool Verify(WebhookRequest request, DateTimeOffset now);

    /// <summary>The addresses to suppress. Events that do not suppress (delivered, opened, soft bounces) give none.</summary>
    /// <param name="body">The parsed body of a verified call.</param>
    /// <param name="now">When it arrived, for events that carry no time.</param>
    /// <returns>The suppressions.</returns>
    /// <exception cref="FormatException">The body is not the provider's format.</exception>
    public abstract IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now);

    /// <summary>Hex-encoded HMAC-SHA256 of <paramref name="data"/>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="data">The data.</param>
    /// <returns>Upper-case hex; compare with ToUpperInvariant on the given value.</returns>
    protected static string HmacHex(byte[] key, ReadOnlySpan<byte> data) => Convert.ToHexString(HMACSHA256.HashData(key, data));

    /// <summary>Compares two strings in constant time for their length.</summary>
    /// <param name="given">From the request.</param>
    /// <param name="expected">Computed or configured.</param>
    /// <returns>True if equal.</returns>
    protected static bool SameText(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    /// <summary>True when <paramref name="unixSeconds"/> is within <paramref name="tolerance"/> of <paramref name="now"/>, which stops replays of old calls.</summary>
    /// <param name="unixSeconds">The call's timestamp, in seconds since 1970.</param>
    /// <param name="now">The current time.</param>
    /// <param name="tolerance">The allowed difference.</param>
    /// <returns>True if fresh.</returns>
    protected static bool Fresh(string? unixSeconds, DateTimeOffset now, TimeSpan tolerance) =>
        long.TryParse(unixSeconds, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() <= tolerance;

    /// <summary>
    /// True when the request carries <paramref name="secret"/> as a bearer token, or as the
    /// password of HTTP basic authentication with <paramref name="user"/>. For providers that
    /// document no signature.
    /// </summary>
    /// <param name="request">The call.</param>
    /// <param name="user">The basic-authentication user, or null to accept a bearer token only.</param>
    /// <param name="secret">The configured secret.</param>
    /// <returns>True if it matches.</returns>
    protected static bool SharedSecret(WebhookRequest request, string? user, string secret)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Header("Authorization");
        if (header is null)
        {
            return false;
        }

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return SameText(header["Bearer ".Length..].Trim(), secret);
        }

        if (user is not null && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return SameText(Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())), user + ":" + secret);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>A string property, or null when absent or not a string.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value, or null.</returns>
    protected static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Receives provider webhooks and puts hard bounces and complaints on the shared suppression list,
/// so every provider in the mailer skips the address from then on.
/// </summary>
public sealed class WebhookReceiver
{
    /// <summary>The largest body read, 1 MB. Providers send far less; a bigger one is refused unread.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly Dictionary<string, EmailWebhook> _webhooks;
    private readonly ISuppressionStore _store;
    private readonly TimeProvider _time;

    /// <summary>Creates a receiver.</summary>
    /// <param name="webhooks">One webhook per provider name.</param>
    /// <param name="store">The suppression list the mailer reads.</param>
    /// <param name="timeProvider">The clock; the system clock when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="webhooks"/> or <paramref name="store"/> is null.</exception>
    /// <exception cref="ArgumentException">Two webhooks share a name.</exception>
    public WebhookReceiver(IEnumerable<EmailWebhook> webhooks, ISuppressionStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(store);
        var list = webhooks.ToList();
        var duplicate = list.GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two webhooks are named '{duplicate.Key}'.", nameof(webhooks));
        }

        _webhooks = list.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The registered names, for building endpoint paths.</summary>
    public IReadOnlyCollection<string> Names => _webhooks.Keys;

    /// <summary>Verifies and reads one call.</summary>
    /// <param name="provider">The webhook's name, from the endpoint path.</param>
    /// <param name="request">The call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened; map it to the HTTP status the result names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<WebhookResult> ReceiveAsync(string provider, WebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (provider is null || !_webhooks.TryGetValue(provider, out var webhook))
        {
            return WebhookResult.UnknownProvider;
        }

        var now = _time.GetUtcNow();
        if (request.Body.Length > MaxBodyBytes || !webhook.Verify(request, now))
        {
            return WebhookResult.Unauthorized;
        }

        List<Suppression> suppressions;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            suppressions = [.. webhook.Read(document.RootElement, now)];
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return WebhookResult.Malformed;
        }

        foreach (var suppression in suppressions)
        {
            await _store.AddAsync(suppression, cancellationToken).ConfigureAwait(false);
        }

        return WebhookResult.Accepted;
    }
}

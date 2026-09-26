using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace FreeTierMail.Mailgun;

/// <summary>
/// Mailgun's webhooks: the body carries <c>signature.timestamp</c>, <c>signature.token</c> and
/// <c>signature.signature</c>, the hex HMAC-SHA256 of timestamp and token joined with no separator,
/// keyed with the account's webhook signing key. In <c>event-data</c>, <c>failed</c> with severity
/// <c>permanent</c> and <c>complained</c> suppress <c>recipient</c>.
/// </summary>
public sealed class MailgunWebhook : EmailWebhook
{
    private readonly byte[] _key;
    private readonly TimeSpan _tolerance;

    /// <summary>Creates the webhook.</summary>
    /// <param name="signingKey">The webhook signing key from the Mailgun dashboard (not the API key).</param>
    /// <param name="name">The name in the endpoint path.</param>
    /// <param name="tolerance">How old a call may be; 5 minutes when null.</param>
    /// <exception cref="ArgumentException"><paramref name="signingKey"/> is empty.</exception>
    public MailgunWebhook(string signingKey, string name = "mailgun", TimeSpan? tolerance = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        _key = Encoding.UTF8.GetBytes(signingKey);
        _tolerance = tolerance ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public override bool Verify(WebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            if (!document.RootElement.TryGetProperty("signature", out var signature))
            {
                return false;
            }

            var timestamp = Text(signature, "timestamp");
            var token = Text(signature, "token");
            var given = Text(signature, "signature");
            return timestamp is not null && token is not null && given is not null
                && Fresh(timestamp, now, _tolerance)
                && SameText(given.ToUpperInvariant(), HmacHex(_key, Encoding.UTF8.GetBytes(timestamp + token)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now)
    {
        if (!body.TryGetProperty("event-data", out var data))
        {
            throw new FormatException("A Mailgun webhook has event-data.");
        }

        SuppressionReason? reason = Text(data, "event") switch
        {
            "failed" when Text(data, "severity") == "permanent" => SuppressionReason.HardBounce,
            "complained" => SuppressionReason.Complaint,
            _ => null,
        };
        if (reason is null || Text(data, "recipient") is not { Length: > 0 } recipient)
        {
            yield break;
        }

        var at = data.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(ts.GetDouble() * 1000))
            : now;
        yield return new Suppression(recipient, reason.Value, Name, at);
    }
}

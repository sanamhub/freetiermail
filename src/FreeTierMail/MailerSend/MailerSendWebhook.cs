using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FreeTierMail.MailerSend;

/// <summary>
/// MailerSend's webhooks: the <c>Signature</c> header is the hex HMAC-SHA256 of the raw body, keyed
/// with the webhook's signing secret. The call carries no signed timestamp, so an old call cannot be
/// told from a new one; suppressing twice is harmless. <c>activity.hard_bounced</c> and
/// <c>activity.spam_complaint</c> suppress <c>data.recipient</c>.
/// </summary>
public sealed class MailerSendWebhook : EmailWebhook
{
    private readonly byte[] _key;

    /// <summary>Creates the webhook.</summary>
    /// <param name="signingSecret">The webhook's signing secret.</param>
    /// <param name="name">The name in the endpoint path.</param>
    /// <exception cref="ArgumentException"><paramref name="signingSecret"/> is empty.</exception>
    public MailerSendWebhook(string signingSecret, string name = "mailersend")
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingSecret);
        _key = Encoding.UTF8.GetBytes(signingSecret);
    }

    /// <inheritdoc />
    public override bool Verify(WebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Header("Signature") is { } given && SameText(given.Trim().ToUpperInvariant(), HmacHex(_key, request.Body.Span));
    }

    /// <inheritdoc />
    public override IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now)
    {
        var type = Text(body, "type") ?? throw new FormatException("A MailerSend event has a type.");
        SuppressionReason? reason = type switch
        {
            "activity.hard_bounced" => SuppressionReason.HardBounce,
            "activity.spam_complaint" => SuppressionReason.Complaint,
            _ => null,
        };
        if (reason is null || !body.TryGetProperty("data", out var data) || Text(data, "recipient") is not { Length: > 0 } recipient)
        {
            yield break;
        }

        var at = DateTimeOffset.TryParse(Text(body, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : now;
        yield return new Suppression(recipient, reason.Value, Name, at);
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FreeTierMail.Brevo;

/// <summary>
/// Brevo's transactional webhooks. Brevo documents no request signature, so the call must carry a
/// shared secret: add it to the webhook in Brevo as a bearer token or basic-authentication
/// password. <c>hard_bounce</c> and <c>spam</c> suppress <c>email</c>. How Brevo sends the
/// credentials is a lead to confirm against a live webhook.
/// </summary>
public sealed class BrevoWebhook : EmailWebhook
{
    private readonly string _secret;
    private readonly string? _user;

    /// <summary>Creates the webhook.</summary>
    /// <param name="secret">The token or password configured on the Brevo webhook.</param>
    /// <param name="user">The basic-authentication user, or null for a bearer token.</param>
    /// <param name="name">The name in the endpoint path.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty.</exception>
    public BrevoWebhook(string secret, string? user = null, string name = "brevo")
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _secret = secret;
        _user = user;
    }

    /// <inheritdoc />
    public override bool Verify(WebhookRequest request, DateTimeOffset now) => SharedSecret(request, _user, _secret);

    /// <inheritdoc />
    public override IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now)
    {
        // One event per call; an array is accepted too, since batched delivery is an option.
        var events = body.ValueKind == JsonValueKind.Array ? [.. body.EnumerateArray()] : new List<JsonElement> { body };
        foreach (var item in events)
        {
            SuppressionReason? reason = Text(item, "event") switch
            {
                "hard_bounce" => SuppressionReason.HardBounce,
                "spam" => SuppressionReason.Complaint,
                _ => null,
            };
            if (reason is not null && Text(item, "email") is { Length: > 0 } email)
            {
                yield return new Suppression(email, reason.Value, Name, now);
            }
        }
    }
}

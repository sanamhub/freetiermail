using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FreeTierMail.Mailjet;

/// <summary>
/// Mailjet's event webhooks. Mailjet signs nothing and recommends basic authentication in the
/// webhook URL, so the call must carry the configured user and password. Events arrive as one
/// object or an array; <c>bounce</c> with <c>hard_bounce</c> true and <c>spam</c> suppress
/// <c>email</c>. The payload fields are a lead to confirm against a live webhook.
/// </summary>
public sealed class MailjetWebhook : EmailWebhook
{
    private readonly string _user;
    private readonly string _password;

    /// <summary>Creates the webhook.</summary>
    /// <param name="user">The basic-authentication user in the webhook URL.</param>
    /// <param name="password">The basic-authentication password in the webhook URL.</param>
    /// <param name="name">The name in the endpoint path.</param>
    /// <exception cref="ArgumentException">The user or password is empty.</exception>
    public MailjetWebhook(string user, string password, string name = "mailjet")
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        _user = user;
        _password = password;
    }

    /// <inheritdoc />
    public override bool Verify(WebhookRequest request, DateTimeOffset now) => SharedSecret(request, _user, _password);

    /// <inheritdoc />
    public override IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now)
    {
        var events = body.ValueKind == JsonValueKind.Array ? [.. body.EnumerateArray()] : new List<JsonElement> { body };
        foreach (var item in events)
        {
            var hard = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("hard_bounce", out var flag) && flag.ValueKind == JsonValueKind.True;
            SuppressionReason? reason = Text(item, "event") switch
            {
                "bounce" when hard => SuppressionReason.HardBounce,
                "spam" => SuppressionReason.Complaint,
                _ => null,
            };
            if (reason is null || Text(item, "email") is not { Length: > 0 } email)
            {
                continue;
            }

            var at = item.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(time.GetInt64())
                : now;
            yield return new Suppression(email, reason.Value, Name, at);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreeTierMail.Resend;

/// <summary>
/// Resend's webhooks, signed by Svix: HMAC-SHA256 over <c>{svix-id}.{svix-timestamp}.{body}</c>
/// with the base64 key after <c>whsec_</c>, sent in <c>svix-signature</c> as space-separated
/// <c>v1,&lt;base64&gt;</c> entries. <c>email.bounced</c> with bounce type <c>Permanent</c> and
/// <c>email.complained</c> suppress every address in <c>data.to</c>.
/// </summary>
public sealed class ResendWebhook : EmailWebhook
{
    private const string Prefix = "whsec_";

    private readonly byte[] _key;
    private readonly TimeSpan _tolerance;

    /// <summary>Creates the webhook.</summary>
    /// <param name="signingSecret">The endpoint's signing secret, <c>whsec_...</c>.</param>
    /// <param name="name">The name in the endpoint path.</param>
    /// <param name="tolerance">How old a call may be; 5 minutes when null.</param>
    /// <exception cref="ArgumentException">The secret is not a <c>whsec_</c> base64 key.</exception>
    public ResendWebhook(string signingSecret, string name = "resend", TimeSpan? tolerance = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingSecret);
        var encoded = signingSecret.StartsWith(Prefix, StringComparison.Ordinal) ? signingSecret[Prefix.Length..] : signingSecret;
        try
        {
            _key = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Resend signing secret must be whsec_ followed by base64.", nameof(signingSecret), ex);
        }

        _tolerance = tolerance ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public override bool Verify(WebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Header("svix-id");
        var timestamp = request.Header("svix-timestamp");
        var signatures = request.Header("svix-signature");
        if (id is null || timestamp is null || signatures is null || !Fresh(timestamp, now, _tolerance))
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var signed = new byte[prefix.Length + request.Body.Length];
        prefix.CopyTo(signed, 0);
        request.Body.Span.CopyTo(signed.AsSpan(prefix.Length));
        var expected = Convert.ToBase64String(HMACSHA256.HashData(_key, signed));

        foreach (var entry in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.StartsWith("v1,", StringComparison.Ordinal) && SameText(entry[3..], expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override IEnumerable<Suppression> Read(JsonElement body, DateTimeOffset now)
    {
        var type = Text(body, "type") ?? throw new FormatException("A Resend event has a type.");
        if (!body.TryGetProperty("data", out var data))
        {
            throw new FormatException("A Resend event has data.");
        }

        SuppressionReason? reason = type switch
        {
            "email.bounced" when data.TryGetProperty("bounce", out var bounce) && Text(bounce, "type") == "Permanent" => SuppressionReason.HardBounce,
            "email.complained" => SuppressionReason.Complaint,
            _ => null,
        };
        if (reason is null || !data.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var at = DateTimeOffset.TryParse(Text(body, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : now;
        foreach (var address in to.EnumerateArray())
        {
            if (address.ValueKind == JsonValueKind.String && address.GetString() is { Length: > 0 } value)
            {
                yield return new Suppression(value, reason.Value, Name, at);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.Brevo;

/// <summary>Options for <see cref="BrevoProvider"/>.</summary>
/// <remarks>Brevo's free plan allows 300 messages a day, shared by marketing and transactional mail (checked 2026-09-26). Set <see cref="EmailProviderOptions.Daily"/> to match your plan.</remarks>
public sealed class BrevoOptions : EmailProviderOptions;

/// <summary>
/// Sends through Brevo's transactional API (<c>POST /v3/smtp/email</c>). The key goes in the
/// <c>api-key</c> header. Brevo has no idempotency key, so a lost answer stays
/// <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from Brevo's API reference for <c>sendTransacEmail</c> (read 2026-09-26):
/// 201 and 202 are accepted; 400 <c>not_enough_credits</c> is quota; 400 <c>unauthorized</c>,
/// <c>permission_denied</c> and <c>account_under_validation</c>, and 401 and 403, are faults of the
/// account; other 400 codes are a refused message, except when Brevo names the sender, which is
/// this account's sender setup; 429 is throttling; 5xx is unavailable.
/// </remarks>
public sealed class BrevoProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.brevo.com/");
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with <see cref="EmailProviderOptions.ApiKey"/> set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public BrevoProvider(HttpClient http, BrevoOptions options)
        : base(http, options, "brevo")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v3/smtp/email");
        _apiKey = options.ApiKey;
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new BrevoRequest
        {
            Sender = Contact(message.From),
            To = [.. message.To.Select(Contact)],
            Cc = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Contact)],
            Bcc = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Contact)],
            ReplyTo = message.ReplyTo is null ? null : Contact(message.ReplyTo),
            Subject = message.Subject,
            // Brevo documents htmlContent as required without a template.
            HtmlContent = message.HtmlBody ?? TextToHtml(message.TextBody!),
            TextContent = message.TextBody,
            Tags = message.Tags.Count == 0 ? null : [.. message.Tags],
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, BrevoJson.Default.BrevoRequest),
        };
        request.Headers.Add("api-key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        switch ((int)status)
        {
            case 201 or 202:
                return ProviderResult.Accepted(Read(body, BrevoJson.Default.BrevoSuccess)?.MessageId);
            case 400:
                return MapBadRequest(Read(body, BrevoJson.Default.BrevoError));
            case 401 or 403:
                return ProviderResult.Of(ProviderOutcome.ProviderFault, "Brevo refused the API key or its permissions.");
            case 402:
                return ProviderResult.Of(ProviderOutcome.QuotaExhausted, "Brevo reports no credits left.");
            case 429:
                return ProviderResult.Of(ProviderOutcome.Throttled, "Brevo rate limited the request.", RetryAfter(headers) ?? RateLimitReset(headers));
            case >= 500:
                return ProviderResult.Of(ProviderOutcome.Unavailable, "Brevo answered HTTP " + (int)status + ".");
            default:
                return Undocumented(status);
        }
    }

    private static ProviderResult MapBadRequest(BrevoError? error) => error?.Code switch
    {
        "not_enough_credits" => ProviderResult.Of(ProviderOutcome.QuotaExhausted, "Brevo reports not_enough_credits."),
        "unauthorized" or "permission_denied" or "account_under_validation" =>
            ProviderResult.Of(ProviderOutcome.ProviderFault, "Brevo refused this account (" + error.Code + ")."),
        "duplicate_request" => ProviderResult.Of(ProviderOutcome.Unknown, "Brevo reports a duplicate request; an earlier one may have sent."),
        // A sender Brevo has not verified is this account's setup, not the message: another provider may send it.
        _ when error?.Message?.Contains("sender", StringComparison.OrdinalIgnoreCase) == true =>
            ProviderResult.Of(ProviderOutcome.ProviderFault, "Brevo refused the sender (" + error.Code + "); verify it in Brevo."),
        { } code => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Brevo refused the message (" + code + ")."),
        null => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Brevo refused the message (HTTP 400)."),
    };

    // Brevo documents x-sib-ratelimit-reset as the seconds until the limit resets.
    private static TimeSpan? RateLimitReset(HttpResponseHeaders headers) =>
        headers.TryGetValues("x-sib-ratelimit-reset", out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static T? Read<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BrevoContact Contact(EmailAddress address) => new() { Email = address.Address, Name = address.DisplayName };

    private static string TextToHtml(string text) =>
        "<p>" + WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>") + "</p>";
}

internal sealed class BrevoRequest
{
    public required BrevoContact Sender { get; init; }

    public required IReadOnlyList<BrevoContact> To { get; init; }

    public IReadOnlyList<BrevoContact>? Cc { get; init; }

    public IReadOnlyList<BrevoContact>? Bcc { get; init; }

    public BrevoContact? ReplyTo { get; init; }

    public required string Subject { get; init; }

    public required string HtmlContent { get; init; }

    public string? TextContent { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

internal sealed class BrevoContact
{
    public required string Email { get; init; }

    public string? Name { get; init; }
}

internal sealed class BrevoSuccess
{
    public string? MessageId { get; init; }
}

internal sealed class BrevoError
{
    public string? Code { get; init; }

    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrevoRequest))]
[JsonSerializable(typeof(BrevoSuccess))]
[JsonSerializable(typeof(BrevoError))]
internal sealed partial class BrevoJson : JsonSerializerContext;

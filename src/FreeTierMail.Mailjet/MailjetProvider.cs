using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.Mailjet;

/// <summary>Options for <see cref="MailjetProvider"/>.</summary>
/// <remarks>
/// Mailjet authenticates with two values: <see cref="EmailProviderOptions.ApiKey"/> is the public
/// API key, <see cref="SecretKey"/> the private one. Its free plan allows 6,000 messages a month and
/// 200 a day (checked 2026-09-26).
/// </remarks>
public sealed class MailjetOptions : EmailProviderOptions
{
    /// <summary>The private API key. A secret, like <see cref="EmailProviderOptions.ApiKey"/>.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <inheritdoc/>
    protected override void Validate(string providerName)
    {
        base.Validate(providerName);
        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            throw new ArgumentException($"{providerName} needs a SecretKey, Mailjet's private API key.");
        }
    }
}

/// <summary>
/// Sends through Mailjet's Send API v3.1 (<c>POST /v3.1/send</c>) with HTTP basic authentication.
/// Mailjet has no idempotency key, so a lost answer stays <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from Mailjet's "Send API v3.1 Errors" page (read 2026-09-26): 200 with status
/// <c>success</c> is accepted; 401 (<c>mj-0001</c> suspended key, <c>mj-0015</c> no credentials) and
/// the sender errors <c>send-0006</c>, <c>send-0007</c> and <c>send-0008</c> are faults of this
/// account; other 400 codes are a refused message; 429 is throttling; 5xx is unavailable. The page
/// does not say how a used-up free quota is reported, so that is counted locally and a 429 is read
/// as throttling; confirm against a live account.
/// </remarks>
public sealed class MailjetProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.mailjet.com/");
    private static readonly string[] SenderErrors = ["send-0006", "send-0007", "send-0008"];
    private readonly Uri _endpoint;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with both keys set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public MailjetProvider(HttpClient http, MailjetOptions options)
        : base(http, options, "mailjet")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v3.1/send");
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ApiKey + ":" + options.SecretKey)));
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new MailjetRequest
        {
            Messages =
            [
                new MailjetMessage
                {
                    From = Contact(message.From),
                    To = [.. message.To.Select(Contact)],
                    Cc = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Contact)],
                    Bcc = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Contact)],
                    ReplyTo = message.ReplyTo is null ? null : Contact(message.ReplyTo),
                    Subject = message.Subject,
                    TextPart = message.TextBody,
                    HTMLPart = message.HtmlBody,
                },
            ],
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, MailjetJson.Default.MailjetRequest),
        };
        request.Headers.Authorization = _authorization;
        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var code = (int)status;
        var response = Read(body);
        var first = response?.Messages is { Count: > 0 } messages ? messages[0] : null;
        if (code == 200 && first?.Status == "success")
        {
            var to = first.To is { Count: > 0 } recipients ? recipients[0] : null;
            return ProviderResult.Accepted(to?.MessageUUID ?? to?.MessageID?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var errors = (first?.Errors ?? []).Select(e => e.ErrorCode).Append(response?.ErrorCode).OfType<string>().ToArray();
        if (code == 401 || errors.Intersect(SenderErrors, StringComparer.Ordinal).Any())
        {
            return ProviderResult.Of(ProviderOutcome.ProviderFault, "Mailjet refused this account or its sender (" + Describe(code, errors) + ").");
        }

        return code switch
        {
            200 or 400 => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Mailjet refused the message (" + Describe(code, errors) + ")."),
            403 => ProviderResult.Of(ProviderOutcome.ProviderFault, "Mailjet refused this account (" + Describe(code, errors) + ")."),
            429 => ProviderResult.Of(ProviderOutcome.Throttled, "Mailjet rate limited the request.", RetryAfter(headers)),
            >= 500 => ProviderResult.Of(ProviderOutcome.Unavailable, "Mailjet answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    // Error codes are Mailjet's own identifiers, never message content, so they are safe to report.
    private static string Describe(int code, string[] errors) =>
        errors.Length == 0 ? "HTTP " + code : string.Join(", ", errors.Distinct(StringComparer.Ordinal));

    private static MailjetResponse? Read(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, MailjetJson.Default.MailjetResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MailjetContact Contact(EmailAddress address) => new() { Email = address.Address, Name = address.DisplayName };
}

internal sealed class MailjetRequest
{
    public required IReadOnlyList<MailjetMessage> Messages { get; init; }
}

internal sealed class MailjetMessage
{
    public required MailjetContact From { get; init; }

    public required IReadOnlyList<MailjetContact> To { get; init; }

    public IReadOnlyList<MailjetContact>? Cc { get; init; }

    public IReadOnlyList<MailjetContact>? Bcc { get; init; }

    public MailjetContact? ReplyTo { get; init; }

    public required string Subject { get; init; }

    public string? TextPart { get; init; }

    [JsonPropertyName("HTMLPart")]
    public string? HTMLPart { get; init; }
}

internal sealed class MailjetContact
{
    public required string Email { get; init; }

    public string? Name { get; init; }
}

internal sealed class MailjetResponse
{
    public IReadOnlyList<MailjetMessageResult>? Messages { get; init; }

    public string? ErrorCode { get; init; }
}

internal sealed class MailjetMessageResult
{
    public string? Status { get; init; }

    public IReadOnlyList<MailjetRecipientResult>? To { get; init; }

    public IReadOnlyList<MailjetError>? Errors { get; init; }
}

internal sealed class MailjetRecipientResult
{
    public string? MessageUUID { get; init; }

    public long? MessageID { get; init; }
}

internal sealed class MailjetError
{
    public string? ErrorCode { get; init; }
}

// Mailjet's JSON uses PascalCase names, which are the C# names.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MailjetRequest))]
[JsonSerializable(typeof(MailjetResponse))]
internal sealed partial class MailjetJson : JsonSerializerContext;

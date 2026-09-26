using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.MailerSend;

/// <summary>Options for <see cref="MailerSendProvider"/>.</summary>
/// <remarks>MailerSend's free plan allows 500 messages a month and 100 API requests a day (checked 2026-09-26). Set <see cref="EmailProviderOptions.Daily"/> and <see cref="EmailProviderOptions.Monthly"/> to match your plan.</remarks>
public sealed class MailerSendOptions : EmailProviderOptions;

/// <summary>
/// Sends through MailerSend's API (<c>POST /v1/email</c>) with a bearer key. The message id comes in
/// the <c>x-message-id</c> response header. MailerSend documents no idempotency key, so a lost answer
/// stays <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from MailerSend's email API reference (read 2026-09-26): 202 is accepted; 401 and 403
/// are faults of this account; 422 is a refused message, except a refusal of the sender's domain,
/// which is this account's setup; 429 is throttling, or the daily quota when the message says
/// quota (a lead to confirm against a live account); 5xx is unavailable.
/// </remarks>
public sealed class MailerSendProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.mailersend.com/");
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with <see cref="EmailProviderOptions.ApiKey"/> set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public MailerSendProvider(HttpClient http, MailerSendOptions options)
        : base(http, options, "mailersend")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v1/email");
        _apiKey = options.ApiKey;
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new MailerSendRequest
        {
            From = Contact(message.From),
            To = [.. message.To.Select(Contact)],
            Cc = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Contact)],
            Bcc = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Contact)],
            ReplyTo = message.ReplyTo is null ? null : Contact(message.ReplyTo),
            Subject = message.Subject,
            Text = message.TextBody,
            Html = message.HtmlBody,
            Tags = message.Tags.Count == 0 ? null : [.. message.Tags],
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, MailerSendJson.Default.MailerSendRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var code = (int)status;
        if (code == 202)
        {
            return ProviderResult.Accepted(headers.TryGetValues("x-message-id", out var ids) ? ids.FirstOrDefault() : null);
        }

        var text = ReadMessage(body);
        return code switch
        {
            401 or 403 => ProviderResult.Of(ProviderOutcome.ProviderFault, "MailerSend refused this account (HTTP " + code + ")."),
            422 when Mentions(text, "from.email") || Mentions(text, "domain") =>
                ProviderResult.Of(ProviderOutcome.ProviderFault, "MailerSend refused the sender's domain; verify it in MailerSend."),
            422 => ProviderResult.Of(ProviderOutcome.RecipientRejected, "MailerSend refused the message (HTTP 422)."),
            429 when Mentions(text, "quota") => new ProviderResult(ProviderOutcome.QuotaExhausted) { ExhaustedPeriod = QuotaPeriod.Daily, Reason = "MailerSend reports the daily quota used up." },
            429 => ProviderResult.Of(ProviderOutcome.Throttled, "MailerSend rate limited the request.", RetryAfter(headers)),
            >= 500 => ProviderResult.Of(ProviderOutcome.Unavailable, "MailerSend answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    private static bool Mentions(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private static string? ReadMessage(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, MailerSendJson.Default.MailerSendError)?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MailerSendContact Contact(EmailAddress address) => new() { Email = address.Address, Name = address.DisplayName };
}

internal sealed class MailerSendRequest
{
    public required MailerSendContact From { get; init; }

    public required IReadOnlyList<MailerSendContact> To { get; init; }

    public IReadOnlyList<MailerSendContact>? Cc { get; init; }

    public IReadOnlyList<MailerSendContact>? Bcc { get; init; }

    public MailerSendContact? ReplyTo { get; init; }

    public required string Subject { get; init; }

    public string? Text { get; init; }

    public string? Html { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

internal sealed class MailerSendContact
{
    public required string Email { get; init; }

    public string? Name { get; init; }
}

internal sealed class MailerSendError
{
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MailerSendRequest))]
[JsonSerializable(typeof(MailerSendError))]
internal sealed partial class MailerSendJson : JsonSerializerContext;

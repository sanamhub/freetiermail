using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.Resend;

/// <summary>Options for <see cref="ResendProvider"/>.</summary>
/// <remarks>Resend's free plan allows 3,000 messages a month and 100 a day (checked 2026-09-26). Set <see cref="EmailProviderOptions.Daily"/> and <see cref="EmailProviderOptions.Monthly"/> to match your plan.</remarks>
public sealed class ResendOptions : EmailProviderOptions;

/// <summary>
/// Sends through Resend's API (<c>POST /emails</c>) with a bearer key. The message's
/// <see cref="EmailMessage.IdempotencyKey"/> goes in Resend's <c>Idempotency-Key</c> header, so Resend
/// itself refuses a duplicate within 24 hours.
/// </summary>
/// <remarks>
/// Mapping, from Resend's error reference (read 2026-09-26): 200 is accepted; 429
/// <c>daily_quota_exceeded</c> and <c>monthly_quota_exceeded</c> are quota, other 429s are throttling;
/// 401 and 403 are faults of the account (key, permissions, unverified domain), except 403
/// <c>email_above_quota</c>, which is quota; 400 and 422 are a refused message; 409
/// <c>concurrent_idempotent_requests</c> is unknown, because the other request may send; 5xx is
/// unavailable.
/// </remarks>
public sealed class ResendProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.resend.com/");
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with <see cref="EmailProviderOptions.ApiKey"/> set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ResendProvider(HttpClient http, ResendOptions options)
        : base(http, options, "resend")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "emails");
        _apiKey = options.ApiKey;
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new ResendRequest
        {
            From = Format(message.From),
            To = [.. message.To.Select(Format)],
            Cc = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Format)],
            Bcc = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Format)],
            ReplyTo = message.ReplyTo is null ? null : [Format(message.ReplyTo)],
            Subject = message.Subject,
            Html = message.HtmlBody,
            Text = message.TextBody,
            Tags = message.Tags.Count == 0 ? null : [.. message.Tags.Select(t => new ResendTag { Name = TagName(t), Value = "true" })],
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, ResendJson.Default.ResendRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (message.IdempotencyKey is { } key)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var code = (int)status;
        if (code == 200)
        {
            return ProviderResult.Accepted(Read(body, ResendJson.Default.ResendSuccess)?.Id);
        }

        var name = Read(body, ResendJson.Default.ResendError)?.Name;
        return (code, name) switch
        {
            (429, "daily_quota_exceeded") => new ProviderResult(ProviderOutcome.QuotaExhausted) { ExhaustedPeriod = QuotaPeriod.Daily, Reason = "Resend reports the daily quota used up." },
            (429, "monthly_quota_exceeded") => new ProviderResult(ProviderOutcome.QuotaExhausted) { ExhaustedPeriod = QuotaPeriod.Monthly, Reason = "Resend reports the monthly quota used up." },
            (429, _) => ProviderResult.Of(ProviderOutcome.Throttled, "Resend rate limited the request.", RetryAfter(headers)),
            (403, "email_above_quota") => ProviderResult.Of(ProviderOutcome.QuotaExhausted, "Resend reports the quota used up."),
            (401 or 403, _) => ProviderResult.Of(ProviderOutcome.ProviderFault, "Resend refused this account (" + (name ?? "HTTP " + code) + ")."),
            (409, "concurrent_idempotent_requests") => ProviderResult.Of(ProviderOutcome.Unknown, "Resend is still sending a request with the same idempotency key."),
            (400 or 409 or 422, _) => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Resend refused the message (" + (name ?? "HTTP " + code) + ")."),
            (>= 500, _) => ProviderResult.Of(ProviderOutcome.Unavailable, "Resend answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    private static string Format(EmailAddress address) =>
        address.DisplayName is null ? address.Address : new MailAddress(address.Address, address.DisplayName).ToString();

    // Resend allows ASCII letters, digits, underscores and dashes in tag names.
    private static string TagName(string tag)
    {
        var name = new StringBuilder(Math.Min(tag.Length, 256));
        foreach (var c in tag.AsSpan(0, Math.Min(tag.Length, 256)))
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return name.Length == 0 ? "tag" : name.ToString();
    }

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
}

internal sealed class ResendRequest
{
    public required string From { get; init; }

    public required IReadOnlyList<string> To { get; init; }

    public IReadOnlyList<string>? Cc { get; init; }

    public IReadOnlyList<string>? Bcc { get; init; }

    [JsonPropertyName("reply_to")]
    public IReadOnlyList<string>? ReplyTo { get; init; }

    public required string Subject { get; init; }

    public string? Html { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<ResendTag>? Tags { get; init; }
}

internal sealed class ResendTag
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

internal sealed class ResendSuccess
{
    public string? Id { get; init; }
}

internal sealed class ResendError
{
    public string? Name { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResendRequest))]
[JsonSerializable(typeof(ResendSuccess))]
[JsonSerializable(typeof(ResendError))]
internal sealed partial class ResendJson : JsonSerializerContext;

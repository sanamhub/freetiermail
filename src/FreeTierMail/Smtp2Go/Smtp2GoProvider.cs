using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.Smtp2Go;

/// <summary>Options for <see cref="Smtp2GoProvider"/>.</summary>
/// <remarks>SMTP2GO's free plan allows 1,000 messages a month (checked 2026-09-26). Set <see cref="EmailProviderOptions.Monthly"/> to match your plan.</remarks>
public sealed class Smtp2GoOptions : EmailProviderOptions;

/// <summary>
/// Sends through SMTP2GO's API (<c>POST /v3/email/send</c>) with the key in the
/// <c>X-Smtp2go-Api-Key</c> header. SMTP2GO has no idempotency key, so a lost answer stays
/// <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from SMTP2GO's API reference for sending (read 2026-09-26): 200 with <c>succeeded</c>
/// above zero and <c>failed</c> zero is accepted; SMTP2GO answers 200 for failed messages too, so a
/// 200 with failures is a refused message; 400 <c>ENDPOINT_PERMISSION_DENIED</c>, 401 and 403 are
/// faults of this account; other 400 codes are a refused message; 429 is throttling; 5xx is
/// unavailable. How a used-up quota is reported is not documented, so it is counted locally.
/// </remarks>
public sealed class Smtp2GoProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.smtp2go.com/");
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with <see cref="EmailProviderOptions.ApiKey"/> set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public Smtp2GoProvider(HttpClient http, Smtp2GoOptions options)
        : base(http, options, "smtp2go")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v3/email/send");
        _apiKey = options.ApiKey;
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new Smtp2GoRequest
        {
            Sender = Format(message.From),
            To = [.. message.To.Select(Format)],
            Cc = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Format)],
            Bcc = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Format)],
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            CustomHeaders = message.ReplyTo is null ? null : [new Smtp2GoHeader { Header = "Reply-To", Value = Format(message.ReplyTo) }],
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, Smtp2GoJson.Default.Smtp2GoRequest),
        };
        request.Headers.Add("X-Smtp2go-Api-Key", _apiKey);
        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var code = (int)status;
        var data = Read(body)?.Data;
        if (code == 200)
        {
            return data is { Succeeded: > 0, Failed: 0 or null }
                ? ProviderResult.Accepted(data.EmailId)
                : ProviderResult.Of(ProviderOutcome.RecipientRejected, "SMTP2GO answered 200 but reports the message failed.");
        }

        var error = data?.ErrorCode;
        return code switch
        {
            400 when error?.EndsWith("ENDPOINT_PERMISSION_DENIED", StringComparison.Ordinal) == true =>
                ProviderResult.Of(ProviderOutcome.ProviderFault, "SMTP2GO refused this API key for sending."),
            400 => ProviderResult.Of(ProviderOutcome.RecipientRejected, "SMTP2GO refused the message (" + (error ?? "HTTP 400") + ")."),
            401 or 403 => ProviderResult.Of(ProviderOutcome.ProviderFault, "SMTP2GO refused this account (HTTP " + code + ")."),
            429 => ProviderResult.Of(ProviderOutcome.Throttled, "SMTP2GO rate limited the request.", RetryAfter(headers)),
            >= 500 => ProviderResult.Of(ProviderOutcome.Unavailable, "SMTP2GO answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    private static Smtp2GoResponse? Read(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, Smtp2GoJson.Default.Smtp2GoResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Format(EmailAddress address) =>
        address.DisplayName is null ? address.Address : new MailAddress(address.Address, address.DisplayName).ToString();
}

internal sealed class Smtp2GoRequest
{
    public required string Sender { get; init; }

    public required IReadOnlyList<string> To { get; init; }

    public IReadOnlyList<string>? Cc { get; init; }

    public IReadOnlyList<string>? Bcc { get; init; }

    public required string Subject { get; init; }

    public string? TextBody { get; init; }

    public string? HtmlBody { get; init; }

    public IReadOnlyList<Smtp2GoHeader>? CustomHeaders { get; init; }
}

internal sealed class Smtp2GoHeader
{
    public required string Header { get; init; }

    public required string Value { get; init; }
}

internal sealed class Smtp2GoResponse
{
    public Smtp2GoData? Data { get; init; }
}

internal sealed class Smtp2GoData
{
    public string? EmailId { get; init; }

    public int? Succeeded { get; init; }

    public int? Failed { get; init; }

    public string? ErrorCode { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Smtp2GoRequest))]
[JsonSerializable(typeof(Smtp2GoResponse))]
internal sealed partial class Smtp2GoJson : JsonSerializerContext;

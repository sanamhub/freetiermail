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

namespace FreeTierMail.ElasticEmail;

/// <summary>Options for <see cref="ElasticEmailProvider"/>.</summary>
/// <remarks>Elastic Email's free plan allows 3,000 messages a month and 100 a day, without webhooks (checked 2026-09-26). The key needs the <c>SendHttp</c> access level.</remarks>
public sealed class ElasticEmailOptions : EmailProviderOptions;

/// <summary>
/// Sends through Elastic Email's API v4 (<c>POST /v4/emails/transactional</c>) with the key in the
/// <c>X-ElasticEmail-ApiKey</c> header. Elastic Email documents no idempotency key, so a lost answer
/// stays <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from Elastic Email's REST API reference (read 2026-09-26): 200 is accepted; 400 is a
/// refused message; 401 and 403 are faults of this account; 429 is throttling; 5xx is unavailable.
/// How a used-up quota is reported is not documented, so it is counted locally.
/// </remarks>
public sealed class ElasticEmailProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.elasticemail.com/");
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with <see cref="EmailProviderOptions.ApiKey"/> set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ElasticEmailProvider(HttpClient http, ElasticEmailOptions options)
        : base(http, options, "elasticemail")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v4/emails/transactional");
        _apiKey = options.ApiKey;
    }

    /// <inheritdoc/>
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = new List<ElasticEmailBody>(2);
        if (message.HtmlBody is { } html)
        {
            body.Add(new ElasticEmailBody { ContentType = "HTML", Content = html });
        }

        if (message.TextBody is { } text)
        {
            body.Add(new ElasticEmailBody { ContentType = "PlainText", Content = text });
        }

        var payload = new ElasticEmailRequest
        {
            Recipients = new ElasticEmailRecipients
            {
                To = [.. message.To.Select(Format)],
                CC = message.Cc.Count == 0 ? null : [.. message.Cc.Select(Format)],
                BCC = message.Bcc.Count == 0 ? null : [.. message.Bcc.Select(Format)],
            },
            Content = new ElasticEmailContent
            {
                From = Format(message.From),
                ReplyTo = message.ReplyTo is null ? null : Format(message.ReplyTo),
                Subject = message.Subject,
                Body = body,
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, ElasticEmailJson.Default.ElasticEmailRequest),
        };
        request.Headers.Add("X-ElasticEmail-ApiKey", _apiKey);
        return request;
    }

    /// <inheritdoc/>
    protected override ProviderResult MapResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var code = (int)status;
        return code switch
        {
            200 => ProviderResult.Accepted(ReadId(body)),
            400 => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Elastic Email refused the message (HTTP 400)."),
            401 or 403 => ProviderResult.Of(ProviderOutcome.ProviderFault, "Elastic Email refused the API key or its access level (HTTP " + code + ")."),
            429 => ProviderResult.Of(ProviderOutcome.Throttled, "Elastic Email rate limited the request.", RetryAfter(headers)),
            >= 500 => ProviderResult.Of(ProviderOutcome.Unavailable, "Elastic Email answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    private static string? ReadId(string body)
    {
        try
        {
            var success = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, ElasticEmailJson.Default.ElasticEmailSuccess);
            return success?.MessageID ?? success?.TransactionID;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Format(EmailAddress address) =>
        address.DisplayName is null ? address.Address : new MailAddress(address.Address, address.DisplayName).ToString();
}

internal sealed class ElasticEmailRequest
{
    public required ElasticEmailRecipients Recipients { get; init; }

    public required ElasticEmailContent Content { get; init; }
}

internal sealed class ElasticEmailRecipients
{
    public required IReadOnlyList<string> To { get; init; }

    public IReadOnlyList<string>? CC { get; init; }

    public IReadOnlyList<string>? BCC { get; init; }
}

internal sealed class ElasticEmailContent
{
    public required string From { get; init; }

    public string? ReplyTo { get; init; }

    public required string Subject { get; init; }

    public required IReadOnlyList<ElasticEmailBody> Body { get; init; }
}

internal sealed class ElasticEmailBody
{
    public required string ContentType { get; init; }

    public required string Content { get; init; }

    public string Charset { get; init; } = "utf-8";
}

internal sealed class ElasticEmailSuccess
{
    public string? TransactionID { get; init; }

    public string? MessageID { get; init; }
}

// Elastic Email's JSON uses PascalCase names, which are the C# names.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ElasticEmailRequest))]
[JsonSerializable(typeof(ElasticEmailSuccess))]
internal sealed partial class ElasticEmailJson : JsonSerializerContext;

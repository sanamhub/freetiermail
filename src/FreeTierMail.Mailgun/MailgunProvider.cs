using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeTierMail.Mailgun;

/// <summary>Options for <see cref="MailgunProvider"/>.</summary>
/// <remarks>
/// <see cref="Domain"/> is the sending domain as registered in Mailgun. For an EU account set
/// <see cref="EmailProviderOptions.BaseAddress"/> to <c>https://api.eu.mailgun.net/</c>. The free
/// plan allows 100 messages a day and one custom domain (checked 2026-09-26).
/// </remarks>
public sealed class MailgunOptions : EmailProviderOptions
{
    /// <summary>The sending domain registered in Mailgun, such as <c>mg.example.org</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The key or domain is missing, or a limit is out of range.</exception>
    public override void Validate(string providerName)
    {
        base.Validate(providerName);
        if (string.IsNullOrWhiteSpace(Domain) || Uri.CheckHostName(Domain) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"{providerName} needs Domain, the sending domain registered in Mailgun.");
        }
    }
}

/// <summary>
/// Sends through Mailgun's messages API (<c>POST /v3/{domain}/messages</c>) as multipart form data,
/// with basic authentication as user <c>api</c>. Mailgun has no idempotency key, so a lost answer
/// stays <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mapping, from Mailgun's API reference for sending (read 2026-09-26): 200 is accepted; 400 and 413
/// are a refused message; 401, 403 and 404 (unknown domain) are faults of this account; 429 is
/// throttling; 5xx is unavailable. The reference does not say how a used-up free quota is
/// reported, so it is counted locally; confirm against a live account.
/// </remarks>
public sealed class MailgunProvider : HttpEmailProvider
{
    private static readonly Uri DefaultBaseAddress = new("https://api.mailgun.net/");
    private readonly Uri _endpoint;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The client. The caller owns it.</param>
    /// <param name="options">The options, with the key and domain set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public MailgunProvider(HttpClient http, MailgunOptions options)
        : base(http, options, "mailgun")
    {
        ArgumentNullException.ThrowIfNull(options);
        _endpoint = new Uri(options.BaseAddress ?? DefaultBaseAddress, "v3/" + Uri.EscapeDataString(options.Domain) + "/messages");
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + options.ApiKey)));
    }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Each part belongs to the form, and the base class disposes the request, which disposes the form and its parts.")]
    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var form = new MultipartFormDataContent
        {
            { new StringContent(Format(message.From)), "from" },
            { new StringContent(message.Subject), "subject" },
        };
        foreach (var to in message.To)
        {
            form.Add(new StringContent(Format(to)), "to");
        }

        foreach (var cc in message.Cc)
        {
            form.Add(new StringContent(Format(cc)), "cc");
        }

        foreach (var bcc in message.Bcc)
        {
            form.Add(new StringContent(Format(bcc)), "bcc");
        }

        if (message.ReplyTo is { } replyTo)
        {
            form.Add(new StringContent(Format(replyTo)), "h:Reply-To");
        }

        if (message.TextBody is { } text)
        {
            form.Add(new StringContent(text), "text");
        }

        if (message.HtmlBody is { } html)
        {
            form.Add(new StringContent(html), "html");
        }

        foreach (var tag in message.Tags)
        {
            form.Add(new StringContent(tag), "o:tag");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        request.Headers.Authorization = _authorization;
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
            400 or 413 => ProviderResult.Of(ProviderOutcome.RecipientRejected, "Mailgun refused the message (HTTP " + code + ")."),
            401 or 403 => ProviderResult.Of(ProviderOutcome.ProviderFault, "Mailgun refused the API key or its permissions (HTTP " + code + ")."),
            404 => ProviderResult.Of(ProviderOutcome.ProviderFault, "Mailgun does not know the sending domain; check Domain and the region."),
            429 => ProviderResult.Of(ProviderOutcome.Throttled, "Mailgun rate limited the request.", RetryAfter(headers)),
            >= 500 => ProviderResult.Of(ProviderOutcome.Unavailable, "Mailgun answered HTTP " + code + "."),
            _ => Undocumented(status),
        };
    }

    private static string? ReadId(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, MailgunJson.Default.MailgunSuccess)?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Format(EmailAddress address) =>
        address.DisplayName is null ? address.Address : new MailAddress(address.Address, address.DisplayName).ToString();
}

internal sealed class MailgunSuccess
{
    public string? Id { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MailgunSuccess))]
internal sealed partial class MailgunJson : JsonSerializerContext;

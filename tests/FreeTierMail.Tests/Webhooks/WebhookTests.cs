using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using FreeTierMail.Brevo;
using FreeTierMail.Mailgun;
using FreeTierMail.MailerSend;
using FreeTierMail.Mailjet;
using FreeTierMail.Resend;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreeTierMail.Tests.Webhooks;

/// <summary>
/// Each provider's signature check and event mapping, with payloads shaped as the providers
/// document them. The secrets are made up; nothing calls a provider.
/// </summary>
public sealed class WebhookTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly string NowSeconds = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private readonly InMemorySuppressionStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Resend (Svix) ---------------------------------------------------------------------------

    private static readonly byte[] SvixKey = Encoding.UTF8.GetBytes("test-svix-key-00000000000000000");
    private static readonly string SvixSecret = "whsec_" + Convert.ToBase64String(SvixKey);

    [Fact]
    public async Task Resend_a_signed_permanent_bounce_suppresses_every_recipient()
    {
        const string body = """{"type":"email.bounced","created_at":"2026-09-26T07:59:00Z","data":{"to":["rider@example.com","guide@example.com"],"bounce":{"type":"Permanent","subType":"General"}}}""";

        var result = await Receiver(new ResendWebhook(SvixSecret)).ReceiveAsync("resend", Svix(body, NowSeconds), Ct);

        Assert.Equal(WebhookResult.Accepted, result);
        Assert.Equal(SuppressionReason.HardBounce, (await _store.FindAsync("RIDER@example.com", Ct))!.Reason);
        Assert.NotNull(await _store.FindAsync("guide@example.com", Ct));
    }

    [Fact]
    public async Task Resend_a_transient_bounce_suppresses_nothing_and_a_complaint_does()
    {
        var receiver = Receiver(new ResendWebhook(SvixSecret));

        await receiver.ReceiveAsync("resend", Svix("""{"type":"email.bounced","data":{"to":["rider@example.com"],"bounce":{"type":"Transient"}}}""", NowSeconds), Ct);
        Assert.Null(await _store.FindAsync("rider@example.com", Ct));

        await receiver.ReceiveAsync("resend", Svix("""{"type":"email.complained","data":{"to":["rider@example.com"]}}""", NowSeconds), Ct);
        Assert.Equal(SuppressionReason.Complaint, (await _store.FindAsync("rider@example.com", Ct))!.Reason);
    }

    [Fact]
    public async Task Resend_a_wrong_signature_or_an_old_call_is_refused_unread()
    {
        const string body = """{"type":"email.complained","data":{"to":["rider@example.com"]}}""";
        var receiver = Receiver(new ResendWebhook(SvixSecret));
        var forged = Svix(body, NowSeconds, key: Encoding.UTF8.GetBytes("some-other-key-0000000000000000"));
        var old = Svix(body, Now.AddMinutes(-6).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("resend", forged, Ct));
        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("resend", old, Ct));
        Assert.Null(await _store.FindAsync("rider@example.com", Ct));
    }

    [Fact]
    public void Resend_refuses_a_secret_that_is_not_base64() =>
        Assert.Throws<ArgumentException>(() => new ResendWebhook("whsec_not base64!"));

    // Mailgun ---------------------------------------------------------------------------------

    private const string MailgunKey = "test-mailgun-signing-key-00000000";

    [Theory]
    [InlineData("failed", "permanent", SuppressionReason.HardBounce)]
    [InlineData("complained", null, SuppressionReason.Complaint)]
    public async Task Mailgun_permanent_failures_and_complaints_suppress(string eventName, string? severity, SuppressionReason expected)
    {
        var severityJson = severity is null ? string.Empty : $",\"severity\":\"{severity}\"";
        var body = Mailgun(NowSeconds, $"{{\"event\":\"{eventName}\"{severityJson},\"recipient\":\"rider@example.com\",\"timestamp\":1790409600.5}}");

        Assert.Equal(WebhookResult.Accepted, await Receiver(new MailgunWebhook(MailgunKey)).ReceiveAsync("mailgun", Request(body), Ct));

        Assert.Equal(expected, (await _store.FindAsync("rider@example.com", Ct))!.Reason);
    }

    [Fact]
    public async Task Mailgun_a_temporary_failure_suppresses_nothing()
    {
        var body = Mailgun(NowSeconds, """{"event":"failed","severity":"temporary","recipient":"rider@example.com"}""");

        await Receiver(new MailgunWebhook(MailgunKey)).ReceiveAsync("mailgun", Request(body), Ct);

        Assert.Null(await _store.FindAsync("rider@example.com", Ct));
    }

    [Fact]
    public async Task Mailgun_a_forged_or_old_signature_is_refused()
    {
        var forged = Mailgun(NowSeconds, """{"event":"complained","recipient":"rider@example.com"}""", key: "wrong-key-000000000000000000000");
        var old = Mailgun(Now.AddHours(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), """{"event":"complained","recipient":"rider@example.com"}""");
        var receiver = Receiver(new MailgunWebhook(MailgunKey));

        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("mailgun", Request(forged), Ct));
        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("mailgun", Request(old), Ct));
    }

    // MailerSend ------------------------------------------------------------------------------

    private const string MailerSendSecret = "test-mailersend-secret-000000000";

    [Theory]
    [InlineData("activity.hard_bounced", SuppressionReason.HardBounce)]
    [InlineData("activity.spam_complaint", SuppressionReason.Complaint)]
    public async Task MailerSend_signed_bounces_and_complaints_suppress(string type, SuppressionReason expected)
    {
        var body = $"{{\"type\":\"{type}\",\"created_at\":\"2026-09-26T07:59:00.000000Z\",\"data\":{{\"recipient\":\"rider@example.com\"}}}}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MailerSendSecret), Encoding.UTF8.GetBytes(body)));

        var result = await Receiver(new MailerSendWebhook(MailerSendSecret)).ReceiveAsync("mailersend", Request(body, ("Signature", signature)), Ct);

        Assert.Equal(WebhookResult.Accepted, result);
        Assert.Equal(expected, (await _store.FindAsync("rider@example.com", Ct))!.Reason);
    }

    [Fact]
    public async Task MailerSend_a_changed_body_fails_the_signature()
    {
        const string body = """{"type":"activity.hard_bounced","data":{"recipient":"rider@example.com"}}""";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MailerSendSecret), Encoding.UTF8.GetBytes(body)));

        var result = await Receiver(new MailerSendWebhook(MailerSendSecret))
            .ReceiveAsync("mailersend", Request(body.Replace("rider", "other", StringComparison.Ordinal), ("Signature", signature)), Ct);

        Assert.Equal(WebhookResult.Unauthorized, result);
    }

    [Fact]
    public async Task A_verified_body_that_is_not_json_is_malformed()
    {
        const string body = "not json";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MailerSendSecret), Encoding.UTF8.GetBytes(body)));

        var result = await Receiver(new MailerSendWebhook(MailerSendSecret)).ReceiveAsync("mailersend", Request(body, ("Signature", signature)), Ct);

        Assert.Equal(WebhookResult.Malformed, result);
    }

    // Brevo and Mailjet: shared secrets -------------------------------------------------------

    [Fact]
    public async Task Brevo_needs_the_shared_token_and_maps_hard_bounces_and_spam()
    {
        var receiver = Receiver(new BrevoWebhook("test-brevo-token-0000"));

        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("brevo", Request("""{"event":"spam","email":"rider@example.com"}"""), Ct));
        Assert.Equal(WebhookResult.Accepted, await receiver.ReceiveAsync("brevo", Request("""{"event":"hard_bounce","email":"rider@example.com"}""", ("Authorization", "Bearer test-brevo-token-0000")), Ct));
        Assert.Equal(WebhookResult.Accepted, await receiver.ReceiveAsync("brevo", Request("""{"event":"delivered","email":"guide@example.com"}""", ("Authorization", "Bearer test-brevo-token-0000")), Ct));

        Assert.Equal(SuppressionReason.HardBounce, (await _store.FindAsync("rider@example.com", Ct))!.Reason);
        Assert.Null(await _store.FindAsync("guide@example.com", Ct));
    }

    [Fact]
    public async Task Mailjet_needs_basic_authentication_and_reads_an_array_of_events()
    {
        const string body = """
            [{"event":"bounce","hard_bounce":true,"email":"rider@example.com","time":1790409600},
             {"event":"bounce","hard_bounce":false,"email":"soft@example.com"},
             {"event":"spam","email":"guide@example.com"}]
            """;
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hooks:test-mailjet-password"));
        var receiver = Receiver(new MailjetWebhook("hooks", "test-mailjet-password"));

        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("mailjet", Request(body, ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hooks:wrong")))), Ct));
        Assert.Equal(WebhookResult.Accepted, await receiver.ReceiveAsync("mailjet", Request(body, ("Authorization", basic)), Ct));

        Assert.Equal(SuppressionReason.HardBounce, (await _store.FindAsync("rider@example.com", Ct))!.Reason);
        Assert.Null(await _store.FindAsync("soft@example.com", Ct));
        Assert.Equal(SuppressionReason.Complaint, (await _store.FindAsync("guide@example.com", Ct))!.Reason);
    }

    // The receiver ----------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_provider_and_an_oversized_body_are_refused()
    {
        var receiver = Receiver(new BrevoWebhook("test-brevo-token-0000"));
        var huge = new WebhookRequest([new("Authorization", "Bearer test-brevo-token-0000")], new byte[WebhookReceiver.MaxBodyBytes + 1]);

        Assert.Equal(WebhookResult.UnknownProvider, await receiver.ReceiveAsync("resend", Request("{}"), Ct));
        Assert.Equal(WebhookResult.Unauthorized, await receiver.ReceiveAsync("brevo", huge, Ct));
    }

    [Fact]
    public void Two_webhooks_with_one_name_are_refused() =>
        Assert.Throws<ArgumentException>(() => new WebhookReceiver([new BrevoWebhook("a-token-0000"), new BrevoWebhook("b-token-0000")], _store));

    [Fact]
    public void A_suppression_prints_no_address() =>
        Assert.DoesNotContain("example.com", new Suppression("rider@example.com", SuppressionReason.HardBounce, "resend", Now).ToString(), StringComparison.Ordinal);

    private WebhookReceiver Receiver(EmailWebhook webhook) => new([webhook], _store, _time);

    private static WebhookRequest Request(string body, params (string Name, string Value)[] headers) =>
        new(headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)), Encoding.UTF8.GetBytes(body));

    private static WebhookRequest Svix(string body, string timestamp, byte[]? key = null)
    {
        const string id = "msg_test0000";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key ?? SvixKey, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
        return Request(body, ("svix-id", id), ("svix-timestamp", timestamp), ("svix-signature", $"v1,bm90LXRoaXMtb25l v1,{signature}"));
    }

    private static string Mailgun(string timestamp, string eventData, string key = MailgunKey)
    {
        const string token = "test-token-0000000000000000000000000000000000000000";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(timestamp + token)));
        return $"{{\"signature\":{{\"timestamp\":\"{timestamp}\",\"token\":\"{token}\",\"signature\":\"{signature}\"}},\"event-data\":{eventData}}}";
    }
}

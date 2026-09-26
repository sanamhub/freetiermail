using System.Net;
using System.Security.Cryptography;
using System.Text;
using FreeTierMail;
using FreeTierMail.Brevo;
using FreeTierMail.MailerSend;
using FreeTierMail.Resend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Brevo answers 503, so the mailer fails over to Resend; then the DI path sends through Mailjet
// bound from configuration; last a signed MailerSend webhook suppresses the rider, and the next send
// is stopped. Under Native AOT this exercises every provider's source-generated JSON, the routing,
// configuration binding without reflection, and webhook parsing.
using var http = new HttpClient(new ProviderStub());
var direct = new FreeTierMailer(
[
    new BrevoProvider(http, new BrevoOptions { ApiKey = "test-key-0000000000000000", Daily = 300 }),
    new ResendProvider(http, new ResendOptions { ApiKey = "test-key-0000000000000000", Daily = 100, Monthly = 3000 }),
], new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered });

var message = new EmailMessage(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Sign in")
{
    TextBody = "Open the link to sign in.",
    IdempotencyKey = "sign-in:aot",
};
var first = await direct.SendAsync(message);
if (first is not { Status: SendStatus.Sent, Provider: "resend" })
{
    Console.Error.WriteLine($"FAIL: direct send was {first.Status} through {first.Provider}");
    return 1;
}

var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["FreeTierMail:Providers:mailjet:ApiKey"] = "test-key-0000000000000000",
    ["FreeTierMail:Providers:mailjet:SecretKey"] = "test-secret-000000000000",
    ["FreeTierMail:Providers:mailjet:Daily"] = "200",
}).Build();
var services = new ServiceCollection();
services.AddFreeTierMail(configuration.GetSection("FreeTierMail")).AddMailjet();
services.AddHttpClient("freetiermail.mailjet").ConfigurePrimaryHttpMessageHandler(() => new ProviderStub());
await using var provider = services.BuildServiceProvider();
var second = await provider.GetRequiredService<FreeTierMailer>().SendAsync(message);
if (second is not { Status: SendStatus.Sent, Provider: "mailjet" })
{
    Console.Error.WriteLine($"FAIL: DI send was {second.Status} through {second.Provider}");
    return 1;
}

var store = new InMemorySuppressionStore();
var receiver = new WebhookReceiver([new MailerSendWebhook("test-secret-000000000000")], store);
var hook = """{"type":"activity.hard_bounced","data":{"recipient":"rider@example.com"}}"""u8.ToArray();
var signature = Convert.ToHexStringLower(HMACSHA256.HashData("test-secret-000000000000"u8, hook));
var received = await receiver.ReceiveAsync("mailersend", new WebhookRequest([new("Signature", signature)], hook));
var suppressed = await new FreeTierMailer([new BrevoProvider(http, new BrevoOptions { ApiKey = "test-key-0000000000000000", Daily = 300 })], new FreeTierMailerOptions { SuppressionStore = store })
    .SendAsync(new EmailMessage(message.From, message.To, "Sign in") { TextBody = "Open the link to sign in." });
if (received != WebhookResult.Accepted || !suppressed.Suppressed)
{
    Console.Error.WriteLine($"FAIL: webhook {received}, suppressed {suppressed.Suppressed}");
    return 1;
}

Console.WriteLine("Delivered");
return 0;

sealed class ProviderStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(request.RequestUri!.Host switch
        {
            "api.brevo.com" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "api.resend.com" => Json("{\"id\":\"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794\"}"),
            "api.mailjet.com" => Json("{\"Messages\":[{\"Status\":\"success\",\"To\":[{\"MessageUUID\":\"123\",\"MessageID\":456}]}]}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

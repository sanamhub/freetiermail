using FreeTierMail;
using FreeTierMail.Brevo;
using FreeTierMail.Resend;

namespace FreeTierMail.Samples;

// README: "Send a message".
internal static class SendSample
{
    public static async Task<SendResult> RunAsync(string brevoKey, string resendKey)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var mailer = new FreeTierMailer(
        [
            new BrevoProvider(http, new BrevoOptions { ApiKey = brevoKey, Daily = 300 }),
            new ResendProvider(http, new ResendOptions { ApiKey = resendKey, Daily = 100, Monthly = 3000, PreferForCritical = true }),
        ]);

        var result = await mailer.SendAsync(new EmailMessage(
            from: new EmailAddress("links@example.org", "Example"),
            to: [new EmailAddress("rider@example.com")],
            subject: "Your sign-in link")
        {
            TextBody = "Open this link to sign in: https://example.org/verify?token=...",
            Priority = EmailPriority.Critical,
            IdempotencyKey = "sign-in:4f1c",
        });

        switch (result.Status)
        {
            case SendStatus.Sent: /* result.Provider accepted it */ break;
            case SendStatus.Unknown: /* a provider may have sent it; do not resend on your own */ break;
            case SendStatus.Failed: /* nothing went out; result.Attempts says why at each provider */ break;
        }

        return result;
    }
}

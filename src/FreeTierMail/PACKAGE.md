# FreeTierMail

Sends transactional email through several providers' free tiers as one sender, from .NET. Each
message goes to a provider with quota left, and a failure at one provider moves it to the next
without sending it twice.

This package holds the mailer, the quota tracking, the HTTP providers (Brevo, Resend, Mailjet,
Mailgun, SMTP2GO, MailerSend, Elastic Email) and the registration for
Microsoft.Extensions.DependencyInjection. SMTP relays are in `FreeTierMail.Smtp`, which adds
MailKit.

```csharp
builder.Services.AddFreeTierMail(builder.Configuration.GetSection("FreeTierMail"))
    .AddBrevo()
    .AddResend();
```

API keys come from user secrets or the environment (`FreeTierMail__Providers__brevo__ApiKey`),
never from a committed `appsettings.json`.

**Status: in development.** The API can change until 1.0.

Source, issues and license (MIT): https://github.com/sanamhub/freetiermail

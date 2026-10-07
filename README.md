# FreeTierMail

Send transactional email from .NET through several providers' free tiers as one sender. Each
message goes to a provider with quota left, and a failure at one provider moves the message to the
next without sending it twice.

**Status: in development.** Nothing is published to nuget.org yet, and the API can change
until 1.0.

## Why

A small project that sends sign-in links, receipts and alerts can stay on free tiers for a long
time, but each tier is small and resets on its own clock. Together the permanent free tiers below
come to roughly 900 messages a day. FreeTierMail spends them in the right order, keeps a share for
the mail that matters most, and routes around a provider that is down.

## What it does

- **Quota-aware routing.** Daily and monthly quotas per provider, with their reset times, are
  counted and reserved before each send. By default the quota that resets soonest is spent first:
  a daily quota that resets tonight goes before a monthly one that must last three more weeks.
- **Failover without double sends.** On throttling, a used-up quota, an outage or a problem with
  the account (a revoked key, an unverified domain), the next provider is tried. When a request
  went out and no answer came back, it is not: the result is `Unknown`, unless you opt in.
- **Critical mail first.** `EmailPriority.Critical` messages try the providers you mark first,
  and may use a reserve (10 percent of each quota by default) that normal mail cannot touch.
- **Idempotency.** The same `IdempotencyKey` within 24 hours returns the first result without
  sending. Resend also gets the key in its own header.
- **Honest results.** Every send returns what happened at each provider it tried.
- **Usage.** `GetUsageAsync()` returns used, limit and reset time per quota, and the
  `freetiermail.quota.remaining` gauge exports the same.

## What it is not for

Marketing campaigns, bulk mail, or getting around a provider's limits. Use one account per
provider per sending domain. Opening several free accounts at one provider to multiply its quota
usually breaks that provider's terms.

## Providers

Three packages:

- [`FreeTierMail`](src/FreeTierMail/PACKAGE.md): the mailer, quota tracking, `AddFreeTierMail()`
  for dependency injection, and every HTTP provider below. The providers add no dependency, and
  Native AOT trimming drops the ones you do not register.
- [`FreeTierMail.Smtp`](src/FreeTierMail.Smtp/PACKAGE.md): any SMTP relay, through MailKit, for a
  provider without an API or a paid fallback. Separate so only SMTP users download MailKit.
- [`FreeTierMail.Testing`](src/FreeTierMail.Testing/PACKAGE.md): a fake provider and the contract
  tests every provider passes.

| Provider | Namespace | Free tier, checked 2026-09-26 |
| --- | --- | --- |
| Brevo | `FreeTierMail.Brevo` | 300 a day, shared by marketing and transactional mail (from 2026 reviews; Brevo's page did not load for us) |
| Mailjet | `FreeTierMail.Mailjet` | 6,000 a month, 200 a day |
| Resend | `FreeTierMail.Resend` | 3,000 a month, 100 a day |
| Mailgun | `FreeTierMail.Mailgun` | 100 a day, one custom domain |
| Elastic Email | `FreeTierMail.ElasticEmail` | 3,000 a month, 100 a day |
| SMTP2GO | `FreeTierMail.Smtp2Go` | 1,000 a month |
| MailerSend | `FreeTierMail.MailerSend` | 500 a month, 100 API requests a day |
| Any SMTP relay | `FreeTierMail.Smtp` (package `FreeTierMail.Smtp`) | whatever your relay allows |

Free tiers change without notice. The numbers are configuration, never constants: set `Daily` and
`Monthly` to what your plan says. SendGrid has had no free plan since May 2025, so it is not
listed.

## Install

Not on nuget.org yet. Once it is:

```
dotnet add package FreeTierMail
```

Add `FreeTierMail.Smtp` only if you send through an SMTP relay.

## Send a message

```csharp
using FreeTierMail;
using FreeTierMail.Brevo;
using FreeTierMail.Resend;

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
```

Create one `FreeTierMailer` per set of accounts and keep it: it holds the quota counts. Give the
`HttpClient` a timeout and no retry handler; the mailer decides retries.

## With dependency injection

```csharp
builder.Services.AddFreeTierMail(builder.Configuration.GetSection("FreeTierMail"))
    .AddBrevo()
    .AddResend()
    .AddMailjet();
```

```json
{
  "FreeTierMail": {
    "Strategy": "ExpiringFirst",
    "CriticalReserve": 0.1,
    "Providers": {
      "brevo": { "Daily": 300 },
      "resend": { "Daily": 100, "Monthly": 3000, "PreferForCritical": true },
      "mailjet": { "Daily": 200, "Monthly": 6000 }
    }
  }
}
```

Keys come from user secrets or the environment, for example
`FreeTierMail__Providers__brevo__ApiKey`, never from a committed `appsettings.json`. A provider
with no key stops the host at start, and the message names the setting to fix.

### Switch providers per environment

List every provider in code once and switch them with `Enabled` in configuration. A provider
switched off is not registered: its key is not checked and it gets no quota. The same code then
sends to a local test inbox in development and through real accounts in production.

```csharp
builder.Services.AddFreeTierMail(builder.Configuration.GetSection("FreeTierMail"))
    .AddResend()
    .AddBrevo()
    .AddSmtp("inbox");
```

`appsettings.json`, production: a paid Resend plan first, Brevo's free tier as the fallback. Leave
`Daily` and `Monthly` out for a plan with no limit.

```json
{
  "FreeTierMail": {
    "Strategy": "Ordered",
    "Providers": {
      "resend": { "PreferForCritical": true },
      "brevo": { "Daily": 300 },
      "inbox": { "Enabled": false }
    }
  }
}
```

`appsettings.Development.json`: everything goes to [Mailpit](https://mailpit.axllent.org/) on
your machine. On a loopback host the SMTP provider needs no login and uses TLS only when offered.

```json
{
  "FreeTierMail": {
    "Providers": {
      "resend": { "Enabled": false },
      "brevo": { "Enabled": false },
      "inbox": { "Host": "localhost", "Port": 1025 }
    }
  }
}
```

If every provider is switched off, building the mailer fails and says so.

### Gmail

A Gmail account works as an SMTP provider: `smtp.gmail.com`, port 587, the address as `Username`
and an app password (it needs 2-Step Verification) as `ApiKey`. It is not unlimited: Google allows
about 500 recipients a day on a free account and 2,000 on Google Workspace, and locks sending for
a day when you go over, so set `Daily` below that. The sender must be the account's address or a
verified alias.

Both samples are compiled by CI from [samples/FreeTierMail.Samples](samples/FreeTierMail.Samples).

## Bounces and complaints

Sending again to an address that bounced or complained hurts every account's reputation, and a
free tier is the first thing a provider takes away. A hard bounce or spam complaint reported by
any provider's webhook goes on one suppression list, and the mailer then skips that address at
every provider: `SendResult.Suppressed` is true and no provider is tried.

| Provider | Webhook check | Events that suppress |
| --- | --- | --- |
| Resend | Svix signature (`whsec_` secret), 5-minute replay window | `email.bounced` with bounce type `Permanent`, `email.complained` |
| Mailgun | HMAC of timestamp and token (webhook signing key), 5-minute window | `failed` with severity `permanent`, `complained` |
| MailerSend | HMAC of the body (`Signature` header) | `activity.hard_bounced`, `activity.spam_complaint` |
| Brevo | no signature documented: a bearer token or basic-auth password you set on the webhook | `hard_bounce`, `spam` |
| Mailjet | no signature documented: basic authentication in the webhook URL | `bounce` with `hard_bounce` true, `spam` |

SMTP2GO and Elastic Email webhooks are not read yet. Brevo's and Mailjet's credential and payload
details come from their guides and are still to be confirmed against a live webhook.

```csharp
builder.Services.AddFreeTierMail(builder.Configuration.GetSection("FreeTierMail"))
    .AddBrevo().AddResend()
    .AddWebhook(new ResendWebhook(builder.Configuration["Webhooks:Resend"]!))
    .AddWebhook(new BrevoWebhook(builder.Configuration["Webhooks:Brevo"]!));

// One endpoint for every provider: /webhooks/email/resend, /webhooks/email/brevo.
app.MapPost("/webhooks/email/{provider}", async (string provider, HttpRequest request, WebhookReceiver receiver, CancellationToken cancellationToken) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body, cancellationToken);
    var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString()));
    return await receiver.ReceiveAsync(provider, new WebhookRequest(headers, body.ToArray()), cancellationToken) switch
    {
        WebhookResult.Accepted => Results.Ok(),
        WebhookResult.Unauthorized => Results.Unauthorized(),
        WebhookResult.UnknownProvider => Results.NotFound(),
        _ => Results.BadRequest(),
    };
}).DisableAntiforgery();
```

The list lives in `InMemorySuppressionStore` by default. Implement `ISuppressionStore` over your
database to keep it across restarts and share it between instances. Remove an address with
`RemoveAsync` when the recipient fixes their mailbox.

## Several app instances

The quota counts live in `InMemoryQuotaStore` by default, which is right for one instance. A
restart forgets the day's count, so a restarted app may reach a provider's own limit first; the
mailer then reads the provider's quota error and marks that window used up until it resets.
Instances that share accounts should share a store: implement `IQuotaStore` (reserve, release,
mark exhausted, read usage) over your database and set `FreeTierMailerOptions.QuotaStore`.

## DNS setup

Each provider signs mail with its own DKIM key, so each needs its own DKIM records on your sending
domain. Follow the provider's domain setup page, then check the records with a DNS lookup before
the first send.

- **DKIM:** add every provider's records. This is what makes mail from each of them pass.
- **DMARC:** publish one record, starting with `p=none` and a report address, then tighten it. An
  aligned DKIM signature is enough for DMARC to pass.
- **SPF:** optional here. SPF allows 10 DNS lookups, and four or more providers' `include:`
  entries can exceed it, which makes SPF fail for everyone. With DKIM and DMARC in place, include
  only the providers that need SPF, or none.

## Observability

Add the `FreeTierMail` source to your tracer and the `FreeTierMail` meter to your meter provider.
There is one `freetiermail.send` span per message, counters for sends and provider attempts, and
gauges for quota remaining and limit. No span, tag, metric or log line carries an address,
subject, body or key; a test checks that.

## Security

- API keys are never logged, traced, put in an exception or printed by `ToString`.
- `EmailAddress` accepts one bare address and refuses line breaks, so a header cannot be
  smuggled in through an address, name or subject.
- Webhooks are verified before their body is read, with constant-time comparison; a call that
  fails the check changes nothing. A suppression prints no address.
- Report a vulnerability as [SECURITY.md](SECURITY.md) describes, not in a public issue.

## License

MIT.

# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `FreeTierMail` core: `FreeTierMailer` routes each message by the quota each provider has left
  (the soonest-resetting quota first by default), fails over on throttling, used-up quota,
  outages and account problems, and returns `Unknown` instead of resending when an answer was
  lost. Critical mail has a reserve. Idempotency keys, quota usage, tracing and metrics.
- Providers in the `FreeTierMail` package: Brevo, Resend, Mailjet, Mailgun, SMTP2GO, MailerSend
  and Elastic Email. Each maps the errors its API reference documents; where a reference is
  silent on quota errors, the quota is counted locally.
- `AddFreeTierMail()` in the `FreeTierMail` package, with configuration binding that works under
  Native AOT, and options validated at start.
- `FreeTierMail.Smtp`: any SMTP relay, through MailKit.
- `FreeTierMail.Testing`: a fake provider and the contract tests every provider passes.
- Suppression list: `ISuppressionStore` and `InMemorySuppressionStore`. A suppressed recipient
  stops the send before any provider, and `SendResult.Suppressed` says so.
- Webhooks for Resend, Mailgun and MailerSend (signatures checked) and Brevo and Mailjet (shared
  secret), read by `WebhookReceiver` into the suppression list. `AddWebhook()` registers them.

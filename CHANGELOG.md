# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `FreeTierMail` core: `FreeTierMailer` routes each message by the quota each provider has left
  (the soonest-resetting quota first by default), fails over on throttling, used-up quota,
  outages and account problems, and returns `Unknown` instead of resending when an answer was
  lost. Critical mail has a reserve. Idempotency keys, quota usage, tracing and metrics.
- Providers: Brevo, Resend, Mailjet, Mailgun, SMTP2GO, MailerSend, Elastic Email and any SMTP
  relay. Each maps the errors its API reference documents; where a reference is silent on quota
  errors, the quota is counted locally.
- `FreeTierMail.Extensions.DependencyInjection`: `AddFreeTierMail()` with configuration binding
  that works under Native AOT, and options validated at start.
- `FreeTierMail.Testing`: a fake provider and the contract tests every provider passes.

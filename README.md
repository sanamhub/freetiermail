# FreeTierMail

Send transactional email through several providers' free tiers as one sender, from .NET. Each
message goes to the provider with quota left, and a failure at one provider moves the message to
the next without sending it twice.

**Status: in development.** Nothing is published to nuget.org yet, and the API can change
until 1.0.

## What it is for

- Small projects that send sign-in links, receipts and alerts and want to stay on free tiers.
- Failover: when one provider is down, throttled or out of quota, the next one sends.

## What it is not for

Marketing campaigns, bulk mail, or getting around a provider's limits. Use one account per
provider per sending domain; opening several free accounts at one provider usually breaks its
terms.

## License

MIT.

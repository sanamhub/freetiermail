# FreeTierMail

Sends transactional email through several providers' free tiers as one sender, from .NET. Each
message goes to a provider with quota left, and a failure at one provider moves it to the next
without sending it twice.

**Status: in development.** The API can change until 1.0.

Source, issues and license (MIT): https://github.com/sanamhub/freetiermail

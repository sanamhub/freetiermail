# FreeTierMail.Testing

Test helpers for [FreeTierMail](https://github.com/sanamhub/freetiermail):

- `FakeEmailProvider`: answers from a script of outcomes and records every message, for testing
  code that sends mail.
- `ScriptedHttpHandler`: answers HTTP requests from a script, for provider tests with no network.
- `ProviderContractTests<TProvider>`: xUnit v3 facts every provider should pass. Derive a class,
  give it the provider and its documented responses.

Source, issues and license (MIT): https://github.com/sanamhub/freetiermail

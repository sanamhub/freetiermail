# FreeTierMail.Extensions.DependencyInjection

Registers [FreeTierMail](https://github.com/sanamhub/freetiermail) in
Microsoft.Extensions.DependencyInjection.

```csharp
builder.Services.AddFreeTierMail(builder.Configuration.GetSection("FreeTierMail"))
    .AddBrevo()
    .AddResend();
```

API keys come from user secrets or the environment (`FreeTierMail__Providers__brevo__ApiKey`),
never from a committed `appsettings.json`.

Source, issues and license (MIT): https://github.com/sanamhub/freetiermail

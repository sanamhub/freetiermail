using FreeTierMail.Samples;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Compiled by CI, not run: sending needs real keys. To try it, pass "send" with the keys in
// FREETIERMAIL_BREVO_KEY and FREETIERMAIL_RESEND_KEY.
if (args is ["send"])
{
    var result = await SendSample.RunAsync(
        Environment.GetEnvironmentVariable("FREETIERMAIL_BREVO_KEY") ?? "",
        Environment.GetEnvironmentVariable("FREETIERMAIL_RESEND_KEY") ?? "");
    Console.WriteLine($"{result.Status} through {result.Provider}");
}
else
{
    var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
    using var provider = DependencyInjectionSample.Register(new ServiceCollection(), configuration).BuildServiceProvider();
    Console.WriteLine("Samples compiled. Pass \"send\" with keys in the environment to send one message.");
}

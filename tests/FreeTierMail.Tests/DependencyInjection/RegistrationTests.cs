using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FreeTierMail.Brevo;
using FreeTierMail.Mailjet;
using FreeTierMail.Resend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FreeTierMail.Tests.DependencyInjection;

public sealed class RegistrationTests
{
    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("FreeTierMail");

    [Fact]
    public async Task Providers_and_mailer_options_bind_from_configuration()
    {
        var section = Section(new()
        {
            ["FreeTierMail:Strategy"] = "Ordered",
            ["FreeTierMail:CriticalReserve"] = "0.25",
            ["FreeTierMail:FailoverOnUnknown"] = "true",
            ["FreeTierMail:Providers:brevo:ApiKey"] = "test-key-brevo",
            ["FreeTierMail:Providers:brevo:Daily"] = "300",
            ["FreeTierMail:Providers:resend:ApiKey"] = "test-key-resend",
            ["FreeTierMail:Providers:resend:Daily"] = "100",
            ["FreeTierMail:Providers:resend:Monthly"] = "3000",
            ["FreeTierMail:Providers:resend:PreferForCritical"] = "true",
            ["FreeTierMail:Providers:mailjet:ApiKey"] = "test-key-mailjet",
            ["FreeTierMail:Providers:mailjet:SecretKey"] = "test-secret-mailjet",
            ["FreeTierMail:Providers:mailjet:ResetTimeZone"] = "Asia/Kathmandu",
        });
        var services = new ServiceCollection();
        services.AddFreeTierMail(section).AddBrevo().AddResend().AddMailjet();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var mailer = provider.GetRequiredService<FreeTierMailer>();
        var options = provider.GetRequiredService<IOptions<FreeTierMailerOptions>>().Value;
        var providers = provider.GetServices<IEmailProvider>().ToArray();

        Assert.Equal(["brevo", "resend", "mailjet"], mailer.ProviderNames);
        Assert.Equal(RoutingStrategy.Ordered, options.Strategy);
        Assert.Equal(0.25, options.CriticalReserve);
        Assert.True(options.FailoverOnUnknown);
        Assert.Equal(300, providers[0].Quota.Windows.Single().Limit);
        Assert.Equal(2, providers[1].Quota.Windows.Count);
        Assert.True(providers[1].PreferForCritical);
        Assert.Same(mailer, provider.GetRequiredService<FreeTierMailer>());
    }

    [Fact]
    public async Task A_provider_switched_off_in_configuration_is_not_registered_or_checked()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail(Section(new()
        {
            ["FreeTierMail:Providers:brevo:Enabled"] = "false",
            ["FreeTierMail:Providers:resend:Enabled"] = "true",
            ["FreeTierMail:Providers:resend:ApiKey"] = "test-key-resend",
        })).AddBrevo().AddResend();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Equal(["resend"], provider.GetRequiredService<FreeTierMailer>().ProviderNames);
    }

    [Fact]
    public void Enabled_that_is_not_a_flag_is_refused_at_registration()
    {
        var builder = new ServiceCollection().AddFreeTierMail(Section(new() { ["FreeTierMail:Providers:brevo:Enabled"] = "no" }));

        var error = Assert.Throws<FormatException>(() => builder.AddBrevo());

        Assert.Contains("'no'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_provider_switched_off_says_so_when_the_mailer_is_built()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail(Section(new() { ["FreeTierMail:Providers:brevo:Enabled"] = "false" })).AddBrevo();
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<ArgumentException>(() => provider.GetRequiredService<FreeTierMailer>());

        Assert.Contains("switch every provider off", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_without_a_key_fails_start_naming_the_setting()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail(Section(new() { ["FreeTierMail:Providers:brevo:Daily"] = "300" })).AddBrevo();
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<BrevoOptions>>().Get("brevo"));

        Assert.Contains("FreeTierMail:Providers:brevo", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_configuration_runs_after_the_section()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail(Section(new() { ["FreeTierMail:Providers:resend:ApiKey"] = "from-config" }))
            .AddResend(configure: o => o.Daily = 5);
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<ResendOptions>>().Get("resend");

        Assert.Equal("from-config", options.ApiKey);
        Assert.Equal(5, options.Daily);
    }

    [Fact]
    public async Task Two_accounts_of_one_provider_need_two_names()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail().AddBrevo("brevo-a", o => o.ApiKey = "a").AddBrevo("brevo-b", o => o.ApiKey = "b");
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(["brevo-a", "brevo-b"], provider.GetRequiredService<FreeTierMailer>().ProviderNames);
    }

    [Fact]
    public void An_unknown_strategy_is_refused_with_the_choices()
    {
        var services = new ServiceCollection();
        services.AddFreeTierMail(Section(new() { ["FreeTierMail:Strategy"] = "Cheapest" })).AddMailjet(configure: o => { o.ApiKey = "a"; o.SecretKey = "b"; });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<FreeTierMailer>());

        Assert.Contains("ExpiringFirst", error.Message, StringComparison.Ordinal);
    }
}

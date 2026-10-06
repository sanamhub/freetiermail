using System;
using System.Globalization;
using System.Net.Http;
using FreeTierMail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers FreeTierMail in a service collection.</summary>
public static class FreeTierMailServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton <see cref="FreeTierMailer"/> over the providers added to the returned builder.
    /// Reads <c>Strategy</c>, <c>CriticalReserve</c> and <c>FailoverOnUnknown</c> from
    /// <paramref name="section"/>; each provider reads <c>Providers:&lt;Name&gt;</c> under it, and
    /// is left out when its <c>Enabled</c> is false.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="section">The <c>FreeTierMail</c> configuration section.</param>
    /// <returns>A builder for adding providers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="section"/> is null.</exception>
    public static IFreeTierMailBuilder AddFreeTierMail(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        return AddCore(services, section, options => ApplyMailerSettings(section, options));
    }

    /// <summary>Adds a singleton <see cref="FreeTierMailer"/> with default options.</summary>
    /// <param name="services">The services.</param>
    /// <returns>A builder for adding providers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IFreeTierMailBuilder AddFreeTierMail(this IServiceCollection services) => AddFreeTierMail(services, static _ => { });

    /// <summary>Adds a singleton <see cref="FreeTierMailer"/> configured in code.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Sets the mailer options.</param>
    /// <returns>A builder for adding providers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IFreeTierMailBuilder AddFreeTierMail(this IServiceCollection services, Action<FreeTierMailerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return AddCore(services, section: null, configure);
    }

    private static FreeTierMailBuilder AddCore(IServiceCollection services, IConfigurationSection? section, Action<FreeTierMailerOptions> configure)
    {
        services.AddOptions<FreeTierMailerOptions>().Configure(configure);
        services.TryAddSingleton<IQuotaStore, InMemoryQuotaStore>();
        services.TryAddSingleton<ISuppressionStore, InMemorySuppressionStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FreeTierMailerOptions>>().Value;
            options.QuotaStore ??= provider.GetRequiredService<IQuotaStore>();
            options.SuppressionStore ??= provider.GetRequiredService<ISuppressionStore>();
            options.TimeProvider ??= provider.GetRequiredService<TimeProvider>();
            return new FreeTierMailer(provider.GetServices<IEmailProvider>(), options, provider.GetService<ILogger<FreeTierMailer>>());
        });
        return new FreeTierMailBuilder(services, section);
    }

    // Read by hand instead of ConfigurationBinder, so the mailer binds under Native AOT with no
    // generator and no reflection.
    private static void ApplyMailerSettings(IConfigurationSection section, FreeTierMailerOptions options)
    {
        if (section["Strategy"] is { Length: > 0 } strategy)
        {
            options.Strategy = Enum.TryParse<RoutingStrategy>(strategy, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"FreeTierMail:Strategy '{strategy}' is not one of ExpiringFirst, Ordered, MostRemaining.");
        }

        if (section["CriticalReserve"] is { Length: > 0 } reserve)
        {
            options.CriticalReserve = double.Parse(reserve, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        if (section["FailoverOnUnknown"] is { Length: > 0 } failover)
        {
            options.FailoverOnUnknown = bool.Parse(failover);
        }
    }
}

/// <summary>Adds provider webhooks that feed the mailer's suppression list.</summary>
public static class FreeTierMailWebhookBuilderExtensions
{
    /// <summary>
    /// Adds <paramref name="webhook"/> and a singleton <see cref="WebhookReceiver"/> over every
    /// webhook added, writing to the same <see cref="ISuppressionStore"/> the mailer reads. Map an
    /// endpoint to <see cref="WebhookReceiver.ReceiveAsync"/> (see the README).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="webhook">The provider's webhook, for example a <c>ResendWebhook</c>.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="webhook"/> is null.</exception>
    public static IFreeTierMailBuilder AddWebhook(this IFreeTierMailBuilder builder, EmailWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(webhook);
        builder.Services.AddSingleton(webhook);
        builder.Services.TryAddSingleton(provider => new WebhookReceiver(
            provider.GetServices<EmailWebhook>(),
            provider.GetRequiredService<ISuppressionStore>(),
            provider.GetService<TimeProvider>()));
        return builder;
    }
}

/// <summary>Adds providers to the mailer registered by <see cref="FreeTierMailServiceCollectionExtensions.AddFreeTierMail(IServiceCollection, IConfigurationSection)"/>.</summary>
public interface IFreeTierMailBuilder
{
    /// <summary>The services.</summary>
    IServiceCollection Services { get; }

    /// <summary>The <c>FreeTierMail</c> section, when the mailer was added from configuration.</summary>
    IConfigurationSection? Section { get; }

    /// <summary>
    /// Adds an HTTP provider named <paramref name="name"/>: options built by <paramref name="configure"/>
    /// and validated when the host starts, a named <see cref="HttpClient"/> with request logging
    /// removed, and the provider as a singleton. For provider packages, ours or a third party's.
    /// Nothing is added when <c>Providers:&lt;name&gt;:Enabled</c> is false in <see cref="Section"/>.
    /// </summary>
    /// <typeparam name="TOptions">The provider's options.</typeparam>
    /// <param name="name">The provider's name in the mailer.</param>
    /// <param name="configure">Fills the options, from configuration or code.</param>
    /// <param name="create">Creates the provider from its client and options.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> or <paramref name="create"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    IFreeTierMailBuilder AddHttpProvider<TOptions>(string name, Action<TOptions> configure, Func<HttpClient, TOptions, IEmailProvider> create)
        where TOptions : EmailProviderOptions, new();
}

internal sealed class FreeTierMailBuilder(IServiceCollection services, IConfigurationSection? section) : IFreeTierMailBuilder
{
    public IServiceCollection Services { get; } = services;

    public IConfigurationSection? Section { get; } = section;

    public IFreeTierMailBuilder AddHttpProvider<TOptions>(string name, Action<TOptions> configure, Func<HttpClient, TOptions, IEmailProvider> create)
        where TOptions : EmailProviderOptions, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(create);
        if (!EmailProviderSettings.IsEnabled(Section?.GetSection("Providers").GetSection(name)))
        {
            return this;
        }

        Services.AddOptions<TOptions>(name)
            .Configure(options =>
            {
                options.Name = name;
                configure(options);
            })
            .Validate(options => IsValid(options, name), $"FreeTierMail provider '{name}' has invalid options: an ApiKey is missing, or a limit or time zone is out of range. Check FreeTierMail:Providers:{name}.")
            .ValidateOnStart();

        // IHttpClientFactory logs request URIs at Information by default. No key travels in a URL
        // today, but a provider that put one there must not leak it (ADR-0005).
        var clientName = "freetiermail." + name;
        Services.AddHttpClient(clientName, http => http.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
        Services.AddSingleton(provider => create(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
            provider.GetRequiredService<IOptionsMonitor<TOptions>>().Get(name)));
        return this;
    }

    private static bool IsValid(EmailProviderOptions options, string name)
    {
        try
        {
            options.Validate(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

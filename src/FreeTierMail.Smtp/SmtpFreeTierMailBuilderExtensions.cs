using System;
using System.Globalization;
using FreeTierMail;
using FreeTierMail.Smtp;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds an SMTP relay to FreeTierMail.</summary>
public static class SmtpFreeTierMailBuilderExtensions
{
    /// <summary>
    /// Adds an SMTP provider named <paramref name="name"/>. When the mailer was added from
    /// configuration, <c>Host</c>, <c>Port</c>, <c>Username</c> and the shared settings are read from
    /// <c>Providers:&lt;name&gt;</c> under its section, then <paramref name="configure"/> runs. The
    /// options are validated when the host starts. Nothing is added when the section's
    /// <c>Enabled</c> is false.
    /// </summary>
    /// <param name="builder">The FreeTierMail builder.</param>
    /// <param name="name">The provider's name in the mailer and its configuration key.</param>
    /// <param name="configure">Sets options in code, after configuration; may be null.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static IFreeTierMailBuilder AddSmtp(this IFreeTierMailBuilder builder, string name = "smtp", Action<SmtpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var section = builder.Section?.GetSection("Providers").GetSection(name);
        if (!EmailProviderSettings.IsEnabled(section))
        {
            return builder;
        }

        builder.Services.AddOptions<SmtpOptions>(name)
            .Configure(options =>
            {
                options.Name = name;
                if (section is not null)
                {
                    EmailProviderSettings.Apply(section, options);
                    options.Host = section["Host"] ?? options.Host;
                    options.Username = section["Username"] ?? options.Username;
                    if (section["Port"] is { Length: > 0 } port)
                    {
                        options.Port = int.Parse(port, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }
                }

                configure?.Invoke(options);
            })
            .Validate(options => IsValid(options, name), $"FreeTierMail provider '{name}' has invalid options: Host and Port are required, and Username and ApiKey (the SMTP password) unless the host is loopback. Check FreeTierMail:Providers:{name}.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IEmailProvider>(provider => new SmtpProvider(provider.GetRequiredService<IOptionsMonitor<SmtpOptions>>().Get(name)));
        return builder;
    }

    private static bool IsValid(SmtpOptions options, string name)
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

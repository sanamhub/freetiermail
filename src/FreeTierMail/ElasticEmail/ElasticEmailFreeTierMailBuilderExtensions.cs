using System;
using FreeTierMail;
using FreeTierMail.ElasticEmail;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds Elastic Email to FreeTierMail.</summary>
public static class ElasticEmailFreeTierMailBuilderExtensions
{
    /// <summary>
    /// Adds an Elastic Email provider named <paramref name="name"/>. When the mailer was added from
    /// configuration, the options are read from <c>Providers:&lt;name&gt;</c> under its section, then
    /// <paramref name="configure"/> runs. The options are validated when the host starts.
    /// </summary>
    /// <param name="builder">The FreeTierMail builder.</param>
    /// <param name="name">The provider's name in the mailer and its configuration key.</param>
    /// <param name="configure">Sets options in code, after configuration; may be null.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static IFreeTierMailBuilder AddElasticEmail(this IFreeTierMailBuilder builder, string name = "elasticemail", Action<ElasticEmailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var section = builder.Section?.GetSection("Providers").GetSection(name);
        return builder.AddHttpProvider<ElasticEmailOptions>(
            name,
            options =>
            {
                if (section is not null)
                {
                    EmailProviderSettings.Apply(section, options);
                }

                configure?.Invoke(options);
            },
            (http, options) => new ElasticEmailProvider(http, options));
    }
}

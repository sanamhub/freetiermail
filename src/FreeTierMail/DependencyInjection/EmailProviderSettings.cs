using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace FreeTierMail;

/// <summary>Reads the settings every provider shares from configuration, without reflection, so binding works under Native AOT.</summary>
public static class EmailProviderSettings
{
    /// <summary>
    /// Copies <c>ApiKey</c>, <c>Daily</c>, <c>Monthly</c>, <c>ResetTimeZone</c>,
    /// <c>MonthlyResetDay</c>, <c>PreferForCritical</c> and <c>BaseAddress</c> from
    /// <paramref name="section"/> into <paramref name="options"/>. Absent keys leave the option as is.
    /// </summary>
    /// <param name="section">The provider's section, such as <c>FreeTierMail:Providers:Brevo</c>.</param>
    /// <param name="options">The options to fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="section"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="FormatException">A number, flag or address does not parse.</exception>
    public static void Apply(IConfiguration section, EmailProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);

        if (section["ApiKey"] is { } key)
        {
            options.ApiKey = key;
        }

        if (section["Daily"] is { Length: > 0 } daily)
        {
            options.Daily = int.Parse(daily, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        if (section["Monthly"] is { Length: > 0 } monthly)
        {
            options.Monthly = int.Parse(monthly, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        if (section["ResetTimeZone"] is { Length: > 0 } zone)
        {
            options.ResetTimeZone = zone;
        }

        if (section["MonthlyResetDay"] is { Length: > 0 } day)
        {
            options.MonthlyResetDay = int.Parse(day, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        if (section["PreferForCritical"] is { Length: > 0 } critical)
        {
            options.PreferForCritical = bool.Parse(critical);
        }

        if (section["BaseAddress"] is { Length: > 0 } address)
        {
            options.BaseAddress = Uri.TryCreate(address, UriKind.Absolute, out var uri)
                ? uri
                : throw new FormatException("BaseAddress is not an absolute URL.");
        }
    }
}

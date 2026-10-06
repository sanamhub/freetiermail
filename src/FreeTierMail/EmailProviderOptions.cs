using System;

namespace FreeTierMail;

/// <summary>Options every provider shares: its name, key and quotas.</summary>
public abstract class EmailProviderOptions
{
    /// <summary>The provider's name in the mailer. Defaults to the provider type's name, such as <c>brevo</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The API key. A secret: read it from user secrets or the environment, never from a committed file.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Messages a day on this account, or null for no daily limit.</summary>
    public int? Daily { get; set; }

    /// <summary>Messages a month on this account, or null for no monthly limit.</summary>
    public int? Monthly { get; set; }

    /// <summary>The time zone (IANA or Windows id) whose midnight resets the quotas. UTC when null.</summary>
    public string? ResetTimeZone { get; set; }

    /// <summary>The day of the month the monthly quota resets, 1 to 28.</summary>
    public int MonthlyResetDay { get; set; } = 1;

    /// <summary>True to try this provider first for critical mail.</summary>
    public bool PreferForCritical { get; set; }

    /// <summary>Overrides the provider's API address, for a regional endpoint or a test server.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The quota plan these options describe.</summary>
    /// <returns>The plan.</returns>
    /// <exception cref="TimeZoneNotFoundException"><see cref="ResetTimeZone"/> names no known time zone.</exception>
    public QuotaPlan ToQuotaPlan() =>
        QuotaPlan.From(Daily, Monthly, ResetTimeZone is null ? null : TimeZoneInfo.FindSystemTimeZoneById(ResetTimeZone), MonthlyResetDay);

    /// <summary>True when the provider cannot send without <see cref="ApiKey"/>. An SMTP relay on loopback, such as a local test inbox, needs none.</summary>
    protected virtual bool RequiresApiKey => true;

    /// <summary>Returns the name and quotas. Never the key.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() => $"{GetType().Name}(Name: {Name}, ApiKey: ***, Daily: {Daily}, Monthly: {Monthly})";

    /// <summary>Checks the options a provider needs before its first send. Providers call it in their constructor; the DI package calls it at start.</summary>
    /// <param name="providerName">The provider, for the message.</param>
    /// <exception cref="ArgumentException">The key is missing, a limit or reset day is out of range, or the time zone is unknown.</exception>
    public virtual void Validate(string providerName)
    {
        if (RequiresApiKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException($"{providerName} needs an ApiKey.");
        }

        if (Daily is <= 0 || Monthly is <= 0 || MonthlyResetDay is < 1 or > 28)
        {
            throw new ArgumentException($"{providerName}: Daily and Monthly are positive when set, and MonthlyResetDay is 1 to 28.");
        }

        if (ResetTimeZone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(ResetTimeZone, out _))
        {
            throw new ArgumentException($"{providerName}: ResetTimeZone '{ResetTimeZone}' is not a known time zone id.");
        }
    }
}

using System;

namespace FreeTierMail;

/// <summary>How the mailer orders the providers that have quota left.</summary>
public enum RoutingStrategy
{
    /// <summary>
    /// The provider whose nearest quota resets soonest goes first, so a daily quota that resets
    /// tonight is spent before a monthly one that must last weeks. Ties keep the configured order;
    /// providers with no quota go last.
    /// </summary>
    ExpiringFirst = 0,

    /// <summary>The configured order.</summary>
    Ordered = 1,

    /// <summary>The provider with the largest share of its tightest window left goes first.</summary>
    MostRemaining = 2,
}

/// <summary>Options for <see cref="FreeTierMailer"/>.</summary>
public sealed class FreeTierMailerOptions
{
    /// <summary>How providers are ordered. <see cref="RoutingStrategy.ExpiringFirst"/> by default.</summary>
    public RoutingStrategy Strategy { get; set; } = RoutingStrategy.ExpiringFirst;

    /// <summary>The share of each quota kept for <see cref="EmailPriority.Critical"/> mail, 0 to 1. 0.1 by default.</summary>
    public double CriticalReserve { get; set; } = 0.1;

    /// <summary>
    /// When true, a provider whose answer was lost does not stop the send: the next provider is tried,
    /// and the recipient may get the message twice. False by default.
    /// </summary>
    public bool FailoverOnUnknown { get; set; }

    /// <summary>Where quota use is counted. A new <see cref="InMemoryQuotaStore"/> when null.</summary>
    public IQuotaStore? QuotaStore { get; set; }

    /// <summary>The clock. <see cref="TimeProvider.System"/> when null.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary><see cref="ProviderOutcome.Unavailable"/> answers within <see cref="CircuitBreakerWindow"/> that open the circuit. 3 by default.</summary>
    public int CircuitBreakerFailures { get; set; } = 3;

    /// <summary>The window in which failures are counted. 5 minutes by default.</summary>
    public TimeSpan CircuitBreakerWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an open circuit skips the provider. 5 minutes by default.</summary>
    public TimeSpan CircuitBreakerOpenFor { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a throttled provider is skipped when it gave no Retry-After. 1 minute by default.</summary>
    public TimeSpan DefaultThrottleWait { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a result is kept for its idempotency key. 24 hours by default.</summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The most idempotency keys kept; the oldest go first. 10,000 by default.</summary>
    public int IdempotencyCapacity { get; set; } = 10_000;

    internal void Validate()
    {
        if (CriticalReserve is < 0 or > 1 || double.IsNaN(CriticalReserve))
        {
            throw new ArgumentException("CriticalReserve is between 0 and 1.");
        }

        if (CircuitBreakerFailures < 1 || IdempotencyCapacity < 1)
        {
            throw new ArgumentException("CircuitBreakerFailures and IdempotencyCapacity are at least 1.");
        }

        if (CircuitBreakerWindow <= TimeSpan.Zero || CircuitBreakerOpenFor <= TimeSpan.Zero || DefaultThrottleWait <= TimeSpan.Zero || IdempotencyRetention <= TimeSpan.Zero)
        {
            throw new ArgumentException("Every duration option is positive.");
        }
    }
}

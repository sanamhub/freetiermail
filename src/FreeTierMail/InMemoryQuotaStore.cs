using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>
/// Counts quota use in this process. Right for one app instance. A restart forgets the counts, so
/// a restarted app may reach a provider's own limit first; the mailer then marks the window
/// exhausted from the provider's answer.
/// </summary>
public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="criticalReserve"/> is outside 0 to 1.</exception>
    public Task<QuotaReservation?> TryReserveAsync(string provider, QuotaPlan plan, EmailPriority priority, double criticalReserve, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(criticalReserve, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(criticalReserve, 1);

        lock (_lock)
        {
            var counters = new Counter[plan.Windows.Count];
            for (var i = 0; i < plan.Windows.Count; i++)
            {
                var window = plan.Windows[i];
                var counter = CurrentCounter(provider, window, now);
                if (counter.Exhausted || counter.Used >= window.AllowanceFor(priority, criticalReserve))
                {
                    return Task.FromResult<QuotaReservation?>(null);
                }

                counters[i] = counter;
            }

            var keys = new string[counters.Length];
            for (var i = 0; i < counters.Length; i++)
            {
                counters[i].Used++;
                keys[i] = counters[i].Key;
            }

            return Task.FromResult<QuotaReservation?>(new QuotaReservation(provider, keys));
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="reservation"/> is null.</exception>
    public Task ReleaseAsync(QuotaReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_lock)
        {
            foreach (var key in reservation.Keys)
            {
                // A window that reset since the reservation has a new key; nothing to give back.
                if (_counters.TryGetValue(key[..key.LastIndexOf(':')], out var counter) && counter.Key == key && counter.Used > 0)
                {
                    counter.Used--;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="window"/> is null.</exception>
    public Task MarkExhaustedAsync(string provider, QuotaWindow window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(window);
        lock (_lock)
        {
            CurrentCounter(provider, window, now).Exhausted = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="plan"/> is null.</exception>
    public Task<IReadOnlyList<QuotaUsage>> GetUsageAsync(string provider, QuotaPlan plan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);
        lock (_lock)
        {
            var usage = new QuotaUsage[plan.Windows.Count];
            for (var i = 0; i < usage.Length; i++)
            {
                var window = plan.Windows[i];
                var counter = CurrentCounter(provider, window, now);
                usage[i] = new QuotaUsage(provider, window.Period, window.Limit, counter.Used, window.NextReset(now), counter.Exhausted);
            }

            return Task.FromResult<IReadOnlyList<QuotaUsage>>(usage);
        }
    }

    // One counter per provider and period, replaced when the window's key changes, so old windows
    // never pile up.
    private Counter CurrentCounter(string provider, QuotaWindow window, DateTimeOffset now)
    {
        var slot = provider + ":" + (window.Period == QuotaPeriod.Daily ? "daily" : "monthly");
        var key = provider + ":" + window.KeyFor(now);
        if (!_counters.TryGetValue(slot, out var counter) || counter.Key != key)
        {
            counter = new Counter(key);
            _counters[slot] = counter;
        }

        return counter;
    }

    private sealed class Counter(string key)
    {
        public string Key { get; } = key;

        public int Used { get; set; }

        public bool Exhausted { get; set; }
    }
}

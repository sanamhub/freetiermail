using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>
/// Counts quota use. Share one store between app instances that send through the same provider
/// accounts, so together they never pass a limit.
/// </summary>
public interface IQuotaStore
{
    /// <summary>
    /// Takes one unit in every window of <paramref name="plan"/>, atomically: either all windows have
    /// room for a message of <paramref name="priority"/> and each takes a unit, or none changes.
    /// </summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="plan">Its quotas.</param>
    /// <param name="priority">The message's priority.</param>
    /// <param name="criticalReserve">The share of each window kept for critical mail, 0 to 1.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The reservation, or null when a window has no room.</returns>
    Task<QuotaReservation?> TryReserveAsync(string provider, QuotaPlan plan, EmailPriority priority, double criticalReserve, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Gives back a reservation's units, for a send that certainly did not happen.</summary>
    /// <param name="reservation">The reservation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task ReleaseAsync(QuotaReservation reservation, CancellationToken cancellationToken);

    /// <summary>Marks a window used up until it resets, because the provider said so.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="window">The window.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task MarkExhaustedAsync(string provider, QuotaWindow window, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Reads the use of every window in <paramref name="plan"/>.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="plan">Its quotas.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One entry per window.</returns>
    Task<IReadOnlyList<QuotaUsage>> GetUsageAsync(string provider, QuotaPlan plan, DateTimeOffset now, CancellationToken cancellationToken);
}

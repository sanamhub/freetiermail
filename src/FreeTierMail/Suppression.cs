using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>Why an address no longer gets mail.</summary>
public enum SuppressionReason
{
    /// <summary>A provider reported a permanent delivery failure: the mailbox or domain does not exist.</summary>
    HardBounce = 0,

    /// <summary>The recipient marked a message as spam.</summary>
    Complaint = 1,

    /// <summary>Added by the application, for example on an unsubscribe.</summary>
    Manual = 2,
}

/// <summary>An address that no provider sends to, and why.</summary>
/// <param name="Address">The address, compared without regard to case.</param>
/// <param name="Reason">Why it is suppressed.</param>
/// <param name="Provider">The provider that reported it, or null when added by the application.</param>
/// <param name="At">When it was reported.</param>
public sealed record Suppression(string Address, SuppressionReason Reason, string? Provider, DateTimeOffset At)
{
    /// <summary>Never the address: it is personal data and would reach logs.</summary>
    /// <returns>The reason and provider only.</returns>
    public override string ToString() => $"Suppression({Reason}, {Provider ?? "manual"})";
}

/// <summary>
/// Addresses that every provider must skip (PLAN phase 2). A hard bounce or complaint reported by
/// one provider stops sends through all of them, which is what keeps each account's sender
/// reputation, and so its free tier.
/// </summary>
public interface ISuppressionStore
{
    /// <summary>The suppression for <paramref name="address"/>, or null.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The suppression, or null.</returns>
    ValueTask<Suppression?> FindAsync(string address, CancellationToken cancellationToken);

    /// <summary>Adds or replaces the suppression for its address.</summary>
    /// <param name="suppression">The suppression.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask AddAsync(Suppression suppression, CancellationToken cancellationToken);

    /// <summary>Removes the suppression, for example after the recipient fixed their mailbox.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True if one was removed.</returns>
    ValueTask<bool> RemoveAsync(string address, CancellationToken cancellationToken);
}

/// <summary>A process-local <see cref="ISuppressionStore"/>. Lost on restart: persist it for production.</summary>
public sealed class InMemorySuppressionStore : ISuppressionStore
{
    private readonly ConcurrentDictionary<string, Suppression> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask<Suppression?> FindAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return ValueTask.FromResult(_entries.TryGetValue(address.Trim(), out var found) ? found : null);
    }

    /// <inheritdoc />
    public ValueTask AddAsync(Suppression suppression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.Address);
        _entries[suppression.Address.Trim()] = suppression;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return ValueTask.FromResult(_entries.TryRemove(address.Trim(), out _));
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail.Testing;

/// <summary>
/// A provider that answers from a script of results, in order, then accepts everything. Records
/// every message it is given. For testing code that sends through <see cref="FreeTierMailer"/>.
/// Thread-safe.
/// </summary>
public sealed class FakeEmailProvider : IEmailProvider
{
    private readonly ConcurrentQueue<ProviderResult> _script = new();
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private int _accepted;

    /// <summary>Creates a fake provider.</summary>
    /// <param name="name">Its name in the mailer.</param>
    /// <param name="quota">Its quotas; unlimited when null.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public FakeEmailProvider(string name = "fake", QuotaPlan? quota = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Quota = quota ?? QuotaPlan.Unlimited;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public QuotaPlan Quota { get; }

    /// <inheritdoc/>
    public bool PreferForCritical { get; init; }

    /// <summary>Every message given to <see cref="SendAsync"/>, in order, whatever the scripted answer.</summary>
    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    /// <summary>Queues results for the next sends.</summary>
    /// <param name="results">The results, in order.</param>
    /// <returns>This provider, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is null.</exception>
    public FakeEmailProvider Then(params ProviderResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            _script.Enqueue(result);
        }

        return this;
    }

    /// <summary>Queues the same outcome <paramref name="times"/> times.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <param name="times">How many sends get it.</param>
    /// <returns>This provider, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
    public FakeEmailProvider Then(ProviderOutcome outcome, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(times);
        for (var i = 0; i < times; i++)
        {
            _script.Enqueue(new ProviderResult(outcome) { Reason = "Scripted " + outcome + "." });
        }

        return this;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public Task<ProviderResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        _sent.Enqueue(message);
        return Task.FromResult(_script.TryDequeue(out var next)
            ? next
            : ProviderResult.Accepted(Name + "-" + Interlocked.Increment(ref _accepted)));
    }
}

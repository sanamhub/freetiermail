using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeTierMail;

/// <summary>
/// Sends each message through one of several providers, chosen by the quota each has left, and
/// moves to the next provider when one certainly did not send (ADR-0004). Thread safe; create one
/// per set of provider accounts and keep it for the life of the app.
/// </summary>
public sealed partial class FreeTierMailer
{
    private readonly IReadOnlyList<ProviderState> _providers;
    private readonly FreeTierMailerOptions _options;
    private readonly IQuotaStore _quota;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IdempotencyCache _idempotency;
    private IReadOnlyList<QuotaUsage> _lastUsage = [];

    /// <summary>Creates a mailer.</summary>
    /// <param name="providers">The providers, in the order used for ties and by <see cref="RoutingStrategy.Ordered"/>.</param>
    /// <param name="options">Options; defaults when null.</param>
    /// <param name="logger">A logger; none when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
    /// <exception cref="ArgumentException">No providers, a null provider, two providers with one name, or an option out of range.</exception>
    public FreeTierMailer(IEnumerable<IEmailProvider> providers, FreeTierMailerOptions? options = null, ILogger<FreeTierMailer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var list = providers.ToArray();
        if (list.Length == 0 || Array.Exists(list, p => p is null))
        {
            throw new ArgumentException("A mailer needs at least one provider, and none may be null.", nameof(providers));
        }

        var duplicate = list.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two providers are named '{duplicate.Key}'. Names key the quota counts, so each must be unique.", nameof(providers));
        }

        _options = options ?? new FreeTierMailerOptions();
        _options.Validate();
        _providers = [.. list.Select((p, i) => new ProviderState(p, i))];
        _quota = _options.QuotaStore ?? new InMemoryQuotaStore();
        _time = _options.TimeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _idempotency = new IdempotencyCache(_options.IdempotencyCapacity, _options.IdempotencyRetention, _time);
        QuotaGauges.Register(this);
    }

    /// <summary>The usage read after the last send, for the quota gauges. Empty until a send while a meter listens.</summary>
    internal IReadOnlyList<QuotaUsage> LastUsage => _lastUsage;

    /// <summary>The providers' names, in configured order.</summary>
    public IReadOnlyList<string> ProviderNames => [.. _providers.Select(p => p.Provider.Name)];

    /// <summary>
    /// Sends <paramref name="message"/>. Never throws for a provider's answer: every outcome is in
    /// the result. A repeat with the same <see cref="EmailMessage.IdempotencyKey"/> returns the first
    /// sent or unknown result without sending.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the send. A provider already called may still send.</param>
    /// <returns>What happened at each provider tried.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The message breaks a rule; the message says which.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();
        return message.IdempotencyKey is { } key
            ? _idempotency.GetOrSendAsync(key, () => SendCoreAsync(message, cancellationToken))
            : SendCoreAsync(message, cancellationToken);
    }

    /// <summary>Reads the use of every quota of every provider.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One entry per window, providers in configured order.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<QuotaUsage>> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var usage = new List<QuotaUsage>();
        foreach (var state in _providers)
        {
            usage.AddRange(await _quota.GetUsageAsync(state.Provider.Name, state.Provider.Quota, now, cancellationToken).ConfigureAwait(false));
        }

        return usage;
    }

    /// <summary>Turns a provider back on after <see cref="ProviderOutcome.ProviderFault"/> disabled it, for example once its key is fixed.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <exception cref="ArgumentException">No provider has that name.</exception>
    public void Enable(string provider)
    {
        var state = _providers.FirstOrDefault(p => p.Provider.Name == provider)
            ?? throw new ArgumentException("No provider has that name.", nameof(provider));
        state.Enable();
    }

    private async Task<SendResult> SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var activity = FreeTierMailDiagnostics.Source.StartActivity(FreeTierMailDiagnostics.SendActivityName, ActivityKind.Client);
        var result = await IsSuppressedAsync(message, cancellationToken).ConfigureAwait(false)
            ? new SendResult(SendStatus.Failed, []) { Suppressed = true }
            : await RouteAsync(message, cancellationToken).ConfigureAwait(false);

        var status = result.Suppressed ? "suppressed" : StatusName(result.Status);
        foreach (var attempt in result.Attempts)
        {
            FreeTierMailDiagnostics.Attempts.Add(1, new(FreeTierMailDiagnostics.ProviderTag, attempt.Provider), new(FreeTierMailDiagnostics.StatusTag, OutcomeName(attempt.Outcome)));
        }

        FreeTierMailDiagnostics.Sends.Add(1, new(FreeTierMailDiagnostics.ProviderTag, result.Provider ?? "none"), new(FreeTierMailDiagnostics.StatusTag, status));
        if (activity is not null)
        {
            activity.SetTag(FreeTierMailDiagnostics.ProviderTag, result.Provider);
            activity.SetTag(FreeTierMailDiagnostics.StatusTag, status);
            activity.SetTag(FreeTierMailDiagnostics.AttemptsTag, result.Attempts.Count);
            activity.SetStatus(result.Status == SendStatus.Sent ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        if (QuotaGauges.Enabled)
        {
            await RefreshUsageAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    // One suppressed recipient stops the whole message: the others would share its envelope.
    private async Task<bool> IsSuppressedAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (_options.SuppressionStore is not { } store)
        {
            return false;
        }

        foreach (var recipient in message.To.Concat(message.Cc).Concat(message.Bcc))
        {
            if (await store.FindAsync(recipient.Address, cancellationToken).ConfigureAwait(false) is not null)
            {
                LogSuppressed(_logger);
                return true;
            }
        }

        return false;
    }

    private async Task<SendResult> RouteAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var attempts = new List<SendAttempt>();
        string? unknownProvider = null;

        foreach (var state in await OrderAsync(message.Priority, cancellationToken).ConfigureAwait(false))
        {
            var now = _time.GetUtcNow();
            var name = state.Provider.Name;
            if (state.SkipReason(now) is { } skip)
            {
                attempts.Add(new SendAttempt(name, skip.Outcome, TimeSpan.Zero, skip.Reason));
                continue;
            }

            var reservation = await _quota.TryReserveAsync(name, state.Provider.Quota, message.Priority, _options.CriticalReserve, now, cancellationToken).ConfigureAwait(false);
            if (reservation is null)
            {
                attempts.Add(new SendAttempt(name, ProviderOutcome.QuotaExhausted, TimeSpan.Zero, "No quota left in this window."));
                continue;
            }

            var started = _time.GetTimestamp();
            var result = await CallAsync(state, message, cancellationToken).ConfigureAwait(false);
            var duration = _time.GetElapsedTime(started);
            attempts.Add(new SendAttempt(name, result.Outcome, duration, result.Reason));

            switch (result.Outcome)
            {
                case ProviderOutcome.Accepted:
                    state.RecordSuccess();
                    LogSent(_logger, name, attempts.Count);
                    return new SendResult(SendStatus.Sent, attempts) { Provider = name, ProviderMessageId = result.ProviderMessageId };

                case ProviderOutcome.RecipientRejected:
                    await _quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
                    LogFailed(_logger, attempts.Count, result.Outcome);
                    return new SendResult(SendStatus.Failed, attempts);

                case ProviderOutcome.Throttled:
                    await _quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
                    state.ThrottleUntil(_time.GetUtcNow() + (result.RetryAfter ?? _options.DefaultThrottleWait));
                    break;

                case ProviderOutcome.QuotaExhausted:
                    await _quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
                    await MarkExhaustedAsync(state, result.ExhaustedPeriod, cancellationToken).ConfigureAwait(false);
                    break;

                case ProviderOutcome.ProviderFault:
                    await _quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
                    state.Disable();
                    LogDisabled(_logger, name);
                    break;

                case ProviderOutcome.Unavailable:
                    await _quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
                    state.RecordFailure(_time.GetUtcNow(), _options);
                    break;

                case ProviderOutcome.Unknown:
                default:
                    // The message may have gone out, so the unit stays counted.
                    unknownProvider ??= name;
                    if (!_options.FailoverOnUnknown)
                    {
                        LogUnknown(_logger, name);
                        return new SendResult(SendStatus.Unknown, attempts) { Provider = name };
                    }

                    break;
            }

            LogFailover(_logger, name, result.Outcome);
        }

        if (unknownProvider is not null)
        {
            LogUnknown(_logger, unknownProvider);
            return new SendResult(SendStatus.Unknown, attempts) { Provider = unknownProvider };
        }

        LogFailed(_logger, attempts.Count, attempts.Count == 0 ? ProviderOutcome.Unavailable : attempts[^1].Outcome);
        return new SendResult(SendStatus.Failed, attempts);
    }

    private async Task<ProviderResult> CallAsync(ProviderState state, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await state.Provider.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider must not throw; if one does, whether it sent is unknown, and the batch must not die.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The exception type only: a provider's message can echo the request.
            LogProviderThrew(_logger, state.Provider.Name, ex.GetType().FullName ?? ex.GetType().Name);
            return ProviderResult.Of(ProviderOutcome.Unknown, "The provider threw " + ex.GetType().Name + "; whether it sent is unknown.");
        }
    }

    private Task MarkExhaustedAsync(ProviderState state, QuotaPeriod? period, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var windows = state.Provider.Quota.Windows;
        var window = windows.FirstOrDefault(w => w.Period == period)
            ?? windows.OrderBy(w => w.NextReset(now)).FirstOrDefault();
        if (window is null)
        {
            // No configured quota, yet the provider says one is used up: skip it for an hour.
            state.ThrottleUntil(now + TimeSpan.FromHours(1));
            return Task.CompletedTask;
        }

        return _quota.MarkExhaustedAsync(state.Provider.Name, window, now, cancellationToken);
    }

    private async Task<IReadOnlyList<ProviderState>> OrderAsync(EmailPriority priority, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        IEnumerable<ProviderState> ordered = _options.Strategy switch
        {
            RoutingStrategy.Ordered => _providers,
            RoutingStrategy.MostRemaining => await ByRemainingAsync(now, cancellationToken).ConfigureAwait(false),
            _ => _providers.OrderBy(p => NearestReset(p.Provider.Quota, now)).ThenBy(p => p.Index),
        };

        // OrderBy is stable, so the strategy's order holds within each group.
        return priority == EmailPriority.Critical
            ? [.. ordered.OrderBy(p => p.Provider.PreferForCritical ? 0 : 1)]
            : [.. ordered];
    }

    private async Task<IEnumerable<ProviderState>> ByRemainingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var shares = new List<(ProviderState State, double Share)>();
        foreach (var state in _providers)
        {
            var usage = await _quota.GetUsageAsync(state.Provider.Name, state.Provider.Quota, now, cancellationToken).ConfigureAwait(false);
            var share = usage.Count == 0 ? 1.0 : usage.Min(u => u.Exhausted ? 0 : Math.Max(0, u.Limit - u.Used) / (double)u.Limit);
            shares.Add((state, share));
        }

        return shares.OrderByDescending(s => s.Share).ThenBy(s => s.State.Index).Select(s => s.State);
    }

    private async Task RefreshUsageAsync(CancellationToken cancellationToken)
    {
        var usage = await GetUsageAsync(cancellationToken).ConfigureAwait(false);
        _lastUsage = usage;
    }

    private static string StatusName(SendStatus status) => status switch
    {
        SendStatus.Sent => "sent",
        SendStatus.Failed => "failed",
        _ => "unknown",
    };

    private static string OutcomeName(ProviderOutcome outcome) => outcome switch
    {
        ProviderOutcome.Accepted => "accepted",
        ProviderOutcome.RecipientRejected => "recipient_rejected",
        ProviderOutcome.Throttled => "throttled",
        ProviderOutcome.QuotaExhausted => "quota_exhausted",
        ProviderOutcome.ProviderFault => "provider_fault",
        ProviderOutcome.Unavailable => "unavailable",
        _ => "unknown",
    };

    private static DateTimeOffset NearestReset(QuotaPlan plan, DateTimeOffset now) =>
        plan.Windows.Count == 0 ? DateTimeOffset.MaxValue : plan.Windows.Min(w => w.NextReset(now));

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Sent through {Provider} after {Attempts} attempt(s)")]
    private static partial void LogSent(ILogger logger, string provider, int attempts);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{Provider} did not send ({Outcome}); trying the next provider")]
    private static partial void LogFailover(ILogger logger, string provider, ProviderOutcome outcome);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{Provider} refused this account and is disabled until Enable is called or the app restarts")]
    private static partial void LogDisabled(ILogger logger, string provider);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "No provider sent the message after {Attempts} attempt(s); last outcome {Outcome}")]
    private static partial void LogFailed(ILogger logger, int attempts, ProviderOutcome outcome);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "{Provider} did not answer after the request was sent; the message may have been sent")]
    private static partial void LogUnknown(ILogger logger, string provider);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "{Provider} threw {ExceptionType} instead of returning an outcome")]
    private static partial void LogProviderThrew(ILogger logger, string provider, string exceptionType);

    // No address in the message: it is personal data (ADR-0005).
    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "A recipient is on the suppression list; no provider was tried")]
    private static partial void LogSuppressed(ILogger logger);

    private sealed class ProviderState(IEmailProvider provider, int index)
    {
        private readonly Lock _lock = new();
        private readonly Queue<DateTimeOffset> _failures = new();
        private bool _disabled;
        private DateTimeOffset _throttledUntil;
        private DateTimeOffset _openUntil;

        public IEmailProvider Provider { get; } = provider;

        public int Index { get; } = index;

        public (ProviderOutcome Outcome, string Reason)? SkipReason(DateTimeOffset now)
        {
            lock (_lock)
            {
                if (_disabled)
                {
                    return (ProviderOutcome.ProviderFault, "Skipped: disabled after the provider refused this account.");
                }

                if (now < _throttledUntil)
                {
                    return (ProviderOutcome.Throttled, "Skipped: the provider asked to wait.");
                }

                if (now < _openUntil)
                {
                    return (ProviderOutcome.Unavailable, "Skipped: the provider failed repeatedly and is resting.");
                }

                return null;
            }
        }

        public void ThrottleUntil(DateTimeOffset until)
        {
            lock (_lock)
            {
                _throttledUntil = until > _throttledUntil ? until : _throttledUntil;
            }
        }

        public void Disable()
        {
            lock (_lock)
            {
                _disabled = true;
            }
        }

        public void Enable()
        {
            lock (_lock)
            {
                _disabled = false;
                _failures.Clear();
                _openUntil = default;
                _throttledUntil = default;
            }
        }

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failures.Clear();
            }
        }

        public void RecordFailure(DateTimeOffset now, FreeTierMailerOptions options)
        {
            lock (_lock)
            {
                _failures.Enqueue(now);
                while (_failures.Count > 0 && _failures.Peek() <= now - options.CircuitBreakerWindow)
                {
                    _failures.Dequeue();
                }

                if (_failures.Count >= options.CircuitBreakerFailures)
                {
                    _openUntil = now + options.CircuitBreakerOpenFor;
                    _failures.Clear();
                }
            }
        }
    }
}

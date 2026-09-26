using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FreeTierMail;

/// <summary>
/// The names FreeTierMail uses for tracing and metrics. Add <see cref="SourceName"/> to your
/// OpenTelemetry tracer and <see cref="MeterName"/> to your meter provider. No span, tag or metric
/// carries an address, subject, body or key (ADR-0005).
/// </summary>
public static class FreeTierMailDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name.</summary>
    public const string SourceName = "FreeTierMail";

    /// <summary>The meter name; the same as <see cref="SourceName"/>.</summary>
    public const string MeterName = SourceName;

    /// <summary>One span per <see cref="FreeTierMailer.SendAsync"/>.</summary>
    public const string SendActivityName = "freetiermail.send";

    /// <summary>Tag: the provider that sent, or whose answer was lost.</summary>
    public const string ProviderTag = "freetiermail.provider";

    /// <summary>Tag: <see cref="SendStatus"/>, or for attempts <see cref="ProviderOutcome"/>.</summary>
    public const string StatusTag = "freetiermail.status";

    /// <summary>Tag: providers tried.</summary>
    public const string AttemptsTag = "freetiermail.attempts";

    /// <summary>Tag: the quota period, <c>daily</c> or <c>monthly</c>.</summary>
    public const string PeriodTag = "freetiermail.period";

    /// <summary>Counter: sends, tagged with status and provider.</summary>
    public const string SendsMetric = "freetiermail.sends";

    /// <summary>Counter: provider attempts, tagged with provider and outcome.</summary>
    public const string AttemptsMetric = "freetiermail.attempts";

    /// <summary>Gauge: messages left in a quota window, tagged with provider and period.</summary>
    public const string QuotaRemainingMetric = "freetiermail.quota.remaining";

    /// <summary>Gauge: a quota window's limit, tagged with provider and period.</summary>
    public const string QuotaLimitMetric = "freetiermail.quota.limit";

    internal static readonly ActivitySource Source = new(SourceName, typeof(FreeTierMailDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Meter Meter = new(MeterName, typeof(FreeTierMailDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Counter<long> Sends = Meter.CreateCounter<long>(SendsMetric, unit: "{message}", description: "Messages sent through FreeTierMail, by final status.");

    internal static readonly Counter<long> Attempts = Meter.CreateCounter<long>(AttemptsMetric, unit: "{attempt}", description: "Provider attempts, by outcome.");
}

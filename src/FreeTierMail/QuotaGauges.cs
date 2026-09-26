using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace FreeTierMail;

/// <summary>
/// The quota gauges, shared by every mailer in the process. Each mailer keeps the usage it read
/// after its last send; the gauges report that, so a scrape never waits on the quota store.
/// </summary>
internal static class QuotaGauges
{
    private static readonly Lock Gate = new();
    private static readonly List<WeakReference<FreeTierMailer>> Mailers = [];

    private static readonly ObservableGauge<long> Remaining = FreeTierMailDiagnostics.Meter.CreateObservableGauge(
        FreeTierMailDiagnostics.QuotaRemainingMetric, () => Measure(u => u.Exhausted ? 0 : Math.Max(0, u.Limit - u.Used)), unit: "{message}", description: "Messages left in a quota window, as of the last send.");

    private static readonly ObservableGauge<long> Limit = FreeTierMailDiagnostics.Meter.CreateObservableGauge(
        FreeTierMailDiagnostics.QuotaLimitMetric, () => Measure(u => u.Limit), unit: "{message}", description: "A quota window's limit.");

    /// <summary>True while a listener collects either gauge; the mailer reads usage only then.</summary>
    public static bool Enabled => Remaining.Enabled || Limit.Enabled;

    public static void Register(FreeTierMailer mailer)
    {
        lock (Gate)
        {
            Mailers.RemoveAll(w => !w.TryGetTarget(out _));
            Mailers.Add(new WeakReference<FreeTierMailer>(mailer));
        }
    }

    private static List<Measurement<long>> Measure(Func<QuotaUsage, long> value)
    {
        var measurements = new List<Measurement<long>>();
        lock (Gate)
        {
            foreach (var reference in Mailers)
            {
                if (!reference.TryGetTarget(out var mailer))
                {
                    continue;
                }

                foreach (var usage in mailer.LastUsage)
                {
                    measurements.Add(new Measurement<long>(
                        value(usage),
                        new KeyValuePair<string, object?>(FreeTierMailDiagnostics.ProviderTag, usage.Provider),
                        new KeyValuePair<string, object?>(FreeTierMailDiagnostics.PeriodTag, usage.Period == QuotaPeriod.Daily ? "daily" : "monthly")));
                }
            }
        }

        return measurements;
    }
}

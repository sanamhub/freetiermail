using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using FreeTierMail.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class DiagnosticsTests
{
    private const string CanaryAddress = "canary-rider-41c7@example.com";
    private const string CanarySubject = "canary subject 9e21";
    private const string CanaryBody = "canary body 77ab";

    private static EmailMessage Canary(string? key = null) =>
        new(new EmailAddress("links@example.org"), [new EmailAddress(CanaryAddress)], CanarySubject) { TextBody = CanaryBody, IdempotencyKey = key };

    [Fact]
    public async Task A_send_makes_one_span_with_provider_status_and_attempts()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var down = new FakeEmailProvider("diag-span-down").Then(ProviderOutcome.Unavailable);
        var up = new FakeEmailProvider("diag-span-up");
        var mailer = new FreeTierMailer([down, up], new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered });

        await mailer.SendAsync(Canary(), TestContext.Current.CancellationToken);

        var span = Assert.Single(spans, s => Equals(s.GetTagItem(FreeTierMailDiagnostics.ProviderTag), "diag-span-up"));
        Assert.Equal(FreeTierMailDiagnostics.SendActivityName, span.OperationName);
        Assert.Equal("sent", span.GetTagItem(FreeTierMailDiagnostics.StatusTag));
        Assert.Equal(2, span.GetTagItem(FreeTierMailDiagnostics.AttemptsTag));
    }

    [Fact]
    public async Task Sends_attempts_and_quota_gauges_are_measured()
    {
        var measurements = new ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == FreeTierMailDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        meters.Start();

        var provider = new FakeEmailProvider("diag-metrics", QuotaPlan.From(daily: 10, monthly: null));
        var mailer = new FreeTierMailer([provider], new FreeTierMailerOptions { CriticalReserve = 0 });
        await mailer.SendAsync(Canary(), TestContext.Current.CancellationToken);
        await mailer.SendAsync(Canary(), TestContext.Current.CancellationToken);
        meters.RecordObservableInstruments();

        bool Mine((string Instrument, long Value, Dictionary<string, object?> Tags) m) => Equals(m.Tags.GetValueOrDefault(FreeTierMailDiagnostics.ProviderTag), "diag-metrics");
        Assert.Equal(2, measurements.Where(Mine).Where(m => m.Instrument == FreeTierMailDiagnostics.SendsMetric).Sum(m => m.Value));
        Assert.Equal(2, measurements.Where(Mine).Where(m => m.Instrument == FreeTierMailDiagnostics.AttemptsMetric).Sum(m => m.Value));
        Assert.Equal(8, measurements.Where(Mine).Single(m => m.Instrument == FreeTierMailDiagnostics.QuotaRemainingMetric).Value);
        Assert.Equal(10, measurements.Where(Mine).Single(m => m.Instrument == FreeTierMailDiagnostics.QuotaLimitMetric).Value);
    }

    [Fact]
    public async Task AC_9_no_log_span_or_metric_carries_an_address_subject_body_or_key()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var logs = new CapturingLogger();
        var tags = new ConcurrentQueue<string>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, l) => l.EnableMeasurementEvents(instrument);
        meters.SetMeasurementEventCallback<long>((_, _, measured, _) =>
        {
            foreach (var tag in measured)
            {
                tags.Enqueue(tag.Key + "=" + tag.Value);
            }
        });
        meters.Start();

        var providers = new[]
        {
            new FakeEmailProvider("leak-a").Then(ProviderOutcome.Unavailable, 3),
            new FakeEmailProvider("leak-b").Then(ProviderOutcome.ProviderFault),
            new FakeEmailProvider("leak-c").Then(ProviderOutcome.Throttled).Then(ProviderOutcome.Unknown),
            new FakeEmailProvider("leak-d").Then(ProviderOutcome.RecipientRejected),
        };
        var mailer = new FreeTierMailer(providers, new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, logs);
        for (var i = 0; i < 4; i++)
        {
            await mailer.SendAsync(Canary("canary-key-" + i), TestContext.Current.CancellationToken);
        }

        var everything = logs.Lines
            .Concat(spans.SelectMany(s => s.TagObjects.Select(t => t.Key + "=" + t.Value)))
            .Concat(tags)
            .ToArray();
        Assert.NotEmpty(logs.Lines);
        foreach (var canary in new[] { CanaryAddress, CanarySubject, CanaryBody, "canary-key-" })
        {
            Assert.DoesNotContain(everything, line => line.Contains(canary, StringComparison.Ordinal));
        }
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FreeTierMailDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class CapturingLogger : ILogger<FreeTierMailer>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Enqueue(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    Lines.Enqueue(key + "=" + value);
                }
            }
        }
    }
}

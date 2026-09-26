using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class MailerTests
{
    private static readonly EmailAddress From = new("links@example.org");
    private static readonly EmailAddress Rider = new("rider@example.com");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailMessage Message(EmailPriority priority = EmailPriority.Normal, string? key = null) =>
        new(From, [Rider], "Hi") { TextBody = "Body", Priority = priority, IdempotencyKey = key };

    private FreeTierMailer Mailer(params ScriptedProvider[] providers) => Mailer(new FreeTierMailerOptions(), providers);

    private FreeTierMailer Mailer(FreeTierMailerOptions options, params ScriptedProvider[] providers)
    {
        options.TimeProvider = _time;
        return new FreeTierMailer(providers, options);
    }

    [Fact]
    public async Task AC_1_quota_fills_providers_in_turn_and_then_fails_without_a_request()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 2, monthly: null));
        var b = new ScriptedProvider("b", QuotaPlan.From(daily: 2, monthly: null));
        var mailer = Mailer(new FreeTierMailerOptions { CriticalReserve = 0, Strategy = RoutingStrategy.Ordered }, a, b);

        var results = new List<SendResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await mailer.SendAsync(Message(), Ct));
        }

        Assert.Equal(["a", "a", "b", "b"], results.Take(4).Select(r => r.Provider));
        Assert.Equal(SendStatus.Failed, results[4].Status);
        Assert.All(results[4].Attempts, attempt => Assert.Equal(ProviderOutcome.QuotaExhausted, attempt.Outcome));
        Assert.Equal(2, a.Sent.Count);
        Assert.Equal(2, b.Sent.Count);
    }

    [Fact]
    public async Task AC_2_expiring_first_spends_the_daily_quota_before_the_monthly_one()
    {
        var monthly = new ScriptedProvider("monthly", QuotaPlan.From(daily: null, monthly: 3000));
        var daily = new ScriptedProvider("daily", QuotaPlan.From(daily: 100, monthly: null));
        var mailer = Mailer(monthly, daily);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal("daily", result.Provider);
    }

    [Fact]
    public async Task AC_3_a_quota_error_marks_the_window_and_fails_over_in_the_same_send()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 100, monthly: null), ProviderResult.Of(ProviderOutcome.QuotaExhausted, "daily quota"));
        var b = new ScriptedProvider("b", QuotaPlan.From(daily: 100, monthly: 1000));
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        var first = await mailer.SendAsync(Message(), Ct);
        var second = await mailer.SendAsync(Message(), Ct);

        Assert.Equal("b", first.Provider);
        Assert.Equal([ProviderOutcome.QuotaExhausted, ProviderOutcome.Accepted], first.Attempts.Select(x => x.Outcome));
        Assert.Equal("b", second.Provider);
        Assert.Single(a.Sent);
        Assert.True((await mailer.GetUsageAsync(Ct)).Single(u => u.Provider == "a").Exhausted);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal("a", (await mailer.SendAsync(Message(), Ct)).Provider);
    }

    [Fact]
    public async Task A_throttled_provider_is_skipped_until_retry_after()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.Throttled, "429", TimeSpan.FromSeconds(30)));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        await mailer.SendAsync(Message(), Ct);
        var during = await mailer.SendAsync(Message(), Ct);
        _time.Advance(TimeSpan.FromSeconds(31));
        var after = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(TimeSpan.Zero, during.Attempts[0].Duration);
        Assert.Equal("b", during.Provider);
        Assert.Equal("a", after.Provider);
    }

    [Fact]
    public async Task AC_4_a_lost_answer_is_unknown_and_tries_no_other_provider()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.Unknown, "timeout"));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Unknown, result.Status);
        Assert.Equal("a", result.Provider);
        Assert.Empty(b.Sent);
    }

    [Fact]
    public async Task AC_4_failover_on_unknown_is_opt_in()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.Unknown, "timeout"));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered, FailoverOnUnknown = true }, a, b);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Sent, result.Status);
        Assert.Equal("b", result.Provider);
    }

    [Fact]
    public async Task A_lost_answer_counts_against_the_quota()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 1, monthly: null), ProviderResult.Of(ProviderOutcome.Unknown, "timeout"));
        var mailer = Mailer(new FreeTierMailerOptions { CriticalReserve = 0 }, a);

        await mailer.SendAsync(Message(), Ct);

        Assert.Equal(1, (await mailer.GetUsageAsync(Ct))[0].Used);
    }

    [Fact]
    public async Task AC_5_an_invalid_recipient_is_not_retried_elsewhere()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.RecipientRejected, "invalid address"));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Failed, result.Status);
        Assert.Empty(b.Sent);
    }

    [Fact]
    public async Task AC_5_a_provider_fault_fails_over_and_disables_the_provider_until_enabled()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.ProviderFault, "key revoked"));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        var first = await mailer.SendAsync(Message(), Ct);
        var second = await mailer.SendAsync(Message(), Ct);
        mailer.Enable("a");
        var third = await mailer.SendAsync(Message(), Ct);

        Assert.Equal("b", first.Provider);
        Assert.Equal(TimeSpan.Zero, second.Attempts[0].Duration);
        Assert.Equal(2, a.Sent.Count); // the fault, then the send after Enable; none while disabled
        Assert.Equal("a", third.Provider);
    }

    [Fact]
    public async Task A_released_reservation_does_not_count()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 5, monthly: null), ProviderResult.Of(ProviderOutcome.Unavailable, "503"));
        var mailer = Mailer(a);

        await mailer.SendAsync(Message(), Ct);

        Assert.Equal(0, (await mailer.GetUsageAsync(Ct))[0].Used);
    }

    [Fact]
    public async Task Three_outages_in_five_minutes_rest_the_provider_for_five_minutes()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited,
            ProviderResult.Of(ProviderOutcome.Unavailable, "503"), ProviderResult.Of(ProviderOutcome.Unavailable, "503"), ProviderResult.Of(ProviderOutcome.Unavailable, "503"));
        var b = new ScriptedProvider("b", QuotaPlan.Unlimited);
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered }, a, b);

        for (var i = 0; i < 3; i++)
        {
            await mailer.SendAsync(Message(), Ct);
        }

        var resting = await mailer.SendAsync(Message(), Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        var back = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(4, a.Sent.Count); // three outages, none while resting, one after
        Assert.Equal("Skipped: the provider failed repeatedly and is resting.", resting.Attempts[0].Reason);
        Assert.Equal("a", back.Provider);
    }

    [Fact]
    public async Task Critical_mail_tries_the_preferred_provider_first()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 100, monthly: null));
        var b = new ScriptedProvider("b", QuotaPlan.From(daily: 100, monthly: 3000)) { PreferForCritical = true };
        var mailer = Mailer(a, b);

        Assert.Equal("a", (await mailer.SendAsync(Message(), Ct)).Provider);
        Assert.Equal("b", (await mailer.SendAsync(Message(EmailPriority.Critical), Ct)).Provider);
    }

    [Fact]
    public async Task Most_remaining_picks_the_emptiest_provider()
    {
        var a = new ScriptedProvider("a", QuotaPlan.From(daily: 10, monthly: null));
        var b = new ScriptedProvider("b", QuotaPlan.From(daily: 10, monthly: null));
        var mailer = Mailer(new FreeTierMailerOptions { Strategy = RoutingStrategy.MostRemaining, CriticalReserve = 0 }, a, b);

        var providers = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            providers.Add((await mailer.SendAsync(Message(), Ct)).Provider);
        }

        Assert.Equal(["a", "b", "a", "b"], providers);
    }

    [Fact]
    public async Task AC_8_a_repeat_with_the_same_key_sends_nothing()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited);
        var mailer = Mailer(a);

        var first = await mailer.SendAsync(Message(key: "sign-in:1"), Ct);
        var second = await mailer.SendAsync(Message(key: "sign-in:1"), Ct);

        Assert.Single(a.Sent);
        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.ProviderMessageId, second.ProviderMessageId);
    }

    [Fact]
    public async Task AC_8_concurrent_sends_with_one_key_share_one_attempt()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited) { Delay = TimeSpan.FromMilliseconds(50) };
        var mailer = new FreeTierMailer([a]);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => mailer.SendAsync(Message(key: "k"), Ct)));

        Assert.Single(a.Sent);
        Assert.Equal(9, results.Count(r => r.IsReplay));
    }

    [Fact]
    public async Task A_failed_send_can_be_retried_with_the_same_key()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited, ProviderResult.Of(ProviderOutcome.Unavailable, "503"));
        var mailer = Mailer(a);

        await mailer.SendAsync(Message(key: "k"), Ct);
        var retry = await mailer.SendAsync(Message(key: "k"), Ct);

        Assert.Equal(SendStatus.Sent, retry.Status);
        Assert.False(retry.IsReplay);
    }

    [Fact]
    public async Task A_provider_that_throws_gives_unknown_not_a_crash()
    {
        var a = new ScriptedProvider("a", QuotaPlan.Unlimited) { Throw = new InvalidOperationException("rider@example.com leaked") };
        var mailer = Mailer(a);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Unknown, result.Status);
        Assert.DoesNotContain("rider", result.Attempts[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_providers_with_one_name_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new FreeTierMailer([new ScriptedProvider("a", QuotaPlan.Unlimited), new ScriptedProvider("a", QuotaPlan.Unlimited)]));
    }

    internal sealed class ScriptedProvider(string name, QuotaPlan quota, params ProviderResult[] script) : IEmailProvider
    {
        private readonly ConcurrentQueue<ProviderResult> _script = new(script);
        private int _count;

        public string Name { get; } = name;

        public QuotaPlan Quota { get; } = quota;

        public bool PreferForCritical { get; init; }

        public TimeSpan Delay { get; init; }

        public Exception? Throw { get; init; }

        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public async Task<ProviderResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            var n = Interlocked.Increment(ref _count);
            return _script.TryDequeue(out var next) ? next : ProviderResult.Accepted(Name + "-" + n);
        }
    }
}

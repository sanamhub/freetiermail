using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class QuotaTests
{
    private static readonly TimeZoneInfo Kathmandu = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu");

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_daily_window_resets_at_utc_midnight_by_default()
    {
        var window = new QuotaWindow(QuotaPeriod.Daily, 100);
        var now = new DateTimeOffset(2026, 9, 26, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), window.StartOf(now));
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), window.NextReset(now));
        Assert.Equal("daily:2026-09-26", window.KeyFor(now));
    }

    [Fact]
    public void A_daily_window_in_kathmandu_resets_at_18_15_utc()
    {
        var window = new QuotaWindow(QuotaPeriod.Daily, 100) { TimeZone = Kathmandu };
        var justBefore = new DateTimeOffset(2026, 9, 26, 18, 14, 0, TimeSpan.Zero);
        var justAfter = new DateTimeOffset(2026, 9, 26, 18, 15, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 18, 15, 0, TimeSpan.Zero), window.NextReset(justBefore).ToUniversalTime());
        Assert.NotEqual(window.KeyFor(justBefore), window.KeyFor(justAfter));
    }

    [Theory]
    [InlineData(2026, 1, 31, 1, "2026-01-01", "2026-02-01")]
    [InlineData(2026, 2, 28, 1, "2026-02-01", "2026-03-01")]
    [InlineData(2026, 3, 14, 15, "2026-02-15", "2026-03-15")]
    [InlineData(2026, 12, 20, 15, "2026-12-15", "2027-01-15")]
    public void A_monthly_window_resets_on_its_day(int year, int month, int day, int resetDay, string start, string next)
    {
        var window = new QuotaWindow(QuotaPeriod.Monthly, 3000) { ResetDay = resetDay };
        var now = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), DateOnly.FromDateTime(window.StartOf(now).DateTime));
        Assert.Equal(DateOnly.Parse(next, System.Globalization.CultureInfo.InvariantCulture), DateOnly.FromDateTime(window.NextReset(now).DateTime));
    }

    [Fact]
    public void A_reset_day_after_28_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaWindow(QuotaPeriod.Monthly, 10) { ResetDay = 31 });
    }

    [Fact]
    public async Task Reservation_stops_at_the_limit_and_a_new_day_starts_fresh()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 2, monthly: null);
        var day1 = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        Assert.NotNull(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, day1, Ct));
        Assert.NotNull(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, day1, Ct));
        Assert.Null(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, day1, Ct));
        Assert.NotNull(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, day1.AddDays(1), Ct));
    }

    [Fact]
    public async Task A_message_needs_room_in_every_window()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 100, monthly: 2);
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now, Ct);
        await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now.AddDays(1), Ct);

        Assert.Null(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now.AddDays(2), Ct));
        var usage = await store.GetUsageAsync("a", plan, now.AddDays(2), Ct);
        Assert.Equal(0, usage.Single(u => u.Period == QuotaPeriod.Daily).Used);
        Assert.Equal(2, usage.Single(u => u.Period == QuotaPeriod.Monthly).Used);
    }

    [Fact]
    public async Task AC_6_normal_mail_leaves_the_critical_reserve()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 10, monthly: null);
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        var normal = 0;
        while (await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0.2, now, Ct) is not null)
        {
            normal++;
        }

        var critical = 0;
        while (await store.TryReserveAsync("a", plan, EmailPriority.Critical, 0.2, now, Ct) is not null)
        {
            critical++;
        }

        Assert.Equal(8, normal);
        Assert.Equal(2, critical);
    }

    [Fact]
    public async Task A_release_gives_the_unit_back()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 1, monthly: null);
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        var reservation = await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now, Ct);
        await store.ReleaseAsync(reservation!, Ct);

        Assert.NotNull(await store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now, Ct));
    }

    [Fact]
    public async Task An_exhausted_window_refuses_until_it_resets()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 100, monthly: null);
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        await store.MarkExhaustedAsync("a", plan.Windows[0], now, Ct);

        Assert.Null(await store.TryReserveAsync("a", plan, EmailPriority.Critical, 0, now, Ct));
        Assert.True((await store.GetUsageAsync("a", plan, now, Ct))[0].Exhausted);
        Assert.NotNull(await store.TryReserveAsync("a", plan, EmailPriority.Critical, 0, now.AddDays(1), Ct));
    }

    [Fact]
    public async Task AC_7_parallel_reservations_never_pass_the_limit()
    {
        var store = new InMemoryQuotaStore();
        var plan = QuotaPlan.From(daily: 20, monthly: 25);
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => store.TryReserveAsync("a", plan, EmailPriority.Normal, 0, now, Ct), Ct)));

        Assert.Equal(20, results.Count(r => r is not null));
    }

    [Fact]
    public void A_plan_with_no_limits_is_unlimited()
    {
        Assert.Same(QuotaPlan.Unlimited, QuotaPlan.From(daily: null, monthly: null));
        Assert.Empty(QuotaPlan.Unlimited.Windows);
    }
}

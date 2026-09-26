using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeTierMail;

/// <summary>How often a quota resets.</summary>
public enum QuotaPeriod
{
    /// <summary>Resets at midnight in the window's time zone.</summary>
    Daily = 0,

    /// <summary>Resets on <see cref="QuotaWindow.ResetDay"/> of each month, at midnight in the window's time zone.</summary>
    Monthly = 1,
}

/// <summary>One quota of a provider, such as "100 a day" or "3,000 a month".</summary>
public sealed record QuotaWindow
{
    /// <summary>Creates a window.</summary>
    /// <param name="period">Daily or monthly.</param>
    /// <param name="limit">Messages allowed per period.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    public QuotaWindow(QuotaPeriod period, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Period = period;
        Limit = limit;
    }

    /// <summary>Daily or monthly.</summary>
    public QuotaPeriod Period { get; }

    /// <summary>Messages allowed per period.</summary>
    public int Limit { get; }

    /// <summary>The time zone whose midnight resets the window. Most providers do not document it; UTC is the default.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The day of the month a monthly window resets, 1 to 28. Ignored for daily windows.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 28.</exception>
    public int ResetDay
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 28);
            field = value;
        }
    } = 1;

    /// <summary>When the window containing <paramref name="now"/> started.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The start, with the window time zone's offset.</returns>
    public DateTimeOffset StartOf(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZone);
        var startDate = Period == QuotaPeriod.Daily
            ? DateOnly.FromDateTime(local.DateTime)
            : MonthStart(DateOnly.FromDateTime(local.DateTime));
        return AtLocalMidnight(startDate);
    }

    /// <summary>When the window containing <paramref name="now"/> resets.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The next reset, with the window time zone's offset.</returns>
    public DateTimeOffset NextReset(DateTimeOffset now)
    {
        var start = DateOnly.FromDateTime(StartOf(now).DateTime);
        return AtLocalMidnight(Period == QuotaPeriod.Daily ? start.AddDays(1) : start.AddMonths(1));
    }

    /// <summary>A key naming the window containing <paramref name="now"/>, such as <c>daily:2026-09-26</c>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The key.</returns>
    public string KeyFor(DateTimeOffset now) =>
        Period == QuotaPeriod.Daily
            ? "daily:" + StartOf(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "monthly:" + StartOf(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Messages a message of <paramref name="priority"/> may use, leaving the critical reserve for critical mail.</summary>
    internal int AllowanceFor(EmailPriority priority, double criticalReserve) =>
        priority == EmailPriority.Critical ? Limit : (int)Math.Floor(Limit * (1 - criticalReserve));

    private DateOnly MonthStart(DateOnly date)
    {
        var thisMonth = new DateOnly(date.Year, date.Month, ResetDay);
        return date >= thisMonth ? thisMonth : thisMonth.AddMonths(-1);
    }

    private DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, TimeZone.GetUtcOffset(midnight));
    }
}

/// <summary>The quotas of one provider. A plan with no windows is unlimited, for example a paid fallback.</summary>
public sealed class QuotaPlan
{
    /// <summary>A plan with no limit.</summary>
    public static QuotaPlan Unlimited { get; } = new([]);

    /// <summary>Creates a plan.</summary>
    /// <param name="windows">The windows. At most one per period.</param>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException">Two windows share a period.</exception>
    public QuotaPlan(IEnumerable<QuotaWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var list = windows.ToArray();
        if (list.GroupBy(w => w.Period).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A plan has at most one daily and one monthly window.", nameof(windows));
        }

        Windows = list;
    }

    /// <summary>The windows.</summary>
    public IReadOnlyList<QuotaWindow> Windows { get; }

    /// <summary>A plan from optional daily and monthly limits, the shape provider options use.</summary>
    /// <param name="daily">Messages a day, or null.</param>
    /// <param name="monthly">Messages a month, or null.</param>
    /// <param name="timeZone">The reset time zone; UTC when null.</param>
    /// <param name="monthlyResetDay">The monthly reset day, 1 to 28.</param>
    /// <returns>The plan.</returns>
    public static QuotaPlan From(int? daily, int? monthly, TimeZoneInfo? timeZone = null, int monthlyResetDay = 1)
    {
        var zone = timeZone ?? TimeZoneInfo.Utc;
        var windows = new List<QuotaWindow>(2);
        if (daily is { } d)
        {
            windows.Add(new QuotaWindow(QuotaPeriod.Daily, d) { TimeZone = zone });
        }

        if (monthly is { } m)
        {
            windows.Add(new QuotaWindow(QuotaPeriod.Monthly, m) { TimeZone = zone, ResetDay = monthlyResetDay });
        }

        return windows.Count == 0 ? Unlimited : new QuotaPlan(windows);
    }
}

/// <summary>How much of one window is used.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Period">Daily or monthly.</param>
/// <param name="Limit">The window's limit.</param>
/// <param name="Used">Messages counted in the current window.</param>
/// <param name="ResetsAt">When the window resets.</param>
/// <param name="Exhausted">True when the provider said the quota is used up, whatever <paramref name="Used"/> says.</param>
public sealed record QuotaUsage(string Provider, QuotaPeriod Period, int Limit, int Used, DateTimeOffset ResetsAt, bool Exhausted);

/// <summary>Units held in a provider's windows for one send, kept or released when the outcome is known.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Keys">The window keys holding a unit.</param>
public sealed record QuotaReservation(string Provider, IReadOnlyList<string> Keys);

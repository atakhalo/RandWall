using System;

namespace RandWall.Models;

/// <summary>切换间隔的显示单位，同时决定「整点基准」的对齐范围。</summary>
public enum IntervalUnit
{
    /// <summary>分钟：以当前整点（整小时）为基准。</summary>
    Minutes,

    /// <summary>小时：以当天 0 点为基准。</summary>
    Hours,

    /// <summary>天：以 0 点为基准按天推进。</summary>
    Days,
}

/// <summary>
/// 整点基准模式下的下次切换时间：把时间轴按「对齐基准 + 间隔的整数倍」切片，
/// 取当前时间之后最近的切片边界。基准按单位选择：分钟→当前整小时，小时→当天 0 点，天→固定参考日。
/// </summary>
public static class ClockAlignedSchedule
{
    /// <summary>天单位的固定参考日，保证「每 N 天」的相位稳定，不会每天从今天 0 点重新起算。</summary>
    private const int ReferenceYear = 2000;
    private const int ReferenceMonth = 1;
    private const int ReferenceDay = 1;

    /// <summary>返回严格晚于 <paramref name="now"/> 的最近对齐时刻。</summary>
    public static DateTimeOffset NextDue(DateTimeOffset now, TimeSpan interval, IntervalUnit unit)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "间隔必须大于 0");
        }

        var reference = GetReference(now, unit);
        var elapsed = now - reference;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        // 当前落在第 index 个格子里（从 0 开始），下一秒切换点是下一个格子边界
        var index = elapsed.Ticks / interval.Ticks;
        var due = reference + TimeSpan.FromTicks(interval.Ticks * (index + 1));

        // 防御：时区偏移变化等情况可能让结果落在 now 之前
        while (due <= now)
        {
            due += interval;
        }

        return due;
    }

    /// <summary>按间隔秒数推断显示单位（与界面的自动选单位规则一致）。</summary>
    public static IntervalUnit InferUnit(long intervalSeconds)
    {
        if (intervalSeconds >= 86400)
        {
            return IntervalUnit.Days;
        }

        return intervalSeconds >= 3600 ? IntervalUnit.Hours : IntervalUnit.Minutes;
    }

    /// <summary>分钟单位下允许的间隔（必须能整除 60，保证对齐点不在一小时中途跳变）。</summary>
    public static bool IsWholeHourDivisor(long minutes)
        => minutes > 0 && minutes <= 60 && 60 % minutes == 0;

    private static DateTimeOffset GetReference(DateTimeOffset now, IntervalUnit unit)
    {
        return unit switch
        {
            IntervalUnit.Minutes => new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset),
            IntervalUnit.Hours => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset),
            _ => new DateTimeOffset(ReferenceYear, ReferenceMonth, ReferenceDay, 0, 0, 0, now.Offset),
        };
    }
}

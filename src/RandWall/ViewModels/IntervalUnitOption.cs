using RandWall.Models;

namespace RandWall.ViewModels;

/// <summary>切换间隔的单位。</summary>
public sealed class IntervalUnitOption
{
    public IntervalUnitOption(long seconds, string label, IntervalUnit unit)
    {
        Seconds = seconds;
        Label = label;
        Unit = unit;
    }

    public long Seconds { get; }

    public string Label { get; }

    public IntervalUnit Unit { get; }

    public override string ToString() => Label;
}

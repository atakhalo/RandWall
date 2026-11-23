namespace RandWall.ViewModels;

/// <summary>切换间隔的单位。</summary>
public sealed class IntervalUnitOption
{
    public IntervalUnitOption(long seconds, string label)
    {
        Seconds = seconds;
        Label = label;
    }

    public long Seconds { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

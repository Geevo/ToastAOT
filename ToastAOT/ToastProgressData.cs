using System.Globalization;

namespace ToastAOT;

/// <summary>Bindable progress values. Increase SequenceNumber on each update; zero disables ordering.</summary>
public sealed record ToastProgressData
{
    public uint SequenceNumber { get; init; }

    public string Title { get; init; } = "";

    public double Value { get; init; }

    public string Status { get; init; } = "";

    public string ValueStringOverride { get; init; } = "";

    public bool IsIndeterminate { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(Value) || Value < 0 || Value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Value));
        }

        ArgumentNullException.ThrowIfNull(Title);
        ArgumentNullException.ThrowIfNull(Status);
        ArgumentNullException.ThrowIfNull(ValueStringOverride);
    }

    internal IEnumerable<KeyValuePair<string, string>> Values()
    {
        Validate();
        yield return new("progressTitle", Title);
        yield return new("progressValue", IsIndeterminate ? "indeterminate" : Value.ToString("R", CultureInfo.InvariantCulture));
        yield return new("progressStatus", Status);
        yield return new("progressValueString", ValueStringOverride);
    }
}

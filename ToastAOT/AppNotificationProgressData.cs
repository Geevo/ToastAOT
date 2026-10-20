namespace ToastAOT;

public sealed class AppNotificationProgressData
{
    private uint _sequenceNumber;

    public AppNotificationProgressData(uint sequenceNumber)
    {
        SequenceNumber = sequenceNumber;
    }

    public uint SequenceNumber
    {
        get
        {
            return _sequenceNumber;
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfZero(value);
            _sequenceNumber = value;
        }
    }

    public string Title { get; set; } = "";

    public double Value { get; set; }

    public string Status { get; set; } = "";

    public string ValueStringOverride { get; set; } = "";

    internal ToastProgressData Snapshot()
    {
        return new()
        {
            SequenceNumber = SequenceNumber,
            Title = Title,
            Value = Value,
            Status = Status,
            ValueStringOverride = ValueStringOverride
        };
    }
}

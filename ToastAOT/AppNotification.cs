namespace ToastAOT;

/// <summary>Mutable notification properties using Windows App SDK naming. No SDK runtime is required.</summary>
public sealed class AppNotification
{
    public AppNotification(string payload)
    {
        new ToastMessage(payload).Validate();
        Payload = payload;
    }

    public string Payload { get; }

    public string Tag { get; set; } = "";

    public string Group { get; set; } = "";

    public DateTimeOffset Expiration { get; set; } = DateTimeOffset.MaxValue;

    public bool ExpiresOnReboot { get; set; }

    public bool SuppressDisplay { get; set; }

    public AppNotificationPriority Priority { get; set; }

    public AppNotificationProgressData? Progress { get; set; }

    internal ToastMessage Snapshot()
    {
        if (!Enum.IsDefined(Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(Priority));
        }

        var message = new ToastMessage(Payload)
        {
            Tag = Tag,
            Group = Group,
            Expiration = Expiration == DateTimeOffset.MaxValue ? null : Expiration,
            SuppressPopup = SuppressDisplay,
            Priority = (ToastPriority)Priority,
            Progress = Progress?.Snapshot(),
            ExpiresOnReboot = ExpiresOnReboot
        };
        message.Validate();
        return message;
    }

    internal static AppNotification FromMessage(ToastMessage message)
    {
        return new(message.Xml)
        {
            Tag = message.Tag ?? "",
            Group = message.Group ?? "",
            Expiration = message.Expiration ?? DateTimeOffset.MaxValue,
            SuppressDisplay = message.SuppressPopup,
            Priority = (AppNotificationPriority)message.Priority,
            ExpiresOnReboot = message.ExpiresOnReboot,
            Progress = message.Progress == null || message.Progress.SequenceNumber == 0 ? null : new AppNotificationProgressData(message.Progress.SequenceNumber)
            {
                Title = message.Progress.Title,
                Value = message.Progress.Value,
                Status = message.Progress.Status,
                ValueStringOverride = message.Progress.ValueStringOverride
            }
        };
    }
}

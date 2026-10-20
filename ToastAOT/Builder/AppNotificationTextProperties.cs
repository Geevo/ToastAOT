namespace ToastAOT.Builder;

public sealed class AppNotificationTextProperties
{
    public string? Language { get; set; }

    public bool IncomingCallAlignment { get; set; }

    public int MaxLines { get; set; }

    public AppNotificationTextProperties SetLanguage(string value)
    {
        Language = value;
        return this;
    }

    public AppNotificationTextProperties SetIncomingCallAlignment()
    {
        IncomingCallAlignment = true;
        return this;
    }

    public AppNotificationTextProperties SetMaxLines(int value)
    {
        MaxLines = value;
        return this;
    }
}

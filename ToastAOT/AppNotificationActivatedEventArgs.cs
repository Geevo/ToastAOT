namespace ToastAOT;

public sealed class AppNotificationActivatedEventArgs : EventArgs
{
    internal AppNotificationActivatedEventArgs(ToastActivation activation)
    {
        Activation = activation;
        Arguments = AppNotificationArgumentCodec.Parse(activation.RawArguments);
    }

    private ToastActivation Activation { get; }

    public string Argument
    {
        get
        {
            return Activation.RawArguments;
        }
    }

    public IReadOnlyDictionary<string, string> Arguments { get; }

    public IReadOnlyDictionary<string, string> UserInput
    {
        get
        {
            return Activation.UserInput;
        }
    }
}

using System.Xml.Linq;

namespace ToastAOT.Builder;

public sealed class AppNotificationButton
{
    private IDictionary<string, string> _arguments = new Dictionary<string, string>(StringComparer.Ordinal);

    public AppNotificationButton()
    {
    }

    public AppNotificationButton(string content)
    {
        Content = content;
    }

    public string Content { get; set; } = "";

    public IDictionary<string, string> Arguments
    {
        get
        {
            return _arguments;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _arguments = value;
        }
    }

    public Uri? Icon { get; set; }

    public string? ToolTip { get; set; }

    public bool ContextMenuPlacement { get; set; }

    public AppNotificationButtonStyle ButtonStyle { get; set; }

    public string? InputId { get; set; }

    public Uri? InvokeUri { get; set; }

    public string? TargetAppId { get; set; }

    public AppNotificationButton AddArgument(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (InvokeUri != null)
        {
            throw new InvalidOperationException("A protocol button cannot carry app arguments.");
        }

        Arguments[key] = value;
        return this;
    }

    public AppNotificationButton SetIcon(Uri value)
    {
        Icon = value;
        return this;
    }

    public AppNotificationButton SetToolTip(string value)
    {
        ToolTip = value;
        return this;
    }

    public AppNotificationButton SetContextMenuPlacement()
    {
        ContextMenuPlacement = true;
        return this;
    }

    public AppNotificationButton SetButtonStyle(AppNotificationButtonStyle value)
    {
        ButtonStyle = value;
        return this;
    }

    public AppNotificationButton SetInputId(string value)
    {
        InputId = value;
        return this;
    }

    public AppNotificationButton SetInvokeUri(Uri protocolUri, string? targetAppId = null)
    {
        ArgumentNullException.ThrowIfNull(protocolUri);
        if (Arguments.Count != 0)
        {
            throw new InvalidOperationException("A protocol button cannot carry app arguments.");
        }

        InvokeUri = protocolUri;
        TargetAppId = targetAppId;
        return this;
    }

    public static bool IsToolTipSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    }

    public static bool IsButtonStyleSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    }

    internal XElement ToXml()
    {
        if (!Enum.IsDefined(ButtonStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(ButtonStyle));
        }

        if (InvokeUri != null && Arguments.Count != 0)
        {
            throw new InvalidOperationException("A protocol button cannot also carry app arguments.");
        }

        var element = new XElement(
            "action",
            new XAttribute("content", Content),
            new XAttribute("arguments", InvokeUri?.AbsoluteUri ?? AppNotificationArgumentCodec.Encode(Arguments)),
            new XAttribute("activationType", InvokeUri == null ? "foreground" : "protocol"));

        if (Icon != null)
        {
            element.SetAttributeValue("imageUri", Icon.AbsoluteUri);
        }

        if (ToolTip != null)
        {
            element.SetAttributeValue("hint-toolTip", ToolTip);
        }

        if (ContextMenuPlacement)
        {
            element.SetAttributeValue("placement", "contextMenu");
        }

        if (InputId != null)
        {
            element.SetAttributeValue("hint-inputId", InputId);
        }

        if (TargetAppId != null)
        {
            if (InvokeUri == null)
            {
                throw new InvalidOperationException("TargetAppId requires a protocol URI.");
            }

            element.SetAttributeValue("protocolActivationTargetApplicationPfn", TargetAppId);
        }
        if (ButtonStyle != AppNotificationButtonStyle.Default)
        {
            element.SetAttributeValue("hint-buttonStyle", ButtonStyle.ToString());
        }

        return element;
    }
}

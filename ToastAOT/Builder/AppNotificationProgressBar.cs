using System.Globalization;
using System.Xml.Linq;

namespace ToastAOT.Builder;

/// <summary>
/// Progress content with SDK-compatible binding modes. Value and status bind by default; title and
/// value text are omitted until set or explicitly bound. Binding retains the stored property value.
/// </summary>
public sealed class AppNotificationProgressBar
{
    private enum BindingMode
    {
        Unset,
        Bound,
        Static
    }

    private string? _title = "";
    private string? _status = "";
    private string? _valueStringOverride = "";
    private double _value;
    private BindingMode _titleMode;
    private BindingMode _statusMode;
    private BindingMode _valueMode;
    private BindingMode _valueStringMode;

    public string? Title
    {
        get
        {
            return _title;
        }
        set
        {
            _title = value;
            _titleMode = value == null ? BindingMode.Bound : BindingMode.Static;
        }
    }

    public string? Status
    {
        get
        {
            return _status;
        }
        set
        {
            _status = value;
            _statusMode = value == null ? BindingMode.Bound : BindingMode.Static;
        }
    }

    public double Value
    {
        get
        {
            return _value;
        }
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _value = value;
            _valueMode = BindingMode.Static;
        }
    }

    public string? ValueStringOverride
    {
        get
        {
            return _valueStringOverride;
        }
        set
        {
            _valueStringOverride = value;
            _valueStringMode = value == null ? BindingMode.Bound : BindingMode.Static;
        }
    }

    public AppNotificationProgressBar SetTitle(string value)
    {
        Title = value;
        return this;
    }

    public AppNotificationProgressBar BindTitle()
    {
        _titleMode = BindingMode.Bound;
        return this;
    }

    public AppNotificationProgressBar SetStatus(string value)
    {
        Status = value;
        return this;
    }

    public AppNotificationProgressBar BindStatus()
    {
        _statusMode = BindingMode.Bound;
        return this;
    }

    public AppNotificationProgressBar SetValue(double value)
    {
        Value = value;
        return this;
    }

    public AppNotificationProgressBar BindValue()
    {
        _valueMode = BindingMode.Bound;
        return this;
    }

    public AppNotificationProgressBar SetValueStringOverride(string value)
    {
        ValueStringOverride = value;
        return this;
    }

    public AppNotificationProgressBar BindValueStringOverride()
    {
        _valueStringMode = BindingMode.Bound;
        return this;
    }

    internal XElement ToXml()
    {
        var element = new XElement("progress");
        if (_titleMode != BindingMode.Unset)
        {
            element.SetAttributeValue("title", _titleMode == BindingMode.Static ? _title : "{progressTitle}");
        }

        element.SetAttributeValue("status", _statusMode == BindingMode.Static ? _status : "{progressStatus}");
        element.SetAttributeValue("value", _valueMode == BindingMode.Static
            ? _value.ToString("R", CultureInfo.InvariantCulture)
            : "{progressValue}");

        if (_valueStringMode != BindingMode.Unset)
        {
            element.SetAttributeValue("valueStringOverride", _valueStringMode == BindingMode.Static
                ? _valueStringOverride
                : "{progressValueString}");
        }

        return element;
    }
}

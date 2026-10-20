using System.Globalization;
using System.Xml.Linq;

namespace ToastAOT.Builder;

/// <summary>Windows App SDK style content builder. BuildNotification snapshots the XML without changing the builder.</summary>
public sealed class AppNotificationBuilder
{
    private readonly Dictionary<string, string> _arguments = new(StringComparer.Ordinal);
    private readonly List<XElement> _texts = [];
    private readonly Dictionary<string, XElement> _images = [];
    private readonly List<XElement> _inputs = [];
    private readonly List<AppNotificationButton> _buttons = [];
    private readonly List<AppNotificationComboBox> _combos = [];
    private readonly List<AppNotificationProgressBar> _progress = [];
    private XElement? _attribution;
    private XElement? _audio;
    private DateTimeOffset? _timestamp;
    private AppNotificationScenario _scenario;
    private AppNotificationDuration _duration;
    private string _tag = "";
    private string _group = "";

    public AppNotificationBuilder AddArgument(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _arguments[key] = value;
        return this;
    }

    public AppNotificationBuilder SetTimeStamp(DateTimeOffset value)
    {
        _timestamp = value;
        return this;
    }

    public AppNotificationBuilder SetDuration(AppNotificationDuration duration)
    {
        if (!Enum.IsDefined(duration))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _duration = duration;
        return this;
    }

    public AppNotificationBuilder SetScenario(AppNotificationScenario value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _scenario = value;
        return this;
    }

    public static bool IsUrgentScenarioSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    }

    public AppNotificationBuilder AddText(string text)
    {
        return AddText(text, new());
    }

    public AppNotificationBuilder AddText(string text, AppNotificationTextProperties properties)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentOutOfRangeException.ThrowIfNegative(properties.MaxLines, nameof(properties));

        var element = new XElement("text", text);
        if (properties.Language != null)
        {
            element.SetAttributeValue("lang", properties.Language);
        }

        if (properties.IncomingCallAlignment)
        {
            element.SetAttributeValue("hint-callScenarioCenterAlign", "true");
        }

        if (properties.MaxLines > 0)
        {
            element.SetAttributeValue("hint-maxLines", properties.MaxLines);
        }

        _texts.Add(element);
        if (properties.IncomingCallAlignment)
        {
            _scenario = AppNotificationScenario.IncomingCall;
        }

        return this;
    }

    public AppNotificationBuilder SetAttributionText(string text, string? language = null)
    {
        _attribution = new XElement("text", new XAttribute("placement", "attribution"), text);
        if (language != null)
        {
            _attribution.SetAttributeValue("lang", language);
        }

        return this;
    }

    public AppNotificationBuilder SetInlineImage(Uri imageUri, AppNotificationImageCrop imageCrop = AppNotificationImageCrop.Default, string? alternateText = null)
    {
        return Image("inline", imageUri, imageCrop, alternateText);
    }

    public AppNotificationBuilder SetAppLogoOverride(Uri imageUri, AppNotificationImageCrop imageCrop = AppNotificationImageCrop.Default, string? alternateText = null)
    {
        return Image("appLogoOverride", imageUri, imageCrop, alternateText);
    }

    public AppNotificationBuilder SetHeroImage(Uri imageUri, string? alternateText = null)
    {
        return Image("hero", imageUri, AppNotificationImageCrop.Default, alternateText);
    }

    private AppNotificationBuilder Image(string placement, Uri uri, AppNotificationImageCrop crop, string? alt)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Enum.IsDefined(crop))
        {
            throw new ArgumentOutOfRangeException(nameof(crop));
        }

        var element = new XElement("image", new XAttribute("src", uri.AbsoluteUri));
        if (placement != "inline")
        {
            element.SetAttributeValue("placement", placement);
        }

        if (crop == AppNotificationImageCrop.Circle)
        {
            element.SetAttributeValue("hint-crop", "circle");
        }

        if (alt != null)
        {
            element.SetAttributeValue("alt", alt);
        }

        _images[placement] = element;
        return this;
    }

    public AppNotificationBuilder SetAudioUri(Uri audioUri, AppNotificationAudioLooping loop = AppNotificationAudioLooping.None)
    {
        ArgumentNullException.ThrowIfNull(audioUri);
        if (!Enum.IsDefined(loop))
        {
            throw new ArgumentOutOfRangeException(nameof(loop));
        }

        _audio = new XElement("audio", new XAttribute("src", audioUri.AbsoluteUri));
        if (loop == AppNotificationAudioLooping.Loop)
        {
            _audio.SetAttributeValue("loop", "true");
        }

        return this;
    }

    public AppNotificationBuilder SetAudioEvent(AppNotificationSoundEvent sound, AppNotificationAudioLooping loop = AppNotificationAudioLooping.None)
    {
        if (!Enum.IsDefined(sound))
        {
            throw new ArgumentOutOfRangeException(nameof(sound));
        }

        string name = sound.ToString();
        string prefix = sound >= AppNotificationSoundEvent.Alarm ? "Looping." : "";
        return SetAudioUri(new Uri("ms-winsoundevent:Notification." + prefix + name), loop);
    }

    public AppNotificationBuilder MuteAudio()
    {
        _audio = new XElement("audio", new XAttribute("silent", "true"));
        return this;
    }

    public AppNotificationBuilder AddTextBox(string id, string? placeHolderText = null, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _inputs.Add(
            new XElement(
                "input",
                new XAttribute("id", id),
                new XAttribute("type", "text"),
                placeHolderText == null ? null : new XAttribute("placeHolderContent", placeHolderText),
                title == null ? null : new XAttribute("title", title)));

        return this;
    }

    public AppNotificationBuilder AddButton(AppNotificationButton value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _buttons.Add(value);
        return this;
    }

    public AppNotificationBuilder AddComboBox(AppNotificationComboBox value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _combos.Add(value);
        return this;
    }

    public AppNotificationBuilder AddProgressBar(AppNotificationProgressBar value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _progress.Add(value);
        return this;
    }

    public AppNotificationBuilder SetTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _tag = value;
        return this;
    }

    public AppNotificationBuilder SetGroup(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _group = value;
        return this;
    }

    public AppNotification BuildNotification()
    {
        if (_texts.Count > 3 || _buttons.Count > 5 || _inputs.Count + _combos.Count > 5)
        {
            throw new InvalidOperationException("Toast limits: three text elements, five buttons and five inputs.");
        }

        var inputs = _inputs
            .Concat(_combos.Select(combo => combo.ToXml()))
            .Select(input => new XElement(input))
            .ToArray();

        if (inputs.Select(input => (string?)input.Attribute("id")).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
        {
            throw new InvalidOperationException("Input IDs must be unique.");
        }

        var textInputIds = _inputs
            .Select(input => (string?)input.Attribute("id"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var button in _buttons)
        {
            if (button.InputId != null && !textInputIds.Contains(button.InputId))
            {
                throw new InvalidOperationException("A button input ID must refer to a text box.");
            }
        }

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        foreach (var text in _texts)
        {
            binding.Add(new XElement(text));
        }

        if (_attribution != null)
        {
            binding.Add(new XElement(_attribution));
        }

        foreach (var image in _images.Values)
        {
            binding.Add(new XElement(image));
        }

        foreach (var progress in _progress)
        {
            binding.Add(progress.ToXml());
        }

        if (!binding.HasElements)
        {
            throw new InvalidOperationException("A notification needs visual content.");
        }

        var root = new XElement(
            "toast",
            new XAttribute("launch", AppNotificationArgumentCodec.Encode(_arguments)),
            new XAttribute("activationType", "foreground"));

        if (_timestamp.HasValue)
        {
            root.SetAttributeValue("displayTimestamp", _timestamp.Value.ToString("O", CultureInfo.InvariantCulture));
        }

        if (_duration == AppNotificationDuration.Long)
        {
            root.SetAttributeValue("duration", "long");
        }

        if (_scenario != AppNotificationScenario.Default)
        {
            root.SetAttributeValue("scenario", _scenario switch
            {
                AppNotificationScenario.IncomingCall => "incomingCall",
                AppNotificationScenario.Urgent => "urgent",
                _ => _scenario.ToString().ToLowerInvariant()
            });
        }

        if (_buttons.Any(button => button.ButtonStyle != AppNotificationButtonStyle.Default))
        {
            root.SetAttributeValue("useButtonStyle", "true");
        }

        root.Add(new XElement("visual", binding));
        if (inputs.Length > 0 || _buttons.Count > 0)
        {
            var actions = new XElement("actions", inputs);
            foreach (var button in _buttons)
            {
                actions.Add(button.ToXml());
            }

            root.Add(actions);
        }

        if (_audio != null)
        {
            root.Add(new XElement(_audio));
        }

        var notification = new AppNotification(root.ToString(SaveOptions.DisableFormatting))
        {
            Tag = _tag,
            Group = _group
        };
        _ = notification.Snapshot();
        return notification;
    }
}

using System.Text;
using System.Xml;

namespace ToastAOT;

/// <summary>Dependency-free XML builder for the Windows ToastGeneric schema.</summary>
public sealed class ToastBuilder
{
    private readonly List<string> _texts = [];
    private readonly List<(string Uri, ToastImagePlacement Placement, bool CropCircle, string? Alt)> _images = [];
    private readonly List<(string Caption, string Argument, ToastActivationType Kind, string? InputId, bool ContextMenu)> _buttons = [];
    private readonly List<(string Id, string? Placeholder, string? Title, string? Default)> _textInputs = [];
    private readonly List<(string Id, string? Default, string? Title, List<(string Id, string Text)> Items)> _selections = [];
    private readonly Dictionary<string, string> _arguments = new(StringComparer.Ordinal);
    private ToastScenario _scenario;
    private ToastDuration _duration;
    private bool _silent;
    private string? _audio;
    private bool _audioLoop;
    private string? _attribution;
    private DateTimeOffset? _timestamp;
    private string? _tag;
    private string? _group;
    private bool _suppress;
    private ToastPriority _priority;
    private string? _rawLaunch;
    private (string Id, string Title, string Arguments)? _header;
    private readonly List<(string Title, double Value, string Status, string? ValueText)> _progress = [];
    private ToastProgressData? _progressData;
    private DateTimeOffset? _expiration;

    public ToastBuilder SetExpiration(DateTimeOffset expiration)
    {
        _expiration = expiration;
        return this;
    }

    /// <summary>Adds one progress bar bound to the keys used by ToastHost.Update.</summary>
    public ToastBuilder AddProgressBar(ToastProgressData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Validate();
        _progressData = data;
        return this;
    }

    public ToastBuilder AddText(string text)
    {
        _texts.Add(text ?? throw new ArgumentNullException(nameof(text)));
        return this;
    }

    public ToastBuilder AddArgument(string key, string value)
    {
        _arguments.Add(key, value);
        return this;
    }

    public ToastBuilder SetLaunchArguments(string raw)
    {
        _rawLaunch = raw;
        return this;
    }

    public ToastBuilder AddAttribution(string text)
    {
        _attribution = text;
        return this;
    }

    public ToastBuilder SetDisplayTimestamp(DateTimeOffset time)
    {
        _timestamp = time;
        return this;
    }

    public ToastBuilder SetScenario(ToastScenario scenario)
    {
        _scenario = scenario;
        return this;
    }

    public ToastBuilder SetDuration(ToastDuration duration)
    {
        _duration = duration;
        return this;
    }

    public ToastBuilder SetSilent(bool silent = true)
    {
        _silent = silent;
        return this;
    }

    public ToastBuilder SetAudio(string uri, bool loop = false)
    {
        _audio = uri;
        _audioLoop = loop;
        return this;
    }

    public ToastBuilder SetIdentity(string tag, string? group = null)
    {
        _tag = tag;
        _group = group;
        return this;
    }

    public ToastBuilder SetSuppressPopup(bool suppress = true)
    {
        _suppress = suppress;
        return this;
    }

    public ToastBuilder SetPriority(ToastPriority priority)
    {
        _priority = priority;
        return this;
    }

    public ToastBuilder AddImage(string uri, ToastImagePlacement placement = ToastImagePlacement.Inline, bool cropCircle = false, string? alt = null)
    {
        _images.Add((uri, placement, cropCircle, alt));
        return this;
    }

    public ToastBuilder AddButton(string caption, string arguments, ToastActivationType activation = ToastActivationType.Foreground, string? hintInputId = null)
    {
        _buttons.Add((caption, arguments, activation, hintInputId, false));
        return this;
    }

    public ToastBuilder AddContextMenuItem(string caption, string arguments)
    {
        _buttons.Add((caption, arguments, ToastActivationType.Foreground, null, true));
        return this;
    }

    public ToastBuilder AddHeader(string id, string title, string arguments)
    {
        _header = (id, title, arguments);
        return this;
    }

    public ToastBuilder AddProgress(string title, double value, string status, string? valueText = null)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Progress must be between 0 and 1.");
        }

        _progress.Add((title, value, status, valueText));
        return this;
    }

    public ToastBuilder AddAction(string caption, string actionId, string? hintInputId = null)
    {
        return AddButton(caption, EncodeArguments(new Dictionary<string, string> { ["action"] = actionId }), ToastActivationType.Foreground, hintInputId);
    }

    public ToastBuilder AddTextInput(string id, string? placeholder = null, string? title = null, string? defaultInput = null)
    {
        _textInputs.Add((id, placeholder, title, defaultInput));
        return this;
    }

    public ToastBuilder AddSelectionInput(string id, IEnumerable<(string Id, string Text)> options,
        string? defaultId = null, string? title = null)
    {
        _selections.Add((id, defaultId, title, [.. options]));
        return this;
    }

    public ToastMessage Build()
    {
        if (_texts.Count == 0)
        {
            throw new InvalidOperationException("A toast needs at least one text element.");
        }

        if (_texts.Count > 3)
        {
            throw new InvalidOperationException("A toast supports up to three text elements.");
        }

        if (!Enum.IsDefined(_scenario) || !Enum.IsDefined(_duration) || !Enum.IsDefined(_priority))
        {
            throw new InvalidOperationException("Unknown notification enum value.");
        }

        foreach (var image in _images)
        {
            if (!Enum.IsDefined(image.Placement))
            {
                throw new InvalidOperationException("Unknown notification enum value.");
            }
        }

        foreach (var button in _buttons)
        {
            if (!Enum.IsDefined(button.Kind))
            {
                throw new InvalidOperationException("Unknown notification enum value.");
            }
        }

        var textInputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in _textInputs)
        {
            if (string.IsNullOrWhiteSpace(input.Id) || !textInputIds.Add(input.Id))
            {
                throw new InvalidOperationException("Input IDs must be nonempty and unique.");
            }
        }

        var inputIds = new HashSet<string>(textInputIds, StringComparer.Ordinal);
        foreach (var selection in _selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Id) || !inputIds.Add(selection.Id))
            {
                throw new InvalidOperationException("Input IDs must be nonempty and unique.");
            }
        }

        foreach (var button in _buttons)
        {
            if (button.InputId != null && !textInputIds.Contains(button.InputId))
            {
                throw new InvalidOperationException("A button input hint must refer to an existing text input.");
            }
        }

        foreach (var selection in _selections)
        {
            if (selection.Items.Count is < 1 or > 5)
            {
                throw new InvalidOperationException("Selections require one to five unique IDs and a matching default.");
            }

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in selection.Items)
            {
                if (string.IsNullOrWhiteSpace(option.Id) || !optionIds.Add(option.Id))
                {
                    throw new InvalidOperationException("Selections require one to five unique IDs and a matching default.");
                }
            }

            if (selection.Default != null && !optionIds.Contains(selection.Default))
            {
                throw new InvalidOperationException("Selections require one to five unique IDs and a matching default.");
            }
        }

        if (_buttons.Count > 5 || _textInputs.Count + _selections.Count > 5)
        {
            throw new InvalidOperationException("Windows toasts allow up to five buttons (including context items) and five inputs.");
        }

        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Document }))
        {
            writer.WriteStartElement("toast");
            if (_scenario != ToastScenario.Default)
            {
                writer.WriteAttributeString("scenario", _scenario switch
                {
                    ToastScenario.Reminder => "reminder",
                    ToastScenario.Alarm => "alarm",
                    _ => "incomingCall"
                });
            }

            if (_duration == ToastDuration.Long)
            {
                writer.WriteAttributeString("duration", "long");
            }

            writer.WriteAttributeString("activationType", "foreground");
            writer.WriteAttributeString("launch", _rawLaunch ?? EncodeArguments(_arguments));
            if (_timestamp.HasValue)
            {
                writer.WriteAttributeString("displayTimestamp", _timestamp.Value.ToString("O"));
            }

            if (_header.HasValue)
            {
                writer.WriteStartElement("header");
                writer.WriteAttributeString("id", _header.Value.Id);
                writer.WriteAttributeString("title", _header.Value.Title);
                writer.WriteAttributeString("arguments", _header.Value.Arguments);
                writer.WriteEndElement();
            }
            writer.WriteStartElement("visual");
            writer.WriteStartElement("binding");
            writer.WriteAttributeString("template", "ToastGeneric");
            foreach (string text in _texts)
            {
                writer.WriteElementString("text", text);
            }

            if (_attribution != null)
            {
                writer.WriteStartElement("text");
                writer.WriteAttributeString("placement", "attribution");
                writer.WriteString(_attribution);
                writer.WriteEndElement();
            }
            foreach (var image in _images)
            {
                writer.WriteStartElement("image");
                writer.WriteAttributeString("src", image.Uri);
                if (image.Placement != ToastImagePlacement.Inline)
                {
                    writer.WriteAttributeString("placement", image.Placement == ToastImagePlacement.Hero ? "hero" : "appLogoOverride");
                }

                if (image.CropCircle)
                {
                    writer.WriteAttributeString("hint-crop", "circle");
                }

                if (image.Alt != null)
                {
                    writer.WriteAttributeString("alt", image.Alt);
                }

                writer.WriteEndElement();
            }
            foreach (var progress in _progress)
            {
                writer.WriteStartElement("progress");
                writer.WriteAttributeString("title", progress.Title);
                writer.WriteAttributeString("value", progress.Value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture));
                if (progress.ValueText != null)
                {
                    writer.WriteAttributeString("valueStringOverride", progress.ValueText);
                }

                writer.WriteAttributeString("status", progress.Status);
                writer.WriteEndElement();
            }
            if (_progressData != null)
            {
                writer.WriteStartElement("progress");
                writer.WriteAttributeString("title", "{progressTitle}");
                writer.WriteAttributeString("value", "{progressValue}");
                writer.WriteAttributeString("status", "{progressStatus}");
                writer.WriteAttributeString("valueStringOverride", "{progressValueString}");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            if (_textInputs.Count != 0 || _selections.Count != 0 || _buttons.Count != 0)
            {
                writer.WriteStartElement("actions");
                foreach (var input in _textInputs)
                {
                    writer.WriteStartElement("input");
                    writer.WriteAttributeString("id", input.Id);
                    writer.WriteAttributeString("type", "text");
                    if (input.Placeholder != null)
                    {
                        writer.WriteAttributeString("placeHolderContent", input.Placeholder);
                    }

                    if (input.Title != null)
                    {
                        writer.WriteAttributeString("title", input.Title);
                    }

                    if (input.Default != null)
                    {
                        writer.WriteAttributeString("defaultInput", input.Default);
                    }

                    writer.WriteEndElement();
                }
                foreach (var selection in _selections)
                {
                    if (selection.Items.Count > 5)
                    {
                        throw new InvalidOperationException("Maximum of five selection options.");
                    }

                    writer.WriteStartElement("input");
                    writer.WriteAttributeString("id", selection.Id);
                    writer.WriteAttributeString("type", "selection");
                    if (selection.Default != null)
                    {
                        writer.WriteAttributeString("defaultInput", selection.Default);
                    }

                    if (selection.Title != null)
                    {
                        writer.WriteAttributeString("title", selection.Title);
                    }

                    foreach (var option in selection.Items)
                    {
                        writer.WriteStartElement("selection");
                        writer.WriteAttributeString("id", option.Id);
                        writer.WriteAttributeString("content", option.Text);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                foreach (var button in _buttons)
                {
                    writer.WriteStartElement("action");
                    writer.WriteAttributeString("content", button.Caption);
                    writer.WriteAttributeString("arguments", button.Argument);
                    writer.WriteAttributeString("activationType", button.Kind == ToastActivationType.Protocol ? "protocol" : "foreground");
                    if (button.InputId != null)
                    {
                        writer.WriteAttributeString("hint-inputId", button.InputId);
                    }

                    if (button.ContextMenu)
                    {
                        writer.WriteAttributeString("placement", "contextMenu");
                    }

                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            if (_silent || _audio != null)
            {
                writer.WriteStartElement("audio");
                if (_silent)
                {
                    writer.WriteAttributeString("silent", "true");
                }
                else
                {
                    writer.WriteAttributeString("src", _audio!);
                    if (_audioLoop)
                    {
                        writer.WriteAttributeString("loop", "true");
                    }
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        var message = new ToastMessage(output.ToString())
        {
            Tag = _tag,
            Group = _group,
            SuppressPopup = _suppress,
            Priority = _priority,
            Expiration = _expiration,
            Progress = _progressData
        };
        message.Validate();
        return message;
    }

    public static string EncodeArguments(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var encodedArguments = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (encodedArguments.Length > 0)
            {
                encodedArguments.Append('&');
            }

            encodedArguments.Append(Uri.EscapeDataString(pair.Key));
            encodedArguments.Append('=');
            encodedArguments.Append(Uri.EscapeDataString(pair.Value));
        }

        return encodedArguments.ToString();
    }
}

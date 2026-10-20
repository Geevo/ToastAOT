using System.Text;
using System.Xml;

namespace ToastAOT;

/// <summary>Notification content together with native delivery properties.</summary>
public sealed record ToastMessage(string Xml)
{
    public string? Tag { get; init; }

    public string? Group { get; init; }

    public bool SuppressPopup { get; init; }

    public ToastPriority Priority { get; init; }

    public DateTimeOffset? Expiration { get; init; }

    public ToastProgressData? Progress { get; init; }

    public bool ExpiresOnReboot { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Xml);
        if (Encoding.UTF8.GetByteCount(Xml) > 5120)
        {
            throw new ArgumentException("Toast XML exceeds the 5 KB payload limit.");
        }

        if (!Enum.IsDefined(Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(Priority));
        }

        ValidateIdentifier(Tag, nameof(Tag));
        ValidateIdentifier(Group, nameof(Group));
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(Xml), settings);
        var document = System.Xml.Linq.XDocument.Load(reader);
        if (document.Root?.Name != "toast")
        {
            throw new ArgumentException("Expected an unqualified toast root element.");
        }

        Progress?.Validate();
    }

    internal static void ValidateIdentifier(string? value, string name)
    {
        if (value != null && (value.Length > 64 || value.Contains('\0')))
        {
            throw new ArgumentException("Notification identifiers must be at most 64 characters and contain no NUL.", name);
        }
    }
}

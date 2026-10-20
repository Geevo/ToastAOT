using System.Xml.Linq;

namespace ToastAOT.Builder;

public sealed class AppNotificationComboBox
{
    private IDictionary<string, string> _items = new Dictionary<string, string>(StringComparer.Ordinal);

    public AppNotificationComboBox(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    internal string Id { get; }

    public IDictionary<string, string> Items
    {
        get
        {
            return _items;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _items = value;
        }
    }

    public string? Title { get; set; }

    public string? SelectedItem { get; set; }

    public AppNotificationComboBox AddItem(string id, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(content);
        Items[id] = content;
        return this;
    }

    public AppNotificationComboBox SetTitle(string value)
    {
        Title = value;
        return this;
    }

    public AppNotificationComboBox SetSelectedItem(string id)
    {
        SelectedItem = id;
        return this;
    }

    internal XElement ToXml()
    {
        if (Items.Count is < 1 or > 5 || Items.Keys.Any(string.IsNullOrWhiteSpace) || SelectedItem != null && !Items.ContainsKey(SelectedItem))
        {
            throw new InvalidOperationException("Combo boxes require one to five unique items and a matching selected item.");
        }

        var input = new XElement(
            "input",
            new XAttribute("id", Id),
            new XAttribute("type", "selection"));

        input.SetAttributeValue("title", Title);
        input.SetAttributeValue("defaultInput", SelectedItem);

        foreach (var item in Items)
        {
            input.Add(
                new XElement("selection",
                new XAttribute("id", item.Key),
                new XAttribute("content", item.Value)));
        }

        return input;
    }
}

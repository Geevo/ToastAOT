namespace ToastAOT;

/// <summary>Persistent identifiers travel with the notification; delegates do not.</summary>
public sealed class ToastActivation
{
    public string AppId { get; }

    public string RawArguments { get; }

    public IReadOnlyDictionary<string, string> Arguments { get; }

    public IReadOnlyDictionary<string, string> UserInput { get; }

    public string? ActionId
    {
        get
        {
            return Arguments.TryGetValue("action", out string? action) ? action : null;
        }
    }

    public ToastActivation(string appId, string rawArguments, IReadOnlyDictionary<string, string> userInput)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(rawArguments);
        ArgumentNullException.ThrowIfNull(userInput);
        AppId = appId;
        RawArguments = rawArguments;
        Arguments = ParseArguments(rawArguments);
        UserInput = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(userInput, StringComparer.Ordinal));
    }

    public static IReadOnlyDictionary<string, string> ParseArguments(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw))
        {
            return result.AsReadOnly();
        }

        foreach (string part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = part.IndexOf('=');
            string key = idx < 0 ? part : part[..idx];
            string value = idx < 0 ? "" : part[(idx + 1)..];
            // Toast argument strings are URL-encoded by ToastBuilder, but callers can also supply raw strings.
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result.AsReadOnly();
    }
}

using System.Text;

namespace ToastAOT;

// AppNotificationBuilder uses the SDK's semicolon format. Keep the legacy
// ToastBuilder URL-query format separate, including its plus-to-space decoding.
internal static class AppNotificationArgumentCodec
{
    internal static string Encode(IEnumerable<KeyValuePair<string, string>> arguments)
    {
        var encodedArguments = new List<string>();
        foreach (KeyValuePair<string, string> pair in arguments)
        {
            ArgumentException.ThrowIfNullOrEmpty(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);

            string encodedKey = Escape(pair.Key);
            string encodedValue = Escape(pair.Value);
            string encodedArgument = pair.Value.Length == 0
                ? encodedKey
                : encodedKey + "=" + encodedValue;

            encodedArguments.Add(encodedArgument);
        }

        return string.Join(";", encodedArguments);
    }

    private static string Escape(string value)
    {
        return value.Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D");
    }

    internal static IReadOnlyDictionary<string, string> Parse(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Decode(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? "" : Decode(pair[(equals + 1)..]);
            result[key] = value;
        }

        return result.AsReadOnly();
    }

    private static string Decode(string value)
    {
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; ++i)
        {
            if (i + 2 < value.Length && value[i] == '%')
            {
                char decoded = value.Substring(i, 3) switch
                {
                    "%25" => '%',
                    "%3B" => ';',
                    "%3D" => '=',
                    _ => '\0'
                };
                if (decoded != '\0')
                {
                    result.Append(decoded);
                    i += 2;
                    continue;
                }
            }
            result.Append(value[i]);
        }
        return result.ToString();
    }
}

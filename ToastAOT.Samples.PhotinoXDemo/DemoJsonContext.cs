using System.Text.Json.Serialization;

namespace ToastAOT.Samples.PhotinoXDemo;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DemoSnapshot))]
internal partial class DemoJsonContext : JsonSerializerContext
{
}

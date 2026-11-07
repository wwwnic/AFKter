using System.Text.Json;
using System.Text.Json.Serialization;
namespace AFKtor.Diagnostics;

public static class DiagnosticJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
public sealed record DiscoveryDescriptor(int SchemaVersion, Guid RunId, string Build, string Endpoint, string Token);

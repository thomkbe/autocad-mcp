using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoCadMcp.Protocol;

// Wire format between the MCP server and the AutoCAD plugin. Each connection to the named
// pipe carries exactly one request line and one response line of UTF-8 JSON.

public sealed record PipeRequest(string Command, JsonObject? Args = null);

public sealed record PipeResponse(bool Ok, JsonNode? Result = null, string? Error = null)
{
    public static PipeResponse Success(JsonNode? result) => new(true, result);

    public static PipeResponse Failure(string error) => new(false, Error: error);
}

public static class PipeProtocol
{
    public const string DefaultPipeName = "autocad-mcp";

    /// <summary>Environment variable that overrides the pipe name on both sides.</summary>
    public const string PipeNameVariable = "AUTOCAD_MCP_PIPE";

    /// <summary>
    /// How long the plugin waits for AutoCAD's main thread to pick up a request before
    /// dropping it unexecuted. The MCP server waits longer, so it always hears the outcome.
    /// </summary>
    public static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(10);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string ResolvePipeName() =>
        Environment.GetEnvironmentVariable(PipeNameVariable) is { Length: > 0 } name ? name : DefaultPipeName;

    public static byte[] Frame<T>(T message) =>
        Encoding.GetBytes(JsonSerializer.Serialize(message, Json) + "\n");
}

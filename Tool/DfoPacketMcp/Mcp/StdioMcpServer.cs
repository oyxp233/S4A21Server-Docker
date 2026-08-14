using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfoPacketMcp.Mcp;

public sealed class StdioMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly PacketToolService _tools;

    public StdioMcpServer(PacketToolService tools) => _tools = tools;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var hasId = root.TryGetProperty("id", out var id);
                var method = root.GetProperty("method").GetString() ?? string.Empty;
                if (!hasId)
                {
                    if (method == "notifications/initialized") continue;
                    continue;
                }
                var result = Handle(method, root.TryGetProperty("params", out var parameters) ? parameters : default);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = CloneId(id), result }, JsonOptions));
            }
            catch (Exception exception)
            {
                JsonElement? id = null;
                if (document is not null && document.RootElement.TryGetProperty("id", out var requestId)) id = requestId.Clone();
                var error = new
                {
                    code = exception is KeyNotFoundException ? -32602 : -32603,
                    message = exception.Message,
                    data = exception.GetType().Name,
                };
                await output.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error }, JsonOptions));
            }
            finally
            {
                document?.Dispose();
            }
        }
    }

    private object Handle(string method, JsonElement parameters)
        => method switch
        {
            "initialize" => Initialize(parameters),
            "ping" => new { },
            "tools/list" => new { tools = ToolDefinitions },
            "tools/call" => CallTool(parameters),
            _ => throw new KeyNotFoundException($"unsupported MCP method: {method}"),
        };

    private static object Initialize(JsonElement parameters)
    {
        var version = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var value)
            ? value.GetString() ?? "2025-06-18"
            : "2025-06-18";
        return new
        {
            protocolVersion = version,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "dfo-packet-mcp", version = "0.2.9" },
            instructions = "Always select flow (c2s/s2c), kind (cmd/noti), type, and variant. Same numeric type may have different names and body schemas by flow or subtype.",
        };
    }

    private object CallTool(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString() ?? string.Empty;
        var arguments = parameters.TryGetProperty("arguments", out var value) ? value : default;
        object result = name switch
        {
            "list_packets" => _tools.ListPackets(arguments),
            "describe_packet" => _tools.DescribePacket(arguments),
            "decode_packet" => _tools.DecodePacket(arguments),
            "compare_packets" => _tools.ComparePackets(arguments),
            "decode_body" => _tools.DecodeBody(arguments),
            "encode_packet" => _tools.EncodePacket(arguments),
            "decode_capture" => _tools.DecodeCapture(arguments),
            "protocol_coverage" => _tools.GetCoverage(),
            _ => throw new KeyNotFoundException($"unknown tool: {name}"),
        };
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return new { content = new[] { new { type = "text", text = json } }, structuredContent = result, isError = false };
    }

    private static object CloneId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? string.Empty,
        JsonValueKind.Number when id.TryGetInt64(out var number) => number,
        _ => id.GetRawText(),
    };

    private static readonly object[] ToolDefinitions =
    {
        Tool("list_packets", "List packet definitions without merging identical numeric types across flow or kind.", new
        {
            type = "object", properties = new
            {
                flow = EnumString("c2s", "s2c"), kind = EnumString("cmd", "noti"),
                supportedOnly = new { type = "boolean", @default = true }, query = new { type = "string" },
                limit = new { type = "integer", minimum = 1, maximum = 2000 }, offset = new { type = "integer", minimum = 0 },
            }
        }),
        Tool("describe_packet", "Describe one direction-specific packet and its body variants.", new
        {
            type = "object", required = new[] { "flow", "kind", "packet" }, properties = new
            {
                flow = EnumString("c2s", "s2c"), kind = EnumString("cmd", "noti"),
                packet = new { type = "string", description = "Hex type, decimal type, enum name, or full direction-specific name" },
            }
        }),
        Tool("decode_packet", "Decode a full 13-byte ingress or 15-byte egress packet using explicit flow.", BinaryPacketSchema(true)),
        Tool("compare_packets", "Compare two raw packets byte-by-byte, envelope fields, opcode body fields, and variants. Flow is optional and can be auto-detected per raw.", new
        {
            type = "object", required = new[] { "rawA", "rawB" }, properties = new
            {
                flow = EnumString("c2s", "s2c"), transport = EnumString("auto", "ingress", "egress"),
                transportA = EnumString("auto", "ingress", "egress"), transportB = EnumString("auto", "ingress", "egress"),
                rawA = new { type = "string" }, rawB = new { type = "string" },
                base64A = new { type = "string" }, base64B = new { type = "string" },
                variantA = new { type = "string" }, variantB = new { type = "string" },
            }
        }),
        Tool("decode_body", "Decode a body using an explicit flow/kind/type definition and select polymorphic variants.", new
        {
            type = "object", required = new[] { "flow", "kind", "packet" }, properties = new
            {
                flow = EnumString("c2s", "s2c"), kind = EnumString("cmd", "noti"), packet = new { type = "string" },
                variant = new { type = "string", description = "Optional explicit body variant for context-discriminated packets" },
                bodyHex = new { type = "string" }, bodyBase64 = new { type = "string" },
            }
        }),
        Tool("encode_packet", "Encode fields or a raw body into a direction-specific packet envelope.", new
        {
            type = "object", required = new[] { "flow", "kind", "packet" }, properties = new
            {
                flow = EnumString("c2s", "s2c"), kind = EnumString("cmd", "noti"), packet = new { type = "string" },
                variant = new { type = "string" }, fields = new { type = "object" }, bodyHex = new { type = "string" },
                bodyBase64 = new { type = "string" }, transport = EnumString("auto", "ingress", "egress"),
            }
        }),
        Tool("decode_capture", "Decode DfoServer packet_log.txt text or a local capture path.", new
        {
            type = "object", properties = new { text = new { type = "string" }, path = new { type = "string" }, limit = new { type = "integer", minimum = 1, maximum = 5000 } }
        }),
        Tool("protocol_coverage", "Report catalog and schema coverage split by flow and kind.", new { type = "object", properties = new { } }),
    };

    private static object BinaryPacketSchema(bool requireFlow) => new
    {
        type = "object", required = requireFlow ? new[] { "flow" } : Array.Empty<string>(), properties = new
        {
            flow = EnumString("c2s", "s2c"), transport = EnumString("auto", "ingress", "egress"),
            hex = new { type = "string" }, base64 = new { type = "string" },
        }
    };
    private static object EnumString(params string[] values) => new { type = "string", @enum = values };
    private static object Tool(string name, string description, object inputSchema) => new { name, description, inputSchema };
}





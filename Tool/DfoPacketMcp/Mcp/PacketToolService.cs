using System.Text.Json;
using System.Text.RegularExpressions;
using DfoPacketMcp.Protocol;

namespace DfoPacketMcp.Mcp;

public sealed class PacketToolService
{
    private static readonly Regex CaptureHeader = new(
        @"^.*?(?<flow>SEND|RECV)\b.*?(?:(?:cmd|command)=0x(?<cmd>[0-9A-Fa-f]{2})).*?type=0x(?<type>[0-9A-Fa-f]{4}).*$",
        RegexOptions.Compiled);
    private static readonly Regex CaptureRaw = new(@"^\s*raw:\s*(?<raw>.+)$", RegexOptions.Compiled);

    private readonly ProtocolCatalog _catalog;
    private readonly PacketDecoder _decoder;

    public PacketToolService(ProtocolCatalog catalog)
    {
        _catalog = catalog;
        _decoder = new PacketDecoder(catalog);
    }

    public object ListPackets(JsonElement arguments)
    {
        var flow = TryGetString(arguments, "flow", out var flowText) ? PacketInput.ParseFlow(flowText) : (PacketFlow?)null;
        var kind = TryGetString(arguments, "kind", out var kindText) ? PacketInput.ParseKind(kindText) : (PacketKind?)null;
        var supportedOnly = GetBool(arguments, "supportedOnly", true);
        var query = TryGetString(arguments, "query", out var queryText) ? queryText : string.Empty;
        var limit = Math.Clamp(GetInt32(arguments, "limit", 200), 1, 2000);
        var offset = Math.Max(0, GetInt32(arguments, "offset", 0));

        var filtered = _catalog.Types
            .Where(item => !flow.HasValue || item.Flow == flow.Value)
            .Where(item => !kind.HasValue || item.Kind == kind.Value)
            .Where(item => !supportedOnly || item.Supported)
            .Where(item => string.IsNullOrWhiteSpace(query) ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.EnumName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                $"0x{item.Type:X4}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Flow).ThenBy(item => item.Kind).ThenBy(item => item.Type)
            .ToArray();
        return new
        {
            total = filtered.Length,
            offset,
            limit,
            items = filtered.Skip(offset).Take(limit).Select(ToSummary).ToArray(),
        };
    }

    public object DescribePacket(JsonElement arguments)
    {
        var flow = PacketInput.ParseFlow(GetRequiredString(arguments, "flow"));
        var kind = PacketInput.ParseKind(GetRequiredString(arguments, "kind"));
        var packet = _catalog.Find(flow, kind, GetRequiredString(arguments, "packet"))
            ?? throw new KeyNotFoundException("packet definition not found for the selected flow and kind");
        return packet;
    }

    public object DecodePacket(JsonElement arguments)
    {
        var flow = PacketInput.ParseFlow(GetRequiredString(arguments, "flow"));
        var bytes = PacketInput.ParseBytes(GetOptionalString(arguments, "hex"), GetOptionalString(arguments, "base64"));
        var transport = PacketInput.ParseTransport(GetOptionalString(arguments, "transport"), flow);
        var decoded = DecodeWithOptionalVariant(bytes, transport, GetOptionalString(arguments, "variant"));
        if (decoded.Flow != flow)
            throw new InvalidDataException($"decoded flow {decoded.Flow} differs from requested {flow}");
        return ToDecodedResult(decoded);
    }

    public object ComparePackets(JsonElement arguments)
    {
        var rawA = PacketInput.ParseBytes(GetOptionalString(arguments, "rawA"), GetOptionalString(arguments, "base64A"));
        var rawB = PacketInput.ParseBytes(GetOptionalString(arguments, "rawB"), GetOptionalString(arguments, "base64B"));
        if (rawA.Length == 0 || rawB.Length == 0) throw new ArgumentException("rawA and rawB are required");
        var requestedFlow = TryGetString(arguments, "flow", out var flowText) ? PacketInput.ParseFlow(flowText) : (PacketFlow?)null;
        var transportA = ResolveCompareTransport(GetOptionalString(arguments, "transportA") ?? GetOptionalString(arguments, "transport"), requestedFlow, rawA);
        var transportB = ResolveCompareTransport(GetOptionalString(arguments, "transportB") ?? GetOptionalString(arguments, "transport"), requestedFlow, rawB);
        var decodedA = DecodeWithOptionalVariant(rawA, transportA, GetOptionalString(arguments, "variantA"));
        var decodedB = DecodeWithOptionalVariant(rawB, transportB, GetOptionalString(arguments, "variantB"));
        if (requestedFlow.HasValue && (decodedA.Flow != requestedFlow.Value || decodedB.Flow != requestedFlow.Value))
            throw new InvalidDataException("both packets must match the requested flow");

        var maxLength = Math.Max(rawA.Length, rawB.Length);
        var byteDiffs = new List<object>();
        var index = 0;
        while (index < maxLength)
        {
            var equal = index < rawA.Length && index < rawB.Length && rawA[index] == rawB[index];
            if (equal) { index++; continue; }
            var start = index;
            while (index < maxLength)
            {
                var same = index < rawA.Length && index < rawB.Length && rawA[index] == rawB[index];
                if (same) break;
                index++;
            }
            var left = index > start && start < rawA.Length
                ? rawA.AsSpan(start, Math.Min(index, rawA.Length) - start).ToArray()
                : Array.Empty<byte>();
            var right = index > start && start < rawB.Length
                ? rawB.AsSpan(start, Math.Min(index, rawB.Length) - start).ToArray()
                : Array.Empty<byte>();
            byteDiffs.Add(new
            {
                offset = start,
                lengthA = left.Length,
                lengthB = right.Length,
                aHex = Convert.ToHexString(left),
                bHex = Convert.ToHexString(right),
                region = start < Math.Min(decodedA.Header.HeaderSize, decodedB.Header.HeaderSize) ? "envelope" : "body",
            });
        }

        var fieldDiffs = CompareFields(decodedA.Fields, decodedB.Fields);
        var headerDiffs = CompareHeader(decodedA.Header, decodedB.Header);
        var changedByteCount = byteDiffs.Sum(item =>
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(item));
            return Math.Max(
                document.RootElement.GetProperty("lengthA").GetInt32(),
                document.RootElement.GetProperty("lengthB").GetInt32());
        });
        return new
        {
            requestedFlow = requestedFlow.HasValue ? FlowName(requestedFlow.Value) : "auto",
            transportA = transportA.ToString().ToLowerInvariant(),
            transportB = transportB.ToString().ToLowerInvariant(),
            packetA = ToDecodedResult(decodedA),
            packetB = ToDecodedResult(decodedB),
            sameEnvelopeFormat = decodedA.Header.HeaderSize == decodedB.Header.HeaderSize,
            headerEqual = JsonSerializer.Serialize(decodedA.Header) == JsonSerializer.Serialize(decodedB.Header),
            opcodeEqual = decodedA.Header.CommandClass == decodedB.Header.CommandClass && decodedA.Header.Type == decodedB.Header.Type,
            variantEqual = decodedA.Variant.Equals(decodedB.Variant, StringComparison.OrdinalIgnoreCase),
            bodyLengthDelta = decodedB.Body.Length - decodedA.Body.Length,
            changedByteCount,
            byteDiffRangeCount = byteDiffs.Count,
            headerDiffs,
            byteDiffs,
            semanticFieldDiffs = fieldDiffs,
            comparisonDiagnostics = BuildComparisonDiagnostics(decodedA, decodedB),
        };
    }

    public object DecodeBody(JsonElement arguments)
    {
        var flow = PacketInput.ParseFlow(GetRequiredString(arguments, "flow"));
        var kind = PacketInput.ParseKind(GetRequiredString(arguments, "kind"));
        var definition = _catalog.Find(flow, kind, GetRequiredString(arguments, "packet"))
            ?? throw new KeyNotFoundException("packet definition not found for the selected flow and kind");
        var body = PacketInput.ParseBytes(GetOptionalString(arguments, "bodyHex"), GetOptionalString(arguments, "bodyBase64"));
        var diagnostics = new List<string>();
        var requestedVariant = GetOptionalString(arguments, "variant");
        var decoded = PacketSchemaRegistry.Decode(definition, body, diagnostics, requestedVariant);
        return new
        {
            definition.Name,
            flow = FlowName(definition.Flow),
            kind = definition.Kind.ToString().ToLowerInvariant(),
            type = $"0x{definition.Type:X4}",
            variant = decoded.Variant,
            requestedVariant,
            fields = decoded.Fields,
            diagnostics,
        };
    }

    public object EncodePacket(JsonElement arguments)
    {
        var flow = PacketInput.ParseFlow(GetRequiredString(arguments, "flow"));
        var kind = PacketInput.ParseKind(GetRequiredString(arguments, "kind"));
        var definition = _catalog.Find(flow, kind, GetRequiredString(arguments, "packet"))
            ?? throw new KeyNotFoundException("packet definition not found for the selected flow and kind");
        var variant = GetOptionalString(arguments, "variant");
        var body = TryGetString(arguments, "bodyHex", out var bodyHex) || TryGetString(arguments, "bodyBase64", out _)
            ? PacketInput.ParseBytes(bodyHex, GetOptionalString(arguments, "bodyBase64"))
            : PacketEncoder.EncodeBody(
                definition,
                variant,
                arguments.TryGetProperty("fields", out var fields) ? fields : default);
        var transport = PacketInput.ParseTransport(GetOptionalString(arguments, "transport"), flow);
        var commandClass = kind == PacketKind.Noti ? (byte)0 : (byte)1;
        var packet = PacketDecoder.Encode(commandClass, definition.Type, body, transport);
        return new
        {
            definition.Name,
            flow = FlowName(flow),
            kind = kind.ToString().ToLowerInvariant(),
            type = $"0x{definition.Type:X4}",
            variant = variant ?? "auto",
            transport = transport.ToString().ToLowerInvariant(),
            bodyHex = Convert.ToHexString(body),
            packetHex = Convert.ToHexString(packet),
            packetBase64 = Convert.ToBase64String(packet),
        };
    }

    public object DecodeCapture(JsonElement arguments)
    {
        var text = GetOptionalString(arguments, "text");
        var path = GetOptionalString(arguments, "path");
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(path))
            text = File.ReadAllText(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text or path is required");
        var limit = Math.Clamp(GetInt32(arguments, "limit", 500), 1, 5000);
        var records = new List<object>();
        string? pendingFlow = null;
        foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
        {
            var headerMatch = CaptureHeader.Match(line);
            if (headerMatch.Success)
            {
                pendingFlow = headerMatch.Groups["flow"].Value;
                continue;
            }
            var rawMatch = CaptureRaw.Match(line);
            if (!rawMatch.Success || pendingFlow is null) continue;
            try
            {
                var rawText = rawMatch.Groups["raw"].Value.Trim();
                if (rawText.StartsWith("[", StringComparison.Ordinal))
                {
                    pendingFlow = null;
                    continue;
                }
                var flow = pendingFlow == "SEND" ? PacketFlow.ServerToClient : PacketFlow.ClientToServer;
                var transport = flow == PacketFlow.ServerToClient ? PacketTransport.Egress : PacketTransport.Ingress;
                var decoded = _decoder.Decode(PacketInput.ParseHex(rawText), transport);
                records.Add(ToDecodedResult(decoded));
            }
            catch (Exception exception)
            {
                records.Add(new { flow = pendingFlow, error = exception.Message, raw = rawMatch.Groups["raw"].Value });
            }
            pendingFlow = null;
            if (records.Count >= limit) break;
        }
        return new { count = records.Count, records };
    }

    public object GetCoverage()
    {
        static PacketBodySchema[] Schemas(PacketTypeDefinition item)
            => item.Variants.Select(variant => variant.Schema).Where(schema => schema is not null).Cast<PacketBodySchema>()
                .Concat(item.InferredSchema is null ? Array.Empty<PacketBodySchema>() : new[] { item.InferredSchema })
                .Distinct().ToArray();

        var groups = _catalog.Types.GroupBy(item => new { item.Flow, item.Kind }).Select(group => new
        {
            flow = FlowName(group.Key.Flow),
            kind = group.Key.Kind.ToString().ToLowerInvariant(),
            catalog = group.Count(),
            supported = group.Count(item => item.Supported),
            structured = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Structured),
            partial = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Partial),
            inferred = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Inferred && Schemas(item).Any(schema => schema.Fields.Length > 0)),
            ignoredBody = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Inferred && Schemas(item).Any(schema => schema.BodyIgnored)),
            lengthOnly = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Inferred && Schemas(item).Any(schema => schema.Fields.Length == 0 && !schema.BodyIgnored)),
            empty = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Empty),
            opaque = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.Opaque),
            rawFallback = group.Count(item => item.Supported && item.SchemaStatus == PacketSchemaStatus.RawFallback),
        }).ToArray();
        return new
        {
            catalog = new
            {
                protocolVersion = _catalog.ProtocolVersion,
                sourceIndependent = _catalog.SourceIndependent,
                origin = _catalog.CatalogOrigin,
                sha256 = _catalog.SnapshotSha256,
            },
            groups,
            variants = new
            {
                total = _catalog.Types.Where(item => item.Supported).Sum(item => Math.Max(1, item.Variants.Length)),
                schemaBacked = _catalog.Types.Where(item => item.Supported).Sum(item => item.Variants.Count(variant => variant.Schema is not null)),
                ambiguousDefinitions = _catalog.Types.Count(item => item.Supported && item.Variants.Count(variant => variant.Schema is not null) > 1),
            },
        };
    }

    private static object ToSummary(PacketTypeDefinition item) => new
    {
        item.Name,
        enumName = item.EnumName,
        flow = FlowName(item.Flow),
        kind = item.Kind.ToString().ToLowerInvariant(),
        type = $"0x{item.Type:X4}",
        item.Supported,
        schemaStatus = item.SchemaStatus.ToString().ToLowerInvariant(),
        variantCount = item.Variants.Length,
    };

    private static object ToDecodedResult(ParsedPacket decoded) => new
    {
        flow = FlowName(decoded.Flow),
        kind = decoded.Kind.ToString().ToLowerInvariant(),
        type = $"0x{decoded.Header.Type:X4}",
        name = decoded.Definition?.Name ?? "UNKNOWN",
        enumName = decoded.Definition?.EnumName,
        decoded.Variant,
        header = decoded.Header,
        packetLayout = BuildPacketLayout(decoded),
        bodyHex = decoded.RawBodyHex,
        decoded.Fields,
        decoded.Diagnostics,
    };

    private ParsedPacket DecodeWithOptionalVariant(byte[] bytes, PacketTransport transport, string? variant)
    {
        var decoded = _decoder.Decode(bytes, transport);
        if (string.IsNullOrWhiteSpace(variant) || decoded.Definition is null)
            return decoded;
        var diagnostics = decoded.Diagnostics.ToList();
        var body = PacketSchemaRegistry.Decode(decoded.Definition, decoded.Body, diagnostics, variant);
        return decoded with { Variant = body.Variant, Fields = body.Fields, Diagnostics = diagnostics };
    }

    private static object BuildPacketLayout(ParsedPacket decoded)
    {
        var bytes = decoded.RawPacket.Length > 0 ? decoded.RawPacket : PacketDecoder.Encode(
            decoded.Header.CommandClass, decoded.Header.Type, decoded.Body,
            decoded.Header.HeaderSize == 15 ? PacketTransport.Egress : PacketTransport.Ingress,
            decoded.Header.FirstControl, decoded.Header.SecondControl ?? 0, decoded.Header.Sequence ?? 0);
        var headerSize = decoded.Header.HeaderSize;
        var segments = new List<object>
        {
            Segment(bytes, 0, 1, "commandClass", "u8", decoded.Header.CommandClass, "opcode command class"),
            Segment(bytes, 1, 2, "opcodeType", "u16le", decoded.Header.Type, decoded.Definition?.EnumName ?? "unknown opcode"),
            Segment(bytes, 3, 4, "packetLength", "u32le", decoded.Header.Length, "envelope length"),
            Segment(bytes, 7, 4, "firstControl", "u32le", decoded.Header.FirstControl, "checksum/control"),
        };
        if (headerSize == 15)
            segments.Add(Segment(bytes, 11, 4, "secondControl", "u32le", decoded.Header.SecondControl ?? 0, "egress control"));
        else
            segments.Add(Segment(bytes, 11, 2, "sequence", "u16le", decoded.Header.Sequence ?? 0, "ingress sequence"));
        var bodySchema = GetBodySchema(decoded);
        var schemaFields = bodySchema is null
            ? Array.Empty<object>()
            : bodySchema.Fields.Select(field =>
            {
                var width = PacketSchemaRegistry.FieldWidth(field.Type);
                var absoluteOffset = headerSize + field.Offset;
                var available = field.Offset >= 0 && field.Offset + width <= decoded.Body.Length;
                return (object)new
                {
                    name = field.Name,
                    bodyOffset = field.Offset,
                    absoluteOffset,
                    length = width,
                    type = field.Type,
                    optional = field.Optional,
                    hex = available ? Convert.ToHexString(decoded.Body.AsSpan(field.Offset, width)) : string.Empty,
                    value = available ? PacketSchemaRegistry.ReadField(decoded.Body, field.Type, field.Offset) : null,
                    source = field.Source,
                    available,
                };
            }).ToArray();
        var bodyBytes = decoded.Body.Select((value, offset) => new
        {
            bodyOffset = offset,
            absoluteOffset = headerSize + offset,
            hex = value.ToString("X2"),
            value,
        }).ToArray();
        segments.Add(new
        {
            name = "body",
            offset = headerSize,
            length = decoded.Body.Length,
            hex = Convert.ToHexString(decoded.Body),
            type = "bytes",
            semantic = decoded.Definition?.EnumName ?? "unknown body",
            schema = bodySchema,
            fields = schemaFields,
            bytes = bodyBytes,
        });
        return new
        {
            totalLength = bytes.Length,
            headerLength = headerSize,
            opcode = $"0x{decoded.Header.Type:X4}",
            segments,
            bytes = bytes.Select((value, offset) => new { offset, hex = value.ToString("X2"), value }).ToArray(),
        };
    }

    private static PacketTransport ResolveCompareTransport(string? value, PacketFlow? flow, byte[] packet)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return PacketInput.ParseTransport(value, flow ?? PacketFlow.ServerToClient);
        if (flow.HasValue)
            return flow.Value == PacketFlow.ClientToServer ? PacketTransport.Ingress : PacketTransport.Egress;
        if (packet.Length >= 15 && packet[13] == 0 && packet[14] == 0)
            return PacketTransport.Egress;
        return PacketTransport.Ingress;
    }

    private static PacketBodySchema? GetBodySchema(ParsedPacket decoded)
    {
        var definition = decoded.Definition;
        if (definition is null) return null;
        var selected = definition.Variants.FirstOrDefault(item => item.Name.Equals(decoded.Variant, StringComparison.OrdinalIgnoreCase));
        return selected?.Schema ?? definition.InferredSchema;
    }

    private static object Segment(byte[] bytes, int offset, int length, string name, string type, object value, string semantic)
        => new { name, offset, length, hex = Convert.ToHexString(bytes.AsSpan(offset, length)), type, value, semantic };

    private static object[] CompareFields(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
    {
        using var leftDoc = JsonDocument.Parse(JsonSerializer.Serialize(left));
        using var rightDoc = JsonDocument.Parse(JsonSerializer.Serialize(right));
        var a = new Dictionary<string, string?>(StringComparer.Ordinal);
        var b = new Dictionary<string, string?>(StringComparer.Ordinal);
        Flatten(leftDoc.RootElement, string.Empty, a);
        Flatten(rightDoc.RootElement, string.Empty, b);
        return a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).OrderBy(item => item)
            .Where(key => key is not "rawHex" and not "bodyLength" and not "consumedBytes"
                && !key.EndsWith(".rawHex", StringComparison.Ordinal)
                && !key.EndsWith(".bodyLength", StringComparison.Ordinal)
                && !key.EndsWith(".consumedBytes", StringComparison.Ordinal))
            .Where(key => !string.Equals(a.GetValueOrDefault(key), b.GetValueOrDefault(key), StringComparison.Ordinal))
            .Select(key => (object)new { path = key, a = a.GetValueOrDefault(key), b = b.GetValueOrDefault(key) })
            .ToArray();
    }

    private static object[] CompareHeader(PacketHeader left, PacketHeader right)
    {
        var result = new List<object>();
        Add("commandClass", 0, 1, left.CommandClass, right.CommandClass);
        Add("opcodeType", 1, 2, $"0x{left.Type:X4}", $"0x{right.Type:X4}");
        Add("packetLength", 3, 4, left.Length, right.Length);
        Add("firstControl", 7, 4, left.FirstControl, right.FirstControl);
        if (left.HeaderSize == 15 || right.HeaderSize == 15)
            Add("secondControl", 11, 4, left.SecondControl, right.SecondControl);
        if (left.HeaderSize == 13 || right.HeaderSize == 13)
            Add("sequence", 11, 2, left.Sequence, right.Sequence);
        return result.ToArray();

        void Add(string name, int offset, int length, object? a, object? b)
        {
            if (!Equals(a, b)) result.Add(new { name, offset, length, a, b });
        }
    }

    private static string[] BuildComparisonDiagnostics(ParsedPacket left, ParsedPacket right)
    {
        var result = new List<string>();
        if (left.Header.HeaderSize != right.Header.HeaderSize)
            result.Add("packets use different envelope formats (13-byte ingress versus 15-byte egress)");
        if (left.Header.CommandClass != right.Header.CommandClass || left.Header.Type != right.Header.Type)
            result.Add("opcodes differ; body field comparison describes two different packet definitions");
        if (!left.Variant.Equals(right.Variant, StringComparison.OrdinalIgnoreCase))
            result.Add("variants differ; identical opcode values may still use incompatible body structures");
        return result.ToArray();
    }

    private static void Flatten(JsonElement element, string path, IDictionary<string, string?> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Flatten(property.Value, string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}", output);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) Flatten(item, $"{path}[{index++}]", output);
            if (index == 0) output[path] = "[]";
            return;
        }
        output[path] = element.GetRawText();
    }

    private static string FlowName(PacketFlow flow) => flow == PacketFlow.ClientToServer ? "c2s" : "s2c";

    private static string GetRequiredString(JsonElement value, string name)
        => TryGetString(value, name, out var result) ? result : throw new ArgumentException($"{name} is required");
    private static string? GetOptionalString(JsonElement value, string name)
        => TryGetString(value, name, out var result) ? result : null;
    private static bool TryGetString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        result = property.GetString() ?? string.Empty;
        return true;
    }
    private static int GetInt32(JsonElement value, string name, int fallback)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : fallback;
    private static bool GetBool(JsonElement value, string name, bool fallback)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : fallback;
}

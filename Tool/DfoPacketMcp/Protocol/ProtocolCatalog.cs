using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace DfoPacketMcp.Protocol;

public sealed class ProtocolCatalog
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<(PacketFlow, PacketKind, ushort), PacketTypeDefinition> _types = new();

    public IReadOnlyCollection<PacketTypeDefinition> Types => _types.Values;
    public string ProtocolVersion { get; private set; } = "development";
    public bool SourceIndependent { get; private set; }
    public string CatalogOrigin { get; private set; } = "legacy-development-files";
    public string? SnapshotSha256 { get; private set; }

    public static ProtocolCatalog Load(string root)
    {
        var protocolDirectory = Path.Combine(root, "Protocol");
        var snapshotPath = Path.Combine(protocolDirectory, "protocol-catalog.json");
        if (File.Exists(snapshotPath))
            return LoadStandalone(snapshotPath, Path.Combine(protocolDirectory, "protocol-manifest.json"));
        return LoadLegacy(root);
    }

    public static ProtocolCatalog LoadLegacy(string root)
    {
        var catalog = new ProtocolCatalog();
        catalog.CatalogOrigin = "legacy-development-files";
        var commandEnums = LoadEnums(Path.Combine(root, "Protocol", "cmd-enum.json"));
        var notiEnums = LoadEnums(Path.Combine(root, "Protocol", "noti-enum.json"));
        var inbound = LoadSupport(Path.Combine(root, "Protocol", "cmd-supported.json"));
        var inferred = LoadInferred(Path.Combine(root, "Protocol", "cmd-inferred-schemas.json"));
        var outbound = LoadOutbound(Path.Combine(root, "Protocol", "outbound-supported.json"));

        foreach (var entry in commandEnums)
        {
            inbound.TryGetValue(entry.Type, out var inboundSupport);
            inferred.TryGetValue(entry.Type, out var inferredInfo);
            var manualStatus = PacketSchemaRegistry.GetStatus(PacketFlow.ClientToServer, PacketKind.Cmd, entry.Name);
            var status = manualStatus == PacketSchemaStatus.Opaque && inferredInfo is not null
                ? PacketSchemaStatus.Inferred
                : manualStatus;
            var inboundVariants = manualStatus == PacketSchemaStatus.Opaque
                ? inferredInfo?.Variants ?? Array.Empty<PacketVariant>()
                : PacketSchemaRegistry.GetManualVariants(PacketFlow.ClientToServer, PacketKind.Cmd, entry.Name);
            catalog.Add(new PacketTypeDefinition(
                PacketFlow.ClientToServer,
                PacketKind.Cmd,
                entry.Type,
                $"C2S_CMD_{entry.Name}_REQUEST",
                entry.Name,
                inboundSupport is not null,
                status,
                PacketSchemaRegistry.GetSemantic(PacketFlow.ClientToServer, PacketKind.Cmd, entry.Name),
                inboundSupport?.Sources ?? Array.Empty<string>(),
                inboundVariants,
                inferredInfo?.SingleSchema));

            var generatedVariants = outbound.TryGetValue((PacketKind.Cmd, entry.Type), out var commandOutput)
                ? commandOutput.Variants
                : Array.Empty<PacketVariant>();
            var manualVariants = PacketSchemaRegistry.GetManualVariants(PacketFlow.ServerToClient, PacketKind.Cmd, entry.Name);
            var variants = MergeVariants(manualVariants, generatedVariants);
            var commandStatus = PacketSchemaRegistry.GetStatus(PacketFlow.ServerToClient, PacketKind.Cmd, entry.Name);
            commandStatus = PacketSchemaRegistry.HasCompleteSemanticCodec(PacketFlow.ServerToClient, PacketKind.Cmd, entry.Name)
                ? PacketSchemaStatus.Structured
                : GetOutboundStatus(commandStatus, variants);
            catalog.Add(new PacketTypeDefinition(
                PacketFlow.ServerToClient,
                PacketKind.Cmd,
                entry.Type,
                $"S2C_CMD_{entry.Name}_RESPONSE",
                entry.Name,
                commandOutput is not null
                    || PacketSchemaRegistry.HasCompleteSemanticCodec(PacketFlow.ServerToClient, PacketKind.Cmd, entry.Name),
                commandStatus,
                PacketSchemaRegistry.GetSemantic(PacketFlow.ServerToClient, PacketKind.Cmd, entry.Name),
                (commandOutput?.Sources ?? Array.Empty<string>())
                    .Concat(manualVariants.SelectMany(item => item.Sources))
                    .Distinct()
                    .ToArray(),
                variants,
                null));
        }

        foreach (var entry in notiEnums)
        {
            var generatedVariants = outbound.TryGetValue((PacketKind.Noti, entry.Type), out var notification)
                ? notification.Variants
                : Array.Empty<PacketVariant>();
            var manualVariants = PacketSchemaRegistry.GetManualVariants(PacketFlow.ServerToClient, PacketKind.Noti, entry.Name);
            var variants = MergeVariants(manualVariants, generatedVariants);
            var notificationStatus = PacketSchemaRegistry.GetStatus(PacketFlow.ServerToClient, PacketKind.Noti, entry.Name);
            notificationStatus = entry.Name == "USERINFO" || PacketSchemaRegistry.HasCompleteSemanticCodec(PacketFlow.ServerToClient, PacketKind.Noti, entry.Name)
                ? PacketSchemaStatus.Structured
                : GetOutboundStatus(notificationStatus, variants);
            catalog.Add(new PacketTypeDefinition(
                PacketFlow.ServerToClient,
                PacketKind.Noti,
                entry.Type,
                $"S2C_NOTI_{entry.Name}",
                entry.Name,
                notification is not null,
                notificationStatus,
                PacketSchemaRegistry.GetSemantic(PacketFlow.ServerToClient, PacketKind.Noti, entry.Name),
                (notification?.Sources ?? Array.Empty<string>())
                    .Concat(manualVariants.SelectMany(item => item.Sources))
                    .Distinct()
                    .ToArray(),
                variants,
                null));
        }

        return catalog;
    }

    public static void ExportStandalone(ProtocolCatalog catalog, string snapshotPath, string protocolVersion)
    {
        var definitions = catalog.Types
            .OrderBy(item => item.Flow)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Type)
            .Select(RemoveSourceDependency)
            .ToArray();
        var snapshot = new ProtocolSnapshot(1, protocolVersion, true, definitions);
        var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath))
            ?? throw new InvalidOperationException("protocol snapshot path has no directory");
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);
        File.WriteAllBytes(snapshotPath, bytes);
        var manifest = new ProtocolManifest(
            1,
            protocolVersion,
            "SHA256",
            Convert.ToHexString(SHA256.HashData(bytes)),
            definitions.Length,
            definitions.Count(item => item.Supported),
            true);
        File.WriteAllText(
            Path.Combine(directory, "protocol-manifest.json"),
            JsonSerializer.Serialize(manifest, SnapshotJsonOptions));
    }

    private static ProtocolCatalog LoadStandalone(string snapshotPath, string manifestPath)
    {
        var bytes = File.ReadAllBytes(snapshotPath);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"standalone protocol manifest is missing: {manifestPath}");
        var manifest = JsonSerializer.Deserialize<ProtocolManifest>(File.ReadAllBytes(manifestPath), SnapshotJsonOptions)
            ?? throw new InvalidDataException("standalone protocol manifest is invalid");
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("standalone protocol catalog hash does not match protocol-manifest.json");
        var snapshot = JsonSerializer.Deserialize<ProtocolSnapshot>(bytes, SnapshotJsonOptions)
            ?? throw new InvalidDataException("standalone protocol catalog is invalid");
        if (snapshot.FormatVersion != 1 || !snapshot.SourceIndependent)
            throw new InvalidDataException("unsupported or source-dependent protocol catalog");
        if (snapshot.Definitions.Length != manifest.DefinitionCount)
            throw new InvalidDataException("standalone protocol definition count does not match manifest");
        var catalog = new ProtocolCatalog();
        catalog.ProtocolVersion = snapshot.ProtocolVersion;
        catalog.SourceIndependent = true;
        catalog.CatalogOrigin = "standalone-protocol-catalog";
        catalog.SnapshotSha256 = actualHash;
        foreach (var definition in snapshot.Definitions) catalog.Add(definition);
        return catalog;
    }

    private static PacketTypeDefinition RemoveSourceDependency(PacketTypeDefinition definition)
        => definition with
        {
            Sources = Array.Empty<string>(),
            Variants = definition.Variants.Select(variant => variant with
            {
                BodyBuilder = null,
                Sources = Array.Empty<string>(),
                Schema = RemoveSourceDependency(variant.Schema),
            }).ToArray(),
            InferredSchema = RemoveSourceDependency(definition.InferredSchema),
        };

    private static PacketBodySchema? RemoveSourceDependency(PacketBodySchema? schema)
        => schema is null
            ? null
            : schema with
            {
                Sources = Array.Empty<string>(),
                Fields = schema.Fields.Select(field => field with { Source = string.Empty }).ToArray(),
            };

    public bool TryGet(PacketFlow flow, PacketKind kind, ushort type, out PacketTypeDefinition definition)
        => _types.TryGetValue((flow, kind, type), out definition!);

    public PacketTypeDefinition? Find(PacketFlow flow, PacketKind kind, string nameOrType)
    {
        if (TryParseType(nameOrType, out var type) && TryGet(flow, kind, type, out var byType))
            return byType;
        return _types.Values.FirstOrDefault(item =>
            item.Flow == flow && item.Kind == kind &&
            (item.Name.Equals(nameOrType, StringComparison.OrdinalIgnoreCase) ||
             item.EnumName.Equals(nameOrType, StringComparison.OrdinalIgnoreCase)));
    }

    private void Add(PacketTypeDefinition definition)
        => _types[(definition.Flow, definition.Kind, definition.Type)] = definition;

    private static PacketVariant[] MergeVariants(PacketVariant[] first, PacketVariant[] second)
    {
        var result = new List<PacketVariant>(first.Length + second.Length);
        foreach (var variant in first.Concat(second))
        {
            if (result.Any(existing => existing.Name.Equals(variant.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.BodyBuilder, variant.BodyBuilder, StringComparison.Ordinal)))
                continue;
            var name = variant.Name;
            var suffix = 2;
            while (result.Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{variant.Name}-{suffix++}";
            result.Add(name.Equals(variant.Name, StringComparison.Ordinal)
                ? variant
                : variant with { Name = name });
        }
        return result.ToArray();
    }

    private static PacketSchemaStatus GetOutboundStatus(PacketSchemaStatus fallback, PacketVariant[] variants)
    {
        if (variants.Length == 0) return fallback;
        static bool HasSchema(PacketVariant item)
            => item.Schema is not null || !string.IsNullOrWhiteSpace(item.FixedBodyHex);
        if (variants.All(HasSchema)) return PacketSchemaStatus.Structured;
        if (variants.Any(HasSchema)) return PacketSchemaStatus.Partial;
        return fallback;
    }

    private static List<(ushort Type, string Name)> LoadEnums(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(item =>
        {
            var text = item.GetProperty("value").GetString()!;
            var type = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(text[2..], 16)
                : Convert.ToUInt16(text);
            return (type, item.GetProperty("name").GetString()!);
        }).ToList();
    }

    private static Dictionary<ushort, SupportInfo> LoadSupport(string path)
    {
        var result = new Dictionary<ushort, SupportInfo>();
        if (!File.Exists(path)) return result;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var type = checked((ushort)item.GetProperty("type").GetInt32());
            result[type] = new SupportInfo(ReadSources(item), Array.Empty<PacketVariant>());
        }
        return result;
    }

    private static Dictionary<(PacketKind, ushort), SupportInfo> LoadOutbound(string path)
    {
        var result = new Dictionary<(PacketKind, ushort), SupportInfo>();
        if (!File.Exists(path)) return result;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString()!.Equals("noti", StringComparison.OrdinalIgnoreCase)
                ? PacketKind.Noti
                : PacketKind.Cmd;
            var type = checked((ushort)item.GetProperty("type").GetInt32());
            var variants = item.TryGetProperty("variants", out var variantsElement)
                ? variantsElement.EnumerateArray().Select(ReadVariant).ToArray()
                : Array.Empty<PacketVariant>();
            result[(kind, type)] = new SupportInfo(ReadSources(item), variants);
        }
        return result;
    }

    private static Dictionary<ushort, InferredInfo> LoadInferred(string path)
    {
        var result = new Dictionary<ushort, InferredInfo>();
        if (!File.Exists(path)) return result;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var type = checked((ushort)item.GetProperty("type").GetInt32());
            PacketVariant[] variants;
            if (item.TryGetProperty("variants", out var variantElements))
            {
                variants = variantElements.EnumerateArray().Select(variant =>
                {
                    var schema = ReadSchema(variant);
                    return new PacketVariant(
                        variant.GetProperty("name").GetString() ?? "inferred-handler-layout",
                        null,
                        ReadSources(variant))
                    {
                        Discriminator = variant.TryGetProperty("discriminator", out var discriminator)
                            ? discriminator.GetString()
                            : "source handler/context",
                        Confidence = "inferred-from-handler",
                        Schema = schema,
                    };
                }).ToArray();
            }
            else
            {
                var schema = ReadSchema(item);
                variants = new[]
                {
                    new PacketVariant("inferred-handler-layout", null, ReadSources(item))
                    {
                        Discriminator = "legacy single inferred schema",
                        Confidence = "inferred-from-handler",
                        Schema = schema,
                    },
                };
            }
            result[type] = new InferredInfo(
                variants,
                variants.Length == 1 ? variants[0].Schema : null);
        }
        return result;
    }

    private static PacketVariant ReadVariant(JsonElement variant)
        => new(
            variant.GetProperty("name").GetString() ?? "default",
            variant.TryGetProperty("bodyBuilder", out var builder) ? builder.GetString() : null,
            ReadSources(variant))
        {
            Discriminator = variant.TryGetProperty("discriminator", out var discriminator)
                ? discriminator.GetString()
                : null,
            Confidence = variant.TryGetProperty("confidence", out var confidence)
                ? confidence.GetString() ?? "source-evidence"
                : "source-evidence",
            FixedBodyHex = variant.TryGetProperty("fixedBodyHex", out var fixedBody)
                ? fixedBody.GetString()
                : null,
            Schema = variant.TryGetProperty("fields", out _)
                || variant.TryGetProperty("exactLength", out _)
                || variant.TryGetProperty("minimumLength", out _)
                ? ReadSchema(variant)
                : null,
        };

    private static PacketBodySchema ReadSchema(JsonElement item)
    {
        var fields = item.TryGetProperty("fields", out var fieldElements)
            ? fieldElements.EnumerateArray().Select(field => new PacketFieldDefinition(
                field.GetProperty("name").GetString() ?? "unknown",
                field.GetProperty("fieldType").GetString() ?? "u8",
                field.GetProperty("offset").GetInt32(),
                field.TryGetProperty("optional", out var optional) && optional.GetBoolean(),
                field.GetProperty("source").GetString() ?? string.Empty)).ToArray()
            : Array.Empty<PacketFieldDefinition>();
        return new PacketBodySchema(
            item.TryGetProperty("exactLength", out var exact) && exact.ValueKind == JsonValueKind.Number ? exact.GetInt32() : null,
            item.TryGetProperty("minimumLength", out var minimum) && minimum.ValueKind == JsonValueKind.Number ? minimum.GetInt32() : null,
            item.TryGetProperty("bodyIgnored", out var ignored) && ignored.GetBoolean(),
            fields,
            ReadSources(item));
    }

    private static string[] ReadSources(JsonElement element)
    {
        if (!element.TryGetProperty("sources", out var sources)) return Array.Empty<string>();
        return sources.EnumerateArray().Select(source =>
        {
            if (source.ValueKind == JsonValueKind.String) return source.GetString() ?? string.Empty;
            if (source.TryGetProperty("location", out var location)) return location.GetString() ?? string.Empty;
            var file = source.TryGetProperty("source", out var sourceFile) ? sourceFile.GetString() : "unknown";
            var line = source.TryGetProperty("line", out var sourceLine) ? sourceLine.GetInt32() : 0;
            return $"{file}:{line}";
        }).Where(value => value.Length > 0).Distinct().ToArray();
    }

    private static bool TryParseType(string text, out ushort value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return ushort.TryParse(text, out value);
    }

    private sealed record SupportInfo(string[] Sources, PacketVariant[] Variants);
    private sealed record InferredInfo(PacketVariant[] Variants, PacketBodySchema? SingleSchema);
    private sealed record ProtocolSnapshot(
        int FormatVersion,
        string ProtocolVersion,
        bool SourceIndependent,
        PacketTypeDefinition[] Definitions);
    private sealed record ProtocolManifest(
        int FormatVersion,
        string ProtocolVersion,
        string HashAlgorithm,
        string Sha256,
        int DefinitionCount,
        int SupportedDefinitionCount,
        bool SourceIndependent);
}

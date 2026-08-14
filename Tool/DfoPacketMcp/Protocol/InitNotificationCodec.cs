using System.Buffers.Binary;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

internal static class InitNotificationCodec
{
    public static PacketVariant[] GetManualVariants(string name) => name switch
    {
        "DUNGEON_PERMISSION" =>
        [Variant("permission-list", "count:u16 + count*(dungeonId:u16 + clearState:u8)", "Server/DfoServer/Network/Builders/Init/DungeonPermissionBodyBuilder.cs:28")],
        "GAME_OPTION" =>
        [Variant("account-game-options", "three i32-length-prefixed byte arrays", "Server/DfoServer/Network/Builders/AccountSettingsPacketBuilder.cs:30")],
        "LOAD_COOLTIME_ITEM_INFO" =>
        [Variant("cooltime-item-values", "count:u8 + count*(itemId:i32 + value:i32)", "Server/DfoServer/Network/Builders/Init/ItemValueListBodyBuilder.cs:21")],
        "LOAD_EFFECT_ITEM_INFO" =>
        [Variant("effect-item-values", "count:u8 + count*(itemId:i32 + value:i32)", "Server/DfoServer/Network/Builders/Init/ItemValueListBodyBuilder.cs:21")],
        "HOTKEY_OPTION" =>
        [Variant("account-hotkeys", "keyType:u8 + byteLength:i32 + raw hotkey bytes", "Server/DfoServer/Network/Builders/AccountSettingsPacketBuilder.cs:37")],
        "COLLECT_BOX" =>
        [Variant("collection-box-state", "boxIndex:u8 + version:u8 + remainSeconds:u32 + statusFlags:u8 + itemCount:u8 + itemIds", "Server/DfoServer/Network/Builders/Init/CollectionBoxBodyBuilder.cs:91")],
        "INCREASE_CHANCE_LOTTERY_ALL" =>
        [Variant("increase-chance-all-state", "fixed length 204; current item header plus eight 24-byte progress records", "Server/DfoServer/Network/Builders/IncreaseChanceLotteryPacketBuilder.cs:12")],
        _ => [],
    };

    public static DecodedBody Decode(string name, byte[] body, List<string> diagnostics, string? requestedVariant) => name switch
    {
        "DUNGEON_PERMISSION" => DecodePermissions(body, diagnostics),
        "GAME_OPTION" => DecodeGameOptions(body, diagnostics),
        "LOAD_COOLTIME_ITEM_INFO" or "LOAD_EFFECT_ITEM_INFO" => DecodeItemValues(name, body, diagnostics),
        "HOTKEY_OPTION" => DecodeHotkeys(body, diagnostics),
        "COLLECT_BOX" => DecodeCollectionBox(body, diagnostics),
        "INCREASE_CHANCE_LOTTERY_ALL" => DecodeIncreaseChance(body, diagnostics),
        _ => new DecodedBody("unsupported", Base(body)),
    };

    public static byte[] Encode(string name, string? variant, JsonElement fields) => name switch
    {
        "DUNGEON_PERMISSION" => EncodePermissions(fields),
        "GAME_OPTION" => EncodeGameOptions(fields),
        "LOAD_COOLTIME_ITEM_INFO" or "LOAD_EFFECT_ITEM_INFO" => EncodeItemValues(fields),
        "HOTKEY_OPTION" => EncodeHotkeys(fields),
        "COLLECT_BOX" => EncodeCollectionBox(fields),
        "INCREASE_CHANCE_LOTTERY_ALL" => EncodeIncreaseChance(fields),
        _ => [],
    };

    private static DecodedBody DecodePermissions(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadUInt16(out var count))
        {
            diagnostics.Add("DUNGEON_PERMISSION count:u16 is truncated");
            return new DecodedBody("permission-list", fields);
        }
        fields["count"] = count;
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadUInt16(out var dungeonId) || !reader.TryReadByte(out var clearState))
            {
                diagnostics.Add($"DUNGEON_PERMISSION entry {index} is truncated");
                break;
            }
            entries.Add(new { dungeonId, clearState });
        }
        fields["entries"] = entries;
        Finish(reader, fields);
        return new DecodedBody("permission-list", fields);
    }

    private static byte[] EncodePermissions(JsonElement fields)
    {
        var entries = Array(fields, "entries");
        if (entries.Length > ushort.MaxValue) throw new ArgumentException("permission entries exceeds 65535");
        return Build(w =>
        {
            w.UInt16((ushort)entries.Length);
            foreach (var entry in entries)
            {
                w.UInt16(U16(entry, "dungeonId"));
                w.Byte(Byte(entry, "clearState"));
            }
        });
    }

    private static DecodedBody DecodeGameOptions(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        var names = new[] { "mainGameOption", "quickchatBank0", "quickchatBank1" };
        foreach (var name in names)
        {
            if (!reader.TryReadInt32(out var length) || length < 0 || length > 1024 * 1024 || !reader.TryReadBytes(length, out var bytes))
            {
                diagnostics.Add($"GAME_OPTION {name} length/payload is truncated or invalid");
                break;
            }
            fields[$"{name}Length"] = length;
            fields[$"{name}Hex"] = Convert.ToHexString(bytes);
        }
        Finish(reader, fields);
        return new DecodedBody("account-game-options", fields);
    }

    private static byte[] EncodeGameOptions(JsonElement fields) => Build(w =>
    {
        foreach (var name in new[] { "mainGameOptionHex", "quickchatBank0Hex", "quickchatBank1Hex" })
        {
            var bytes = Hex(fields, name);
            w.Int32(bytes.Length);
            w.Bytes(bytes);
        }
    });

    private static DecodedBody DecodeItemValues(string name, byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var count))
        {
            diagnostics.Add($"{name} count:u8 is truncated");
            return new DecodedBody(name == "LOAD_COOLTIME_ITEM_INFO" ? "cooltime-item-values" : "effect-item-values", fields);
        }
        fields["count"] = count;
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadInt32(out var itemId) || !reader.TryReadInt32(out var value))
            {
                diagnostics.Add($"{name} entry {index} is truncated");
                break;
            }
            entries.Add(new { itemId, value });
        }
        fields["entries"] = entries;
        Finish(reader, fields);
        return new DecodedBody(name == "LOAD_COOLTIME_ITEM_INFO" ? "cooltime-item-values" : "effect-item-values", fields);
    }

    private static byte[] EncodeItemValues(JsonElement fields)
    {
        var entries = Array(fields, "entries");
        if (entries.Length > byte.MaxValue) throw new ArgumentException("item value entries exceeds 255");
        return Build(w =>
        {
            w.Byte((byte)entries.Length);
            foreach (var entry in entries)
            {
                w.Int32(I32(entry, "itemId"));
                w.Int32(I32(entry, "value"));
            }
        });
    }

    private static DecodedBody DecodeHotkeys(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var keyType) || !reader.TryReadInt32(out var byteLength) || byteLength < 0 || byteLength > 1024 * 1024 || !reader.TryReadBytes(byteLength, out var hotkeys))
        {
            diagnostics.Add("HOTKEY_OPTION header or payload is truncated or invalid");
            return new DecodedBody("account-hotkeys", fields);
        }
        fields["keyType"] = keyType;
        fields["byteLength"] = byteLength;
        fields["hotkeysHex"] = Convert.ToHexString(hotkeys);
        fields["hotkeySlotCount"] = byteLength / 2;
        Finish(reader, fields);
        return new DecodedBody("account-hotkeys", fields);
    }

    private static byte[] EncodeHotkeys(JsonElement fields) => Build(w =>
    {
        var bytes = Hex(fields, "hotkeysHex");
        w.Byte(Byte(fields, "keyType"));
        w.Int32(bytes.Length);
        w.Bytes(bytes);
    });

    private static DecodedBody DecodeCollectionBox(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var boxIndex) || !reader.TryReadByte(out var version) || !reader.TryReadUInt32(out var remainSeconds) || !reader.TryReadByte(out var statusFlags) || !reader.TryReadByte(out var itemCount))
        {
            diagnostics.Add("COLLECT_BOX fixed header is truncated");
            return new DecodedBody("collection-box-state", fields);
        }
        fields["boxIndex"] = boxIndex;
        fields["version"] = version;
        fields["remainSeconds"] = remainSeconds;
        fields["statusFlags"] = statusFlags;
        fields["itemCount"] = itemCount;
        var items = new List<uint>();
        for (var index = 0; index < itemCount; index++)
        {
            if (!reader.TryReadUInt32(out var itemId))
            {
                diagnostics.Add($"COLLECT_BOX item {index} is truncated");
                break;
            }
            items.Add(itemId);
        }
        fields["itemIds"] = items;
        Finish(reader, fields);
        return new DecodedBody("collection-box-state", fields);
    }

    private static byte[] EncodeCollectionBox(JsonElement fields)
    {
        var itemIds = U32Array(fields, "itemIds");
        if (itemIds.Length > byte.MaxValue) throw new ArgumentException("COLLECT_BOX itemIds exceeds 255");
        return Build(w =>
        {
            w.Byte(Byte(fields, "boxIndex"));
            w.Byte(Byte(fields, "version", 1));
            w.UInt32(U32(fields, "remainSeconds"));
            w.Byte(Byte(fields, "statusFlags", 1));
            w.Byte((byte)itemIds.Length);
            foreach (var itemId in itemIds) w.UInt32(itemId);
        });
    }

    private static DecodedBody DecodeIncreaseChance(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        if (body.Length != 204) diagnostics.Add($"INCREASE_CHANCE_LOTTERY_ALL expects 204 bytes, got {body.Length}");
        if (body.Length >= 12)
        {
            fields["activeState"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4));
            fields["currentItemTemplateId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4));
            fields["newRewardIndexPlusOne"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8, 4));
            fields["newRewardIndex"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8, 4)) - 1;
        }
        var records = new List<object>();
        for (var index = 0; index < 8 && 12 + index * 24 + 24 <= body.Length; index++)
        {
            var offset = 12 + index * 24;
            var claims = body.AsSpan(offset + 4, 20).ToArray().Where(value => value != 0).Select(value => value - 1).ToArray();
            records.Add(new
            {
                recordIndex = index,
                itemTemplateId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4)),
                claimedRewardIndexes = claims,
                rawHex = Convert.ToHexString(body.AsSpan(offset, 24)),
            });
        }
        fields["records"] = records;
        return new DecodedBody("increase-chance-all-state", fields);
    }

    private static byte[] EncodeIncreaseChance(JsonElement fields)
    {
        var body = new byte[204];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), I32(fields, "activeState", 2));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4, 4), I32(fields, "currentItemTemplateId", -1));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8, 4), I32(fields, "newRewardIndexPlusOne", I32(fields, "newRewardIndex", -1) + 1));
        var records = Array(fields, "records");
        for (var index = 0; index < Math.Min(8, records.Length); index++)
        {
            var offset = 12 + index * 24;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset, 4), I32(records[index], "itemTemplateId"));
            foreach (var claim in U16Array(records[index], "claimedRewardIndexes"))
            {
                if (claim < 20) body[offset + 4 + claim] = checked((byte)(claim + 1));
            }
        }
        return body;
    }

    private static PacketVariant Variant(string name, string discriminator, params string[] sources)
        => new(name, null, sources) { Discriminator = discriminator, Confidence = "confirmed-from-server-source" };

    private static Dictionary<string, object?> Base(byte[] body) => new(StringComparer.Ordinal)
    {
        ["bodyLength"] = body.Length,
        ["rawHex"] = Convert.ToHexString(body),
    };

    private static void Finish(PacketReader reader, Dictionary<string, object?> fields)
    {
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail);
    }

    private static JsonElement[] Array(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToArray() : [];

    private static byte Byte(JsonElement value, string name, byte fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((byte)property.GetInt32()) : fallback;
    private static ushort U16(JsonElement value, string name, ushort fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((ushort)property.GetInt32()) : fallback;
    private static int I32(JsonElement value, string name, int fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
    private static uint U32(JsonElement value, string name, uint fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetUInt32() : fallback;
    private static byte[] Hex(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return [];
        return PacketInput.ParseHex(property.GetString() ?? string.Empty);
    }
    private static uint[] U32Array(JsonElement value, string name)
        => Array(value, name).Select(item => item.GetUInt32()).ToArray();
    private static ushort[] U16Array(JsonElement value, string name)
        => Array(value, name).Select(item => checked((ushort)item.GetInt32())).ToArray();
    private static byte[] Build(Action<Writer> action) { var writer = new Writer(); action(writer); return writer.ToArray(); }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];
        public void Byte(byte value) => _bytes.Add(value);
        public void Bytes(IEnumerable<byte> value) => _bytes.AddRange(value);
        public void Int32(int value) { var buffer = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(buffer, value); Bytes(buffer); }
        public void UInt16(ushort value) { var buffer = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(buffer, value); Bytes(buffer); }
        public void UInt32(uint value) { var buffer = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buffer, value); Bytes(buffer); }
        public byte[] ToArray() => _bytes.ToArray();
    }
}

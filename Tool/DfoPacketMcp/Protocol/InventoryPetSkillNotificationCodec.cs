using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

internal static class InventoryPetSkillNotificationCodec
{
    private const byte AvatarListType = 1;
    private const byte PetListType = 7;
    private const byte AccountCargoListType = 12;
    private const int CommonItemEntrySize = 84;
    private const int AvatarItemEntrySize = 126;
    private const int AvatarSocketSize = 30;

    public static PacketVariant[] GetManualVariants(string name) => name switch
    {
        "UPDATE_ITEM_LIST" =>
        [
            Variant("common-entry-updates", "listType != 1; listType:u8 + count:u16 + count*84-byte entries", "Server/DfoServer/Network/Builders/Inventory/ItemListUpdateBuilder.cs:32", "Server/DfoServer/Network/Builders/Inventory/ItemListProtocolWriter.cs:128"),
            Variant("avatar-entry-updates", "listType == 1; listType:u8 + count:u16 + count*126-byte entries", "Server/DfoServer/Network/Builders/Inventory/ItemListUpdateBuilder.cs:32", "Server/DfoServer/Network/Builders/Inventory/ItemListProtocolWriter.cs:58"),
        ],
        "ITEM_LIST" =>
        [
            Variant("common-item-list", "listType not in {1,7,12}; listType:u8 + listParam:u16 + count:u16 + count*84-byte entries", "Server/DfoServer/Network/Builders/Init/ItemListPacketBuilder.cs:158"),
            Variant("avatar-item-list", "listType == 1; listType:u8 + listParam:u16 + count:u16 + count*126-byte entries", "Server/DfoServer/Network/Builders/Init/ItemListPacketBuilder.cs:40"),
            Variant("pet-item-list", "listType == 7; listType:u8 + count:u16 + count*84-byte entries", "Server/DfoServer/Network/Builders/Init/ItemListPacketBuilder.cs:61"),
            Variant("account-cargo-item-list", "listType == 12; listType:u8 + selectionKey:u16 + money:i32 + count:u16 + count*84-byte entries", "Server/DfoServer/Network/Builders/Init/ItemListPacketBuilder.cs:105"),
        ],
        "CREATURE_ITEM_LIST" =>
        [
            Variant("creature-item-list", "count:u8 + variable creature entries", "Server/DfoServer/Network/Builders/Init/CreatureListBodyBuilder.cs:18"),
        ],
        "ITEM_LOCK_LIST" =>
        [
            Variant("equipment-item-lock-list", "count:u16 + variable lock records; state 2 adds remainingSeconds:i32", "Server/DfoServer/Network/Builders/Inventory/EquipmentItemLockBuilder.cs:56"),
        ],
        "CREATURE_STATE" =>
        [
            Variant("runtime-state-pair", "exact length 8: creatureKey:i32 + stateValue:i32", "Server/DfoServer/Network/Handlers/Pets/PetCreatureRuntimeService.cs:767"),
            Variant("creature-entry-refresh", "minimum length 16; one variable creature entry without a count", "Server/DfoServer/Network/Builders/Init/CreatureListBodyBuilder.cs:10", "Server/DfoServer/Network/Handlers/Pets/PetCreatureHandler.cs:236"),
        ],
        "CREATURE_SCRIPT_MESSAGE" =>
        [
            Variant("creature-script-broadcast", "mode:u8 + senderUserId:u16 + serverGroup:u8 + message:dstr", "Server/DfoServer/Game/Inventory/Pets/PetCreatureScript.cs:24"),
        ],
        "SKILLINFO" =>
        [
            Variant("two-page-skill-info", "two variable skill pages followed by tail0:u16 + tail1:u16", "Server/DfoServer/Network/Builders/Init/SkillInfoBodyBuilder.cs:16", "Server/DfoServer/Game/CharacterData/SqliteCharacterProgressRepository.cs:151"),
        ],
        "COMBO_SKILL_INFO" =>
        [
            Variant("dark-knight-combo-pages", "reserved:u8 + pageCount:u8 + counted page/root/child records", "Server/DfoServer/Game/Skills/DarkKnightComboSkillInfoCodec.cs:58"),
        ],
        _ => [],
    };

    public static DecodedBody Decode(
        string name,
        byte[] body,
        List<string> diagnostics,
        string? requestedVariant) => name switch
    {
        "UPDATE_ITEM_LIST" => DecodeItemList(body, diagnostics, requestedVariant, update: true),
        "ITEM_LIST" => DecodeItemList(body, diagnostics, requestedVariant, update: false),
        "CREATURE_ITEM_LIST" => DecodeCreatureList(body, diagnostics),
        "ITEM_LOCK_LIST" => DecodeItemLockList(body, diagnostics),
        "CREATURE_STATE" => DecodeCreatureState(body, diagnostics, requestedVariant),
        "CREATURE_SCRIPT_MESSAGE" => DecodeCreatureScriptMessage(body, diagnostics),
        "SKILLINFO" => DecodeSkillInfo(body, diagnostics),
        "COMBO_SKILL_INFO" => DecodeComboSkillInfo(body, diagnostics),
        _ => new DecodedBody("unsupported", Base(body)),
    };

    public static byte[] Encode(string name, string? variant, JsonElement fields) => name switch
    {
        "UPDATE_ITEM_LIST" => EncodeItemList(fields, update: true),
        "ITEM_LIST" => EncodeItemList(fields, update: false),
        "CREATURE_ITEM_LIST" => EncodeCreatureList(fields),
        "ITEM_LOCK_LIST" => EncodeItemLockList(fields),
        "CREATURE_STATE" => EncodeCreatureState(variant, fields),
        "CREATURE_SCRIPT_MESSAGE" => EncodeCreatureScriptMessage(fields),
        "SKILLINFO" => EncodeSkillInfo(fields),
        "COMBO_SKILL_INFO" => EncodeComboSkillInfo(fields),
        _ => [],
    };

    private static DecodedBody DecodeItemList(
        byte[] body,
        List<string> diagnostics,
        string? requestedVariant,
        bool update)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var listType))
        {
            diagnostics.Add($"{(update ? "UPDATE_ITEM_LIST" : "ITEM_LIST")} is missing listType:u8");
            return new DecodedBody(update ? "item-entry-updates-invalid" : "item-list-invalid", fields);
        }

        fields["listType"] = listType;
        fields["listTypeName"] = InventoryListTypeName(listType);
        ushort count;
        string variant;
        if (update)
        {
            variant = listType == AvatarListType ? "avatar-entry-updates" : "common-entry-updates";
            if (!reader.TryReadUInt16(out count))
            {
                diagnostics.Add("UPDATE_ITEM_LIST is missing count:u16");
                return new DecodedBody(variant, fields);
            }
        }
        else if (listType == PetListType)
        {
            variant = "pet-item-list";
            if (!reader.TryReadUInt16(out count))
            {
                diagnostics.Add("pet ITEM_LIST is missing count:u16");
                return new DecodedBody(variant, fields);
            }
        }
        else if (listType == AccountCargoListType)
        {
            variant = "account-cargo-item-list";
            if (!reader.TryReadUInt16(out var selectionKey)
                || !reader.TryReadInt32(out var money)
                || !reader.TryReadUInt16(out count))
            {
                diagnostics.Add("account cargo ITEM_LIST header is truncated");
                return new DecodedBody(variant, fields);
            }
            fields["selectionKey"] = selectionKey;
            fields["money"] = money;
        }
        else
        {
            variant = listType == AvatarListType ? "avatar-item-list" : "common-item-list";
            if (!reader.TryReadUInt16(out var listParam) || !reader.TryReadUInt16(out count))
            {
                diagnostics.Add("common ITEM_LIST header is truncated");
                return new DecodedBody(variant, fields);
            }
            fields["listParam"] = listParam;
        }

        ValidateRequestedVariant(requestedVariant, variant, diagnostics);
        fields["count"] = count;
        var entrySize = listType == AvatarListType ? AvatarItemEntrySize : CommonItemEntrySize;
        var expectedLength = reader.Offset + count * entrySize;
        if (body.Length != expectedLength)
            diagnostics.Add($"{variant} expects {expectedLength} bytes for {count} entries, got {body.Length}");

        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadBytes(entrySize, out var entryBytes))
            {
                diagnostics.Add($"item entry {index} is truncated; expected {entrySize} bytes");
                break;
            }
            entries.Add(DecodeItemEntry(entryBytes, listType == AvatarListType, diagnostics, index));
        }
        fields["entries"] = entries;
        Finish(reader, fields);
        return new DecodedBody(variant, fields);
    }

    private static Dictionary<string, object?> DecodeItemEntry(
        byte[] entry,
        bool avatar,
        List<string> diagnostics,
        int index)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rawHex"] = Convert.ToHexString(entry),
            ["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(entry.AsSpan(0, 2)),
            ["itemTemplateId"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(2, 4)),
        };
        var itemTemplateId = (int)fields["itemTemplateId"]!;
        fields["empty"] = itemTemplateId == -1;
        if (itemTemplateId == -1)
            return fields;

        fields["value"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(6, 4));
        fields["attribute"] = entry[10];
        fields["durability"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(11, 2));
        fields["sealFlag"] = entry[13];
        fields["enchantCardId"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(14, 4));
        fields["enchantUpgradeCount"] = entry[18];
        fields["amplifyType"] = entry[19];
        fields["amplifyValue"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(20, 2));
        fields["marker16"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(22, 4));

        var chronicleCount = entry[26];
        fields["chronicleCount"] = chronicleCount;
        var chronicleOptions = new List<object>();
        for (var optionIndex = 0; optionIndex < Math.Min(chronicleCount, (byte)2); optionIndex++)
        {
            chronicleOptions.Add(new
            {
                optionId = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(27 + optionIndex * 4, 4)),
                job = entry[35 + optionIndex],
                firstGrowType = entry[37 + optionIndex],
                equipmentType = entry[39 + optionIndex],
                optionNo = entry[41 + optionIndex],
            });
        }
        if (chronicleCount > 2)
            diagnostics.Add($"item entry {index} chronicleCount {chronicleCount} exceeds fixed capacity 2");
        fields["chronicleOptions"] = chronicleOptions;

        fields["expireTime"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(43, 4));
        fields["emblemSocketCount"] = entry[47];
        fields["emblemId1"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(48, 4));
        fields["emblemId2"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(52, 4));
        fields["rune"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(56, 2));

        var randomCount = entry[58];
        fields["randomOptionCount"] = randomCount;
        var randomOptions = new List<object>();
        for (var optionIndex = 0; optionIndex < Math.Min(randomCount, (byte)3); optionIndex++)
        {
            randomOptions.Add(new
            {
                type = entry[59 + optionIndex],
                value1 = entry[62 + optionIndex],
                value2 = entry[65 + optionIndex],
            });
        }
        if (randomCount > 3)
            diagnostics.Add($"item entry {index} randomOptionCount {randomCount} exceeds fixed capacity 3");
        fields["randomOptions"] = randomOptions;
        fields["randomOptionState"] = entry[68];
        fields["randomOptionChangedIndex"] = entry[69];
        fields["randomOptionChangeState"] = entry[70];
        fields["randomOptionChangeType"] = entry[71];
        fields["randomOptionChangeValue1"] = entry[72];
        fields["randomOptionChangeValue2"] = entry[73];
        fields["genuineUpgrade"] = entry[74];
        fields["emancipateEquipmentLevel"] = entry[75];
        fields["tradeRestriction"] = entry[76];
        fields["tailUnknown0"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(77, 2));
        fields["tailUnknown1"] = entry[79];
        fields["tailUnknown2"] = entry[80];
        fields["tailUnknown3"] = entry[81];
        fields["remainUseCount"] = entry[82];
        fields["sortLockFlag"] = entry[83];

        if (avatar)
        {
            var socketLength = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(84, 4));
            fields["jewelSocketLength"] = socketLength;
            fields["jewelSocketHex"] = Convert.ToHexString(entry.AsSpan(88, AvatarSocketSize));
            fields["colorBlockLength"] = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(118, 4));
            fields["color1"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(122, 2));
            fields["color2"] = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(124, 2));
            if (socketLength != AvatarSocketSize)
                diagnostics.Add($"avatar item entry {index} jewelSocketLength is {socketLength}, expected {AvatarSocketSize}");
        }
        return fields;
    }

    private static byte[] EncodeItemList(JsonElement fields, bool update)
    {
        var listType = Byte(fields, "listType");
        var entries = Array(fields, "entries");
        if (entries.Length > ushort.MaxValue)
            throw new ArgumentException("fields.entries exceeds 65535 entries");

        return Build(writer =>
        {
            writer.Byte(listType);
            if (!update)
            {
                if (listType == AccountCargoListType)
                {
                    writer.UInt16(U16(fields, "selectionKey"));
                    writer.Int32(I32(fields, "money"));
                }
                else if (listType != PetListType)
                {
                    writer.UInt16(U16(fields, "listParam"));
                }
            }
            writer.UInt16((ushort)entries.Length);
            foreach (var entry in entries)
                writer.Bytes(EncodeItemEntry(entry, listType == AvatarListType));
        });
    }

    private static byte[] EncodeItemEntry(JsonElement fields, bool avatar)
    {
        var size = avatar ? AvatarItemEntrySize : CommonItemEntrySize;
        if (Bool(fields, "empty") || I32(fields, "itemTemplateId", -1) == -1)
        {
            var empty = new byte[size];
            BinaryPrimitives.WriteInt16LittleEndian(empty.AsSpan(0, 2), I16(fields, "slotIndex"));
            BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(2, 4), -1);
            return empty;
        }

        var result = new byte[size];
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(0, 2), I16(fields, "slotIndex"));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2, 4), I32(fields, "itemTemplateId"));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(6, 4), I32(fields, "value"));
        result[10] = Byte(fields, "attribute");
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(11, 2), U16(fields, "durability"));
        result[13] = Byte(fields, "sealFlag");
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14, 4), I32(fields, "enchantCardId"));
        result[18] = Byte(fields, "enchantUpgradeCount");
        result[19] = Byte(fields, "amplifyType");
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), U16(fields, "amplifyValue"));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22, 4), I32(fields, "marker16"));

        var chronicle = Array(fields, "chronicleOptions");
        if (chronicle.Length > 2)
            throw new ArgumentException("fields.chronicleOptions exceeds the fixed capacity of 2");
        result[26] = checked((byte)chronicle.Length);
        for (var index = 0; index < chronicle.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(27 + index * 4, 4), I32(chronicle[index], "optionId"));
            result[35 + index] = Byte(chronicle[index], "job");
            result[37 + index] = Byte(chronicle[index], "firstGrowType");
            result[39 + index] = Byte(chronicle[index], "equipmentType");
            result[41 + index] = Byte(chronicle[index], "optionNo");
        }

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(43, 4), I32(fields, "expireTime"));
        result[47] = Byte(fields, "emblemSocketCount");
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(48, 4), I32(fields, "emblemId1"));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(52, 4), I32(fields, "emblemId2"));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(56, 2), U16(fields, "rune"));

        var randomOptions = Array(fields, "randomOptions");
        if (randomOptions.Length > 3)
            throw new ArgumentException("fields.randomOptions exceeds the fixed capacity of 3");
        result[58] = checked((byte)randomOptions.Length);
        for (var index = 0; index < randomOptions.Length; index++)
        {
            result[59 + index] = Byte(randomOptions[index], "type");
            result[62 + index] = Byte(randomOptions[index], "value1");
            result[65 + index] = Byte(randomOptions[index], "value2");
        }
        result[68] = Byte(fields, "randomOptionState");
        result[69] = Byte(fields, "randomOptionChangedIndex");
        result[70] = Byte(fields, "randomOptionChangeState");
        result[71] = Byte(fields, "randomOptionChangeType");
        result[72] = Byte(fields, "randomOptionChangeValue1");
        result[73] = Byte(fields, "randomOptionChangeValue2");
        result[74] = Byte(fields, "genuineUpgrade");
        result[75] = Byte(fields, "emancipateEquipmentLevel");
        result[76] = Byte(fields, "tradeRestriction");
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(77, 2), U16(fields, "tailUnknown0"));
        result[79] = Byte(fields, "tailUnknown1");
        result[80] = Byte(fields, "tailUnknown2");
        result[81] = Byte(fields, "tailUnknown3");
        result[82] = Byte(fields, "remainUseCount");
        result[83] = Byte(fields, "sortLockFlag");

        if (avatar)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(84, 4), I32(fields, "jewelSocketLength", AvatarSocketSize));
            Hex(fields, "jewelSocketHex", AvatarSocketSize).CopyTo(result, 88);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(118, 4), I32(fields, "colorBlockLength", 4));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(122, 2), U16(fields, "color1"));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(124, 2), U16(fields, "color2"));
        }
        return result;
    }

    private static DecodedBody DecodeCreatureList(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var count))
        {
            diagnostics.Add("CREATURE_ITEM_LIST is missing count:u8");
            return new DecodedBody("creature-item-list", fields);
        }
        fields["count"] = count;
        fields["entries"] = DecodeCreatureEntries(reader, count, diagnostics, "creature list");
        Finish(reader, fields);
        return new DecodedBody("creature-item-list", fields);
    }

    private static DecodedBody DecodeCreatureState(byte[] body, List<string> diagnostics, string? requestedVariant)
    {
        if (requestedVariant?.Equals("runtime-state-pair", StringComparison.OrdinalIgnoreCase) == true
            || requestedVariant is null && body.Length == 8)
        {
            var fields = Base(body);
            if (body.Length != 8)
                diagnostics.Add($"runtime-state-pair expects 8 bytes, got {body.Length}");
            if (body.Length >= 8)
            {
                fields["creatureKey"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4));
                fields["stateValue"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4));
            }
            return new DecodedBody("runtime-state-pair", fields);
        }

        var entryFields = Base(body);
        var reader = new PacketReader(body);
        var entries = DecodeCreatureEntries(reader, 1, diagnostics, "creature state refresh");
        if (entries.Count > 0)
            entryFields["entry"] = entries[0];
        Finish(reader, entryFields);
        ValidateRequestedVariant(requestedVariant, "creature-entry-refresh", diagnostics);
        return new DecodedBody("creature-entry-refresh", entryFields);
    }

    private static List<object> DecodeCreatureEntries(
        PacketReader reader,
        int count,
        List<string> diagnostics,
        string semantic)
    {
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadInt32(out var creatureKey)
                || !reader.TryReadByte(out var field04)
                || !reader.TryReadByte(out var modeFlag)
                || !reader.TryReadInt32(out var progressValue32))
            {
                diagnostics.Add($"{semantic} entry {index} fixed prefix is truncated");
                break;
            }
            byte? mode1Field0A = null;
            byte? mode1Field0B = null;
            if (modeFlag == 1)
            {
                if (!reader.TryReadByte(out var mode1A) || !reader.TryReadByte(out var mode1B))
                {
                    diagnostics.Add($"{semantic} entry {index} mode-1 fields are truncated");
                    break;
                }
                mode1Field0A = mode1A;
                mode1Field0B = mode1B;
            }
            if (!reader.TryReadByte(out var fieldAfterValue32)
                || !reader.TryReadInt32(out var textLength)
                || textLength < 0
                || textLength > 1024 * 1024
                || !reader.TryReadBytes(textLength, out var textBytes)
                || !reader.TryReadByte(out var tailFlag))
            {
                diagnostics.Add($"{semantic} entry {index} variable tail is truncated or malformed");
                break;
            }
            entries.Add(new
            {
                creatureKey,
                field04,
                modeFlag,
                progressValue32,
                mode1Field0A,
                mode1Field0B,
                fieldAfterValue32,
                creatureTextHex = Convert.ToHexString(textBytes),
                creatureTextUtf8 = Encoding.UTF8.GetString(textBytes),
                tailFlag,
            });
        }
        return entries;
    }

    private static byte[] EncodeCreatureList(JsonElement fields)
    {
        var entries = Array(fields, "entries");
        if (entries.Length > byte.MaxValue)
            throw new ArgumentException("fields.entries exceeds 255 entries");
        return Build(writer =>
        {
            writer.Byte((byte)entries.Length);
            foreach (var entry in entries)
                EncodeCreatureEntry(writer, entry);
        });
    }

    private static byte[] EncodeCreatureState(string? variant, JsonElement fields)
    {
        var selected = variant ?? (fields.TryGetProperty("stateValue", out _) ? "runtime-state-pair" : "creature-entry-refresh");
        if (selected.Equals("runtime-state-pair", StringComparison.OrdinalIgnoreCase))
            return Build(writer => { writer.Int32(I32(fields, "creatureKey")); writer.Int32(I32(fields, "stateValue")); });
        var entry = fields.TryGetProperty("entry", out var nested) ? nested : fields;
        return Build(writer => EncodeCreatureEntry(writer, entry));
    }

    private static void EncodeCreatureEntry(Writer writer, JsonElement entry)
    {
        writer.Int32(I32(entry, "creatureKey"));
        writer.Byte(Byte(entry, "field04"));
        var mode = Byte(entry, "modeFlag");
        writer.Byte(mode);
        writer.Int32(I32(entry, "progressValue32"));
        if (mode == 1)
        {
            writer.Byte(Byte(entry, "mode1Field0A"));
            writer.Byte(Byte(entry, "mode1Field0B"));
        }
        writer.Byte(Byte(entry, "fieldAfterValue32"));
        writer.Dbytes(Bytes(entry, "creatureTextHex", "creatureTextUtf8"));
        writer.Byte(Byte(entry, "tailFlag"));
    }

    private static DecodedBody DecodeItemLockList(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadUInt16(out var count))
        {
            diagnostics.Add("ITEM_LOCK_LIST is missing count:u16");
            return new DecodedBody("equipment-item-lock-list", fields);
        }
        fields["count"] = count;
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadByte(out var listType)
                || !reader.TryReadInt16(out var slotIndex)
                || !reader.TryReadByte(out var state))
            {
                diagnostics.Add($"ITEM_LOCK_LIST entry {index} is truncated");
                break;
            }
            int? remainingSeconds = null;
            if (state == 2)
            {
                if (!reader.TryReadInt32(out var remaining))
                {
                    diagnostics.Add($"ITEM_LOCK_LIST entry {index} remainingSeconds is truncated");
                    break;
                }
                remainingSeconds = remaining;
            }
            entries.Add(new { listType, listTypeName = InventoryListTypeName(listType), slotIndex, state, remainingSeconds });
        }
        fields["entries"] = entries;
        Finish(reader, fields);
        return new DecodedBody("equipment-item-lock-list", fields);
    }

    private static byte[] EncodeItemLockList(JsonElement fields)
    {
        var entries = Array(fields, "entries");
        if (entries.Length > ushort.MaxValue)
            throw new ArgumentException("fields.entries exceeds 65535 entries");
        return Build(writer =>
        {
            writer.UInt16((ushort)entries.Length);
            foreach (var entry in entries)
            {
                writer.Byte(Byte(entry, "listType"));
                writer.Int16(I16(entry, "slotIndex"));
                var state = Byte(entry, "state");
                writer.Byte(state);
                if (state == 2)
                    writer.Int32(I32(entry, "remainingSeconds"));
            }
        });
    }

    private static DecodedBody DecodeCreatureScriptMessage(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var mode)
            || !reader.TryReadUInt16(out var senderUserId)
            || !reader.TryReadByte(out var serverGroup)
            || !reader.TryReadInt32(out var messageLength)
            || messageLength < 0
            || messageLength > 1024 * 1024
            || !reader.TryReadBytes(messageLength, out var messageBytes))
        {
            diagnostics.Add("CREATURE_SCRIPT_MESSAGE is truncated or has an invalid dstr length");
            return new DecodedBody("creature-script-broadcast", fields);
        }
        fields["mode"] = mode;
        fields["senderUserId"] = senderUserId;
        fields["serverGroup"] = serverGroup;
        fields["messageHex"] = Convert.ToHexString(messageBytes);
        fields["messageUtf8"] = Encoding.UTF8.GetString(messageBytes);
        Finish(reader, fields);
        return new DecodedBody("creature-script-broadcast", fields);
    }

    private static byte[] EncodeCreatureScriptMessage(JsonElement fields) => Build(writer =>
    {
        writer.Byte(Byte(fields, "mode"));
        writer.UInt16(U16(fields, "senderUserId"));
        writer.Byte(Byte(fields, "serverGroup"));
        writer.Dbytes(Bytes(fields, "messageHex", "messageUtf8"));
    });

    private static DecodedBody DecodeSkillInfo(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        var pages = new List<object>();
        for (var pageIndex = 0; pageIndex < 2; pageIndex++)
        {
            if (!reader.TryReadUInt16(out var headerValue) || !reader.TryReadByte(out var count))
            {
                diagnostics.Add($"SKILLINFO page {pageIndex} header is truncated");
                break;
            }
            var entries = new List<object>();
            for (var entryIndex = 0; entryIndex < count; entryIndex++)
            {
                if (!reader.TryReadByte(out var slot)
                    || !reader.TryReadUInt16(out var skillId)
                    || !reader.TryReadByte(out var level)
                    || !reader.TryReadByte(out var extraCount)
                    || !reader.TryReadBytes(extraCount, out var extras))
                {
                    diagnostics.Add($"SKILLINFO page {pageIndex} entry {entryIndex} is truncated");
                    break;
                }
                entries.Add(new { slot, skillId, level, extraValues = extras });
            }
            pages.Add(new { pageIndex, headerValue, count, entries });
        }
        fields["pages"] = pages;
        if (pages.Count == 2)
        {
            if (reader.TryReadUInt16(out var tail0) && reader.TryReadUInt16(out var tail1))
            {
                fields["tail0"] = tail0;
                fields["tail1"] = tail1;
            }
            else
            {
                diagnostics.Add("SKILLINFO tail0/tail1 is truncated");
            }
        }
        Finish(reader, fields);
        return new DecodedBody("two-page-skill-info", fields);
    }

    private static byte[] EncodeSkillInfo(JsonElement fields)
    {
        var pages = Array(fields, "pages");
        if (pages.Length != 2)
            throw new ArgumentException("fields.pages must contain exactly two SKILLINFO pages");
        return Build(writer =>
        {
            foreach (var page in pages)
            {
                var entries = Array(page, "entries");
                if (entries.Length > byte.MaxValue)
                    throw new ArgumentException("SKILLINFO page entries exceeds 255");
                writer.UInt16(U16(page, "headerValue"));
                writer.Byte((byte)entries.Length);
                foreach (var entry in entries)
                {
                    var extras = ByteArray(entry, "extraValues");
                    if (extras.Length > byte.MaxValue)
                        throw new ArgumentException("SKILLINFO extraValues exceeds 255");
                    writer.Byte(Byte(entry, "slot"));
                    writer.UInt16(U16(entry, "skillId"));
                    writer.Byte(Byte(entry, "level"));
                    writer.Byte((byte)extras.Length);
                    writer.Bytes(extras);
                }
            }
            writer.UInt16(U16(fields, "tail0"));
            writer.UInt16(U16(fields, "tail1"));
        });
    }

    private static DecodedBody DecodeComboSkillInfo(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var reserved) || !reader.TryReadByte(out var pageCount))
        {
            diagnostics.Add("COMBO_SKILL_INFO notification header is truncated");
            return new DecodedBody("dark-knight-combo-pages", fields);
        }
        fields["reserved"] = reserved;
        fields["pageCount"] = pageCount;
        var pages = new List<object>();
        for (var pageOrdinal = 0; pageOrdinal < pageCount; pageOrdinal++)
        {
            if (!reader.TryReadByte(out var pageIndex) || !reader.TryReadByte(out var rootCount))
            {
                diagnostics.Add($"COMBO_SKILL_INFO page {pageOrdinal} header is truncated");
                break;
            }
            var roots = new List<object>();
            for (var rootIndex = 0; rootIndex < rootCount; rootIndex++)
            {
                if (!reader.TryReadUInt16(out var rootSkillId) || !reader.TryReadByte(out var childCount))
                {
                    diagnostics.Add($"COMBO_SKILL_INFO page {pageOrdinal} root {rootIndex} header is truncated");
                    break;
                }
                var children = new ushort[childCount];
                var complete = true;
                for (var childIndex = 0; childIndex < childCount; childIndex++)
                {
                    if (!reader.TryReadUInt16(out children[childIndex]))
                    {
                        diagnostics.Add($"COMBO_SKILL_INFO page {pageOrdinal} root {rootIndex} child {childIndex} is truncated");
                        complete = false;
                        break;
                    }
                }
                if (!complete)
                    break;
                roots.Add(new { rootSkillId, childSkillIds = children });
            }
            pages.Add(new { pageIndex, rootCount, roots });
        }
        fields["pages"] = pages;
        Finish(reader, fields);
        return new DecodedBody("dark-knight-combo-pages", fields);
    }

    private static byte[] EncodeComboSkillInfo(JsonElement fields)
    {
        var pages = Array(fields, "pages");
        if (pages.Length == 0 || pages.Length > byte.MaxValue)
            throw new ArgumentException("fields.pages must contain 1..255 combo pages");
        return Build(writer =>
        {
            writer.Byte(Byte(fields, "reserved"));
            writer.Byte((byte)pages.Length);
            foreach (var page in pages)
            {
                var roots = Array(page, "roots");
                if (roots.Length > byte.MaxValue)
                    throw new ArgumentException("combo page roots exceeds 255");
                writer.Byte(Byte(page, "pageIndex"));
                writer.Byte((byte)roots.Length);
                foreach (var root in roots)
                {
                    var children = U16Array(root, "childSkillIds");
                    if (children.Length > byte.MaxValue)
                        throw new ArgumentException("combo root childSkillIds exceeds 255");
                    writer.UInt16(U16(root, "rootSkillId"));
                    writer.Byte((byte)children.Length);
                    foreach (var child in children)
                        writer.UInt16(child);
                }
            }
        });
    }

    private static PacketVariant Variant(string name, string discriminator, params string[] sources)
        => new(name, null, sources)
        {
            Discriminator = discriminator,
            Confidence = "confirmed-from-server-source",
        };

    private static Dictionary<string, object?> Base(byte[] body) => new(StringComparer.Ordinal)
    {
        ["bodyLength"] = body.Length,
        ["rawHex"] = Convert.ToHexString(body),
    };

    private static void Finish(PacketReader reader, Dictionary<string, object?> fields)
    {
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail))
            fields["trailingHex"] = Convert.ToHexString(tail);
    }

    private static void ValidateRequestedVariant(string? requested, string actual, List<string> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(requested)
            && !requested.Equals(actual, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add($"requested variant '{requested}' does not match decoded variant '{actual}'");
    }

    private static string InventoryListTypeName(byte value) => value switch
    {
        0 => "Main",
        1 => "Avatar",
        2 => "PersonalCargo",
        3 => "Equipment",
        7 => "Pet",
        12 => "AccountCargo",
        29 => "QuickSlot",
        _ => $"Unknown({value})",
    };

    private static byte[] Build(Action<Writer> action)
    {
        var writer = new Writer();
        action(writer);
        return writer.ToArray();
    }

    private static JsonElement[] Array(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToArray()
            : [];

    private static byte Byte(JsonElement value, string name, byte fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? checked((byte)property.GetInt32())
            : fallback;

    private static short I16(JsonElement value, string name, short fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? checked((short)property.GetInt32())
            : fallback;

    private static ushort U16(JsonElement value, string name, ushort fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? checked((ushort)property.GetInt32())
            : fallback;

    private static int I32(JsonElement value, string name, int fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property.GetInt32()
            : fallback;

    private static bool Bool(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static byte[] Hex(JsonElement value, string name, int length)
    {
        var bytes = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? PacketInput.ParseHex(property.GetString() ?? string.Empty)
            : new byte[length];
        if (bytes.Length != length)
            throw new ArgumentException($"fields.{name} must contain exactly {length} bytes");
        return bytes;
    }

    private static byte[] Bytes(JsonElement value, string hexName, string textName)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(hexName, out var hex))
            return PacketInput.ParseHex(hex.GetString() ?? string.Empty);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(textName, out var text))
            return Encoding.UTF8.GetBytes(text.GetString() ?? string.Empty);
        return [];
    }

    private static byte[] ByteArray(JsonElement value, string name)
    {
        var items = Array(value, name);
        return items.Select(item => checked((byte)item.GetInt32())).ToArray();
    }

    private static ushort[] U16Array(JsonElement value, string name)
    {
        var items = Array(value, name);
        return items.Select(item => checked((ushort)item.GetInt32())).ToArray();
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void Byte(byte value) => _bytes.Add(value);
        public void Bytes(IEnumerable<byte> value) => _bytes.AddRange(value);
        public void Int16(short value) { var buffer = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(buffer, value); Bytes(buffer); }
        public void UInt16(ushort value) { var buffer = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(buffer, value); Bytes(buffer); }
        public void Int32(int value) { var buffer = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(buffer, value); Bytes(buffer); }
        public void Dbytes(byte[] value) { Int32(value.Length); Bytes(value); }
        public byte[] ToArray() => _bytes.ToArray();
    }
}

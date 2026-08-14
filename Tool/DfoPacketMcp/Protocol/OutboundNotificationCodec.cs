using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

internal static class OutboundNotificationCodec
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "USER_UDP_IP_PORT",
        "GET_ITEM",
        "USER_STATE",
        "PARTY_INFO",
        "ENTER_SELECT_DUNGEON",
        "REQUEST_PEER",
        "PARTY_MEMBER_REALTIME_INFO",
        "AREA_USERS",
        "UPDATE_ITEM_LIST",
        "ITEM_LIST",
        "CREATURE_ITEM_LIST",
        "ITEM_LOCK_LIST",
        "CREATURE_STATE",
        "CREATURE_SCRIPT_MESSAGE",
        "SKILLINFO",
        "COMBO_SKILL_INFO",
        "DUNGEON_INFO",
        "START_MAP",
        "CLEAR_DUNGEON_REWARD",
        "DIE_MONSTER",
        "DEATH_TOWER_INFO",
        "START_DEATH_TOWER_MAP",
        "DEATH_TOWER_STATE_RANKING",
        "DEATH_TOWER_STATE_REWARD",
        "DEATH_TOWER_STATE_EPLP",
        "BLOOD_DUNGEON_STATE_RANKING",
        "BLOOD_DUNGEON_STATE_REWARD",
        "BLOOD_MONSTER_SPAWN",
        "START_BLOOD_MAP",
        "BLOOD_ROUND_INTERVAL_TIME",
        "HELL_PARTY_MONSTER_INFO",
        "DUNGEON_PERMISSION",
        "GAME_OPTION",
        "LOAD_COOLTIME_ITEM_INFO",
        "LOAD_EFFECT_ITEM_INFO",
        "HOTKEY_OPTION",
        "COLLECT_BOX",
        "INCREASE_CHANCE_LOTTERY_ALL",
        "RAID_SET_SYMBOL",
        "RAID_DUNGEON_PARTICIPATION_INFO",
        "RAID_WAITING_LIST",
        "RAID_ENTRY_COST_INFO",
        "RAID_REWARD_LIST",
        "RAID_BUFF_SYSTEM",
        "RAID_MONSTER_HP",
        "ACCEPTABLE_QUEST_LIST", "EXPERT_JOB_INFO", "ITEM_EFFECT", "SECRET_SHOP_NPC", "SECRET_SHOP_ITEM_LIST",
        "USER_APC_INFO_TOD", "TOD_CLEAR_REWARD", "TITLE_BOOK_LIST", "TOURNAMENT_INFO", "TOURNAMENT_MAP_INFO",
        "TOURNAMENT_CLEAR_REWARD", "CHARACTER_DEL_BUFF", "CHARACTER_BUFF_DUNGEON", "MINIMAP_ICON_INFO", "DAILY_CHALLENGE",
        "SERVER_BROADCAST", "TAG_CHARACTER_INFO", "RAID_MODIFY",
    };

    public static bool Supports(string name) => Supported.Contains(name);

    public static PacketVariant[] GetManualVariants(string name) => name switch
    {
        "USER_UDP_IP_PORT" => new[]
        {
            Variant("peer-endpoint-roster", "count:u8 + count*22-byte endpoint records", "Server/DfoServer/Network/Builders/Party/PartyIpInfoBuilder.cs:17", "Server/DfoServer/Network/Builders/Pvp/PvpPeerInfoBuilder.cs:13"),
        },
        "GET_ITEM" => new[]
        {
            Variant("pickup-item", "exact length 17", "Server/DfoServer/Network/Builders/Dungeon/DropItemBuilder.cs:75"),
            Variant("pickup-gold", "exact length 53", "Server/DfoServer/Network/Builders/Dungeon/DropItemBuilder.cs:96"),
        },
        "USER_STATE" => new[]
        {
            Variant("user-state-list", "count:u8 + count*3-byte records", "Server/DfoServer/Network/Builders/Dungeon/EnterSelectDungeonStateBuilder.cs:12"),
        },
        "PARTY_INFO" => new[]
        {
            Variant("party-info-and-roster", "block type 0", "Server/DfoServer/Network/Builders/Party/PartyInfoNotiBuilder.cs:18"),
            Variant("party-info-only", "block type 1", "Server/DfoServer/Network/Builders/Party/PartyInfoNotiBuilder.cs:18"),
            Variant("party-roster-only", "block type 2", "Server/DfoServer/Network/Builders/Party/PartyInfoNotiBuilder.cs:18"),
            Variant("party-type5", "block type 5", "Server/DfoServer/Network/Builders/Party/PartyInfoNotiBuilder.cs:18"),
        },
        "ENTER_SELECT_DUNGEON" => new[]
        {
            Variant("enter-select-dungeon", "fixed prefix + counted users + floor", "Server/DfoServer/Network/Builders/Dungeon/EnterSelectDungeonStateBuilder.cs:33"),
        },
        "REQUEST_PEER" => new[]
        {
            Variant("party-invite", "body[2] == 0; exact length 13", "Server/DfoServer/Network/Handlers/PartyHandler.cs:1149"),
            Variant("trade-invite", "body[2] == 1; exact length 11", "Server/DfoServer/Network/Handlers/PartyHandler.cs:1064"),
            Variant("pvp-room-invite", "body[2] == 2; exact length 7", "Server/DfoServer/Network/Handlers/PvpRoomHandler.cs:290"),
        },
        "PARTY_MEMBER_REALTIME_INFO" => new[]
        {
            Variant("party-realtime-list", "count:u8 + count*5-byte records", "Server/DfoServer/Network/Builders/Party/PartyRealtimeInfoBuilder.cs:11"),
        },
        "AREA_USERS" => new[]
        {
            Variant("area-user-roster", "town:u8 + area:u8 + count:u16 + count*8-byte records", "Server/DfoServer/Network/Builders/Town/TownAreaNotificationBuilder.cs:43"),
        },
        _ => InitNotificationCodec.GetManualVariants(name) is { Length: > 0 } initVariants
            ? initVariants
            : InventoryPetSkillNotificationCodec.GetManualVariants(name) is { Length: > 0 } inventoryVariants
            ? inventoryVariants
            : DungeonNotificationCodec.GetManualVariants(name) is { Length: > 0 } dungeonVariants
            ? dungeonVariants
            : RaidNotificationCodec.GetManualVariants(name) is { Length: > 0 } raidVariants
            ? raidVariants
            : ExtendedNotificationCodec.GetManualVariants(name),
    };

    public static bool TryDecode(
        string name,
        byte[] body,
        List<string> diagnostics,
        string? requestedVariant,
        out DecodedBody decoded)
    {
        decoded = name switch
        {
            "USER_UDP_IP_PORT" => DecodeEndpointRoster(body, diagnostics),
            "GET_ITEM" => DecodePickup(body, diagnostics, requestedVariant),
            "USER_STATE" => DecodeUserState(body, diagnostics),
            "PARTY_INFO" => DecodePartyInfo(body, diagnostics, requestedVariant),
            "ENTER_SELECT_DUNGEON" => DecodeEnterSelectDungeon(body, diagnostics),
            "REQUEST_PEER" => DecodePeerInvite(body, diagnostics, requestedVariant),
            "PARTY_MEMBER_REALTIME_INFO" => DecodePartyRealtime(body, diagnostics),
            "AREA_USERS" => DecodeAreaUsers(body, diagnostics),
            "UPDATE_ITEM_LIST" or "ITEM_LIST" or "CREATURE_ITEM_LIST" or "ITEM_LOCK_LIST"
                or "CREATURE_STATE" or "CREATURE_SCRIPT_MESSAGE" or "SKILLINFO" or "COMBO_SKILL_INFO"
                => InventoryPetSkillNotificationCodec.Decode(name, body, diagnostics, requestedVariant),
            "DUNGEON_INFO" or "START_MAP" or "CLEAR_DUNGEON_REWARD" or "DIE_MONSTER"
                or "DEATH_TOWER_INFO" or "START_DEATH_TOWER_MAP" or "DEATH_TOWER_STATE_RANKING"
                or "DEATH_TOWER_STATE_REWARD" or "DEATH_TOWER_STATE_EPLP"
                or "BLOOD_DUNGEON_STATE_RANKING" or "BLOOD_DUNGEON_STATE_REWARD"
                or "BLOOD_MONSTER_SPAWN" or "START_BLOOD_MAP"
                or "BLOOD_ROUND_INTERVAL_TIME" or "HELL_PARTY_MONSTER_INFO"
                => DungeonNotificationCodec.Decode(name, body, diagnostics, requestedVariant),
            "DUNGEON_PERMISSION" or "GAME_OPTION" or "LOAD_COOLTIME_ITEM_INFO" or "LOAD_EFFECT_ITEM_INFO"
                or "HOTKEY_OPTION" or "COLLECT_BOX" or "INCREASE_CHANCE_LOTTERY_ALL"
                => InitNotificationCodec.Decode(name, body, diagnostics, requestedVariant),
            "RAID_SET_SYMBOL" or "RAID_DUNGEON_PARTICIPATION_INFO" or "RAID_WAITING_LIST"
                or "RAID_ENTRY_COST_INFO" or "RAID_REWARD_LIST" or "RAID_BUFF_SYSTEM" or "RAID_MONSTER_HP"
                => RaidNotificationCodec.Decode(name, body, diagnostics, requestedVariant),
            "ACCEPTABLE_QUEST_LIST" or "EXPERT_JOB_INFO" or "ITEM_EFFECT" or "SECRET_SHOP_NPC" or "SECRET_SHOP_ITEM_LIST"
                or "USER_APC_INFO_TOD" or "TOD_CLEAR_REWARD" or "TITLE_BOOK_LIST" or "TOURNAMENT_INFO" or "TOURNAMENT_MAP_INFO"
                or "TOURNAMENT_CLEAR_REWARD" or "CHARACTER_DEL_BUFF" or "CHARACTER_BUFF_DUNGEON" or "MINIMAP_ICON_INFO" or "DAILY_CHALLENGE"
                or "SERVER_BROADCAST" or "TAG_CHARACTER_INFO" or "RAID_MODIFY"
                => ExtendedNotificationCodec.Decode(name, body, diagnostics, requestedVariant),
            _ => null!,
        };
        return decoded is not null;
    }

    public static bool TryEncode(string name, string? variant, JsonElement fields, out byte[] body)
    {
        body = name switch
        {
            "USER_UDP_IP_PORT" => EncodeEndpointRoster(fields),
            "GET_ITEM" => EncodePickup(variant, fields),
            "USER_STATE" => EncodeUserState(fields),
            "PARTY_INFO" => EncodePartyInfo(variant, fields),
            "ENTER_SELECT_DUNGEON" => EncodeEnterSelectDungeon(fields),
            "REQUEST_PEER" => EncodePeerInvite(variant, fields),
            "PARTY_MEMBER_REALTIME_INFO" => EncodePartyRealtime(fields),
            "AREA_USERS" => EncodeAreaUsers(fields),
            "UPDATE_ITEM_LIST" or "ITEM_LIST" or "CREATURE_ITEM_LIST" or "ITEM_LOCK_LIST"
                or "CREATURE_STATE" or "CREATURE_SCRIPT_MESSAGE" or "SKILLINFO" or "COMBO_SKILL_INFO"
                => InventoryPetSkillNotificationCodec.Encode(name, variant, fields),
            "DUNGEON_INFO" or "START_MAP" or "CLEAR_DUNGEON_REWARD" or "DIE_MONSTER"
                or "DEATH_TOWER_INFO" or "START_DEATH_TOWER_MAP" or "DEATH_TOWER_STATE_RANKING"
                or "DEATH_TOWER_STATE_REWARD" or "DEATH_TOWER_STATE_EPLP"
                or "BLOOD_DUNGEON_STATE_RANKING" or "BLOOD_DUNGEON_STATE_REWARD"
                or "BLOOD_MONSTER_SPAWN" or "START_BLOOD_MAP"
                or "BLOOD_ROUND_INTERVAL_TIME" or "HELL_PARTY_MONSTER_INFO"
                => DungeonNotificationCodec.Encode(name, variant, fields),
            "DUNGEON_PERMISSION" or "GAME_OPTION" or "LOAD_COOLTIME_ITEM_INFO" or "LOAD_EFFECT_ITEM_INFO"
                or "HOTKEY_OPTION" or "COLLECT_BOX" or "INCREASE_CHANCE_LOTTERY_ALL"
                => InitNotificationCodec.Encode(name, variant, fields),
            "RAID_SET_SYMBOL" or "RAID_DUNGEON_PARTICIPATION_INFO" or "RAID_WAITING_LIST"
                or "RAID_ENTRY_COST_INFO" or "RAID_REWARD_LIST" or "RAID_BUFF_SYSTEM" or "RAID_MONSTER_HP"
                => RaidNotificationCodec.Encode(name, variant, fields),
            "ACCEPTABLE_QUEST_LIST" or "EXPERT_JOB_INFO" or "ITEM_EFFECT" or "SECRET_SHOP_NPC" or "SECRET_SHOP_ITEM_LIST"
                or "USER_APC_INFO_TOD" or "TOD_CLEAR_REWARD" or "TITLE_BOOK_LIST" or "TOURNAMENT_INFO" or "TOURNAMENT_MAP_INFO"
                or "TOURNAMENT_CLEAR_REWARD" or "CHARACTER_DEL_BUFF" or "CHARACTER_BUFF_DUNGEON" or "MINIMAP_ICON_INFO" or "DAILY_CHALLENGE"
                or "SERVER_BROADCAST" or "TAG_CHARACTER_INFO" or "RAID_MODIFY"
                => ExtendedNotificationCodec.Encode(name, variant, fields),
            _ => System.Array.Empty<byte>(),
        };
        return Supports(name);
    }

    private static DecodedBody DecodeEndpointRoster(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        if (body.Length < 1)
        {
            diagnostics.Add("endpoint roster requires count:u8");
            return new DecodedBody("peer-endpoint-roster", fields);
        }
        var count = body[0];
        fields["memberCount"] = count;
        if (body.Length != 1 + count * 22)
            diagnostics.Add($"endpoint roster expects {1 + count * 22} bytes, got {body.Length}");
        var records = new List<object>();
        for (var index = 0; index < count && 1 + (index + 1) * 22 <= body.Length; index++)
        {
            var offset = 1 + index * 22;
            records.Add(new
            {
                userId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)),
                innerIpv4 = string.Join('.', body.AsSpan(offset + 2, 4).ToArray()),
                outerIpv4 = string.Join('.', body.AsSpan(offset + 6, 4).ToArray()),
                port = (ushort)((body[offset + 10] << 8) | body[offset + 11]),
                accountId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 12, 4)),
                natType = body[offset + 16],
                mtu = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 17, 4)),
                characterAttribute = body[offset + 21],
            });
        }
        fields["members"] = records;
        return new DecodedBody("peer-endpoint-roster", fields);
    }

    private static DecodedBody DecodePickup(byte[] body, List<string> diagnostics, string? requestedVariant)
    {
        var fields = Base(body);
        var variant = requestedVariant ?? (body.Length == 17 ? "pickup-item" : body.Length == 53 ? "pickup-gold" : string.Empty);
        if (variant.Equals("pickup-item", StringComparison.OrdinalIgnoreCase))
        {
            if (body.Length != 17) diagnostics.Add($"pickup-item expects 17 bytes, got {body.Length}");
            Read(fields, body, ("sourceSceneSlot", "u16", 0), ("pickerActorId", "u16", 2), ("pickerActorEcho", "u16", 12), ("destinationSlot", "u16", 14), ("moveFlag", "u8", 16));
            if (body.Length >= 12) fields["reserved8Hex"] = Convert.ToHexString(body.AsSpan(4, 8));
            return new DecodedBody("pickup-item", fields);
        }
        if (variant.Equals("pickup-gold", StringComparison.OrdinalIgnoreCase))
        {
            if (body.Length != 53) diagnostics.Add($"pickup-gold expects 53 bytes, got {body.Length}");
            Read(fields, body, ("sourceSceneSlot", "u16", 0), ("pickerActorId", "u16", 2), ("effectFlag", "u8", 4), ("goldAmount", "u32", 5), ("extraFlag", "u8", 9), ("extraGold", "u32", 10), ("reservedValue", "u32", 14));
            if (body.Length >= 53) fields["emptyEffectRecordsHex"] = Convert.ToHexString(body.AsSpan(18, 35));
            return new DecodedBody("pickup-gold", fields);
        }
        diagnostics.Add($"GET_ITEM notification has unknown body length {body.Length}; expected 17 or 53");
        return new DecodedBody("pickup-unresolved", fields);
    }

    private static DecodedBody DecodeUserState(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        if (body.Length < 1) { diagnostics.Add("USER_STATE requires count:u8"); return new DecodedBody("user-state-list", fields); }
        var count = body[0]; fields["count"] = count;
        if (body.Length != 1 + count * 3) diagnostics.Add($"USER_STATE expects {1 + count * 3} bytes, got {body.Length}");
        fields["users"] = Enumerable.Range(0, Math.Min(count, Math.Max(0, (body.Length - 1) / 3))).Select(index =>
        {
            var offset = 1 + index * 3;
            return new { userId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)), userState = body[offset + 2] };
        }).ToArray();
        return new DecodedBody("user-state-list", fields);
    }

    private static DecodedBody DecodePartyInfo(byte[] body, List<string> diagnostics, string? requestedVariant)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        if (!reader.TryReadUInt16(out var blockCount) || !reader.TryReadUInt16(out var partyId) || !reader.TryReadByte(out var type))
        {
            diagnostics.Add("PARTY_INFO header is truncated");
            return new DecodedBody("party-info-invalid", fields);
        }
        fields["blockCount"] = blockCount;
        fields["partyId"] = partyId;
        fields["type"] = type;
        if (!string.IsNullOrWhiteSpace(requestedVariant) && VariantForPartyType(type) != requestedVariant)
            diagnostics.Add($"requested PARTY_INFO variant '{requestedVariant}' does not match type {type}");
        if (type is 0 or 1)
        {
            ReadByte(reader, fields, diagnostics, "titleIndex");
            ReadDstr(reader, fields, diagnostics, "title");
            ReadByte(reader, fields, diagnostics, "isReturnUserParty");
            ReadByte(reader, fields, diagnostics, "userMax");
            ReadUInt16(reader, fields, diagnostics, "dungeonIndex");
            ReadByte(reader, fields, diagnostics, "dungeonDifficulty");
            ReadByte(reader, fields, diagnostics, "isEventCharacterParty");
        }
        if (type is 0 or 2)
        {
            var slots = new List<object>();
            for (var index = 0; index < 8; index++)
            {
                if (!reader.TryReadUInt16(out var userId) || !reader.TryReadByte(out var reserved0) || !reader.TryReadByte(out var reserved1) || !reader.TryReadByte(out var flag))
                { diagnostics.Add($"PARTY_INFO roster slot {index} is truncated"); break; }
                slots.Add(new { slot = index, userId, empty = userId == 0xFFFF, reserved0, reserved1, flag });
            }
            fields["slots"] = slots;
            ReadByte(reader, fields, diagnostics, "rosterTail0");
            ReadByte(reader, fields, diagnostics, "rosterTail1");
            ReadByte(reader, fields, diagnostics, "partyLevelFlag");
        }
        if (type == 5) ReadByte(reader, fields, diagnostics, "type5Reserved");
        if (type <= 2) ReadByte(reader, fields, diagnostics, "hasExtra");
        Finish(reader, fields);
        return new DecodedBody(VariantForPartyType(type), fields);
    }

    private static DecodedBody DecodeEnterSelectDungeon(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body); var reader = new PacketReader(body);
        ReadInt32(reader, fields, diagnostics, "mode"); ReadUInt16(reader, fields, diagnostics, "reservedHeader");
        if (!reader.TryReadByte(out var count)) { diagnostics.Add("ENTER_SELECT_DUNGEON user count is truncated"); return new DecodedBody("enter-select-dungeon", fields); }
        fields["userCount"] = count;
        var users = new List<object>();
        for (var index = 0; index < count; index++)
        { if (!reader.TryReadUInt16(out var userId) || !reader.TryReadByte(out var state)) { diagnostics.Add($"ENTER_SELECT_DUNGEON user {index} is truncated"); break; } users.Add(new { userId, state }); }
        fields["users"] = users;
        ReadInt32(reader, fields, diagnostics, "reservedBody"); ReadUInt16(reader, fields, diagnostics, "towerOfDespairFloor");
        if (reader.TryReadBytes(3, out var tail)) fields["reservedTailHex"] = Convert.ToHexString(tail); else diagnostics.Add("ENTER_SELECT_DUNGEON tail is truncated");
        Finish(reader, fields);
        return new DecodedBody("enter-select-dungeon", fields);
    }

    private static DecodedBody DecodePeerInvite(byte[] body, List<string> diagnostics, string? requestedVariant)
    {
        var fields = Base(body);
        if (body.Length < 3) { diagnostics.Add("REQUEST_PEER notification requires inviterUid:u16 and requestType:u8"); return new DecodedBody("peer-invite-invalid", fields); }
        var type = body[2];
        var variant = type switch { 0 => "party-invite", 1 => "trade-invite", 2 => "pvp-room-invite", _ => "peer-invite-unknown" };
        if (!string.IsNullOrWhiteSpace(requestedVariant) && !variant.Equals(requestedVariant, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add($"requested REQUEST_PEER variant '{requestedVariant}' does not match request type {type}");
        fields["inviterUserId"] = BinaryPrimitives.ReadUInt16LittleEndian(body);
        fields["requestType"] = type;
        if (body.Length >= 7) fields["peerToken"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3, 4));
        if (type == 0)
        {
            if (body.Length != 13) diagnostics.Add($"party invite expects 13 bytes, got {body.Length}");
            if (body.Length >= 13) fields["partyValues"] = new[]
            {
                BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(9, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(11, 2)),
            };
        }
        else if (type == 1)
        {
            if (body.Length != 11) diagnostics.Add($"trade invite expects 11 bytes, got {body.Length}");
            if (body.Length >= 11) fields["inviterCreateTime"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(7, 4));
        }
        else if (type == 2 && body.Length != 7) diagnostics.Add($"PvP room invite expects 7 bytes, got {body.Length}");
        return new DecodedBody(variant, fields);
    }

    private static DecodedBody DecodePartyRealtime(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        if (body.Length < 1) { diagnostics.Add("party realtime roster requires count:u8"); return new DecodedBody("party-realtime-list", fields); }
        var count = body[0]; fields["memberCount"] = count;
        if (body.Length != 1 + count * 5) diagnostics.Add($"party realtime roster expects {1 + count * 5} bytes, got {body.Length}");
        fields["members"] = Enumerable.Range(0, Math.Min(count, Math.Max(0, (body.Length - 1) / 5))).Select(index =>
        {
            var offset = 1 + index * 5;
            return new { userId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)), hpPercent = body[offset + 2], isHelpAbuseParty = body[offset + 3] != 0, slotIndex = body[offset + 4] };
        }).ToArray();
        return new DecodedBody("party-realtime-list", fields);
    }

    private static DecodedBody DecodeAreaUsers(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        if (body.Length < 4) { diagnostics.Add("AREA_USERS requires town, area, and count"); return new DecodedBody("area-user-roster", fields); }
        fields["townId"] = body[0]; fields["areaId"] = body[1];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2)); fields["userCount"] = count;
        if (body.Length != 4 + count * 8) diagnostics.Add($"AREA_USERS expects {4 + count * 8} bytes, got {body.Length}");
        fields["users"] = Enumerable.Range(0, Math.Min(count, Math.Max(0, (body.Length - 4) / 8))).Select(index =>
        {
            var offset = 4 + index * 8;
            return new { userId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)), x = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 2, 2)), y = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 4, 2)), direction = body[offset + 6], state = body[offset + 7] };
        }).ToArray();
        return new DecodedBody("area-user-roster", fields);
    }

    private static byte[] EncodeEndpointRoster(JsonElement fields)
    {
        var members = GetArray(fields, "members"); if (members.Length > byte.MaxValue) throw new ArgumentException("members exceeds 255 entries");
        return Build(writer =>
        {
            writer.Byte((byte)members.Length);
            foreach (var member in members)
            {
                writer.UInt16(U16(member, "userId")); writer.Bytes(ParseIpv4(String(member, "innerIpv4"))); writer.Bytes(ParseIpv4(String(member, "outerIpv4")));
                var port = U16(member, "port"); writer.Byte((byte)(port >> 8)); writer.Byte((byte)port); writer.UInt32(U32(member, "accountId"));
                writer.Byte(Byte(member, "natType")); writer.UInt32(U32(member, "mtu")); writer.Byte(Byte(member, "characterAttribute"));
            }
        });
    }

    private static byte[] EncodePickup(string? variant, JsonElement fields)
    {
        var selected = variant ?? (fields.TryGetProperty("goldAmount", out _) ? "pickup-gold" : "pickup-item");
        if (selected.Equals("pickup-gold", StringComparison.OrdinalIgnoreCase))
            return Build(writer => { writer.UInt16(U16(fields, "sourceSceneSlot")); writer.UInt16(U16(fields, "pickerActorId")); writer.Byte(Byte(fields, "effectFlag", 1)); writer.UInt32(U32(fields, "goldAmount")); writer.Byte(Byte(fields, "extraFlag", 1)); writer.UInt32(U32(fields, "extraGold")); writer.UInt32(U32(fields, "reservedValue")); writer.Bytes(new byte[35]); });
        return Build(writer => { writer.UInt16(U16(fields, "sourceSceneSlot")); writer.UInt16(U16(fields, "pickerActorId")); writer.Bytes(Hex(fields, "reserved8Hex", 8)); writer.UInt16(U16(fields, "pickerActorEcho", U16(fields, "pickerActorId"))); writer.UInt16(U16(fields, "destinationSlot")); writer.Byte(Byte(fields, "moveFlag", 7)); });
    }

    private static byte[] EncodeUserState(JsonElement fields)
    {
        var users = GetArray(fields, "users"); if (users.Length > byte.MaxValue) throw new ArgumentException("users exceeds 255 entries");
        return Build(writer => { writer.Byte((byte)users.Length); foreach (var user in users) { writer.UInt16(U16(user, "userId")); writer.Byte(Byte(user, "userState")); } });
    }

    private static byte[] EncodePartyInfo(string? variant, JsonElement fields)
    {
        var type = fields.TryGetProperty("type", out var typeValue) ? checked((byte)typeValue.GetInt32()) : variant switch { "party-info-only" => (byte)1, "party-roster-only" => (byte)2, "party-type5" => (byte)5, _ => (byte)0 };
        return Build(writer =>
        {
            writer.UInt16(U16(fields, "blockCount", 1)); writer.UInt16(U16(fields, "partyId")); writer.Byte(type);
            if (type is 0 or 1)
            {
                writer.Byte(Byte(fields, "titleIndex")); writer.Dstr(String(fields, "title")); writer.Byte(Byte(fields, "isReturnUserParty"));
                writer.Byte(Byte(fields, "userMax", 4)); writer.UInt16(U16(fields, "dungeonIndex")); writer.Byte(Byte(fields, "dungeonDifficulty")); writer.Byte(Byte(fields, "isEventCharacterParty"));
            }
            if (type is 0 or 2)
            {
                var slots = GetArray(fields, "slots");
                for (var index = 0; index < 8; index++)
                {
                    var slot = slots.FirstOrDefault(item => item.TryGetProperty("slot", out var slotIndex) && slotIndex.GetInt32() == index);
                    writer.UInt16(slot.ValueKind == JsonValueKind.Object ? U16(slot, "userId", 0xFFFF) : (ushort)0xFFFF);
                    writer.Byte(slot.ValueKind == JsonValueKind.Object ? Byte(slot, "reserved0") : (byte)0);
                    writer.Byte(slot.ValueKind == JsonValueKind.Object ? Byte(slot, "reserved1") : (byte)0);
                    writer.Byte(slot.ValueKind == JsonValueKind.Object ? Byte(slot, "flag") : (byte)0);
                }
                writer.Byte(Byte(fields, "rosterTail0")); writer.Byte(Byte(fields, "rosterTail1")); writer.Byte(Byte(fields, "partyLevelFlag"));
            }
            if (type == 5) writer.Byte(Byte(fields, "type5Reserved"));
            if (type <= 2) writer.Byte(Byte(fields, "hasExtra"));
        });
    }

    private static byte[] EncodeEnterSelectDungeon(JsonElement fields)
    {
        var users = GetArray(fields, "users"); if (users.Length > byte.MaxValue) throw new ArgumentException("users exceeds 255 entries");
        return Build(writer => { writer.Int32(I32(fields, "mode", 1)); writer.UInt16(U16(fields, "reservedHeader")); writer.Byte((byte)users.Length); foreach (var user in users) { writer.UInt16(U16(user, "userId")); writer.Byte(Byte(user, "state")); } writer.Int32(I32(fields, "reservedBody")); writer.UInt16(U16(fields, "towerOfDespairFloor")); writer.Bytes(Hex(fields, "reservedTailHex", 3)); });
    }

    private static byte[] EncodePeerInvite(string? variant, JsonElement fields)
    {
        var type = fields.TryGetProperty("requestType", out var typeValue) ? checked((byte)typeValue.GetInt32()) : variant switch { "trade-invite" => (byte)1, "pvp-room-invite" => (byte)2, _ => (byte)0 };
        return Build(writer =>
        {
            writer.UInt16(U16(fields, "inviterUserId")); writer.Byte(type); writer.Int32(I32(fields, "peerToken"));
            if (type == 0) { var values = GetArray(fields, "partyValues"); for (var index = 0; index < 3; index++) writer.UInt16(index < values.Length ? checked((ushort)values[index].GetInt32()) : (ushort)0); }
            else if (type == 1) writer.Int32(I32(fields, "inviterCreateTime"));
        });
    }

    private static byte[] EncodePartyRealtime(JsonElement fields)
    {
        var members = GetArray(fields, "members"); if (members.Length > byte.MaxValue) throw new ArgumentException("members exceeds 255 entries");
        return Build(writer => { writer.Byte((byte)members.Length); foreach (var member in members) { writer.UInt16(U16(member, "userId")); writer.Byte(Byte(member, "hpPercent", 100)); writer.Byte(BoolByte(member, "isHelpAbuseParty")); writer.Byte(Byte(member, "slotIndex")); } });
    }

    private static byte[] EncodeAreaUsers(JsonElement fields)
    {
        var users = GetArray(fields, "users"); if (users.Length > ushort.MaxValue) throw new ArgumentException("users exceeds 65535 entries");
        return Build(writer => { writer.Byte(Byte(fields, "townId")); writer.Byte(Byte(fields, "areaId")); writer.UInt16((ushort)users.Length); foreach (var user in users) { writer.UInt16(U16(user, "userId")); writer.Int16(I16(user, "x")); writer.Int16(I16(user, "y")); writer.Byte(Byte(user, "direction")); writer.Byte(Byte(user, "state")); } });
    }

    private static string VariantForPartyType(byte type) => type switch { 0 => "party-info-and-roster", 1 => "party-info-only", 2 => "party-roster-only", 5 => "party-type5", _ => $"party-type-{type}" };
    private static PacketVariant Variant(string name, string discriminator, params string[] sources) => new(name, null, sources) { Discriminator = discriminator, Confidence = "confirmed-from-server-source" };
    private static Dictionary<string, object?> Base(byte[] body) => new(StringComparer.Ordinal) { ["bodyLength"] = body.Length, ["rawHex"] = Convert.ToHexString(body) };
    private static void Finish(PacketReader reader, Dictionary<string, object?> fields) { fields["consumedBytes"] = reader.Offset; if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail); }
    private static void Read(Dictionary<string, object?> fields, byte[] body, params (string Name, string Type, int Offset)[] schema) { foreach (var field in schema) { var width = PacketSchemaRegistry.FieldWidth(field.Type); if (field.Offset + width <= body.Length) fields[field.Name] = PacketSchemaRegistry.ReadField(body, field.Type, field.Offset); } }
    private static void ReadByte(PacketReader r, IDictionary<string, object?> f, List<string> d, string n) { if (r.TryReadByte(out var v)) f[n] = v; else d.Add($"{n}:u8 is truncated"); }
    private static void ReadUInt16(PacketReader r, IDictionary<string, object?> f, List<string> d, string n) { if (r.TryReadUInt16(out var v)) f[n] = v; else d.Add($"{n}:u16 is truncated"); }
    private static void ReadInt32(PacketReader r, IDictionary<string, object?> f, List<string> d, string n) { if (r.TryReadInt32(out var v)) f[n] = v; else d.Add($"{n}:i32 is truncated"); }
    private static void ReadDstr(PacketReader r, IDictionary<string, object?> f, List<string> d, string n) { if (r.TryReadDString(Encoding.UTF8, out var v)) f[n] = v; else d.Add($"{n}:dstr is truncated or malformed"); }
    private static byte[] Build(Action<Writer> action) { var writer = new Writer(); action(writer); return writer.ToArray(); }
    private static JsonElement[] GetArray(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : System.Array.Empty<JsonElement>();
    private static byte Byte(JsonElement value, string name, byte fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((byte)property.GetInt32()) : fallback;
    private static short I16(JsonElement value, string name, short fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((short)property.GetInt32()) : fallback;
    private static ushort U16(JsonElement value, string name, ushort fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((ushort)property.GetInt32()) : fallback;
    private static int I32(JsonElement value, string name, int fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
    private static uint U32(JsonElement value, string name, uint fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetUInt32() : fallback;
    private static string String(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetString() ?? fallback : fallback;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static byte BoolByte(JsonElement value, string name, byte fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return fallback;
        return property.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number => checked((byte)property.GetInt32()),
            _ => fallback,
        };
    }
    private static byte[] Hex(JsonElement value, string name, int length) { var bytes = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? PacketInput.ParseHex(property.GetString() ?? string.Empty) : new byte[length]; if (bytes.Length != length) throw new ArgumentException($"fields.{name} must contain exactly {length} bytes"); return bytes; }
    private static byte[] ParseIpv4(string text) { var parts = text.Split('.'); if (parts.Length != 4) throw new ArgumentException($"invalid IPv4 address '{text}'"); return parts.Select(byte.Parse).ToArray(); }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = new();
        public void Byte(byte value) => _bytes.Add(value);
        public void Bytes(IEnumerable<byte> value) => _bytes.AddRange(value);
        public void Int16(short value) { var buffer = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(buffer, value); Bytes(buffer); }
        public void UInt16(ushort value) { var buffer = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(buffer, value); Bytes(buffer); }
        public void Int32(int value) { var buffer = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(buffer, value); Bytes(buffer); }
        public void UInt32(uint value) { var buffer = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buffer, value); Bytes(buffer); }
        public void Dstr(string value) { var bytes = Encoding.UTF8.GetBytes(value); Int32(bytes.Length); Bytes(bytes); }
        public byte[] ToArray() => _bytes.ToArray();
    }
}

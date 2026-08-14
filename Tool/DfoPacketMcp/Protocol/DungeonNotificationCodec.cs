using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

internal static class DungeonNotificationCodec
{
    public static PacketVariant[] GetManualVariants(string name) => name switch
    {
        "DUNGEON_INFO" =>
        [
            Variant("dungeon-info", "i16 dungeonId + difficulty/mode/room bytes + counted extra pair groups + fixed tail", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:13"),
        ],
        "START_MAP" =>
        [
            Variant("start-map-standard", "header + counted monsters + counted passive drops + optional ridable group", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:92"),
            Variant("start-map-revisit", "exact length 16; map revisit header without monster/item lists", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:187"),
        ],
        "CLEAR_DUNGEON_REWARD" =>
        [
            Variant("clear-reward", "fixed reward/experience prefix with variable free-card item tail", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:348"),
        ],
        "DIE_MONSTER" =>
        [
            Variant("monster-death-drops", "monsterSeq:u16 + dropCount:u8 + count*39-byte drops + fixed 4-byte tail", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:202"),
        ],
        "DEATH_TOWER_INFO" =>
        [
            Variant("tower-info", "exact length 8", "Server/DfoServer/Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:15"),
        ],
        "START_DEATH_TOWER_MAP" =>
        [
            Variant("tower-stage-map", "stage/map header + counted 14-byte monsters + counted 18-byte items", "Server/DfoServer/Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:30"),
        ],
        "DEATH_TOWER_STATE_RANKING" =>
        [
            Variant("tower-ranking", "fixed header + five ranking groups of eight dstr records", "Server/DfoServer/Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:75"),
        ],
        "DEATH_TOWER_STATE_REWARD" =>
        [
            Variant("tower-reward", "rewardExp:i32 + four counted item groups", "Server/DfoServer/Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:107"),
        ],
        "DEATH_TOWER_STATE_EPLP" =>
        [
            Variant("tower-eplp-state", "exact length 1", "Server/DfoServer/Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:129"),
        ],
        "BLOOD_DUNGEON_STATE_RANKING" =>
        [
            Variant("blood-ranking", "six u32 values", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:90"),
        ],
        "BLOOD_DUNGEON_STATE_REWARD" =>
        [
            Variant("blood-reward", "round/current/max/count + count*8-byte rewards + three group tails", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:108"),
        ],
        "BLOOD_MONSTER_SPAWN" =>
        [
            Variant("blood-monster-wave", "count:u16 + count*15-byte monster records + tail:u16", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:36"),
        ],
        "START_BLOOD_MAP" =>
        [
            Variant("blood-map-revisit", "exact length 8; revisit flag 0", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:13"),
            Variant("blood-map-standard", "exact length 13; revisit flag 1 plus map header", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:13"),
        ],
        "BLOOD_ROUND_INTERVAL_TIME" =>
        [
            Variant("blood-round-interval", "round:u8 + intervalMilliseconds:u32", "Server/DfoServer/Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:59"),
        ],
        "HELL_PARTY_MONSTER_INFO" =>
        [
            Variant("hell-party-monster-levels", "count:i32 + count*(actorId:i32 + level:i32)", "Server/DfoServer/Network/Builders/Dungeon/DungeonNotificationBuilder.cs:78"),
        ],
        _ => [],
    };

    public static DecodedBody Decode(string name, byte[] body, List<string> diagnostics, string? requestedVariant) => name switch
    {
        "DUNGEON_INFO" => DecodeDungeonInfo(body, diagnostics),
        "START_MAP" => DecodeStartMap(body, diagnostics, requestedVariant),
        "CLEAR_DUNGEON_REWARD" => DecodeClearReward(body, diagnostics),
        "DIE_MONSTER" => DecodeMonsterDie(body, diagnostics),
        "DEATH_TOWER_INFO" => DecodeTowerInfo(body, diagnostics),
        "START_DEATH_TOWER_MAP" => DecodeTowerStageMap(body, diagnostics),
        "DEATH_TOWER_STATE_RANKING" => DecodeTowerRanking(body, diagnostics),
        "DEATH_TOWER_STATE_REWARD" => DecodeTowerReward(body, diagnostics),
        "DEATH_TOWER_STATE_EPLP" => DecodeFixedByte(body, diagnostics, "tower-eplp-state", "allMembersHaveRequiredItem"),
        "BLOOD_DUNGEON_STATE_RANKING" => DecodeBloodRanking(body, diagnostics),
        "BLOOD_DUNGEON_STATE_REWARD" => DecodeBloodReward(body, diagnostics),
        "BLOOD_MONSTER_SPAWN" => DecodeBloodMonsterSpawn(body, diagnostics),
        "START_BLOOD_MAP" => DecodeBloodMap(body, diagnostics, requestedVariant),
        "BLOOD_ROUND_INTERVAL_TIME" => DecodeBloodInterval(body, diagnostics),
        "HELL_PARTY_MONSTER_INFO" => DecodeHellParty(body, diagnostics),
        _ => new DecodedBody("unsupported", Base(body)),
    };

    public static byte[] Encode(string name, string? variant, JsonElement fields) => name switch
    {
        "DUNGEON_INFO" => EncodeDungeonInfo(fields),
        "START_MAP" => EncodeStartMap(variant, fields),
        "CLEAR_DUNGEON_REWARD" => EncodeClearReward(fields),
        "DIE_MONSTER" => EncodeMonsterDie(fields),
        "DEATH_TOWER_INFO" => EncodeTowerInfo(fields),
        "START_DEATH_TOWER_MAP" => EncodeTowerStageMap(fields),
        "DEATH_TOWER_STATE_RANKING" => EncodeTowerRanking(fields),
        "DEATH_TOWER_STATE_REWARD" => EncodeTowerReward(fields),
        "DEATH_TOWER_STATE_EPLP" => [Byte(fields, "allMembersHaveRequiredItem")],
        "BLOOD_DUNGEON_STATE_RANKING" => EncodeBloodRanking(fields),
        "BLOOD_DUNGEON_STATE_REWARD" => EncodeBloodReward(fields),
        "BLOOD_MONSTER_SPAWN" => EncodeBloodMonsterSpawn(fields),
        "START_BLOOD_MAP" => EncodeBloodMap(variant, fields),
        "BLOOD_ROUND_INTERVAL_TIME" => Build(w => { w.Byte(Byte(fields, "round")); w.UInt32(U32(fields, "intervalMilliseconds")); }),
        "HELL_PARTY_MONSTER_INFO" => EncodeHellParty(fields),
        _ => [],
    };

    private static DecodedBody DecodeDungeonInfo(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body);
        var reader = new PacketReader(body);
        ReadI16(reader, fields, diagnostics, "dungeonId");
        ReadByte(reader, fields, diagnostics, "difficulty");
        ReadByte(reader, fields, diagnostics, "modeFlag");
        ReadByte(reader, fields, diagnostics, "bossX");
        ReadByte(reader, fields, diagnostics, "bossY");
        ReadByte(reader, fields, diagnostics, "hellPartyRoomX");
        ReadByte(reader, fields, diagnostics, "hellPartyRoomY");
        ReadByte(reader, fields, diagnostics, "dungeonMode");
        var groups = new List<object>();
        if (!reader.TryReadByte(out var groupCount))
        {
            diagnostics.Add("DUNGEON_INFO extraPairGroup count is truncated");
            return new DecodedBody("dungeon-info", fields);
        }
        fields["extraPairGroupCount"] = groupCount;
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            if (!reader.TryReadByte(out var pairCount))
            {
                diagnostics.Add($"DUNGEON_INFO group {groupIndex} count is truncated");
                break;
            }
            var pairs = new List<object>();
            for (var pairIndex = 0; pairIndex < pairCount; pairIndex++)
            {
                if (!reader.TryReadByte(out var first) || !reader.TryReadByte(out var second))
                {
                    diagnostics.Add($"DUNGEON_INFO group {groupIndex} pair {pairIndex} is truncated");
                    break;
                }
                pairs.Add(new { first, second });
            }
            groups.Add(new { groupIndex, pairCount, pairs });
        }
        fields["extraPairGroups"] = groups;
        ReadUInt16(reader, fields, diagnostics, "hellPartyEnabled");
        ReadUInt16(reader, fields, diagnostics, "value1");
        ReadByte(reader, fields, diagnostics, "value2");
        ReadByte(reader, fields, diagnostics, "flagA");
        ReadUInt32(reader, fields, diagnostics, "packetSeed");
        ReadByte(reader, fields, diagnostics, "paramA");
        ReadByte(reader, fields, diagnostics, "paramB");
        ReadByte(reader, fields, diagnostics, "paramC");
        ReadByte(reader, fields, diagnostics, "tailFlag0");
        ReadByte(reader, fields, diagnostics, "tailFlag1");
        ReadByte(reader, fields, diagnostics, "tailFlag2");
        ReadUInt32(reader, fields, diagnostics, "tailReserved");
        Finish(reader, fields);
        return new DecodedBody("dungeon-info", fields);
    }

    private static byte[] EncodeDungeonInfo(JsonElement fields) => Build(w =>
    {
        w.Int16(I16(fields, "dungeonId"));
        w.Byte(Byte(fields, "difficulty"));
        w.Byte(Byte(fields, "modeFlag"));
        w.Byte(Byte(fields, "bossX"));
        w.Byte(Byte(fields, "bossY"));
        w.Byte(Byte(fields, "hellPartyRoomX", 0xFF));
        w.Byte(Byte(fields, "hellPartyRoomY", 0xFF));
        w.Byte(Byte(fields, "dungeonMode"));
        var groups = Array(fields, "extraPairGroups");
        if (groups.Length > byte.MaxValue) throw new ArgumentException("extraPairGroups exceeds 255");
        w.Byte((byte)groups.Length);
        foreach (var group in groups)
        {
            var pairs = Array(group, "pairs");
            if (pairs.Length > byte.MaxValue) throw new ArgumentException("DUNGEON_INFO pair group exceeds 255");
            w.Byte((byte)pairs.Length);
            foreach (var pair in pairs) { w.Byte(Byte(pair, "first")); w.Byte(Byte(pair, "second")); }
        }
        w.UInt16(U16(fields, "hellPartyEnabled"));
        w.UInt16(U16(fields, "value1", 0x000C));
        w.Byte(Byte(fields, "value2")); w.Byte(Byte(fields, "flagA"));
        w.UInt32(U32(fields, "packetSeed", 0xFFFFFFFF));
        w.Byte(Byte(fields, "paramA")); w.Byte(Byte(fields, "paramB")); w.Byte(Byte(fields, "paramC"));
        w.Byte(Byte(fields, "tailFlag0")); w.Byte(Byte(fields, "tailFlag1")); w.Byte(Byte(fields, "tailFlag2"));
        w.UInt32(U32(fields, "tailReserved"));
    });

    private static DecodedBody DecodeStartMap(byte[] body, List<string> diagnostics, string? requestedVariant)
    {
        if (requestedVariant?.Equals("start-map-revisit", StringComparison.OrdinalIgnoreCase) == true || body.Length == 16)
        {
            var fields = Base(body);
            if (body.Length != 16) diagnostics.Add($"start-map-revisit expects 16 bytes, got {body.Length}");
            if (body.Length >= 16)
            {
                fields["x"] = body[0]; fields["y"] = body[1]; fields["layeredRoomFlag"] = body[2];
                fields["randomSeed"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3, 4));
                fields["hellPartyMode"] = body[7]; fields["unknownAfterHellPartyMode"] = body[8];
                fields["roomStateValue"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(9, 4));
                fields["roomStateFlag"] = body[13]; fields["hellPartyFogFlag"] = body[14]; fields["partyMemberIndex"] = body[15];
            }
            return new DecodedBody("start-map-revisit", fields);
        }
        var fieldsStandard = Base(body);
        var reader = new PacketReader(body);
        ReadByte(reader, fieldsStandard, diagnostics, "x"); ReadByte(reader, fieldsStandard, diagnostics, "y");
        ReadByte(reader, fieldsStandard, diagnostics, "layeredRoomFlag"); ReadInt32(reader, fieldsStandard, diagnostics, "randomSeed");
        ReadByte(reader, fieldsStandard, diagnostics, "hellPartyMode"); ReadByte(reader, fieldsStandard, diagnostics, "unknownAfterHellPartyMode");
        ReadInt32(reader, fieldsStandard, diagnostics, "roomStateValue"); ReadByte(reader, fieldsStandard, diagnostics, "roomStateFlag");
        ReadUInt16(reader, fieldsStandard, diagnostics, "mapIndex");
        if (!reader.TryReadByte(out var monsterCount)) { diagnostics.Add("START_MAP monsterCount is truncated"); return new DecodedBody("start-map-standard", fieldsStandard); }
        fieldsStandard["monsterCount"] = monsterCount;
        var monsters = new List<object>();
        for (var i = 0; i < monsterCount; i++)
        {
            if (!reader.TryReadUInt16(out var templateOrder) || !reader.TryReadInt32(out var packetIndex) || !reader.TryReadUInt16(out var sequenceId)
                || !reader.TryReadInt32(out var code) || !reader.TryReadByte(out var level) || !reader.TryReadByte(out var type)
                || !reader.TryReadByte(out var flag0) || !reader.TryReadByte(out var flag1) || !reader.TryReadInt32(out var extraState))
            { diagnostics.Add($"START_MAP monster {i} is truncated"); break; }
            monsters.Add(new { templateOrder, packetIndex, sequenceId, code, level, type, flag0, flag1, extraState });
        }
        fieldsStandard["monsters"] = monsters;
        if (!reader.TryReadByte(out var extraCount)) { diagnostics.Add("START_MAP extra entry count is truncated"); return new DecodedBody("start-map-standard", fieldsStandard); }
        fieldsStandard["extraEntryCount"] = extraCount;
        var extras = new List<object>();
        for (var i = 0; i < extraCount; i++)
        {
            if (!reader.TryReadByte(out var objectIndex) || !reader.TryReadUInt16(out var globalSeq) || !reader.TryReadUInt32(out var itemTemplateId)
                || !reader.TryReadUInt32(out var value) || !reader.TryReadUInt16(out var endurance) || !reader.TryReadByte(out var amplifyType)
                || !reader.TryReadUInt16(out var amplifyValue) || !reader.TryReadUInt16(out var extended16) || !reader.TryReadByte(out var extended8))
            { diagnostics.Add($"START_MAP extra entry {i} is truncated"); break; }
            extras.Add(new { objectIndex, globalSeq, itemTemplateId, value, endurance, amplifyType, amplifyValue, extended16, extended8 });
        }
        fieldsStandard["extraEntries"] = extras;
        ReadByte(reader, fieldsStandard, diagnostics, "hellPartyFogFlag");
        if (reader.TryReadByte(out var ridableGroupCount))
        {
            fieldsStandard["ridableGroupCount"] = ridableGroupCount;
            var groups = new List<object>();
            for (var g = 0; g < ridableGroupCount; g++)
            {
                if (!reader.TryReadByte(out var objectCount)) { diagnostics.Add($"START_MAP ridable group {g} count is truncated"); break; }
                var objects = new List<object>();
                for (var i = 0; i < objectCount; i++)
                {
                    if (!reader.TryReadInt32(out var posX) || !reader.TryReadInt32(out var posY) || !reader.TryReadInt32(out var objectIndex)
                        || !reader.TryReadInt32(out var faction) || !reader.TryReadInt32(out var spawnMode)) { diagnostics.Add($"START_MAP ridable object {g}/{i} is truncated"); break; }
                    objects.Add(new { posX, posY, objectIndex, faction, spawnMode });
                }
                groups.Add(new { groupIndex = g, objectCount, objects });
            }
            fieldsStandard["ridableGroups"] = groups;
        }
        ReadByte(reader, fieldsStandard, diagnostics, "partyMemberIndex");
        Finish(reader, fieldsStandard);
        Validate(requestedVariant, "start-map-standard", diagnostics);
        return new DecodedBody("start-map-standard", fieldsStandard);
    }

    private static byte[] EncodeStartMap(string? variant, JsonElement fields)
    {
        if (variant?.Equals("start-map-revisit", StringComparison.OrdinalIgnoreCase) == true)
            return Build(w => { w.Byte(Byte(fields, "x")); w.Byte(Byte(fields, "y")); w.Byte(Byte(fields, "layeredRoomFlag")); w.Int32(I32(fields, "randomSeed")); w.Byte(Byte(fields, "hellPartyMode")); w.Byte(Byte(fields, "unknownAfterHellPartyMode")); w.Int32(I32(fields, "roomStateValue", 1)); w.Byte(Byte(fields, "roomStateFlag")); w.Byte(Byte(fields, "hellPartyFogFlag")); w.Byte(Byte(fields, "partyMemberIndex", 0xFF)); });
        var monsters = Array(fields, "monsters"); var extras = Array(fields, "extraEntries"); var groups = Array(fields, "ridableGroups");
        return Build(w =>
        {
            w.Byte(Byte(fields, "x")); w.Byte(Byte(fields, "y")); w.Byte(Byte(fields, "layeredRoomFlag")); w.Int32(I32(fields, "randomSeed")); w.Byte(Byte(fields, "hellPartyMode")); w.Byte(Byte(fields, "unknownAfterHellPartyMode")); w.Int32(I32(fields, "roomStateValue", 1)); w.Byte(Byte(fields, "roomStateFlag")); w.UInt16(U16(fields, "mapIndex"));
            w.Byte(checked((byte)monsters.Length)); foreach (var m in monsters) { w.UInt16(U16(m, "templateOrder")); w.Int32(I32(m, "packetIndex")); w.UInt16(U16(m, "sequenceId")); w.Int32(I32(m, "code")); w.Byte(Byte(m, "level")); w.Byte(Byte(m, "type")); w.Byte(Byte(m, "flag0")); w.Byte(Byte(m, "flag1")); w.Int32(I32(m, "extraState")); }
            w.Byte(checked((byte)extras.Length)); foreach (var e in extras) { w.Byte(Byte(e, "objectIndex")); w.UInt16(U16(e, "globalSeq")); w.UInt32(U32(e, "itemTemplateId")); w.UInt32(U32(e, "value")); w.UInt16(U16(e, "endurance")); w.Byte(Byte(e, "amplifyType")); w.UInt16(U16(e, "amplifyValue")); w.UInt16(U16(e, "extended16")); w.Byte(Byte(e, "extended8")); }
            w.Byte(Byte(fields, "hellPartyFogFlag")); w.Byte(checked((byte)groups.Length)); foreach (var g in groups) { var objects = Array(g, "objects"); w.Byte(checked((byte)objects.Length)); foreach (var o in objects) { w.Int32(I32(o, "posX")); w.Int32(I32(o, "posY")); w.Int32(I32(o, "objectIndex")); w.Int32(I32(o, "faction")); w.Int32(I32(o, "spawnMode")); } } w.Byte(Byte(fields, "partyMemberIndex", 0xFF));
        });
    }

    private static DecodedBody DecodeClearReward(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body); var reader = new PacketReader(body);
        ReadUInt32(reader, fields, diagnostics, "clearBaseExp"); ReadInt32(reader, fields, diagnostics, "scoreBonusExp"); ReadUInt32(reader, fields, diagnostics, "partyClearBreakdownExp"); ReadInt32(reader, fields, diagnostics, "avatarExp"); ReadByte(reader, fields, diagnostics, "reservedBaseByte");
        var bonus = new List<int>(); for (var i = 0; i < 25; i++) { if (!reader.TryReadInt32(out var value)) { diagnostics.Add($"CLEAR_DUNGEON_REWARD bonus {i} is truncated"); break; } bonus.Add(value); } fields["bonusExpSlots"] = bonus;
        var post = new List<int>(); for (var i = 0; i < 8; i++) { if (!reader.TryReadInt32(out var value)) { diagnostics.Add($"CLEAR_DUNGEON_REWARD postBase {i} is truncated"); break; } post.Add(value); } fields["postBaseSlots"] = post;
        var score = new List<int>(); for (var i = 0; i < 4; i++) { if (!reader.TryReadInt32(out var value)) { diagnostics.Add($"CLEAR_DUNGEON_REWARD score {i} is truncated"); break; } score.Add(value); } fields["scoreSlots"] = score;
        ReadUInt32(reader, fields, diagnostics, "questValue"); ReadByte(reader, fields, diagnostics, "dropTableCount");
        if (reader.TryReadByte(out var freeCount))
        {
            fields["freeCardCount"] = freeCount;
            ReadInt32(reader, fields, diagnostics, "freeCardItemId");
            ReadInt32(reader, fields, diagnostics, "freeCardGold");
            if (freeCount > 1)
            {
                ReadInt32(reader, fields, diagnostics, "freeCardBonusItemId");
                ReadInt32(reader, fields, diagnostics, "freeCardBonusItemCount");
            }
            if (reader.TryReadBytes(7, out var seatFlags)) fields["freeCardSeatFlagsHex"] = Convert.ToHexString(seatFlags); else diagnostics.Add("CLEAR_DUNGEON_REWARD free-card seat flags are truncated");
        }
        ReadInt32(reader, fields, diagnostics, "paidCardCost");
        if (reader.TryReadBytes(8, out var buffTable0)) fields["buffTable0Hex"] = Convert.ToHexString(buffTable0); else diagnostics.Add("CLEAR_DUNGEON_REWARD buff table 0 is truncated");
        if (reader.TryReadBytes(8, out var buffTable1)) fields["buffTable1Hex"] = Convert.ToHexString(buffTable1); else diagnostics.Add("CLEAR_DUNGEON_REWARD buff table 1 is truncated");
        ReadInt32(reader, fields, diagnostics, "tailCardItemId");
        ReadByte(reader, fields, diagnostics, "endFlagA");
        ReadByte(reader, fields, diagnostics, "endFlagB");
        ReadUInt32(reader, fields, diagnostics, "monsterExp");
        ReadInt32(reader, fields, diagnostics, "tailReserved");
        Finish(reader, fields); return new DecodedBody("clear-reward", fields);
    }

    private static byte[] EncodeClearReward(JsonElement fields) => Build(w =>
    {
        w.UInt32(U32(fields, "clearBaseExp")); w.Int32(I32(fields, "scoreBonusExp")); w.UInt32(U32(fields, "partyClearBreakdownExp")); w.Int32(I32(fields, "avatarExp")); w.Byte(Byte(fields, "reservedBaseByte"));
        var bonus = U32Array(fields, "bonusExpSlots", 25); foreach (var value in bonus) w.UInt32(value); var post = U32Array(fields, "postBaseSlots", 8); foreach (var value in post) w.UInt32(value); var score = U32Array(fields, "scoreSlots", 4); foreach (var value in score) w.UInt32(value); w.UInt32(U32(fields, "questValue")); w.Byte(Byte(fields, "dropTableCount"));
        var hasBonusItem = I32(fields, "freeCardBonusItemId") > 0;
        w.Byte((byte)(hasBonusItem ? 2 : 1));
        w.Int32(I32(fields, "freeCardItemId"));
        w.Int32(I32(fields, "freeCardGold"));
        if (hasBonusItem)
        {
            w.Int32(I32(fields, "freeCardBonusItemId"));
            w.Int32(I32(fields, "freeCardBonusItemCount"));
        }
        w.Bytes(Hex(fields, "freeCardSeatFlagsHex", 7));
        w.Int32(I32(fields, "paidCardCost"));
        w.Bytes(Hex(fields, "buffTable0Hex", 8));
        w.Bytes(Hex(fields, "buffTable1Hex", 8));
        w.Int32(I32(fields, "tailCardItemId"));
        w.Byte(Byte(fields, "endFlagA"));
        w.Byte(Byte(fields, "endFlagB"));
        w.UInt32(U32(fields, "monsterExp"));
        w.Int32(I32(fields, "tailReserved"));
    });

    private static DecodedBody DecodeMonsterDie(byte[] body, List<string> diagnostics)
    {
        var fields = Base(body); var reader = new PacketReader(body); ReadUInt16(reader, fields, diagnostics, "monsterSequenceId"); if (!reader.TryReadByte(out var count)) { diagnostics.Add("DIE_MONSTER dropCount is truncated"); return new DecodedBody("monster-death-drops", fields); } fields["dropCount"] = count;
        var drops = new List<object>(); for (var i = 0; i < count; i++) { if (!reader.TryReadUInt16(out var sceneSlot) || !reader.TryReadUInt32(out var itemTemplateId) || !reader.TryReadByte(out var upgradeLevel) || !reader.TryReadUInt32(out var valueOrCount) || !reader.TryReadUInt16(out var endurance) || !reader.TryReadUInt32(out var unknown32) || !reader.TryReadByte(out var refineLevel) || !reader.TryReadByte(out var amplifyType) || !reader.TryReadUInt16(out var amplifyValue) || !reader.TryReadUInt32(out var enchantCardId) || !reader.TryReadByte(out var socketCount) || !reader.TryReadUInt16(out var extra16) || !reader.TryReadByte(out var listCount) || !reader.TryReadBytes(8, out var padding) || !reader.TryReadUInt16(out var ownerActorId)) { diagnostics.Add($"DIE_MONSTER drop {i} is truncated"); break; } drops.Add(new { sceneSlot, itemTemplateId, upgradeLevel, valueOrCount, endurance, unknown32, refineLevel, amplifyType, amplifyValue, enchantCardId, socketCount, extra16, listCount, paddingHex = Convert.ToHexString(padding), ownerActorId }); }
        fields["drops"] = drops; if (reader.TryReadBytes(4, out var tail)) fields["fixedTailHex"] = Convert.ToHexString(tail); else diagnostics.Add("DIE_MONSTER fixed tail is truncated"); Finish(reader, fields); return new DecodedBody("monster-death-drops", fields);
    }

    private static byte[] EncodeMonsterDie(JsonElement fields) { var drops = Array(fields, "drops"); return Build(w => { w.UInt16(U16(fields, "monsterSequenceId")); w.Byte(checked((byte)drops.Length)); foreach (var d in drops) { w.UInt16(U16(d, "sceneSlot")); w.UInt32(U32(d, "itemTemplateId")); w.Byte(Byte(d, "upgradeLevel")); w.UInt32(U32(d, "valueOrCount")); w.UInt16(U16(d, "endurance")); w.UInt32(U32(d, "unknown32")); w.Byte(Byte(d, "refineLevel")); w.Byte(Byte(d, "amplifyType")); w.UInt16(U16(d, "amplifyValue")); w.UInt32(U32(d, "enchantCardId")); w.Byte(Byte(d, "socketCount")); w.UInt16(U16(d, "extra16")); w.Byte(Byte(d, "listCount")); w.Bytes(Hex(d, "paddingHex", 8)); w.UInt16(U16(d, "ownerActorId")); } w.Bytes(Hex(fields, "fixedTailHex", 4)); }); }

    private static DecodedBody DecodeTowerInfo(byte[] body, List<string> diagnostics) { var fields = Base(body); if (body.Length != 8) diagnostics.Add($"DEATH_TOWER_INFO expects 8 bytes, got {body.Length}"); if (body.Length >= 8) { fields["dungeonId"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4)); fields["endStage"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2)); fields["towerInfoModeByte"] = body[6]; fields["randomBuffType"] = body[7]; } return new DecodedBody("tower-info", fields); }
    private static byte[] EncodeTowerInfo(JsonElement fields) => Build(w => { w.UInt32(U32(fields, "dungeonId")); w.UInt16(U16(fields, "endStage")); w.Byte(Byte(fields, "towerInfoModeByte")); w.Byte(Byte(fields, "randomBuffType", 11)); });

    private static DecodedBody DecodeTowerStageMap(byte[] body, List<string> diagnostics) { var fields = Base(body); var reader = new PacketReader(body); ReadUInt16(reader, fields, diagnostics, "currentStage"); ReadUInt32(reader, fields, diagnostics, "randomSeed"); ReadUInt16(reader, fields, diagnostics, "mapId"); if (!reader.TryReadByte(out var monsterCount)) { diagnostics.Add("START_DEATH_TOWER_MAP monsterCount is truncated"); return new DecodedBody("tower-stage-map", fields); } fields["monsterCount"] = monsterCount; var monsters = new List<object>(); for (var i=0;i<monsterCount;i++){ if(!reader.TryReadUInt32(out var listIndex)||!reader.TryReadUInt16(out var monsterUniqueId)||!reader.TryReadUInt32(out var monsterIndex)||!reader.TryReadByte(out var monsterLevel)||!reader.TryReadByte(out var monsterType)||!reader.TryReadByte(out var isBoxMonster)||!reader.TryReadByte(out var boxIndex)){diagnostics.Add($"tower monster {i} is truncated");break;} monsters.Add(new{listIndex,monsterUniqueId,monsterIndex,monsterLevel,monsterType,isBoxMonster,boxIndex}); } fields["monsters"]=monsters; if(!reader.TryReadByte(out var itemCount)){diagnostics.Add("tower itemCount is truncated");return new DecodedBody("tower-stage-map",fields);} fields["itemCount"]=itemCount; var items=new List<object>(); for(var i=0;i<itemCount;i++){if(!reader.TryReadUInt32(out var sourceListIndex)||!reader.TryReadUInt16(out var itemUniqueId)||!reader.TryReadUInt32(out var itemId)||!reader.TryReadUInt32(out var dropRate)||!reader.TryReadUInt32(out var stackCount)){diagnostics.Add($"tower item {i} is truncated");break;} items.Add(new{sourceListIndex,itemUniqueId,itemId,dropRate,stackCount});} fields["items"]=items; Finish(reader,fields); return new DecodedBody("tower-stage-map",fields); }
    private static byte[] EncodeTowerStageMap(JsonElement fields) { var monsters=Array(fields,"monsters");var items=Array(fields,"items");return Build(w=>{w.UInt16(U16(fields,"currentStage"));w.UInt32(U32(fields,"randomSeed"));w.UInt16(U16(fields,"mapId"));w.Byte(checked((byte)monsters.Length));foreach(var m in monsters){w.UInt32(U32(m,"listIndex"));w.UInt16(U16(m,"monsterUniqueId"));w.UInt32(U32(m,"monsterIndex"));w.Byte(Byte(m,"monsterLevel"));w.Byte(Byte(m,"monsterType"));w.Byte(Byte(m,"isBoxMonster"));w.Byte(Byte(m,"boxIndex"));}w.Byte(checked((byte)items.Length));foreach(var i in items){w.UInt32(U32(i,"sourceListIndex"));w.UInt16(U16(i,"itemUniqueId"));w.UInt32(U32(i,"itemId"));w.UInt32(U32(i,"dropRate"));w.UInt32(U32(i,"stackCount"));}}); }

    private static DecodedBody DecodeTowerRanking(byte[] body, List<string> diagnostics) { var fields=Base(body);var reader=new PacketReader(body);ReadByte(reader,fields,diagnostics,"flag0");ReadInt32(reader,fields,diagnostics,"clearTimeMilliseconds");ReadInt32(reader,fields,diagnostics,"clearedFloorCount");ReadByte(reader,fields,diagnostics,"flag3");ReadUInt32(reader,fields,diagnostics,"dungeonId");ReadByte(reader,fields,diagnostics,"hasMyBestRecord");var groups=new List<object>();for(var g=0;g<5;g++){var records=new List<object>();for(var r=0;r<8;r++){if(!reader.TryReadDString(Encoding.UTF8,out var name)||!reader.TryReadByte(out var byteA)||!reader.TryReadByte(out var byteB)){diagnostics.Add($"tower ranking group {g} record {r} is truncated");break;}records.Add(new{name,byteA,byteB});}if(!reader.TryReadUInt16(out var groupU16)||!reader.TryReadUInt32(out var groupU32A)||!reader.TryReadUInt32(out var groupU32B)){diagnostics.Add($"tower ranking group {g} tail is truncated");break;}groups.Add(new{groupIndex=g,records,groupU16,groupU32A,groupU32B});}fields["groups"]=groups;Finish(reader,fields);return new DecodedBody("tower-ranking",fields);}
    private static byte[] EncodeTowerRanking(JsonElement fields) { var groups=Array(fields,"groups");return Build(w=>{w.Byte(Byte(fields,"flag0"));w.Int32(I32(fields,"clearTimeMilliseconds"));w.Int32(I32(fields,"clearedFloorCount"));w.Byte(Byte(fields,"flag3"));w.UInt32(U32(fields,"dungeonId"));w.Byte(Byte(fields,"hasMyBestRecord"));for(var groupIndex=0;groupIndex<5;groupIndex++){var g=groupIndex<groups.Length?groups[groupIndex]:default;var records=Array(g,"records");for(var i=0;i<8;i++){var rec=i<records.Length?records[i]:default;w.Dstr(String(rec,"name"));w.Byte(Byte(rec,"byteA"));w.Byte(Byte(rec,"byteB"));}w.UInt16(U16(g,"groupU16"));w.UInt32(U32(g,"groupU32A"));w.UInt32(U32(g,"groupU32B"));}});}

    private static DecodedBody DecodeTowerReward(byte[] body,List<string> diagnostics){var fields=Base(body);var reader=new PacketReader(body);ReadInt32(reader,fields,diagnostics,"rewardExp");var groups=new List<object>();for(var g=0;g<4;g++){if(!reader.TryReadByte(out var count)){diagnostics.Add($"tower reward group {g} count is truncated");break;}var items=new List<object>();for(var i=0;i<count;i++){if(!reader.TryReadInt32(out var itemId)||!reader.TryReadInt32(out var addInfo)){diagnostics.Add($"tower reward group {g} item {i} is truncated");break;}items.Add(new{itemId,addInfo});}groups.Add(new{groupIndex=g,count,items});}fields["groups"]=groups;Finish(reader,fields);return new DecodedBody("tower-reward",fields);}
    private static byte[] EncodeTowerReward(JsonElement fields){var groups=Array(fields,"groups");return Build(w=>{w.Int32(I32(fields,"rewardExp"));for(var g=0;g<4;g++){var group=g<groups.Length?groups[g]:default;var items=Array(group,"items");w.Byte(checked((byte)items.Length));foreach(var item in items){w.Int32(I32(item,"itemId"));w.Int32(I32(item,"addInfo"));}}});}

    private static DecodedBody DecodeBloodRanking(byte[] body,List<string> diagnostics){var fields=Base(body);var reader=new PacketReader(body);ReadUInt32(reader,fields,diagnostics,"playTimeMilliseconds");ReadUInt32(reader,fields,diagnostics,"currentRound");ReadUInt32(reader,fields,diagnostics,"bestTimeMilliseconds");ReadUInt32(reader,fields,diagnostics,"bestRound");ReadUInt32(reader,fields,diagnostics,"maxRound");ReadUInt32(reader,fields,diagnostics,"rewardExperience");Finish(reader,fields);return new DecodedBody("blood-ranking",fields);}
    private static byte[] EncodeBloodRanking(JsonElement fields)=>Build(w=>{w.UInt32(U32(fields,"playTimeMilliseconds"));w.UInt32(U32(fields,"currentRound"));w.UInt32(U32(fields,"bestTimeMilliseconds"));w.UInt32(U32(fields,"bestRound"));w.UInt32(U32(fields,"maxRound"));w.UInt32(U32(fields,"rewardExperience"));});
    private static DecodedBody DecodeBloodReward(byte[] body,List<string> diagnostics){var fields=Base(body);var reader=new PacketReader(body);ReadByte(reader,fields,diagnostics,"currentRound");ReadByte(reader,fields,diagnostics,"maxRound");if(!reader.TryReadByte(out var count)){diagnostics.Add("blood reward count is truncated");return new DecodedBody("blood-reward",fields);}fields["count"]=count;var rewards=new List<object>();for(var i=0;i<count;i++){if(!reader.TryReadInt32(out var itemId)||!reader.TryReadInt32(out var value)){diagnostics.Add($"blood reward {i} is truncated");break;}rewards.Add(new{itemId,value,isGold=itemId==0});}fields["rewards"]=rewards;Finish(reader,fields);return new DecodedBody("blood-reward",fields);}
    private static byte[] EncodeBloodReward(JsonElement fields){var rewards=Array(fields,"rewards");return Build(w=>{w.Byte(Byte(fields,"currentRound"));w.Byte(Byte(fields,"maxRound"));w.Byte(checked((byte)rewards.Length));foreach(var r in rewards){w.Int32(I32(r,"itemId"));w.Int32(I32(r,"value"));}w.Bytes(Hex(fields,"groupTailHex",3));});}
    private static DecodedBody DecodeBloodMonsterSpawn(byte[] body,List<string> diagnostics){var fields=Base(body);var reader=new PacketReader(body);if(!reader.TryReadUInt16(out var count)){diagnostics.Add("BLOOD_MONSTER_SPAWN count is truncated");return new DecodedBody("blood-monster-wave",fields);}fields["count"]=count;var monsters=new List<object>();for(var i=0;i<count;i++){if(!reader.TryReadByte(out var variant)||!reader.TryReadUInt16(out var sequenceId)||!reader.TryReadUInt32(out var monsterCode)||!reader.TryReadByte(out var monsterType)||!reader.TryReadByte(out var level)||!reader.TryReadUInt16(out var scale)||!reader.TryReadUInt16(out var x)||!reader.TryReadUInt16(out var y)||!reader.TryReadUInt16(out var z)){diagnostics.Add($"blood monster {i} is truncated");break;}monsters.Add(new{variant,sequenceId,monsterCode,monsterType,level,scale,x,y,z});}fields["monsters"]=monsters;ReadUInt16(reader,fields,diagnostics,"tailValue");Finish(reader,fields);return new DecodedBody("blood-monster-wave",fields);}
    private static byte[] EncodeBloodMonsterSpawn(JsonElement fields){var monsters=Array(fields,"monsters");return Build(w=>{w.UInt16(checked((ushort)monsters.Length));foreach(var m in monsters){w.Byte(Byte(m,"variant"));w.UInt16(U16(m,"sequenceId"));w.UInt32(U32(m,"monsterCode"));w.Byte(Byte(m,"monsterType"));w.Byte(Byte(m,"level"));w.UInt16(U16(m,"scale"));w.UInt16(U16(m,"x"));w.UInt16(U16(m,"y"));w.UInt16(U16(m,"z"));}w.UInt16(U16(fields,"tailValue"));});}
    private static DecodedBody DecodeBloodMap(byte[] body,List<string> diagnostics,string? requestedVariant){var fields=Base(body);if(body.Length!=8&&body.Length!=13)diagnostics.Add($"START_BLOOD_MAP expects 8 or 13 bytes, got {body.Length}");if(body.Length>=8){fields["x"]=body[0];fields["y"]=body[1];fields["seed"]=BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2,4));fields["reserved0"]=body[6];fields["revisitFlag"]=body[7];if(body.Length>=13){fields["mapId"]=BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8,2));fields["tail0"]=body[10];fields["tail1"]=body[11];fields["tail2"]=body[12];}}var actual=body.Length==8?"blood-map-revisit":"blood-map-standard";Validate(requestedVariant,actual,diagnostics);return new DecodedBody(actual,fields);}
    private static byte[] EncodeBloodMap(string? variant,JsonElement fields){var revisit=variant?.Equals("blood-map-revisit",StringComparison.OrdinalIgnoreCase)==true;return Build(w=>{w.Byte(Byte(fields,"x"));w.Byte(Byte(fields,"y"));w.Int32(I32(fields,"seed"));w.Byte(Byte(fields,"reserved0"));w.Byte(revisit?(byte)0:(byte)1);if(!revisit){w.UInt16(U16(fields,"mapId"));w.Byte(Byte(fields,"tail0"));w.Byte(Byte(fields,"tail1"));w.Byte(Byte(fields,"tail2"));}});}
    private static DecodedBody DecodeBloodInterval(byte[] body,List<string> diagnostics){var fields=Base(body);if(body.Length!=5)diagnostics.Add($"BLOOD_ROUND_INTERVAL_TIME expects 5 bytes, got {body.Length}");if(body.Length>=5){fields["round"]=body[0];fields["intervalMilliseconds"]=BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1,4));}return new DecodedBody("blood-round-interval",fields);}
    private static DecodedBody DecodeHellParty(byte[] body,List<string> diagnostics){var fields=Base(body);var reader=new PacketReader(body);if(!reader.TryReadInt32(out var count)){diagnostics.Add("HELL_PARTY_MONSTER_INFO count is truncated");return new DecodedBody("hell-party-monster-levels",fields);}fields["count"]=count;var entries=new List<object>();for(var i=0;i<Math.Max(0,count);i++){if(!reader.TryReadInt32(out var actorId)||!reader.TryReadInt32(out var level)){diagnostics.Add($"HELL_PARTY_MONSTER_INFO entry {i} is truncated");break;}entries.Add(new{actorId,level});}fields["entries"]=entries;Finish(reader,fields);return new DecodedBody("hell-party-monster-levels",fields);}
    private static byte[] EncodeHellParty(JsonElement fields){var entries=Array(fields,"entries");return Build(w=>{w.Int32(entries.Length);foreach(var e in entries){w.Int32(I32(e,"actorId"));w.Int32(I32(e,"level"));}});}

    private static DecodedBody DecodeFixedByte(byte[] body,List<string> diagnostics,string variant,string name){var fields=Base(body);if(body.Length!=1)diagnostics.Add($"{variant} expects 1 byte, got {body.Length}");if(body.Length>0)fields[name]=body[0];return new DecodedBody(variant,fields);}
    private static PacketVariant Variant(string name,string discriminator,params string[] sources)=>new(name,null,sources){Discriminator=discriminator,Confidence="confirmed-from-server-source"};
    private static Dictionary<string,object?> Base(byte[] body)=>new(StringComparer.Ordinal){["bodyLength"]=body.Length,["rawHex"]=Convert.ToHexString(body)};
    private static void Finish(PacketReader reader,Dictionary<string,object?> fields){fields["consumedBytes"]=reader.Offset;if(reader.Remaining>0&&reader.TryReadBytes(reader.Remaining,out var tail))fields["trailingHex"]=Convert.ToHexString(tail);}
    private static void Validate(string? requested,string actual,List<string> diagnostics){if(!string.IsNullOrWhiteSpace(requested)&&!requested.Equals(actual,StringComparison.OrdinalIgnoreCase))diagnostics.Add($"requested variant '{requested}' does not match decoded variant '{actual}'");}
    private static void ReadByte(PacketReader r,IDictionary<string,object?> f,List<string>d,string n){if(r.TryReadByte(out var v))f[n]=v;else d.Add($"{n}:u8 is truncated");}
    private static void ReadI16(PacketReader r,IDictionary<string,object?> f,List<string>d,string n){if(r.TryReadInt16(out var v))f[n]=v;else d.Add($"{n}:i16 is truncated");}
    private static void ReadInt32(PacketReader r,IDictionary<string,object?> f,List<string>d,string n){if(r.TryReadInt32(out var v))f[n]=v;else d.Add($"{n}:i32 is truncated");}
    private static void ReadUInt16(PacketReader r,IDictionary<string,object?> f,List<string>d,string n){if(r.TryReadUInt16(out var v))f[n]=v;else d.Add($"{n}:u16 is truncated");}
    private static void ReadUInt32(PacketReader r,IDictionary<string,object?> f,List<string>d,string n){if(r.TryReadUInt32(out var v))f[n]=v;else d.Add($"{n}:u32 is truncated");}
    private static byte Byte(JsonElement v,string n,byte fallback=0)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?checked((byte)p.GetInt32()):fallback;
    private static short I16(JsonElement v,string n,short fallback=0)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?checked((short)p.GetInt32()):fallback;
    private static ushort U16(JsonElement v,string n,ushort fallback=0)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?checked((ushort)p.GetInt32()):fallback;
    private static uint U32(JsonElement v,string n,uint fallback=0)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?p.GetUInt32():fallback;
    private static int I32(JsonElement v,string n,int fallback=0)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?p.GetInt32():fallback;
    private static string String(JsonElement v,string n,string fallback="")=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?p.GetString()??fallback:fallback;
    private static JsonElement[] Array(JsonElement v,string n)=>v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().ToArray():[];
    private static uint[] U32Array(JsonElement v,string n,int length){var a=Array(v,n);var result=new uint[length];for(var i=0;i<Math.Min(length,a.Length);i++)result[i]=a[i].ValueKind==JsonValueKind.Number?a[i].GetUInt32():0;return result;}
    private static byte[] Hex(JsonElement v,string n,int length){var bytes=v.ValueKind==JsonValueKind.Object&&v.TryGetProperty(n,out var p)?PacketInput.ParseHex(p.GetString()??string.Empty):new byte[length];if(bytes.Length!=length)throw new ArgumentException($"fields.{n} must contain exactly {length} bytes");return bytes;}
    private static byte[] Build(Action<Writer> action){var w=new Writer();action(w);return w.ToArray();}
    private sealed class Writer{private readonly List<byte> b=[];public void Byte(byte v)=>b.Add(v);public void Bytes(IEnumerable<byte> v)=>b.AddRange(v);public void Int16(short v){var x=new byte[2];BinaryPrimitives.WriteInt16LittleEndian(x,v);Bytes(x);}public void UInt16(ushort v){var x=new byte[2];BinaryPrimitives.WriteUInt16LittleEndian(x,v);Bytes(x);}public void Int32(int v){var x=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(x,v);Bytes(x);}public void UInt32(uint v){var x=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(x,v);Bytes(x);}public void Dstr(string v){var x=Encoding.UTF8.GetBytes(v??string.Empty);Int32(x.Length);Bytes(x);}public byte[] ToArray()=>b.ToArray();}
}

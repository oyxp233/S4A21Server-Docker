using System.Buffers.Binary;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

internal static class RaidNotificationCodec
{
    public static PacketVariant[] GetManualVariants(string name) => name switch
    {
        "RAID_SET_SYMBOL" => [Variant("symbol-table", "count:u32 + count*(symbolId:u32 + value:u32)", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:287")],
        "RAID_DUNGEON_PARTICIPATION_INFO" => [
            Variant("participation-enter", "op:u32 == 0", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:345"),
            Variant("participation-exit", "op:u32 == 2", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:345")],
        "RAID_WAITING_LIST" => [Variant("waiting-member-list", "count:u32 + count*(userId:u16 + partyIndex:u32)", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:182")],
        "RAID_ENTRY_COST_INFO" => [Variant("entry-cost-statuses", "count:u32 + count*(userId:u16 + ready:u32 + ownedCount:u32)", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:371")],
        "RAID_REWARD_LIST" => [Variant("reward-list", "rewardType:u32 + count:u32 + count*17-byte reward entries", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:264")],
        "RAID_BUFF_SYSTEM" => [Variant("buff-status-groups", "groupCount:u8 + groups with counted entries", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:389")],
        "RAID_MONSTER_HP" => [Variant("monster-situation-status", "count:u8 + variable situation/member/runtime rows", "Server/DfoServer/Network/Builders/Raid/RaidPacketBuilder.cs:417")],
        _ => [],
    };

    public static DecodedBody Decode(string name, byte[] body, List<string> diagnostics, string? requestedVariant) => name switch
    {
        "RAID_SET_SYMBOL" => DecodeSymbols(body, diagnostics),
        "RAID_DUNGEON_PARTICIPATION_INFO" => DecodeParticipation(body, diagnostics, requestedVariant),
        "RAID_WAITING_LIST" => DecodeWaiting(body, diagnostics),
        "RAID_ENTRY_COST_INFO" => DecodeEntryCost(body, diagnostics),
        "RAID_REWARD_LIST" => DecodeRewards(body, diagnostics),
        "RAID_BUFF_SYSTEM" => DecodeBuffs(body, diagnostics),
        "RAID_MONSTER_HP" => DecodeMonsterHp(body, diagnostics),
        _ => new DecodedBody("unsupported", Base(body)),
    };

    public static byte[] Encode(string name, string? variant, JsonElement fields) => name switch
    {
        "RAID_SET_SYMBOL" => EncodeSymbols(fields),
        "RAID_DUNGEON_PARTICIPATION_INFO" => EncodeParticipation(fields),
        "RAID_WAITING_LIST" => EncodeWaiting(fields),
        "RAID_ENTRY_COST_INFO" => EncodeEntryCost(fields),
        "RAID_REWARD_LIST" => EncodeRewards(fields),
        "RAID_BUFF_SYSTEM" => EncodeBuffs(fields),
        "RAID_MONSTER_HP" => EncodeMonsterHp(fields),
        _ => [],
    };

    private static DecodedBody DecodeSymbols(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body);
        if (!r.TryReadUInt32(out var count)) { d.Add("RAID_SET_SYMBOL count is truncated"); return new("symbol-table", f); }
        f["count"] = count; var entries = new List<object>();
        for (var i = 0; i < count; i++) { if (!r.TryReadUInt32(out var id) || !r.TryReadUInt32(out var value)) { d.Add($"RAID_SET_SYMBOL entry {i} is truncated"); break; } entries.Add(new { symbolId = id, value }); }
        f["entries"] = entries; Finish(r, f); return new("symbol-table", f);
    }
    private static byte[] EncodeSymbols(JsonElement f) { var a = Array(f, "entries"); return Build(w => { w.UInt32((uint)a.Length); foreach (var e in a) { w.UInt32(U32(e, "symbolId")); w.UInt32(U32(e, "value")); } }); }

    private static DecodedBody DecodeParticipation(byte[] body, List<string> d, string? requested)
    {
        var f = Base(body); var r = new PacketReader(body);
        if (!r.TryReadUInt32(out var header) || !r.TryReadUInt32(out var targetId) || !r.TryReadUInt32(out var op) || !r.TryReadUInt32(out var count)) { d.Add("RAID_DUNGEON_PARTICIPATION_INFO header is truncated"); return new("participation-invalid", f); }
        f["header"] = header; f["targetId"] = targetId; f["op"] = op; f["count"] = count;
        var users = new List<uint>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt32(out var userId)) { d.Add($"raid participation member {i} is truncated"); break; } users.Add(userId); }
        f["memberUserIds"] = users; var variant = op == 0 ? "participation-enter" : op == 2 ? "participation-exit" : $"participation-op-{op}";
        if (!string.IsNullOrWhiteSpace(requested) && !requested.Equals(variant, StringComparison.OrdinalIgnoreCase)) d.Add($"requested variant '{requested}' does not match op {op}");
        Finish(r, f); return new(variant, f);
    }
    private static byte[] EncodeParticipation(JsonElement f) { var users = U32Array(f, "memberUserIds"); return Build(w => { w.UInt32(U32(f, "header", 1)); w.UInt32(U32(f, "targetId")); w.UInt32(U32(f, "op")); w.UInt32((uint)users.Length); foreach (var user in users) w.UInt32(user); }); }

    private static DecodedBody DecodeWaiting(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body); if (!r.TryReadUInt32(out var count)) { d.Add("RAID_WAITING_LIST count is truncated"); return new("waiting-member-list", f); }
        f["count"] = count; var entries = new List<object>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt16(out var userId) || !r.TryReadUInt32(out var partyIndex)) { d.Add($"raid waiting entry {i} is truncated"); break; } entries.Add(new { userId, partyIndex }); } f["entries"] = entries; Finish(r, f); return new("waiting-member-list", f);
    }
    private static byte[] EncodeWaiting(JsonElement f) { var a = Array(f, "entries"); return Build(w => { w.UInt32((uint)a.Length); foreach (var e in a) { w.UInt16(U16(e, "userId")); w.UInt32(U32(e, "partyIndex")); } }); }

    private static DecodedBody DecodeEntryCost(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body); if (!r.TryReadUInt32(out var count)) { d.Add("RAID_ENTRY_COST_INFO count is truncated"); return new("entry-cost-statuses", f); }
        f["count"] = count; var entries = new List<object>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt16(out var userId) || !r.TryReadUInt32(out var ready) || !r.TryReadUInt32(out var ownedCount)) { d.Add($"raid entry cost {i} is truncated"); break; } entries.Add(new { userId, ready = ready != 0, readyValue = ready, ownedCount }); } f["entries"] = entries; Finish(r, f); return new("entry-cost-statuses", f);
    }
    private static byte[] EncodeEntryCost(JsonElement f) { var a = Array(f, "entries"); return Build(w => { w.UInt32((uint)a.Length); foreach (var e in a) { w.UInt16(U16(e, "userId")); w.UInt32(Bool(e, "ready") ? 1u : U32(e, "readyValue")); w.UInt32(U32(e, "ownedCount")); } }); }

    private static DecodedBody DecodeRewards(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body); if (!r.TryReadUInt32(out var rewardType) || !r.TryReadUInt32(out var count)) { d.Add("RAID_REWARD_LIST header is truncated"); return new("reward-list", f); }
        f["rewardType"] = rewardType; f["count"] = count; var entries = new List<object>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt16(out var userId) || !r.TryReadByte(out var cardType) || !r.TryReadUInt32(out var flags) || !r.TryReadUInt32(out var itemId) || !r.TryReadUInt32(out var quantity)) { d.Add($"raid reward {i} is truncated"); break; } entries.Add(new { userId, cardType, flags, itemId, quantity }); } f["entries"] = entries; Finish(r, f); return new("reward-list", f);
    }
    private static byte[] EncodeRewards(JsonElement f) { var a = Array(f, "entries"); return Build(w => { w.UInt32(U32(f, "rewardType")); w.UInt32((uint)a.Length); foreach (var e in a) { w.UInt16(U16(e, "userId")); w.Byte(Byte(e, "cardType")); w.UInt32(U32(e, "flags")); w.UInt32(U32(e, "itemId")); w.UInt32(U32(e, "quantity")); } }); }

    private static DecodedBody DecodeBuffs(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body); if (!r.TryReadByte(out var groupCount)) { d.Add("RAID_BUFF_SYSTEM groupCount is truncated"); return new("buff-status-groups", f); }
        f["groupCount"] = groupCount; var groups = new List<object>(); for (var g = 0; g < groupCount; g++) { if (!r.TryReadByte(out var buffType) || !r.TryReadByte(out var count)) { d.Add($"raid buff group {g} header is truncated"); break; } var entries = new List<object>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt16(out var partyIndex) || !r.TryReadUInt16(out var userId) || !r.TryReadUInt32(out var activeUntilTimestamp) || !r.TryReadUInt32(out var cooldownUntilTimestamp)) { d.Add($"raid buff {g}/{i} is truncated"); break; } entries.Add(new { partyIndex, userId, activeUntilTimestamp, cooldownUntilTimestamp }); } groups.Add(new { buffType, count, entries }); } f["groups"] = groups; Finish(r, f); return new("buff-status-groups", f);
    }
    private static byte[] EncodeBuffs(JsonElement f) { var a = Array(f, "groups"); return Build(w => { w.Byte((byte)a.Length); foreach (var g in a) { var e = Array(g, "entries"); w.Byte(Byte(g, "buffType")); w.Byte((byte)e.Length); foreach (var x in e) { w.UInt16(U16(x, "partyIndex")); w.UInt16(U16(x, "userId")); w.UInt32(U32(x, "activeUntilTimestamp")); w.UInt32(U32(x, "cooldownUntilTimestamp")); } } }); }

    private static DecodedBody DecodeMonsterHp(byte[] body, List<string> d)
    {
        var f = Base(body); var r = new PacketReader(body); if (!r.TryReadByte(out var count)) { d.Add("RAID_MONSTER_HP count is truncated"); return new("monster-situation-status", f); }
        f["count"] = count; var entries = new List<object>(); for (var i = 0; i < count; i++) { if (!r.TryReadUInt16(out var situationIndex) || !r.TryReadByte(out var memberCount)) { d.Add($"raid monster status {i} header is truncated"); break; } var memberIds = new List<ushort>(); for (var m = 0; m < memberCount; m++) { if (!r.TryReadUInt16(out var memberId)) { d.Add($"raid monster status {i} member {m} is truncated"); break; } memberIds.Add(memberId); } if (!r.TryReadUInt32(out var usedCoinCount) || !r.TryReadByte(out var runtimeCount)) { d.Add($"raid monster status {i} tail is truncated"); break; } var runtimeValues = new List<uint>(); for (var x = 0; x < runtimeCount; x++) { if (!r.TryReadUInt32(out var value)) { d.Add($"raid monster status {i} runtime {x} is truncated"); break; } runtimeValues.Add(value); } entries.Add(new { situationIndex, memberIds, usedCoinCount, runtimeValues }); } f["entries"] = entries; Finish(r, f); return new("monster-situation-status", f);
    }
    private static byte[] EncodeMonsterHp(JsonElement f) { var a = Array(f, "entries"); return Build(w => { w.Byte((byte)a.Length); foreach (var e in a) { var members = U16Array(e, "memberIds"); var runtime = U32Array(e, "runtimeValues"); w.UInt16(U16(e, "situationIndex")); w.Byte((byte)members.Length); foreach (var member in members) w.UInt16(member); w.UInt32(U32(e, "usedCoinCount")); w.Byte((byte)runtime.Length); foreach (var value in runtime) w.UInt32(value); } }); }

    private static PacketVariant Variant(string name, string discriminator, params string[] sources) => new(name, null, sources) { Discriminator = discriminator, Confidence = "confirmed-from-server-source" };
    private static Dictionary<string, object?> Base(byte[] body) => new(StringComparer.Ordinal) { ["bodyLength"] = body.Length, ["rawHex"] = Convert.ToHexString(body) };
    private static void Finish(PacketReader reader, Dictionary<string, object?> fields) { fields["consumedBytes"] = reader.Offset; if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail); }
    private static JsonElement[] Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [];
    private static byte Byte(JsonElement value, string name, byte fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((byte)property.GetInt32()) : fallback;
    private static ushort U16(JsonElement value, string name, ushort fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? checked((ushort)property.GetInt32()) : fallback;
    private static uint U32(JsonElement value, string name, uint fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetUInt32() : fallback;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static ushort[] U16Array(JsonElement value, string name) => Array(value, name).Select(item => checked((ushort)item.GetInt32())).ToArray();
    private static uint[] U32Array(JsonElement value, string name) => Array(value, name).Select(item => item.GetUInt32()).ToArray();
    private static byte[] Build(Action<Writer> action) { var writer = new Writer(); action(writer); return writer.ToArray(); }
    private sealed class Writer { private readonly List<byte> _bytes = []; public void Byte(byte value) => _bytes.Add(value); public void Bytes(IEnumerable<byte> values) => _bytes.AddRange(values); public void UInt16(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); Bytes(b); } public void UInt32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); Bytes(b); } public byte[] ToArray() => _bytes.ToArray(); }
}

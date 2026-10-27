using System;
using System.Collections.Generic;
using System.Text.Json;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Mailbox
{
    internal static partial class MailboxItemDetailCodec
    {
        // Existing legacy mail remains readable by BuildCreateOptions. New
        // instance transfers select this contract from durable mail origin,
        // never from the presence/absence of a potentially damaged JSON marker.
        internal static string CaptureStrict(InventoryService inventory, ItemCore core)
        {
            ArgumentNullException.ThrowIfNull(inventory);
            RequireCore(core);
            if (core.ItemKind == ItemCore.KindAvatar)
            {
                if (core.AvatarUid <= 0 || !inventory.AvatarDetails.TryGetDetail(core.AvatarUid, out var a)
                    || a.AvatarUid != core.AvatarUid || a.OwnerId != inventory.AccountId
                    || a.CharacterId != inventory.CharacterId || a.ItemId != core.ItemId)
                    throw new InvalidOperationException("Missing or foreign owned avatar detail.");
                var json = JsonSerializer.Serialize(new { FormatVersion = 1, Kind = "avatar", Avatar = new {
                    a.ExpireDate, a.ClearAvatarId, a.JewelSocket, a.Color1, a.Color2, a.DeleteDate } });
                ValidateStrict(core, json);
                return json;
            }
            if (core.ItemKind == ItemCore.KindCreature)
            {
                if (core.CreatureUid <= 0 || !inventory.CreatureDetails.TryGetDetail(core.CreatureUid, out var c)
                    || c.Uid != core.CreatureUid)
                    throw new InvalidOperationException("Missing owned creature detail.");
                var json = JsonSerializer.Serialize(new { FormatVersion = 1, Kind = "creature", Creature = new {
                    c.NameBytes, c.Field04, c.ModeFlag, c.Mode1Field0A, c.Mode1Field0B,
                    c.ProgressValue32, c.FieldAfterValue32, c.ExpireDate, c.TailFlag, c.ExtraJson } });
                ValidateStrict(core, json);
                return json;
            }
            return string.Empty;
        }

        internal static void ValidateStrict(ItemCore core, string detailJson)
            => BuildCreateOptionsStrict(core, detailJson);

        internal static InventoryCreateOptions BuildCreateOptionsStrict(ItemCore core, string detailJson)
        {
            RequireCore(core);
            bool avatar = core.ItemKind == ItemCore.KindAvatar;
            bool creature = core.ItemKind == ItemCore.KindCreature;
            if (!avatar && !creature)
            {
                if (detailJson != string.Empty) throw new InvalidOperationException("Unexpected detail on a plain instance.");
                return null;
            }
            if (string.IsNullOrEmpty(detailJson) || detailJson.Length > 1024 * 1024)
                throw new InvalidOperationException("Missing or oversized strict instance detail.");
            try
            {
                using var document = JsonDocument.Parse(detailJson);
                var root = document.RootElement;
                RequireStrictProperties(root, "FormatVersion", "Kind", avatar ? "Avatar" : "Creature");
                if (root.GetProperty("FormatVersion").GetInt32() != 1
                    || root.GetProperty("Kind").GetString() != (avatar ? "avatar" : "creature"))
                    throw new InvalidOperationException("Strict detail kind/version differs from its core.");
                if (avatar)
                {
                    var p = root.GetProperty("Avatar");
                    RequireStrictProperties(p, "ExpireDate", "ClearAvatarId", "JewelSocket", "Color1", "Color2", "DeleteDate");
                    var socket = p.GetProperty("JewelSocket").GetBytesFromBase64();
                    if (socket.Length != JewelSocket.Size)
                        throw new InvalidOperationException("Avatar transfer requires all five socket entries.");
                    var a = new AvatarDetail {
                        ExpireDate = p.GetProperty("ExpireDate").GetInt32(),
                        ClearAvatarId = p.GetProperty("ClearAvatarId").GetInt32(),
                        JewelSocket = socket,
                        Color1 = p.GetProperty("Color1").GetUInt16(),
                        Color2 = p.GetProperty("Color2").GetUInt16(),
                        DeleteDate = p.GetProperty("DeleteDate").GetInt32()
                    };
                    if (!a.JewelSocket.AsSpan().SequenceEqual(socket))
                        throw new InvalidOperationException("Noncanonical avatar socket data cannot be normalized on transfer.");
                    return new InventoryCreateOptions { AvatarAbilityNo = core.AbilityNo,
                        AvatarDetailTemplate = a };
                }
                else
                {
                    var p = root.GetProperty("Creature");
                    RequireStrictProperties(p, "NameBytes", "Field04", "ModeFlag", "Mode1Field0A", "Mode1Field0B",
                        "ProgressValue32", "FieldAfterValue32", "ExpireDate", "TailFlag", "ExtraJson");
                    var c = new CreatureDetail {
                        NameBytes = p.GetProperty("NameBytes").GetBytesFromBase64(),
                        Field04 = p.GetProperty("Field04").GetByte(),
                        ModeFlag = p.GetProperty("ModeFlag").GetByte(),
                        Mode1Field0A = p.GetProperty("Mode1Field0A").GetByte(),
                        Mode1Field0B = p.GetProperty("Mode1Field0B").GetByte(),
                        ProgressValue32 = p.GetProperty("ProgressValue32").GetInt32(),
                        FieldAfterValue32 = p.GetProperty("FieldAfterValue32").GetInt32(),
                        ExpireDate = p.GetProperty("ExpireDate").GetInt32(),
                        TailFlag = p.GetProperty("TailFlag").GetByte(),
                        ExtraJson = p.GetProperty("ExtraJson").GetString()
                            ?? throw new InvalidOperationException("Missing opaque creature instance state.")
                    };
                    // This model currently has no durable expiry column. Do not
                    // claim to transfer a timed detail that a reload would lose.
                    if (c.ExpireDate != 0)
                        throw new InvalidOperationException("Timed creature detail transfer is not supported.");
                    return new InventoryCreateOptions { CreatureType = core.SealFlag,
                        CreatureDetailTemplate = c };
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is FormatException
                || ex is OverflowException || ex is ArgumentException)
            { throw new InvalidOperationException("Invalid strict instance detail.", ex); }
        }

        private static void RequireCore(ItemCore core)
        {
            if (core == null || core.IsEmpty || core.ItemId <= 0
                || core.ItemKind < ItemCore.KindEquipment || core.ItemKind > ItemCore.KindEpicPiece)
                throw new InvalidOperationException("Strict detail requires a real native core.");
        }

        private static void RequireStrictProperties(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Strict detail object required.");
            var expected = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!expected.Remove(property.Name)) throw new InvalidOperationException("Unknown or duplicate strict detail property.");
            if (expected.Count != 0) throw new InvalidOperationException("Incomplete strict detail object.");
        }
    }
}

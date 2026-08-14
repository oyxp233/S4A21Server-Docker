using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DfoPacketMcp.Protocol;

public static class PacketEncoder
{
    public static byte[] EncodeBody(PacketTypeDefinition definition, string? variant, JsonElement fields)
    {
        if (fields.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            fields = JsonDocument.Parse("{}").RootElement.Clone();

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Noti && definition.EnumName == "USERINFO")
            return EncodeUserInfoHeaderVariant(variant, fields);

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Cmd)
            return definition.EnumName switch
            {
                "CARD_SELECT_RIGHT_STATE" => EncodeCardLayoutResponse(fields),
                "TOURNAMENT_REWARD_SELECT" => EncodeTournamentRewardSelection(fields),
                "SET_CLONE_TITLE" => Build(writer => { writer.WriteByte(GetByte(fields, "status", 1)); writer.WriteInt32(GetInt32(fields, "cloneTitleItemId")); }),
                "BUY_CERASHOP_ITEM" => EncodeCeraShopPurchaseResponse(variant, fields),
                "PREMIUM_SERVICE" => EncodePremiumServiceResponse(fields),
                "SAVE_GAME_OPTION_1" => EncodeRentalCatalogResponse(fields),
                "SELECT_CARD" => EncodeCardInfoResponse(fields),
                "CHANGE_TUTORIAL_FLAG" => EncodeTutorialRewardResponse(fields),
                "SUMMON_MONSTER" => EncodeSummonMonsterResponse(fields),
                "QUERY_CHARAC_INFO_MAILBOX" => EncodeMailboxCharacterQueryResponse(variant, fields),
                "SKILL_COMMAND_CUSTOMIZING" => EncodeSkillCommandEchoResponse(fields),
                "GET_EXPAND_EXP_GAGE_REWARD" => EncodeGrowthCapsuleClaimResponse(variant, fields),
                "BUY_SKILL" => EncodeBuySkillResponse(variant, fields),
                "TOURNAMENT_REWARD_SELECT_STATE" => EncodeTournamentSelectionRights(fields),
                "SELECT_CHARACTER" => EncodeSelectCharacterResponse(variant, fields),
                "BUY_ITEM" => EncodeBuyItemResponse(variant, fields),
                "INVEST_ITEM_AMPLIFY_OPTION" => EncodeInvestAmplifyResponse(variant, fields),
                "COMPOUND_ITEM" => EncodeCompoundItemResponse(variant, fields),
                "RESET_ITEM_ATTR" => EncodeResetItemAttributeResponse(variant, fields),
                "SECRET_SHOP_BUY_ITEM" => EncodeSecretShopBuyResponse(variant, fields),
                "USE_STACKABLE" => EncodeUseStackableResponse(variant, fields),
                "USE_LOTTERY_ITEM" => EncodeLotteryItemResponse(variant, fields),
                "UPGRADE_CHRONICLE" => EncodeChronicleGrowthResponse(variant, fields),
                "CHARGE_RENTPOINT" => EncodeChargeRentPointResponse(variant, fields),
                "MOVE_ITEMSPACE" => EncodeMoveItemSpaceResponse(variant, fields),
                "CRANE_START_USE" => EncodeCraneStartResponse(variant, fields),
                "DISJOINT_ITEM" => EncodeDisjointItemResponse(variant, fields),
                "USE_BOOSTER_ITEM" => EncodeSelectablePackageResponse(variant, fields),
                "BIND_PLUS" => EncodeAvatarCompoundSetResponse(variant, fields),
                "REQUEST_CHARAC_SKILL_INFO" => EncodeCharacterSkillListResponse(variant, fields),
                "UPGRADE_ITEM" => EncodeItemUpgradeResponse(variant, fields),
                "REPAIR_EQUIPMENT" => EncodeRepairEquipmentResponse(variant, fields),
                "USE_RANDOMBOX_ITEM_EXPAND" => EncodeMagicBoxResponse(variant, fields, batch: true),
                "ENCHANT_3RD_CHRONICLE_ITEM" => EncodeChronicleRefineResponse(variant, fields),
                "COMPOUND_AVATAR" => EncodeAvatarCompoundResponse(variant, fields),
                "DELETE_ITEM" => EncodeDeleteItemResponse(variant, fields),
                "USE_RANDOMBOX_ITEM" => EncodeMagicBoxResponse(variant, fields, batch: false),
                "DISJOINT_AVATAR" => EncodeAvatarDisjointResponse(variant, fields),
                "REQUEST_DISJOINT_ITEM" => EncodeExpertDisjointResponse(variant, fields),
                "REPAIR_DISJOINT_MACHINE" or "REPAIR_EXPERT_JOB_STORE" => EncodeExpertRepairResponse(variant, fields),
                "UPGRADE_DISJOINT_MACHINE" => EncodeExpertUpgradeResponse(variant, fields),
                "USE_ENCHANT_STORE" => EncodeExpertEnchantResponse(variant, fields),
                "COMPOUND_ITEM_BY_EXPERT_JOB" => EncodeExpertCompoundResponse(variant, fields),
                "GIVEUP_EXPERT_JOB" => EncodeExpertGiveupResponse(variant, fields),
                "CREATE_EXPERT_JOB_STORE" => EncodeStatusAckResponse(variant, fields),
                "ENTER_EXPERT_JOB_STORE" => EncodeExpertEnterResponse(variant, fields),
                "ENTER_PVP_ROOM" => EncodePvpEnterResponse(variant, fields),
                "DAILY_CHALLENGE_REWARD" => EncodeDailyChallengeRewardResponse(variant, fields),
                _ => EncodeCommandResponse(definition, variant, fields),
            };

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Noti)
        {
            if (OutboundNotificationCodec.TryEncode(definition.EnumName, variant, fields, out var notificationBody))
                return notificationBody;
            return EncodeOutboundVariant(definition, variant, fields);
        }

        if (definition.Flow != PacketFlow.ClientToServer || definition.Kind != PacketKind.Cmd)
            throw new NotSupportedException($"No semantic encoder for {definition.Name}; pass bodyHex or bodyBase64");

        if (definition.SchemaStatus == PacketSchemaStatus.Inferred)
            return EncodeInferredVariant(definition, variant, fields);

        return definition.EnumName switch
        {
            "LOGIN" => EncodeLogin(fields),
            "SELECT_CHARACTER" => Build(writer => writer.WriteUInt16(GetUInt16(fields, "characterSlot"))),
            "RECOVER_STAMINA" or "ENTER_SELECT_DUNGEON" or "DIE_CHARACTER"
                or "SCORE_SCROLL_STATE" or "CHARACTER_STATISTIC" or "UPGRADE_CARGO"
                or "IMAGE_COMMUNICATION_EQUIPMENT_USE" or "VERIFY_CREATURE_QUEST"
                or "START_RAID" or "LOAD_EXTEND_CHARACS" or "PREMIUM_SERVICE" => Array.Empty<byte>(),
            "REQUEST_PEER" => Build(writer =>
            {
                writer.WriteUInt16(GetUInt16(fields, "targetUserId"));
                writer.WriteByte(GetByte(fields, "requestType"));
                if (fields.TryGetProperty("peerId", out var peerId)) writer.WriteInt32(peerId.GetInt32());
            }),
            "WALKOUT_PARTY_MEMBER" => new[] { GetByte(fields, "targetPartySlot") },
            "REPAIR_EQUIPMENT" => EncodeRepairEquipment(fields),
            "USE_COIN" => Build(writer => writer.WriteUInt16(GetUInt16(fields, "targetActorId"))),
            "SET_PLAY_RESULT" => EncodeSetPlayResult(fields),
            "RES_PVP_RANK" => EncodeRequiredRaw(fields, "clientRankSettlementHex", 70),
            "SELECT_CARD" => new[] { GetByte(fields, "cardType"), GetByte(fields, "cardIndex") },
            "EPLP_COMMAND" => new[] { GetByte(fields, "state"), GetByte(fields, "option") },
            "MAILBOX_SEND" => EncodeMailbox(fields, multi: false),
            "MULTI_MAILBOX_SEND" => EncodeMailbox(fields, multi: true),
            "CREATURE_SCRIPT_MESSAGE" => EncodeCreatureScriptMessage(fields),
            "DEATH_TOWER_STAGE_CMD" => new[] { GetByte(fields, "operation") },
            "OVERFLOW_INFO" => EncodeOverflowInfo(variant, fields),
            "CHANGE_ANOTHER_SKILL_TREE" => EncodeSkillTreeSwitch(variant, fields),
            "ONE_TO_ONE_CHAT_STATE" or "SAVE_CHARACTER_OPTION" or "COMBO_SKILL_INFO" => EncodeRawField(fields),
            "INFORM_NOTICE" => EncodeSceneUniqueId(variant, fields),
            "COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET" => fields.TryGetProperty("page", out var page) ? new[] { checked((byte)page.GetInt32()) } : Array.Empty<byte>(),
            "SET_CLONE_TITLE" => Build(writer => writer.WriteInt32(GetInt32(fields, "cloneTitleItemId"))),
            "RAID_DO_BEHAVIOR" => Build(writer => { writer.WriteUInt32(GetUInt32(fields, "targetObjectId")); writer.WriteUInt32(GetUInt32(fields, "behaviorId")); }),
            "DAILY_CHALLENGE_REWARD" => Build(writer => writer.WriteInt32(GetInt32(fields, "groupIndex"))),
            "ADD_EQUIPMENT_EFFECT" => EncodeEquipmentEffect(fields),
            "RENT_EQUIPMENT_ITEM" => EncodeRentalEquipment(fields),
            "CHARGE_RENTPOINT" => EncodePurchaseCount(fields),
            "SELECT_COLLECTBOX" => new[] { GetByte(fields, "boxIndex") },
            "DELETE_ITEM" => EncodeDeleteItem(variant, fields),
            "INCREASE_STATUS" => Build(writer => writer.WriteInt16(GetInt16(fields, "slotIndex"))),
            "SELECT_DUNGEON" => Build(writer =>
            {
                writer.WriteUInt16(GetUInt16(fields, "dungeonId"));
                writer.WriteByte(GetByte(fields, "difficulty"));
                writer.WriteByte(GetByte(fields, "flag1"));
                writer.WriteByte(GetByte(fields, "flag2"));
            }),
            "GET_ITEM" => Build(writer => writer.WriteUInt16(GetUInt16(fields, "srcSlot"))),
            "BOSS_DIE_CHECK" => Build(writer =>
            {
                writer.WriteUInt16(GetUInt16(fields, "userId"));
                writer.WriteUInt16(GetUInt16(fields, "bossSequence"));
            }),
            "TOURNAMENT_REWARD_SELECT" => new[] { GetByte(fields, "cardType"), GetByte(fields, "cardIndex") },
            "SELECT_ULTIMATE_DIFFICULTY" => new[] { GetByte(fields, "difficulty") },
            "DIE_BLOOD_MONSTER" => EncodeUInt16List(fields, "sequenceIds"),
            "SECRET_SHOP_OPEN_CLOSE" => new[] { GetBool(fields, "open") ? (byte)1 : (byte)0 },
            "PARTY_TELEPORT" => Build(writer =>
            {
                writer.WriteByte(GetByte(fields, "townId")); writer.WriteByte(GetByte(fields, "areaId"));
                writer.WriteInt16(GetInt16(fields, "x")); writer.WriteInt16(GetInt16(fields, "y"));
                writer.WriteByte(GetByte(fields, "direction"));
            }),
            _ when definition.SchemaStatus == PacketSchemaStatus.Empty => Array.Empty<byte>(),
            _ => throw new NotSupportedException($"No semantic encoder for {definition.Name}; pass bodyHex or bodyBase64"),
        };
    }

    private static byte[] EncodeLogin(JsonElement fields) => Build(writer =>
    {
        writer.WriteDString(GetString(fields, "mId"), Encoding.ASCII);
        writer.WriteDString(GetString(fields, "passwordHash"), Encoding.ASCII);
    });

    private static byte[] EncodeInferredVariant(PacketTypeDefinition definition, string? variant, JsonElement fields)
    {
        var candidates = definition.Variants.Where(item => item.Schema is not null).ToArray();
        if (!string.IsNullOrWhiteSpace(variant))
        {
            var selected = candidates.FirstOrDefault(item => item.Name.Equals(variant, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"unknown variant '{variant}' for {definition.Name}; candidates: {string.Join(", ", candidates.Select(item => item.Name))}");
            return EncodeInferred(selected.Schema!, fields);
        }
        if (candidates.Length == 1) return EncodeInferred(candidates[0].Schema!, fields);
        if (candidates.Length > 1)
            throw new ArgumentException($"{definition.Name} has multiple body variants; specify one of: {string.Join(", ", candidates.Select(item => item.Name))}");
        if (definition.InferredSchema is not null) return EncodeInferred(definition.InferredSchema, fields);
        throw new NotSupportedException($"No inferred schema for {definition.Name}; pass bodyHex or bodyBase64");
    }

    private static byte[] EncodeRepairEquipment(JsonElement fields)
    {
        var body = new byte[fields.TryGetProperty("quickRepair", out _) ? 8 : fields.TryGetProperty("autoRepair", out _) ? 6 : 5];
        body[0] = GetByte(fields, "inventoryType");
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1, 2), GetInt16(fields, "slotIndex"));
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(3, 2), GetInt16(fields, "repairItemSlot"));
        if (body.Length >= 6) body[5] = GetBool(fields, "autoRepair") ? (byte)1 : (byte)0;
        if (body.Length >= 8) body[7] = GetBool(fields, "quickRepair") ? (byte)1 : (byte)0;
        return body;
    }

    private static byte[] EncodeSetPlayResult(JsonElement fields)
    {
        var body = fields.TryGetProperty("prefixHex", out var prefix)
            ? PacketInput.ParseHex(prefix.GetString() ?? string.Empty)
            : new byte[11];
        if (body.Length < 11) Array.Resize(ref body, 11);
        body[10] = GetByte(fields, "clientRankPoint");
        return body;
    }

    private static byte[] EncodeRequiredRaw(JsonElement fields, string name, int? exactLength = null)
    {
        var body = PacketInput.ParseHex(GetString(fields, name));
        if (exactLength.HasValue && body.Length != exactLength.Value)
            throw new ArgumentException($"fields.{name} must contain exactly {exactLength.Value} bytes");
        return body;
    }

    private static byte[] EncodeRawField(JsonElement fields)
    {
        if (fields.TryGetProperty("rawHex", out var raw)) return PacketInput.ParseHex(raw.GetString() ?? string.Empty);
        if (fields.TryGetProperty("payloadHex", out var payload)) return PacketInput.ParseHex(payload.GetString() ?? string.Empty);
        return Array.Empty<byte>();
    }

    private static byte[] EncodeMailbox(JsonElement fields, bool multi)
    {
        var attachments = fields.TryGetProperty("attachments", out var values)
            ? values.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        if (attachments.Length > 10) throw new ArgumentException("mailbox attachments exceed server limit 10");
        return Build(writer =>
        {
            writer.WriteDString(GetString(fields, "receiverName"), Encoding.UTF8);
            writer.WriteInt32(GetInt32(fields, "gold"));
            if (multi) writer.WriteUInt16(checked((ushort)attachments.Length));
            for (var index = 0; index < attachments.Length; index++)
            {
                var attachment = attachments[index];
                if (!multi || index > 0) writer.WriteByte(GetByte(attachment, "itemType"));
                writer.WriteUInt16(GetUInt16(attachment, "slot"));
                writer.WriteInt32(GetInt32(attachment, "itemId"));
                writer.WriteInt32(GetInt32(attachment, "count"));
            }
            if (fields.TryGetProperty("text", out var text)) writer.WriteDString(text.GetString() ?? string.Empty, Encoding.UTF8);
            if (fields.TryGetProperty("tailHex", out var tail)) writer.WriteBytes(PacketInput.ParseHex(tail.GetString() ?? string.Empty));
        });
    }

    private static byte[] EncodeCreatureScriptMessage(JsonElement fields) => Build(writer =>
    {
        var mode = GetByte(fields, "mode");
        writer.WriteByte(mode);
        writer.WriteUInt16(GetUInt16(fields, "targetUniqueId"));
        writer.WriteUInt32(GetUInt32(fields, "characterId"));
        writer.WriteDString(GetString(fields, "message"), Encoding.UTF8);
        if (mode is 1 or 7) writer.WriteDString(GetString(fields, "targetName"), Encoding.UTF8);
    });

    private static byte[] EncodeOverflowInfo(string? variant, JsonElement fields)
    {
        var selected = variant ?? (fields.TryGetProperty("operation", out var operation) ? operation.GetString() : null);
        return selected?.ToLowerInvariant() switch
        {
            "raid-create-popup-close" or "close-raid-create-popup" => new byte[] { 0x01, 0x99, 0x02 },
            "lottery-overflow-confirm" or "confirm-lottery-overflow" => new byte[] { 0x01, 0x1B, 0x00 },
            _ => throw new ArgumentException("OVERFLOW_INFO requires variant raid-create-popup-close or lottery-overflow-confirm"),
        };
    }

    private static byte[] EncodeSkillTreeSwitch(string? variant, JsonElement fields)
    {
        var index = GetByte(fields, "wireSkillTreeIndex");
        return variant?.Equals("legacy-prefixed-index", StringComparison.OrdinalIgnoreCase) == true
            ? new byte[] { GetByte(fields, "prefix"), index }
            : new[] { index };
    }

    private static byte[] EncodeSceneUniqueId(string? variant, JsonElement fields)
        => variant?.Equals("scene-id-u32", StringComparison.OrdinalIgnoreCase) == true
            ? Build(writer => writer.WriteUInt32(GetUInt32(fields, "rawSceneValue")))
            : Build(writer => writer.WriteUInt16(GetUInt16(fields, "sceneUniqueId")));

    private static byte[] EncodeEquipmentEffect(JsonElement fields)
    {
        var body = fields.TryGetProperty("prefixHex", out var prefix)
            ? PacketInput.ParseHex(prefix.GetString() ?? string.Empty)
            : new byte[21];
        if (body.Length < 21) Array.Resize(ref body, 21);
        body[12] = GetByte(fields, "targetListType");
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13, 4), GetInt32(fields, "targetSlotIndex"));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17, 4), GetInt32(fields, "sourceSlotIndex"));
        return body;
    }

    private static byte[] EncodeRentalEquipment(JsonElement fields)
    {
        var body = new byte[21];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GetUInt32(fields, "shopWeaponId"));
        if (fields.TryGetProperty("reservedHex", out var reserved))
        {
            var bytes = PacketInput.ParseHex(reserved.GetString() ?? string.Empty);
            bytes.AsSpan(0, Math.Min(9, bytes.Length)).CopyTo(body.AsSpan(4, 9));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13, 4), GetUInt32(fields, "inventoryTemplateId"));
        body[17] = GetByte(fields, "rentalDays");
        body[18] = GetByte(fields, "starCostHalf");
        body[19] = GetByte(fields, "priceTier");
        body[20] = fields.TryGetProperty("reservedTail", out var tail) ? checked((byte)tail.GetInt32()) : (byte)0;
        return body;
    }

    private static byte[] EncodePurchaseCount(JsonElement fields)
    {
        var body = fields.TryGetProperty("prefixHex", out var prefix)
            ? PacketInput.ParseHex(prefix.GetString() ?? string.Empty)
            : new byte[19];
        if (body.Length < 19) Array.Resize(ref body, 19);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(17, 2), GetUInt16(fields, "purchaseCount"));
        return body;
    }

    private static byte[] EncodeDeleteItem(string? variant, JsonElement fields)
    {
        var selected = variant?.ToLowerInvariant() ?? "simple-list-prefixed";
        if (selected == "simple-legacy")
            return Build(writer => { writer.WriteInt16(GetInt16(fields, "slotIndex")); writer.WriteInt16(GetInt16(fields, "itemCount")); });
        if (selected == "simple-list-prefixed")
            return Build(writer => { writer.WriteByte(GetByte(fields, "listType")); writer.WriteInt16(GetInt16(fields, "slotIndex")); writer.WriteInt16(GetInt16(fields, "itemCount")); });
        if (selected == "extended-array")
        {
            var entries = fields.GetProperty("entries").EnumerateArray().ToArray();
            if (entries.Length is < 1 or > 100) throw new ArgumentException("DELETE_ITEM extended entries must contain 1..100 records");
            return Build(writer =>
            {
                writer.WriteByte(GetByte(fields, "listType"));
                writer.WriteByte(checked((byte)entries.Length));
                foreach (var entry in entries)
                {
                    writer.WriteInt16(GetInt16(entry, "operationType"));
                    writer.WriteInt16(GetInt16(entry, "slotIndex"));
                    writer.WriteInt32(GetInt32(entry, "itemId"));
                    writer.WriteInt32(GetInt32(entry, "deleteCount"));
                }
            });
        }
        throw new ArgumentException("DELETE_ITEM variant must be simple-legacy, simple-list-prefixed, or extended-array");
    }

    private static byte[] EncodeCommandResponse(PacketTypeDefinition definition, string? variant, JsonElement fields)
    {
        if (definition.Variants.Any(item => item.Schema is not null || !string.IsNullOrWhiteSpace(item.FixedBodyHex)))
            return EncodeOutboundVariant(definition, variant, fields);

        var status = fields.TryGetProperty("status", out var statusValue)
            ? checked((byte)statusValue.GetInt32())
            : variant?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ? (byte)0 : (byte)1;
        if (status == 0)
            return new[] { status, fields.TryGetProperty("errorCode", out var error) ? checked((byte)error.GetInt32()) : (byte)1 };
        return new[] { status };
    }

    private static byte[] EncodeStatusAckResponse(string? variant, JsonElement fields)
        => IsErrorVariant(variant, fields) ? ErrorBody(fields, 1) : new[] { (byte)1 };

    private static byte[] EncodeExpertDisjointResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var materials = GetArray(fields, "materials");
        if (materials.Length > byte.MaxValue) throw new ArgumentException("materials exceeds 255 entries");
        return Build(writer =>
        {
            writer.WriteByte(1);
            writer.WriteInt16(GetInt16(fields, "targetSlotIndex"));
            writer.WriteByte(GetByte(fields, "itemSpace"));
            writer.WriteByte((byte)materials.Length);
            WriteReward10List(writer, materials);
            writer.WriteInt32(GetInt32(fields, "requesterGold"));
            writer.WriteInt32(GetInt32(fields, "endurance"));
        });
    }

    private static byte[] EncodeExpertRepairResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "gold")); writer.WriteInt32(GetInt32(fields, "endurance")); });
    }

    private static byte[] EncodeExpertUpgradeResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "gold")); writer.WriteInt32(GetInt32(fields, "grade")); writer.WriteInt32(GetInt32(fields, "endurance")); });
    }

    private static byte[] EncodeExpertEnchantResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "enchantSucceeded")); writer.WriteUInt32(GetUInt32(fields, "finalExperience")); writer.WriteByte(GetByte(fields, "reserved")); writer.WriteInt32(GetInt32(fields, "endurance")); });
    }

    private static byte[] EncodeExpertCompoundResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var outputs = GetArray(fields, "outputs");
        if (outputs.Length > byte.MaxValue) throw new ArgumentException("outputs exceeds 255 entries");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteByte((byte)outputs.Length);
            foreach (var output in outputs) { writer.WriteInt32(GetInt32(output, "itemId")); writer.WriteInt32(GetInt32(output, "count")); }
            writer.WriteInt32(GetInt32(fields, "successCount")); writer.WriteInt32(GetInt32(fields, "failureCount")); writer.WriteByte(GetByte(fields, "reserved"));
        });
    }

    private static byte[] EncodeExpertGiveupResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "currentGold")); writer.WriteByte(GetByte(fields, "giveupCount")); });
    }

    private static byte[] EncodeExpertEnterResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        if (variant?.Contains("enchant", StringComparison.OrdinalIgnoreCase) == true)
            return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "kind")); writer.WriteUInt16(GetUInt16(fields, "ownerUserId")); writer.WriteInt32(GetInt32(fields, "endurance")); });
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "kind")); writer.WriteByte(GetByte(fields, "machineGrade")); writer.WriteInt32(GetInt32(fields, "cost")); writer.WriteInt32(GetInt32(fields, "endurance")); });
    }

    private static byte[] EncodePvpEnterResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var states = GetArray(fields, "readyStates");
        if (states.Length != 8) throw new ArgumentException("readyStates must contain exactly 8 entries");
        return new[] { (byte)1 }.Concat(states.Select(state => checked((byte)state.GetInt32()))).ToArray();
    }

    private static byte[] EncodeDailyChallengeRewardResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 0);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "groupIndex")); writer.WriteInt32(GetInt32(fields, "reserved")); });
    }

    private static byte[] EncodeCardLayoutResponse(JsonElement fields)
    {
        var rights = fields.TryGetProperty("cardRights", out var value)
            ? value.EnumerateArray().Select(item => checked((ushort)item.GetInt32())).ToArray()
            : new ushort[] { 1, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF };
        if (rights.Length != 8) throw new ArgumentException("fields.cardRights must contain exactly 8 u16 values");
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            foreach (var right in rights) writer.WriteUInt16(right);
        });
    }

    private static byte[] EncodeTournamentRewardSelection(JsonElement fields)
    {
        var types = fields.GetProperty("cardTypes").EnumerateArray().ToArray();
        if (types.Length != 2) throw new ArgumentException("fields.cardTypes must contain exactly 2 card types");
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            foreach (var type in types)
            {
                var selections = type.GetProperty("selections").EnumerateArray().Select(item => checked((byte)item.GetInt32())).ToArray();
                if (selections.Length > byte.MaxValue) throw new ArgumentException("tournament selection list exceeds 255 entries");
                writer.WriteByte((byte)selections.Length);
                writer.WriteBytes(selections);
            }
        });
    }

    private static byte[] EncodeCeraShopPurchaseResponse(string? variant, JsonElement fields)
    {
        var success = fields.TryGetProperty("success", out var successValue)
            ? successValue.GetBoolean()
            : variant?.Contains("error", StringComparison.OrdinalIgnoreCase) != true;
        return Build(writer =>
        {
            writer.WriteByte(success ? (byte)1 : (byte)0);
            writer.WriteByte(success ? GetByte(fields, "resultOption", 0) : GetByte(fields, "errorCode", 4));
            writer.WriteInt32(GetInt32(fields, "category", -1));
            writer.WriteInt32(GetInt32(fields, "commodityNo", success ? 0 : -1));
            writer.WriteInt32(GetInt32(fields, "value0"));
            writer.WriteInt32(GetInt32(fields, "value1"));
            writer.WriteInt32(GetInt32(fields, "value2"));
            if (!success) return;
            var entries = fields.TryGetProperty("extraItems", out var extraItems)
                ? extraItems.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
            if (entries.Length > ushort.MaxValue) throw new ArgumentException("extraItems exceeds 65535 entries");
            writer.WriteUInt16((ushort)entries.Length);
            foreach (var entry in entries)
            {
                writer.WriteUInt32(GetUInt32(entry, "itemId"));
                writer.WriteUInt32(GetUInt32(entry, "value"));
            }
        });
    }

    private static byte[] EncodePremiumServiceResponse(JsonElement fields)
    {
        var data = fields.TryGetProperty("serviceDataHex", out var dataHex)
            ? PacketInput.ParseHex(dataHex.GetString() ?? string.Empty)
            : new byte[74];
        if (data.Length != 74) throw new ArgumentException("fields.serviceDataHex must contain exactly 74 bytes");
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            writer.WriteUInt16(GetUInt16(fields, "serviceType"));
            writer.WriteBytes(data);
        });
    }

    private static byte[] EncodeRentalCatalogResponse(JsonElement fields)
    {
        var catalog = fields.TryGetProperty("catalogHex", out var raw)
            ? PacketInput.ParseHex(raw.GetString() ?? string.Empty)
            : new byte[134];
        if (catalog.Length != 134) throw new ArgumentException("fields.catalogHex must contain exactly 134 bytes");
        if (fields.TryGetProperty("luckyStar", out var luckyStar))
            BinaryPrimitives.WriteUInt16LittleEndian(catalog.AsSpan(10, 2), checked((ushort)luckyStar.GetInt32()));
        if (fields.TryGetProperty("purchaseMarker", out var marker))
            BinaryPrimitives.WriteUInt16LittleEndian(catalog.AsSpan(36, 2), checked((ushort)marker.GetInt32()));
        if (fields.TryGetProperty("purchaseCount", out var count))
            BinaryPrimitives.WriteUInt16LittleEndian(catalog.AsSpan(116, 2), checked((ushort)count.GetInt32()));
        return Build(writer => { writer.WriteInt32(134); writer.WriteBytes(catalog); });
    }

    private static byte[] EncodeCardInfoResponse(JsonElement fields)
    {
        JsonElement[] records = fields.TryGetProperty("records", out var recordsValue)
            ? recordsValue.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            for (var index = 0; index < 8; index++)
            {
                if (index >= 4)
                {
                    writer.WriteBytes(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
                    continue;
                }
                if (index > 0)
                {
                    writer.WriteBytes(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });
                    continue;
                }
                var record = records.FirstOrDefault(item => item.TryGetProperty("index", out var recordIndex) && recordIndex.GetInt32() == 0);
                var freeState = record.ValueKind == JsonValueKind.Object ? GetByte(record, "freeState", 0xFF) : (byte)0xFF;
                var paidState = record.ValueKind == JsonValueKind.Object ? GetByte(record, "paidState", 0xFF) : (byte)0xFF;
                writer.WriteByte(freeState);
                writer.WriteByte(paidState);
                if (paidState == 0)
                {
                    writer.WriteByte(2);
                    var paidReward = record.TryGetProperty("paidReward", out var reward) && reward.ValueKind == JsonValueKind.Object ? reward : default;
                    writer.WriteUInt32(GetUInt32(paidReward, "reservedValue"));
                    writer.WriteInt32(GetInt32(paidReward, "gold"));
                    writer.WriteUInt32(GetUInt32(paidReward, "itemId"));
                    writer.WriteInt32(GetInt32(paidReward, "itemCount"));
                }
                else writer.WriteByte(0);
                writer.WriteByte(record.ValueKind == JsonValueKind.Object ? GetByte(record, "tail", 0) : (byte)0);
            }
        });
    }

    private static byte[] EncodeTutorialRewardResponse(JsonElement fields)
    {
        var rewards = fields.TryGetProperty("rewards", out var rewardsValue)
            ? rewardsValue.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        if (rewards.Length > byte.MaxValue) throw new ArgumentException("tutorial rewards exceed 255 entries");
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            writer.WriteByte((byte)rewards.Length);
            foreach (var reward in rewards)
            {
                writer.WriteUInt16(GetUInt16(reward, "slot"));
                writer.WriteUInt32(GetUInt32(reward, "itemId"));
                writer.WriteUInt32(GetUInt32(reward, "count"));
            }
        });
    }

    private static byte[] EncodeSummonMonsterResponse(JsonElement fields) => Build(writer =>
    {
        writer.WriteByte(GetByte(fields, "result"));
        writer.WriteInt32(GetInt32(fields, "state"));
        writer.WriteByte(GetByte(fields, "count"));
        writer.WriteUInt16(GetUInt16(fields, "runtimeKey"));
        writer.WriteInt32(GetInt32(fields, "monsterCode"));
        writer.WriteByte(GetByte(fields, "mode"));
        writer.WriteUInt16(GetUInt16(fields, "parameter"));
    });

    private static byte[] EncodeMailboxCharacterQueryResponse(string? variant, JsonElement fields)
    {
        var error = variant?.Contains("error", StringComparison.OrdinalIgnoreCase) == true
            || fields.TryGetProperty("status", out var status) && status.GetInt32() == 0;
        if (error) return new[] { (byte)0, GetByte(fields, "errorCode", 21) };
        return Build(writer =>
        {
            writer.WriteByte(1);
            writer.WriteDString(GetString(fields, "name"), Encoding.UTF8);
            writer.WriteUInt16(GetUInt16(fields, "level"));
            writer.WriteByte(GetByte(fields, "job"));
            writer.WriteByte(GetByte(fields, "growType"));
            writer.WriteByte(GetByte(fields, "reserved", 0));
        });
    }

    private static byte[] EncodeSkillCommandEchoResponse(JsonElement fields)
    {
        var records = fields.GetProperty("records").EnumerateArray().ToArray();
        if (records.Length == 0) throw new ArgumentException("fields.records must contain at least one skill command record");
        var page = GetByte(fields, "page", 0);
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                var skillId = GetUInt16(record, "skillId");
                if (skillId > byte.MaxValue) throw new ArgumentException("skill command wire format supports skillId <= 255");
                var command = PacketInput.ParseHex(GetString(record, "commandHex"));
                if (command.Length > ushort.MaxValue) throw new ArgumentException("skill command exceeds 65535 bytes");
                if (index == 0) writer.WriteByte(page);
                writer.WriteByte((byte)skillId);
                writer.WriteByte((byte)(command.Length >> 8));
                writer.WriteByte((byte)command.Length);
                writer.WriteBytes(command);
            }
        });
    }

    private static byte[] EncodeGrowthCapsuleClaimResponse(string? variant, JsonElement fields)
    {
        var success = fields.TryGetProperty("success", out var value)
            ? value.GetBoolean()
            : variant?.Contains("failure", StringComparison.OrdinalIgnoreCase) != true;
        if (!success) return new byte[] { 1 };
        return Build(writer =>
        {
            writer.WriteByte(0);
            writer.WriteUInt32(GetUInt32(fields, "reserved"));
            writer.WriteUInt32(GetUInt32(fields, "itemId"));
            writer.WriteUInt32(GetUInt32(fields, "itemCount"));
        });
    }

    private static byte[] EncodeBuySkillResponse(string? variant, JsonElement fields)
    {
        var success = fields.TryGetProperty("success", out var value)
            ? value.GetBoolean()
            : variant?.Contains("error", StringComparison.OrdinalIgnoreCase) != true;
        if (!success) return new[] { (byte)0, GetByte(fields, "errorCode", 1) };
        var entries = fields.TryGetProperty("entries", out var entriesValue)
            ? entriesValue.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        if (entries.Length > byte.MaxValue) throw new ArgumentException("BUY_SKILL entries exceed 255");
        return Build(writer =>
        {
            writer.WriteByte(1);
            writer.WriteByte(GetByte(fields, "skillTree"));
            writer.WriteUInt16(GetUInt16(fields, "remainSp"));
            writer.WriteUInt16(GetUInt16(fields, "remainTp"));
            writer.WriteByte((byte)entries.Length);
            foreach (var entry in entries)
            {
                writer.WriteByte(GetByte(entry, "slot"));
                writer.WriteUInt16(GetUInt16(entry, "skillId"));
                writer.WriteByte(GetByte(entry, "level"));
                writer.WriteByte(GetBool(entry, "hasCommand") ? (byte)1 : (byte)0);
            }
        });
    }

    private static byte[] EncodeTournamentSelectionRights(JsonElement fields)
    {
        var types = fields.GetProperty("cardTypes").EnumerateArray().ToArray();
        if (types.Length != 2) throw new ArgumentException("fields.cardTypes must contain exactly 2 entries");
        return Build(writer =>
        {
            writer.WriteByte(GetByte(fields, "status", 1));
            foreach (var type in types)
            {
                var slots = type.GetProperty("partySlots").EnumerateArray().Select(item => checked((byte)item.GetInt32())).ToArray();
                if (slots.Length != 4) throw new ArgumentException("each tournament card type must contain exactly 4 partySlots");
                writer.WriteBytes(slots);
            }
        });
    }

    private static byte[] EncodeSelectCharacterResponse(string? variant, JsonElement fields)
    {
        var error = variant?.Contains("error", StringComparison.OrdinalIgnoreCase) == true
            || fields.TryGetProperty("status", out var status) && status.GetInt32() == 0;
        if (error) return new[] { (byte)0, GetByte(fields, "errorCode", 19) };
        var premiums = fields.TryGetProperty("premiums", out var premiumValues)
            ? premiumValues.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var quests = fields.TryGetProperty("activeQuestSlots", out var questValues)
            ? questValues.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var notifyIds = fields.TryGetProperty("questNotifyIds", out var notifyValues)
            ? notifyValues.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : Array.Empty<int>();
        var tutorialFlags = fields.TryGetProperty("tutorialFlagIndexes", out var flagValues)
            ? flagValues.EnumerateArray().Select(item => checked((byte)item.GetInt32())).ToArray()
            : Array.Empty<byte>();
        if (premiums.Length > byte.MaxValue || tutorialFlags.Length > byte.MaxValue)
            throw new ArgumentException("SELECT_CHARACTER count exceeds byte range");
        return Build(writer =>
        {
            writer.WriteByte(1);
            writer.WriteInt32(GetInt32(fields, "accountRegistrationTime"));
            writer.WriteInt32(GetInt32(fields, "characterCreatedTime"));
            writer.WriteInt16(GetInt16(fields, "uniqueId"));
            writer.WriteInt16(GetInt16(fields, "totalFatigue"));
            writer.WriteInt16(GetInt16(fields, "fatigueLimit", 188));
            writer.WriteInt16(GetInt16(fields, "usedFatigue"));
            writer.WriteByte((byte)premiums.Length);
            foreach (var premium in premiums)
            {
                writer.WriteByte(GetByte(premium, "type"));
                var endTime = PacketInput.ParseHex(GetString(premium, "endTimeHex"));
                if (endTime.Length != 8) throw new ArgumentException("premium.endTimeHex must contain exactly 8 bytes");
                writer.WriteBytes(endTime);
            }
            writer.WriteInt32(GetInt32(fields, "cera"));
            for (var index = 0; index < 30; index++)
            {
                var quest = quests.FirstOrDefault(item => item.TryGetProperty("slot", out var slot) && slot.GetInt32() == index);
                writer.WriteUInt16(quest.ValueKind == JsonValueKind.Object ? GetUInt16(quest, "questId", 0xFFFF) : (ushort)0xFFFF);
                writer.WriteUInt32(quest.ValueKind == JsonValueKind.Object ? GetUInt32(quest, "triggerValue") : 0);
            }
            for (var index = 0; index < 4; index++) writer.WriteInt32(index < notifyIds.Length ? notifyIds[index] : 0);
            writer.WriteByte(GetByte(fields, "characterSlotIndex"));
            writer.WriteByte(GetByte(fields, "tutorialFlag"));
            writer.WriteByte((byte)tutorialFlags.Length);
            writer.WriteBytes(tutorialFlags);
            writer.WriteUInt16(GetUInt16(fields, "fatigueBattery"));
            writer.WriteUInt16(GetUInt16(fields, "fatigueGrownUpBuff"));
            writer.WriteByte(GetByte(fields, "tradePunishFlag"));
            writer.WriteUInt16(GetUInt16(fields, "extraField86Jp"));
            var reserved8 = fields.TryGetProperty("reserved8Hex", out var reserved) ? PacketInput.ParseHex(reserved.GetString() ?? string.Empty) : new byte[8];
            if (reserved8.Length != 8) throw new ArgumentException("reserved8Hex must contain exactly 8 bytes");
            writer.WriteBytes(reserved8);
            writer.WriteByte(GetByte(fields, "tutorialSkippable"));
            writer.WriteUInt16(GetUInt16(fields, "postTutorialValue"));
            var tail = fields.TryGetProperty("reservedTailHex", out var tailValue) ? PacketInput.ParseHex(tailValue.GetString() ?? string.Empty) : new byte[22];
            if (tail.Length != 22) throw new ArgumentException("reservedTailHex must contain exactly 22 bytes");
            writer.WriteBytes(tail);
        });
    }

    private static byte[] EncodeBuyItemResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 4);
        var costItems = GetArray(fields, "costItems");
        if (costItems.Length > byte.MaxValue) throw new ArgumentException("costItems exceeds 255 entries");
        return Build(writer =>
        {
            writer.WriteByte(1);
            writer.WriteInt32(GetInt32(fields, "updatedGold"));
            writer.WriteInt32(GetInt32(fields, "updatedSp"));
            writer.WriteInt32(GetInt32(fields, "reservedCurrency"));
            writer.WriteInt32(GetInt32(fields, "updatedCoin"));
            writer.WriteInt16(GetInt16(fields, "slotIndex"));
            writer.WriteInt32(GetInt32(fields, "itemTemplateId"));
            writer.WriteInt32(GetInt32(fields, "instanceValue"));
            writer.WriteUInt16(GetUInt16(fields, "durability"));
            writer.WriteByte(GetByte(fields, "attr"));
            writer.WriteUInt16(GetUInt16(fields, "reservedItem16"));
            writer.WriteInt32(GetInt32(fields, "expireTime"));
            writer.WriteBytes(GetFixedHex(fields, "reservedItemTailHex", 11));
            writer.WriteByte((byte)costItems.Length);
            foreach (var item in costItems)
            {
                writer.WriteInt32(GetInt32(item, "itemTemplateId"));
                writer.WriteInt32(GetInt32(item, "newStackCount"));
            }
        });
    }

    private static byte[] EncodeInvestAmplifyResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var action = GetByte(fields, "action");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteByte(action);
            writer.WriteInt16(GetInt16(fields, "materialSlotIndex"));
            writer.WriteInt32(GetInt32(fields, "materialRemainingCount"));
            writer.WriteInt16(GetInt16(fields, "targetSlotIndex"));
            writer.WriteByte(GetByte(fields, "amplifyType"));
            writer.WriteUInt16(GetUInt16(fields, "amplifyValue"));
            if (action == 2) writer.WriteByte(GetByte(fields, "amplifyLevel"));
        });
    }

    private static byte[] EncodeCompoundItemResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 17);
        var deleted = GetArray(fields, "deletedEntries");
        var rewards = GetArray(fields, "rewards");
        if (deleted.Length > byte.MaxValue || rewards.Length > byte.MaxValue) throw new ArgumentException("compound item list exceeds 255 entries");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteByte((byte)deleted.Length);
            WriteSlotCountEntries(writer, deleted, includeListType: true);
            writer.WriteByte((byte)rewards.Length);
            foreach (var reward in rewards)
            {
                writer.WriteByte(GetByte(reward, "listType"));
                writer.WriteInt16(GetInt16(reward, "slotIndex"));
                writer.WriteInt32(GetInt32(reward, "itemTemplateId"));
                writer.WriteInt32(GetInt32(reward, "count", 1));
                writer.WriteBytes(GetFixedHex(reward, "reservedHex", 21));
            }
        });
    }

    private static byte[] EncodeResetItemAttributeResponse(string? variant, JsonElement fields)
    {
        if (variant?.Contains("wax", StringComparison.OrdinalIgnoreCase) == true || fields.TryGetProperty("resultCode", out _))
            return Build(writer => { writer.WriteInt32(GetInt32(fields, "targetSlotIndex")); writer.WriteInt32(GetInt32(fields, "targetItemId")); writer.WriteInt32(GetInt32(fields, "resultCode")); });
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "targetItemId")); writer.WriteByte(GetByte(fields, "listType", 1)); writer.WriteInt32(GetInt32(fields, "targetSlotIndex")); });
    }

    private static byte[] EncodeSecretShopBuyResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 4);
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "updatedGold")); writer.WriteUInt16(GetUInt16(fields, "assignedSlot"));
            writer.WriteInt32(GetInt32(fields, "itemId")); writer.WriteInt32(GetInt32(fields, "itemValue")); writer.WriteByte(GetByte(fields, "extData0"));
            writer.WriteUInt16(GetUInt16(fields, "durability")); writer.WriteInt32(GetInt32(fields, "requiredItemId", -1));
            writer.WriteInt32(GetInt32(fields, "costItemRemainingCount")); writer.WriteInt32(GetInt32(fields, "offerRemainingCount"));
        });
    }

    private static byte[] EncodeUseStackableResponse(string? variant, JsonElement fields)
    {
        var practice = variant?.Contains("practice", StringComparison.OrdinalIgnoreCase) == true;
        var error = IsErrorVariant(variant, fields) && !practice;
        return Build(writer =>
        {
            if (error || practice)
            {
                writer.WriteByte(0); writer.WriteByte(practice ? (byte)0 : GetByte(fields, "errorCode", 23));
                writer.WriteByte(GetByte(fields, "listType")); writer.WriteInt32(GetInt32(fields, "instanceValue")); writer.WriteInt32(GetInt32(fields, "itemCode"));
                return;
            }
            writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "slotIndex")); writer.WriteByte(GetByte(fields, "listType"));
            writer.WriteInt32(GetInt32(fields, "instanceValue")); writer.WriteInt32(GetInt32(fields, "itemCode"));
        });
    }

    private static byte[] EncodeLotteryItemResponse(string? variant, JsonElement fields)
    {
        var selected = variant?.ToLowerInvariant() ?? "phase-start";
        if (selected.Contains("error"))
            return Build(writer => { writer.WriteByte(0); writer.WriteInt16(-1); writer.WriteUInt16(0); writer.WriteInt32(0); writer.WriteInt32(0); });
        if (selected.Contains("phase"))
            return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex", -1)); writer.WriteUInt16(GetUInt16(fields, "reserved")); writer.WriteInt32(GetInt32(fields, "previewItemId")); writer.WriteInt32(GetInt32(fields, "previewItemId2", GetInt32(fields, "previewItemId"))); });
        if (selected.Contains("avatar"))
            return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteBytes(GetFixedHex(fields, "avatarEntry126Hex", 126)); });
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteInt16(GetInt16(fields, "rewardSlotIndex"));
            writer.WriteInt32(GetInt32(fields, "itemId")); writer.WriteInt32(GetInt32(fields, "displayValue")); writer.WriteUInt16(GetUInt16(fields, "durability"));
            writer.WriteByte(GetByte(fields, "attr")); writer.WriteByte(GetByte(fields, "amplifyType")); writer.WriteUInt16(GetUInt16(fields, "amplifyValue"));
            if (selected.Contains("equipment")) writer.WriteBytes(GetFixedHex(fields, "equipmentSocketExtensionHex", 30));
            writer.WriteBytes(GetFixedHex(fields, "inventoryTailHex", 3));
        });
    }

    private static byte[] EncodeChronicleGrowthResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var entries = GetArray(fields, "consumptions"); if (entries.Length > byte.MaxValue) throw new ArgumentException("consumptions exceeds 255 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetBool(fields, "growthSucceeded") ? (byte)1 : (byte)0); writer.WriteByte((byte)entries.Length); WriteSlotCountEntries(writer, entries, true); });
    }

    private static byte[] EncodeChargeRentPointResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 4);
        var echo = fields.TryGetProperty("requestEchoHex", out var raw) ? PacketInput.ParseHex(raw.GetString() ?? string.Empty) : new byte[21];
        if (echo.Length < 21) Array.Resize(ref echo, 21);
        var body = new byte[1 + echo.Length]; body[0] = 1; echo.CopyTo(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13, 4), GetInt32(fields, "totalLuckyStar"));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(18, 4), GetInt32(fields, "changeCount"));
        return body;
    }

    private static byte[] EncodeMoveItemSpaceResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return new[] { (byte)0, GetByte(fields, "errorCode", 2), GetByte(fields, "sourceListType"), GetByte(fields, "destinationListType") };
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "sourceListType")); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteInt32(GetInt32(fields, "moveValue")); writer.WriteByte(GetByte(fields, "destinationListType")); writer.WriteInt16(GetInt16(fields, "destinationSlotIndex")); });
    }

    private static byte[] EncodeCraneStartResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 4);
        var indexes = GetArray(fields, "displayCatalogIndexes"); if (indexes.Length != 6) throw new ArgumentException("displayCatalogIndexes must contain 6 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteUInt16(GetUInt16(fields, "machineId")); writer.WriteUInt32(GetUInt32(fields, "materialRemainingCount")); foreach (var index in indexes) writer.WriteUInt32(index.GetUInt32()); });
    }

    private static byte[] EncodeDisjointItemResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var materials = GetArray(fields, "materials"); if (materials.Length > byte.MaxValue) throw new ArgumentException("materials exceeds 255 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "targetSlotIndex")); writer.WriteByte(GetByte(fields, "itemSpace")); writer.WriteByte((byte)materials.Length); WriteReward10List(writer, materials); });
    }

    private static byte[] EncodeSelectablePackageResponse(string? variant, JsonElement fields)
    {
        var selected = variant?.ToLowerInvariant() ?? string.Empty;
        if (selected.Contains("success-ack")) return new byte[] { 1 };
        if (selected.Contains("short")) return new byte[] { 0 };
        if (selected.Contains("error") || IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var items = GetArray(fields, "grantedItems"); if (items.Length > ushort.MaxValue) throw new ArgumentException("grantedItems exceeds 65535 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteInt32(GetInt32(fields, "reserved0")); writer.WriteInt32(GetInt32(fields, "reserved1")); writer.WriteUInt16((ushort)items.Length); foreach (var item in items) { writer.WriteInt32(GetInt32(item, "itemTemplateId")); writer.WriteInt32(GetInt32(item, "displayCount", 1)); } });
    }

    private static byte[] EncodeAvatarCompoundSetResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 0x16);
        var slots = GetArray(fields, "consumedSlots"); if (slots.Length != 8) throw new ArgumentException("consumedSlots must contain 8 entries");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteBytes(GetFixedHex(fields, "headerHex", 8)); writer.WriteInt16(GetInt16(fields, "newSlotIndex")); writer.WriteInt32(GetInt32(fields, "newItemId"));
            writer.WriteUInt16(GetUInt16(fields, "abilityNo")); writer.WriteInt16(GetInt16(fields, "resultCount", 1)); foreach (var slot in slots) writer.WriteInt16(checked((short)slot.GetInt32())); writer.WriteBytes(GetFixedHex(fields, "reservedTailHex", 24));
        });
    }

    private static byte[] EncodeCharacterSkillListResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 0);
        var skills = GetArray(fields, "skills"); if (skills.Length > byte.MaxValue) throw new ArgumentException("skills exceeds 255 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteUInt16(GetUInt16(fields, "requestEcho")); writer.WriteByte(GetByte(fields, "reserved0")); writer.WriteByte(GetByte(fields, "reserved1")); writer.WriteByte((byte)skills.Length); foreach (var skill in skills) { writer.WriteByte(GetByte(skill, "reserved")); writer.WriteUInt16(GetUInt16(skill, "skillId")); writer.WriteByte(GetByte(skill, "level")); } });
    }

    private static byte[] EncodeItemUpgradeResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var rewards = GetArray(fields, "destroyRewards"); var resultCode = GetByte(fields, "resultCode"); if (rewards.Length > byte.MaxValue) throw new ArgumentException("destroyRewards exceeds 255 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "method")); writer.WriteInt16(GetInt16(fields, "materialSlotIndex")); writer.WriteInt32(GetInt32(fields, "materialRemainingCount")); writer.WriteInt16(GetInt16(fields, "optionalTicketSlotIndex")); writer.WriteByte(GetByte(fields, "reserved0")); writer.WriteByte(GetByte(fields, "oldLevel")); writer.WriteByte(resultCode); writer.WriteByte(GetByte(fields, "newLevel")); writer.WriteByte(GetByte(fields, "reserved1")); writer.WriteInt16(GetInt16(fields, "targetSlotIndex")); writer.WriteInt16(GetInt16(fields, "ticketSlotEcho")); if (resultCode == 3) { writer.WriteByte((byte)rewards.Length); WriteReward10List(writer, rewards); } });
    }

    private static byte[] EncodeRepairEquipmentResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 10);
        return Build(writer => { writer.WriteByte(1); writer.WriteInt32(GetInt32(fields, "updatedGold")); writer.WriteByte(GetByte(fields, "inventoryType")); writer.WriteInt16(GetInt16(fields, "slotIndex")); writer.WriteInt16(GetInt16(fields, "reserved")); });
    }

    private static byte[] EncodeMagicBoxResponse(string? variant, JsonElement fields, bool batch)
    {
        var selected = variant?.ToLowerInvariant() ?? string.Empty;
        if (selected.Contains("silent")) return new byte[] { 1, 0xFF, 0 };
        if (selected.Contains("short")) return new byte[] { 0 };
        if (selected.Contains("error") || IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var primary = GetArray(fields, "primaryRewards"); var doubles = GetArray(fields, "doubleRewards"); if (primary.Length > ushort.MaxValue || doubles.Length > ushort.MaxValue) throw new ArgumentException("magic box reward list exceeds 65535 entries");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteByte(GetByte(fields, "clientType")); writer.WriteByte(GetBool(fields, "hasDoubleRewards", doubles.Length > 0) ? (byte)1 : (byte)0);
            if (batch) writer.WriteUInt16(GetUInt16(fields, "consumedSourceCount", 1)); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteInt16(GetInt16(fields, "materialSlotIndex", -1));
            writer.WriteUInt16((ushort)primary.Length); WriteMagicBoxRewards(writer, primary);
            if (batch) { writer.WriteUInt16(GetUInt16(fields, "reservedBetweenLists")); writer.WriteUInt16((ushort)doubles.Length); WriteMagicBoxRewards(writer, doubles); }
        });
    }

    private static byte[] EncodeChronicleRefineResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var success = GetBool(fields, "refineSucceeded"); var rewards = GetArray(fields, "failureRewards"); if (rewards.Length > byte.MaxValue) throw new ArgumentException("failureRewards exceeds 255 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "materialSlotIndex")); writer.WriteInt16(GetInt16(fields, "materialRemainingCount")); writer.WriteByte(success ? (byte)1 : (byte)0); if (!success) { writer.WriteByte(GetByte(fields, "reserved")); writer.WriteInt16(GetInt16(fields, "destroyedTargetSlotIndex")); writer.WriteByte((byte)rewards.Length); WriteReward10List(writer, rewards); } });
    }

    private static byte[] EncodeAvatarCompoundResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return new[] { (byte)0, GetByte(fields, "errorCode", 0x16), GetByte(fields, "errorTail", 0) };
        var deleted = GetArray(fields, "deletedEntries"); var rewards = GetArray(fields, "rewards"); if (deleted.Length > byte.MaxValue || rewards.Length != 2) throw new ArgumentException("avatar compound requires <=255 deleted entries and exactly 2 rewards");
        return Build(writer =>
        {
            writer.WriteByte(1); writer.WriteByte((byte)deleted.Length); WriteSlotCountEntries(writer, deleted, true);
            foreach (var reward in rewards) { writer.WriteInt16(GetInt16(reward, "slot", -1)); writer.WriteInt32(GetInt32(reward, "itemId")); writer.WriteInt32(GetInt32(reward, "value")); writer.WriteUInt16(GetUInt16(reward, "abilityNo")); var expansion = GetFixedHex(reward, "expansionHex", 30); writer.WriteInt32(expansion.Length); writer.WriteBytes(expansion); var colors = GetFixedHex(reward, "colorHex", 4); writer.WriteInt32(colors.Length); writer.WriteBytes(colors); }
        });
    }

    private static byte[] EncodeDeleteItemResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return new[] { (byte)0, GetByte(fields, "errorCode", 0x17), GetByte(fields, "listType") };
        return Build(writer => { writer.WriteByte(1); writer.WriteByte(GetByte(fields, "listType")); writer.WriteByte(GetByte(fields, "entryCount", 1)); writer.WriteInt16(GetInt16(fields, "slotIndex")); writer.WriteInt32(GetInt32(fields, "appliedCount")); writer.WriteInt16(GetInt16(fields, "reserved")); });
    }

    private static byte[] EncodeAvatarDisjointResponse(string? variant, JsonElement fields)
    {
        if (IsErrorVariant(variant, fields)) return ErrorBody(fields, 1);
        var materials = GetArray(fields, "materials"); if (materials.Length > ushort.MaxValue) throw new ArgumentException("materials exceeds 65535 entries");
        return Build(writer => { writer.WriteByte(1); writer.WriteInt16(GetInt16(fields, "sourceSlotIndex")); writer.WriteUInt16((ushort)materials.Length); WriteReward10List(writer, materials); });
    }

    private static bool IsErrorVariant(string? variant, JsonElement fields)
        => variant?.Contains("error", StringComparison.OrdinalIgnoreCase) == true
            || fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("status", out var status) && status.TryGetInt32(out var value) && value == 0;

    private static byte[] ErrorBody(JsonElement fields, byte fallback) => new[] { (byte)0, GetByte(fields, "errorCode", fallback) };
    private static JsonElement[] GetArray(JsonElement fields, string name)
        => fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    private static byte[] GetFixedHex(JsonElement fields, string name, int length)
    {
        var value = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var raw) ? PacketInput.ParseHex(raw.GetString() ?? string.Empty) : new byte[length];
        if (value.Length != length) throw new ArgumentException($"fields.{name} must contain exactly {length} bytes");
        return value;
    }
    private static void WriteSlotCountEntries(BodyWriter writer, JsonElement[] entries, bool includeListType)
    {
        foreach (var entry in entries) { if (includeListType) writer.WriteByte(GetByte(entry, "listType")); writer.WriteInt16(GetInt16(entry, "slotIndex")); writer.WriteInt32(GetInt32(entry, "itemCount", GetInt32(entry, "count", 1))); }
    }
    private static void WriteReward10List(BodyWriter writer, JsonElement[] entries)
    {
        foreach (var entry in entries) { writer.WriteInt16(GetInt16(entry, "slotIndex")); writer.WriteInt32(GetInt32(entry, "itemTemplateId")); writer.WriteInt32(GetInt32(entry, "count", 1)); }
    }
    private static void WriteMagicBoxRewards(BodyWriter writer, JsonElement[] entries)
    {
        foreach (var entry in entries) { writer.WriteInt16(GetInt16(entry, "slot", -1)); writer.WriteInt32(GetInt32(entry, "itemId")); writer.WriteInt32(GetInt32(entry, "displayCount", 1)); writer.WriteBytes(GetFixedHex(entry, "reservedHex", 21)); }
    }

    private static byte[] EncodeOutboundVariant(PacketTypeDefinition definition, string? variant, JsonElement fields)
    {
        var candidates = definition.Variants
            .Where(item => item.Schema is not null || !string.IsNullOrWhiteSpace(item.FixedBodyHex))
            .ToArray();
        if (candidates.Length == 0)
            throw new NotSupportedException($"No schema-backed outbound variant for {definition.Name}; pass bodyHex or bodyBase64");

        PacketVariant selected;
        if (!string.IsNullOrWhiteSpace(variant))
        {
            selected = candidates.FirstOrDefault(item => item.Name.Equals(variant, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"unknown outbound variant '{variant}' for {definition.Name}; candidates: {string.Join(", ", candidates.Select(item => item.Name))}");
        }
        else if (candidates.Length == 1)
        {
            selected = candidates[0];
        }
        else
        {
            throw new ArgumentException($"{definition.Name} has multiple schema-backed outbound variants; specify one of: {string.Join(", ", candidates.Select(item => item.Name))}");
        }

        if (!string.IsNullOrWhiteSpace(selected.FixedBodyHex))
            return PacketInput.ParseHex(selected.FixedBodyHex);

        var schema = selected.Schema
            ?? throw new NotSupportedException($"Outbound variant {selected.Name} has no loadable schema");
        var size = schema.ExactLength
            ?? schema.MinimumLength
            ?? schema.Fields.Select(field => field.Offset + PacketSchemaRegistry.FieldWidth(field.Type)).DefaultIfEmpty(0).Max();
        var body = new byte[size];
        foreach (var field in schema.Fields)
        {
            if (!fields.TryGetProperty(field.Name, out var value))
            {
                if (field.Name.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    body[field.Offset] = (byte)(variant?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1);
                    continue;
                }
                if (!field.Optional)
                    throw new ArgumentException($"fields.{field.Name} is required by outbound variant {selected.Name}");
                continue;
            }
            WriteField(body, field, value);
        }
        return body;
    }

    private static byte[] EncodeUserInfoHeaderVariant(string? variant, JsonElement fields)
    {
        var subtype = variant?.ToLowerInvariant() switch
        {
            "subtype0" or "subtype0-character-state" => (byte)0,
            "subtype1" or "subtype1-stats-equipment-skills" => (byte)1,
            "subtype2" or "subtype2-character-roster" => (byte)2,
            "subtype3" or "subtype3-inspect-player" => (byte)3,
            _ when fields.TryGetProperty("subtype", out var value) => checked((byte)value.GetInt32()),
            _ => throw new ArgumentException("USERINFO requires variant subtype0/subtype1/subtype2/subtype3 or fields.subtype"),
        };
        if (fields.TryGetProperty("payloadHex", out var payload))
            return new[] { subtype }.Concat(PacketInput.ParseHex(payload.GetString() ?? string.Empty)).ToArray();

        var userId = fields.TryGetProperty("userId", out var id) ? checked((ushort)id.GetInt32()) : (ushort)0;
        return Build(writer =>
        {
            writer.WriteByte(subtype);
            if (subtype == 2)
            {
                writer.WriteUInt16(GetUInt16(fields, "slotLimit"));
                writer.WriteUInt16(GetUInt16(fields, "gateOrCount2"));
                writer.WriteByte(GetByte(fields, "manageLevel"));
                writer.WriteInt32(GetInt32(fields, "totalPoint"));
                writer.WriteUInt16(GetUInt16(fields, "unknown16"));
                writer.WriteInt32(GetInt32(fields, "unknown32"));
                writer.WriteUInt16(GetUInt16(fields, "characterCount"));
                return;
            }
            writer.WriteUInt16(fields.TryGetProperty("recordCount", out var count) ? checked((ushort)count.GetInt32()) : (ushort)1);
            writer.WriteUInt16(userId);
            if (subtype == 0)
            {
                writer.WriteDString(fields.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty, Encoding.UTF8);
                writer.WriteByte(GetByte(fields, "job")); writer.WriteByte(GetByte(fields, "growType"));
                writer.WriteByte(GetByte(fields, "level")); writer.WriteByte(GetByte(fields, "pvpGrade"));
                writer.WriteByte(GetByte(fields, "pvpRatingGrade")); writer.WriteByte(GetByte(fields, "userState"));
                writer.WriteByte(0);
            }
        });
    }

    private static byte[] EncodeUInt16List(JsonElement fields, string name)
    {
        var values = fields.GetProperty(name).EnumerateArray().Select(value => checked((ushort)value.GetInt32())).ToArray();
        if (values.Length > byte.MaxValue) throw new ArgumentException($"{name} has more than 255 entries");
        return Build(writer =>
        {
            writer.WriteByte((byte)values.Length);
            foreach (var value in values) writer.WriteUInt16(value);
        });
    }

    private static byte[] EncodeInferred(PacketBodySchema schema, JsonElement fields)
    {
        var size = schema.ExactLength
            ?? schema.MinimumLength
            ?? schema.Fields.Select(field => field.Offset + PacketSchemaRegistry.FieldWidth(field.Type)).DefaultIfEmpty(0).Max();
        var body = new byte[size];
        foreach (var field in schema.Fields)
        {
            if (!fields.TryGetProperty(field.Name, out var value))
            {
                if (!field.Optional) throw new ArgumentException($"fields.{field.Name} is required by inferred schema");
                continue;
            }
            WriteField(body, field, value);
        }
        return body;
    }

    private static void WriteField(byte[] body, PacketFieldDefinition field, JsonElement value)
    {
        switch (field.Type)
        {
            case "u8": body[field.Offset] = checked((byte)value.GetInt32()); break;
            case "i8": body[field.Offset] = unchecked((byte)checked((sbyte)value.GetInt32())); break;
            case "bool8": body[field.Offset] = value.GetBoolean() ? (byte)1 : (byte)0; break;
            case "u16": BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(field.Offset, 2), checked((ushort)value.GetInt32())); break;
            case "i16": BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(field.Offset, 2), checked((short)value.GetInt32())); break;
            case "u32": BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(field.Offset, 4), value.GetUInt32()); break;
            case "i32": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(field.Offset, 4), value.GetInt32()); break;
            case "u64": BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(field.Offset, 8), value.GetUInt64()); break;
            case "i64": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(field.Offset, 8), value.GetInt64()); break;
            default: throw new NotSupportedException($"unsupported inferred field type {field.Type}");
        }
    }

    private static byte[] Build(Action<BodyWriter> write)
    {
        var writer = new BodyWriter(); write(writer); return writer.ToArray();
    }

    private static byte GetByte(JsonElement value, string name, byte fallback = 0)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.True) return 1;
            if (property.ValueKind == JsonValueKind.False) return 0;
            if (property.TryGetInt32(out var number)) return checked((byte)number);
        }
        return fallback;
    }
    private static short GetInt16(JsonElement value, string name, short fallback = 0) => checked((short)GetInt32(value, name, fallback));
    private static ushort GetUInt16(JsonElement value, string name, ushort fallback = 0) => checked((ushort)GetInt32(value, name, fallback));
    private static uint GetUInt32(JsonElement value, string name, uint fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetUInt32() : fallback;
    private static int GetInt32(JsonElement value, string name, int fallback = 0)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
    private static bool GetBool(JsonElement value, string name, bool fallback = false)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetBoolean() : fallback;
    private static string GetString(JsonElement value, string name, string fallback = "")
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetString() ?? fallback : fallback;

    private sealed class BodyWriter
    {
        private readonly List<byte> _bytes = new();
        public void WriteByte(byte value) => _bytes.Add(value);
        public void WriteInt16(short value) { var bytes = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(bytes, value); _bytes.AddRange(bytes); }
        public void WriteUInt16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); _bytes.AddRange(bytes); }
        public void WriteInt32(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); _bytes.AddRange(bytes); }
        public void WriteUInt32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); _bytes.AddRange(bytes); }
        public void WriteBytes(IEnumerable<byte> value) => _bytes.AddRange(value);
        public void WriteDString(string value, Encoding encoding)
        {
            var bytes = encoding.GetBytes(value); WriteInt32(bytes.Length); _bytes.AddRange(bytes);
        }
        public byte[] ToArray() => _bytes.ToArray();
    }
}

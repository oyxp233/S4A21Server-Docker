using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace DfoPacketMcp.Protocol;

internal sealed record DecodedBody(string Variant, IReadOnlyDictionary<string, object?> Fields);

internal static class PacketSchemaRegistry
{
    public static bool HasCompleteSemanticCodec(PacketFlow flow, PacketKind kind, string name)
        => flow == PacketFlow.ServerToClient && kind == PacketKind.Noti
            ? OutboundNotificationCodec.Supports(name)
            : flow == PacketFlow.ServerToClient && kind == PacketKind.Cmd && name is
            "CARD_SELECT_RIGHT_STATE" or "TOURNAMENT_REWARD_SELECT" or "SET_CLONE_TITLE"
            or "BUY_CERASHOP_ITEM" or "PREMIUM_SERVICE" or "SAVE_GAME_OPTION_1"
            or "SELECT_CARD" or "CHANGE_TUTORIAL_FLAG" or "SUMMON_MONSTER"
            or "QUERY_CHARAC_INFO_MAILBOX" or "SKILL_COMMAND_CUSTOMIZING"
            or "GET_EXPAND_EXP_GAGE_REWARD" or "BUY_SKILL"
            or "TOURNAMENT_REWARD_SELECT_STATE" or "SELECT_CHARACTER"
            or "BUY_ITEM" or "INVEST_ITEM_AMPLIFY_OPTION" or "COMPOUND_ITEM"
            or "RESET_ITEM_ATTR" or "SECRET_SHOP_BUY_ITEM" or "USE_STACKABLE"
            or "USE_LOTTERY_ITEM" or "UPGRADE_CHRONICLE" or "CHARGE_RENTPOINT"
            or "MOVE_ITEMSPACE" or "CRANE_START_USE" or "DISJOINT_ITEM"
            or "USE_BOOSTER_ITEM" or "BIND_PLUS" or "REQUEST_CHARAC_SKILL_INFO"
            or "UPGRADE_ITEM" or "REPAIR_EQUIPMENT" or "USE_RANDOMBOX_ITEM_EXPAND"
            or "ENCHANT_3RD_CHRONICLE_ITEM" or "COMPOUND_AVATAR" or "DELETE_ITEM"
            or "USE_RANDOMBOX_ITEM" or "DISJOINT_AVATAR"
            or "REQUEST_DISJOINT_ITEM" or "REPAIR_DISJOINT_MACHINE"
            or "UPGRADE_DISJOINT_MACHINE" or "USE_ENCHANT_STORE"
            or "REPAIR_EXPERT_JOB_STORE" or "COMPOUND_ITEM_BY_EXPERT_JOB"
            or "GIVEUP_EXPERT_JOB" or "CREATE_EXPERT_JOB_STORE"
            or "ENTER_EXPERT_JOB_STORE" or "ENTER_PVP_ROOM"
            or "DAILY_CHALLENGE_REWARD";

    public static PacketVariant[] GetManualVariants(PacketFlow flow, PacketKind kind, string name)
    {
        if (flow == PacketFlow.ServerToClient && kind == PacketKind.Noti)
        {
            var variants = OutboundNotificationCodec.GetManualVariants(name);
            if (variants.Length > 0) return variants;
        }
        if (flow == PacketFlow.ServerToClient && kind == PacketKind.Cmd)
        {
            return name switch
            {
                "CARD_SELECT_RIGHT_STATE" => new[] { Variant("card-layout", "exact length 17", "Server/DfoServer/Network/Handlers/Dungeon/CardRewardNotificationSender.cs:133") },
                "TOURNAMENT_REWARD_SELECT" => new[] { Variant("selection-state", "status:u8 + two counted selection lists", "Server/DfoServer/Network/Builders/Dungeon/TournamentPacketBuilder.cs:129") },
                "SET_CLONE_TITLE" => new[] { Variant("clone-title-ack", "exact length 5", "Server/DfoServer/Game/Appearance/AppearanceService.cs:115") },
                "BUY_CERASHOP_ITEM" => new[]
                {
                    Variant("purchase-success", "body[0] == 1; minimum length 24", "Server/DfoServer/Network/Builders/CeraShop/CeraShopPurchaseAckBuilder.cs:18"),
                    Variant("purchase-error", "body[0] == 0; exact length 22", "Server/DfoServer/Network/Builders/CeraShop/CeraShopPurchaseAckBuilder.cs:54"),
                },
                "PREMIUM_SERVICE" => new[] { Variant("premium-service-state", "exact length 77", "Server/DfoServer/Network/Handlers/PremiumQueryHandler.cs:46") },
                "SAVE_GAME_OPTION_1" => new[] { Variant("rental-catalog", "exact length 138", "Server/DfoServer/Game/Inventory/RentalCatalogCodec.cs:46") },
                "SELECT_CARD" => new[]
                {
                    Variant("card-info-standard", "status + eight card records; record 0 may contain paid reward details", "Server/DfoServer/Network/Handlers/Dungeon/CardRewardNotificationSender.cs:72"),
                },
                "CHANGE_TUTORIAL_FLAG" => new[] { Variant("tutorial-reward-ack", "status:u8 + count:u8 + count*10-byte entries", "Server/DfoServer/Network/Handlers/Dungeon/DungeonTutorialHandler.cs:104") },
                "SUMMON_MONSTER" => new[] { Variant("summon-create-response", "exact length 15", "Server/DfoServer/Network/Builders/Dungeon/SpecialDungeonNotificationBuilder.cs:121") },
                "QUERY_CHARAC_INFO_MAILBOX" => new[]
                {
                    Variant("query-success", "body[0] == 1; variable dstr", "Server/DfoServer/Network/Handlers/MailboxHandler.cs:783"),
                    Variant("query-error", "body[0] == 0; exact length 2", "Server/DfoServer/Network/Handlers/MailboxHandler.cs:529"),
                },
                "SKILL_COMMAND_CUSTOMIZING" => new[] { Variant("command-record-echo", "status:u8 followed by original command-record body", "Server/DfoServer/Network/Handlers/SkillHandler.cs:45") },
                "GET_EXPAND_EXP_GAGE_REWARD" => new[]
                {
                    Variant("claim-success", "body[0] == 0; exact length 13", "Server/DfoServer/Network/Builders/GrowthCapsulePacketBuilder.cs:5"),
                    Variant("claim-failure", "body[0] == 1; exact length 1", "Server/DfoServer/Network/Builders/GrowthCapsulePacketBuilder.cs:5"),
                },
                "BUY_SKILL" => new[]
                {
                    Variant("buy-skill-success", "body[0] == 1; status header plus counted 5-byte entries", "Server/DfoServer/Network/Builders/BuySkillAckBuilder.cs:15"),
                    Variant("buy-skill-error", "body[0] == 0; exact length 2", "Server/DfoServer/Network/Builders/BuySkillAckBuilder.cs:15"),
                },
                "TOURNAMENT_REWARD_SELECT_STATE" => new[] { Variant("selection-rights", "exact length 9", "Server/DfoServer/Network/Builders/Dungeon/TournamentPacketBuilder.cs:107") },
                "SELECT_CHARACTER" => new[]
                {
                    Variant("select-success", "body[0] == 1; dynamic premium list and fixed quest slots", "Server/DfoServer/Network/Builders/Init/SelectCharacterAckBodyBuilder.cs:23"),
                    Variant("select-error", "body[0] == 0; exact length 2", "Server/DfoServer/Network/Builders/Init/SelectCharacterAckBodyBuilder.cs:12"),
                },
                "REQUEST_DISJOINT_ITEM" => new[]
                {
                    Variant("disjoint-success", "status:u8 + targetSlot:i16 + itemSpace:u8 + counted material records + requesterGold:i32 + endurance:i32", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:75"),
                    Variant("disjoint-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:96"),
                },
                "REPAIR_DISJOINT_MACHINE" or "REPAIR_EXPERT_JOB_STORE" => new[]
                {
                    Variant("repair-success", "status:u8 + gold:i32 + endurance:i32", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:144"),
                    Variant("repair-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:150"),
                },
                "UPGRADE_DISJOINT_MACHINE" => new[]
                {
                    Variant("upgrade-success", "status:u8 + gold:i32 + grade:i32 + endurance:i32", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:162"),
                    Variant("upgrade-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:171"),
                },
                "USE_ENCHANT_STORE" => new[]
                {
                    Variant("enchant-success", "status:u8 + enchantSucceeded:u8 + finalExperience:u32 + reserved:u8 + endurance:i32", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:108"),
                    Variant("enchant-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Handlers/ExpertJobStoreHandler.cs:875"),
                },
                "COMPOUND_ITEM_BY_EXPERT_JOB" => new[]
                {
                    Variant("compound-success", "status:u8 + outputCount:u8 + output records(itemId:i32,count:i32) + successCount:i32 + failureCount:i32 + reserved:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobCompoundPacketBuilder.cs:18"),
                    Variant("compound-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobCompoundPacketBuilder.cs:36"),
                },
                "GIVEUP_EXPERT_JOB" => new[]
                {
                    Variant("giveup-success", "status:u8 + currentGold:i32 + giveupCount:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobGiveupPacketBuilder.cs:16"),
                    Variant("giveup-error", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobGiveupPacketBuilder.cs:28"),
                },
                "CREATE_EXPERT_JOB_STORE" => new[]
                {
                    Variant("success-ack", "exact body 01", "Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:7"),
                    Variant("error-ack", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:12"),
                },
                "ENTER_EXPERT_JOB_STORE" => new[]
                {
                    Variant("disjoint-enter-success", "status:u8 + kind:u8 + machineGrade:u8 + cost:i32 + endurance:i32", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:48"),
                    Variant("enchant-enter-success", "status:u8 + kind:u8 + ownerUserId:u16 + endurance:i32; exact length 8", "Server/DfoServer/Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:37"),
                    Variant("error-ack", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Handlers/ExpertJobStoreHandler.cs:831"),
                },
                "ENTER_PVP_ROOM" => new[]
                {
                    Variant("enter-success", "status:u8 + eight ready-state bytes", "Server/DfoServer/Network/Builders/Pvp/PvpRoomNotificationBuilder.cs:215"),
                    Variant("error-ack", "status:u8 == 0 + errorCode:u8", "Server/DfoServer/Network/Handlers/PvpRoomHandler.cs:5722"),
                },
                "DAILY_CHALLENGE_REWARD" => new[]
                {
                    Variant("claim-success", "status:u8 + groupIndex:i32 + reserved:i32; exact length 9", "Server/DfoServer/Network/Builders/Quest/DailyChallengeRewardAckBuilder.cs:11"),
                    Variant("claim-error", "status:u8 == 0 + errorCode:u8; exact length 2", "Server/DfoServer/Network/Builders/Quest/DailyChallengeRewardAckBuilder.cs:9"),
                },
                _ => Array.Empty<PacketVariant>(),
            };
        }
        if (flow == PacketFlow.ServerToClient && kind == PacketKind.Noti && name == "USERINFO")
        {
            return new[]
            {
                Variant("subtype0-character-state", "body[0] == 0", "Server/DfoServer/Network/Builders/Init/UserInfoSubtype0Builder.cs"),
                Variant("subtype1-stats-equipment-skills", "body[0] == 1", "Server/DfoServer/Network/Builders/Init/UserInfoSubtype1Builder.cs"),
                Variant("subtype2-character-roster", "body[0] == 2", "Server/DfoServer/Network/Builders/Init/AccountCharacterListBodyBuilder.cs"),
                Variant("subtype3-inspect-player", "body[0] == 3", "Server/DfoServer/Network/Builders/Init/UserInfoSubtype3Builder.cs"),
            };
        }
        if (flow != PacketFlow.ClientToServer || kind != PacketKind.Cmd)
            return Array.Empty<PacketVariant>();

        return name switch
        {
            "REQUEST_PEER" => new[]
            {
                Variant("party-invite", "body[2] == 0; minimum length 3", "Server/DfoServer/Network/Handlers/PartyHandler.cs:1001"),
                Variant("trade-invite", "body[2] == 1; minimum length 3", "Server/DfoServer/Network/Handlers/PartyHandler.cs:1033"),
                Variant("pvp-room-invite", "body[2] == 2; exact length 7", "Server/DfoServer/Network/Handlers/PartyHandler.cs:1036"),
            },
            "REPAIR_EQUIPMENT" => new[]
            {
                Variant("manual-repair", "minimum length 5; body[5] absent or != 1; body[7] absent or != 1", "Server/DfoServer/Network/Handlers/InventoryHandler.Repair.cs:16"),
                Variant("auto-repair", "minimum length 6; body[5] == 1", "Server/DfoServer/Network/Handlers/InventoryHandler.Repair.cs:30"),
                Variant("quick-repair", "minimum length 8; body[7] == 1", "Server/DfoServer/Network/Handlers/InventoryHandler.Repair.cs:31"),
            },
            "OVERFLOW_INFO" => new[]
            {
                Variant("raid-create-popup-close", "body == 01-99-02", "Server/DfoServer/Network/Handlers/RaidHandler.cs:290"),
                Variant("lottery-overflow-confirm", "body == 01-1B-00", "Server/DfoServer/Network/Handlers/LotteryItemHandler.cs:172"),
            },
            "CHANGE_ANOTHER_SKILL_TREE" => new[]
            {
                Variant("direct-index", "exact length 1; skill-tree byte at +0x00", "Server/DfoServer/Network/Handlers/SkillHandler.cs:465"),
                Variant("legacy-prefixed-index", "exact length 2; skill-tree byte at +0x01", "Server/DfoServer/Network/Handlers/SkillHandler.cs:472"),
                Variant("session-default-skill-tree", "all other lengths; server uses current session value", "Server/DfoServer/Network/Handlers/SkillHandler.cs:478"),
            },
            "OPEN_CERAPACKAGE" => new[]
            {
                Variant("avatar-package", "body[2] is choice count and body length >= 3 + count*5", "Server/DfoServer/Network/Handlers/CeraShopHandler.cs"),
                Variant("selectable-or-general-package", "minimum length 9 when avatar discriminator does not match", "Server/DfoServer/Network/Handlers/CeraShopHandler.cs"),
            },
            "DELETE_ITEM" => new[]
            {
                Variant("simple-legacy", "exact length 4: slotIndex:i16 + itemCount:i16", "Server/DfoServer/Network/Handlers/InventoryHandler.cs:118"),
                Variant("simple-list-prefixed", "length 5..14 and body[0] is InventoryListType", "Server/DfoServer/Network/Handlers/InventoryHandler.cs:110"),
                Variant("extended-array", "minimum length 15; body[1] is entry count 1..100; 12-byte entries", "Server/DfoServer/Network/Handlers/InventoryHandler.Trade.cs:35"),
            },
            _ => Array.Empty<PacketVariant>(),
        };
    }

    private static PacketVariant Variant(string name, string discriminator, params string[] sources)
        => new(name, null, sources)
        {
            Discriminator = discriminator,
            Confidence = "confirmed-from-server-source",
        };

    private static readonly HashSet<string> EmptyInbound = new(StringComparer.Ordinal)
    {
        "EXIT", "RETURN_SELECT_CHARACTER", "LEAVE_PARTY", "FINISH_LOADING",
        "GIVEUP_GAME", "BACK_2_VILLAGE", "PVP_CHANNEL_INFO", "GEN_CERATICKET",
        "REQUEST_HATCHED_CREATURE", "GIVEUP_EXPERT_JOB", "CLOSE_EXPERT_JOB_STORE",
        "REPAIR_EXPERT_JOB_STORE", "UPGRADE_DISJOINT_MACHINE",
        "BLOOD_ROUND_UI_PREPARE_FINISH_", "TOURNAMENT_REWARD_SELECT_STATE",
        "PVP_REQUEST_FIGHT", "END_PVP_RESULT",
    };

    private static readonly HashSet<string> StructuredInbound = new(StringComparer.Ordinal)
    {
        "LOGIN", "SET_UDP_IP_PORT", "SET_PARTY_INFO", "SELECT_DUNGEON", "DIE_MONSTER",
        "GET_ITEM", "MOVE_MAP", "DROP_ITEM", "INCREASE_STATUS", "ACCEPT_QUEST",
        "GIVEUP_QUEST", "SET_QUEST_TRIGGER", "FINISH_QUEST", "BOSS_DIE_CHECK",
        "UPGRADE_ITEM", "RESET_ITEM_ATTR", "DISJOINT_ITEM", "USE_LOTTERY_ITEM",
        "PURIFY_ITEM", "INVEST_ITEM_AMPLIFY_OPTION", "DISJOINT_AVATAR", "COMPOUND_EMBLEM",
        "ENCHANT_BY_BEAD", "UPGRADE_CHRONICLE", "UPGRADE_ITEM_SEPARATE", "USE_LIMIT_CUBE",
        "USE_TITLE_CHANGE_ITEM", "AVATAR_OPTION_CHANGE", "INCREASE_CHANCE_LOTTERY_RESET",
        "SECRET_SHOP_BUY_ITEM", "SECRET_SHOP_OPEN_CLOSE", "PARTY_TELEPORT", "TELEPORT",
        "CREATE_EXPERT_JOB_STORE", "ENTER_EXPERT_JOB_STORE", "REQUEST_DISJOINT_ITEM",
        "EXPERT_EXTRACTION", "USE_ENCHANT_STORE", "COMPOUND_ITEM_BY_EXPERT_JOB",
        "TOURNAMENT_REWARD_SELECT", "DIE_BLOOD_MONSTER", "SELECT_ULTIMATE_DIFFICULTY",
        "SUMMON_MONSTER", "SEA_CHASE_MINI_GAME_RESULT", "REJOIN_DUNGEON",
        "CANCEL_REJOIN_DUNGEON", "MAKE_PVP_ROOM", "ENTER_PVP_ROOM", "SET_PVP_SEAT_STATE",
        "SET_PVP_READY_STATE", "SET_PVP_TEAM_MODE", "DIE_PVP_CHARACTER", "CONNECT_P2P_PVP",
        "CHECK_DOUBLE_CHARACTER_NAME", "SAVE_QUEST_NOTIFY", "OPEN_CERAPACKAGE",
        "USE_RANDOMBOX_ITEM", "USE_RANDOMBOX_ITEM_EXPAND", "UPGRADE_CARD",
        "ENCHANT_3RD_CHRONICLE_ITEM", "PVP_TIME_OUT", "REPAIR_DISJOINT_MACHINE",
        "SELECT_CHARACTER", "RECOVER_STAMINA", "REQUEST_PEER", "WALKOUT_PARTY_MEMBER",
        "ENTER_SELECT_DUNGEON", "REPAIR_EQUIPMENT", "DIE_CHARACTER", "USE_COIN",
        "SET_PLAY_RESULT", "RES_PVP_RANK", "SCORE_SCROLL_STATE", "SELECT_CARD",
        "EPLP_COMMAND", "MAILBOX_SEND", "CREATURE_SCRIPT_MESSAGE", "CHARACTER_STATISTIC",
        "DEATH_TOWER_STAGE_CMD", "OVERFLOW_INFO", "CHANGE_ANOTHER_SKILL_TREE",
        "MULTI_MAILBOX_SEND", "ONE_TO_ONE_CHAT_STATE", "UPGRADE_CARGO",
        "SAVE_CHARACTER_OPTION", "IMAGE_COMMUNICATION_EQUIPMENT_USE", "INFORM_NOTICE",
        "VERIFY_CREATURE_QUEST", "COMBO_SKILL_INFO",
        "COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET", "SET_CLONE_TITLE", "RAID_DO_BEHAVIOR",
        "START_RAID", "LOAD_EXTEND_CHARACS", "DAILY_CHALLENGE_REWARD", "PREMIUM_SERVICE",
        "ADD_EQUIPMENT_EFFECT", "RENT_EQUIPMENT_ITEM", "CHARGE_RENTPOINT",
        "SELECT_COLLECTBOX", "DELETE_ITEM",
    };

    public static PacketSchemaStatus GetStatus(PacketFlow flow, PacketKind kind, string name)
    {
        if (flow == PacketFlow.ClientToServer && kind == PacketKind.Cmd)
        {
            if (EmptyInbound.Contains(name)) return PacketSchemaStatus.Empty;
            if (StructuredInbound.Contains(name)) return PacketSchemaStatus.Structured;
            return PacketSchemaStatus.Opaque;
        }
        if (flow == PacketFlow.ServerToClient && kind == PacketKind.Noti && name == "USERINFO")
            return PacketSchemaStatus.Structured;
        if (HasCompleteSemanticCodec(flow, kind, name))
            return PacketSchemaStatus.Structured;
        return PacketSchemaStatus.RawFallback;
    }

    public static string GetSemantic(PacketFlow flow, PacketKind kind, string name)
    {
        if (flow == PacketFlow.ClientToServer) return $"Client request for {name}";
        if (kind == PacketKind.Cmd) return $"Server CMD response for {name}";
        return name == "USERINFO"
            ? "Polymorphic user information notification; body byte 0 selects subtype"
            : $"Server notification {name}";
    }

    public static DecodedBody Decode(
        PacketTypeDefinition? definition,
        byte[] body,
        List<string> diagnostics,
        string? requestedVariant = null)
    {
        var fields = BaseFields(body);
        if (definition is null)
        {
            diagnostics.Add("No catalog definition for the selected flow/kind/type");
            AddScalarPreview(fields, body);
            return new DecodedBody("unknown", fields);
        }

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Noti && definition.EnumName == "USERINFO")
        {
            if (!string.IsNullOrWhiteSpace(requestedVariant)
                && requestedVariant.StartsWith("subtype", StringComparison.OrdinalIgnoreCase))
                return DecodeUserInfoRequestedVariant(body, fields, diagnostics, requestedVariant);
            return DecodeUserInfo(body, fields, diagnostics);
        }

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Noti
            && OutboundNotificationCodec.TryDecode(definition.EnumName, body, diagnostics, requestedVariant, out var notification))
            return notification;

        if (definition.Flow == PacketFlow.ServerToClient && definition.Kind == PacketKind.Cmd)
            return DecodeCommandResponse(definition, body, fields, diagnostics, requestedVariant);

        if (definition.Flow != PacketFlow.ClientToServer || definition.Kind != PacketKind.Cmd)
        {
            var outbound = DecodeOutboundVariants(definition, body, fields, diagnostics, requestedVariant);
            if (outbound is not null) return outbound;
            AddScalarPreview(fields, body);
            return new DecodedBody("default", fields);
        }

        if (EmptyInbound.Contains(definition.EnumName))
        {
            if (body.Length != 0) diagnostics.Add($"{definition.EnumName} is expected to have an empty body");
            return new DecodedBody("empty", fields);
        }

        if (definition.SchemaStatus == PacketSchemaStatus.Inferred)
            return DecodeInferredVariants(definition, body, fields, diagnostics, requestedVariant);

        var variant = DecodeInbound(definition.EnumName, body, fields, diagnostics);
        if (variant == "opaque") AddScalarPreview(fields, body);
        return new DecodedBody(variant, fields);
    }

    private static DecodedBody DecodeInferred(
        PacketBodySchema schema,
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (schema.ExactLength.HasValue && body.Length != schema.ExactLength.Value)
            diagnostics.Add($"inferred exact length is {schema.ExactLength.Value}, got {body.Length}");
        if (schema.MinimumLength.HasValue && body.Length < schema.MinimumLength.Value)
            diagnostics.Add($"inferred minimum length is {schema.MinimumLength.Value}, got {body.Length}");
        foreach (var field in schema.Fields)
        {
            var width = FieldWidth(field.Type);
            if (width == 0 || field.Offset + width > body.Length)
            {
                if (!field.Optional)
                    diagnostics.Add($"inferred field {field.Name} at +0x{field.Offset:X} is truncated");
                continue;
            }
            fields[field.Name] = ReadField(body, field.Type, field.Offset);
        }
        fields["schemaConfidence"] = "inferred-from-handler";
        fields["schemaSources"] = schema.Sources.Concat(schema.Fields.Select(field => field.Source)).Distinct().ToArray();
        return new DecodedBody(schema.Fields.Length == 0 ? "inferred-length-only" : "inferred-handler-layout", fields);
    }

    private static DecodedBody DecodeInferredVariants(
        PacketTypeDefinition definition,
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics,
        string? requestedVariant)
    {
        var variants = definition.Variants.Where(variant => variant.Schema is not null).ToArray();
        if (variants.Length == 0 && definition.InferredSchema is not null)
            return DecodeInferred(definition.InferredSchema, body, fields, diagnostics);
        if (variants.Length == 0)
        {
            diagnostics.Add("inferred packet has no loadable variant schema");
            AddScalarPreview(fields, body);
            return new DecodedBody("unresolved-inferred", fields);
        }

        if (!string.IsNullOrWhiteSpace(requestedVariant))
        {
            var selected = variants.FirstOrDefault(variant => variant.Name.Equals(requestedVariant, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                fields["candidateVariants"] = variants.Select(ToVariantCandidate).ToArray();
                diagnostics.Add($"requested variant '{requestedVariant}' was not found");
                return new DecodedBody("unknown-requested-variant", fields);
            }
            var decoded = DecodeInferred(selected.Schema!, body, fields, diagnostics);
            fields["selectedVariant"] = selected.Name;
            fields["variantDiscriminator"] = selected.Discriminator;
            return new DecodedBody(selected.Name, decoded.Fields);
        }

        var exact = variants.Where(variant => variant.Schema!.ExactLength == body.Length).ToArray();
        var candidates = exact.Length > 0
            ? exact
            : variants.Where(variant =>
                !variant.Schema!.ExactLength.HasValue
                && (!variant.Schema.MinimumLength.HasValue || body.Length >= variant.Schema.MinimumLength.Value)).ToArray();
        if (candidates.Length == 0)
        {
            fields["candidateVariants"] = variants.Select(ToVariantCandidate).ToArray();
            diagnostics.Add($"no inferred variant length rule accepts body length {body.Length}");
            AddScalarPreview(fields, body);
            return new DecodedBody("unresolved-variant", fields);
        }
        if (candidates.Length == 1)
        {
            var decoded = DecodeInferred(candidates[0].Schema!, body, fields, diagnostics);
            fields["selectedVariant"] = candidates[0].Name;
            fields["variantDiscriminator"] = candidates[0].Discriminator;
            return new DecodedBody(candidates[0].Name, decoded.Fields);
        }

        fields["candidateVariants"] = candidates.Select(candidate =>
        {
            var candidateFields = BaseFields(body);
            var candidateDiagnostics = new List<string>();
            DecodeInferred(candidate.Schema!, body, candidateFields, candidateDiagnostics);
            return new
            {
                candidate.Name,
                candidate.Discriminator,
                candidate.Confidence,
                fields = candidateFields,
                diagnostics = candidateDiagnostics,
                candidate.Sources,
            };
        }).ToArray();
        diagnostics.Add($"body length {body.Length} matches multiple handler/context variants; select one explicitly using context");
        return new DecodedBody("ambiguous-inferred-variant", fields);
    }

    private static object ToVariantCandidate(PacketVariant variant) => new
    {
        variant.Name,
        variant.Discriminator,
        variant.Confidence,
        exactLength = variant.Schema?.ExactLength,
        minimumLength = variant.Schema?.MinimumLength,
        fixedBodyHex = variant.FixedBodyHex,
        variant.Sources,
    };

    private static string DecodeInbound(
        string name,
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        switch (name)
        {
            case "SELECT_CHARACTER": return DecodeFixed(body, 2, fields, diagnostics, ("characterSlot", "u16", 0));
            case "RECOVER_STAMINA": return DecodeIgnoredBody(body, fields, "weakness recovery is selected from session state; request bytes are not consumed");
            case "REQUEST_PEER": return DecodeRequestPeer(body, fields, diagnostics);
            case "WALKOUT_PARTY_MEMBER": return DecodeFixed(body, 1, fields, diagnostics, ("targetPartySlot", "u8", 0));
            case "ENTER_SELECT_DUNGEON": return DecodeIgnoredBody(body, fields, "dungeon-selection entry uses current session state");
            case "REPAIR_EQUIPMENT": return DecodeRepairEquipment(body, fields, diagnostics);
            case "DIE_CHARACTER": return DecodeIgnoredBody(body, fields, "character death is authoritative session/run state; request bytes are logged only");
            case "USE_COIN": return DecodeAtLeast(body, 2, fields, diagnostics, ("targetActorId", "u16", 0));
            case "SET_PLAY_RESULT": return DecodeSetPlayResult(body, fields, diagnostics);
            case "RES_PVP_RANK": return DecodeOpaqueLength(body, 70, fields, diagnostics, "clientRankSettlementBlock");
            case "SCORE_SCROLL_STATE": return DecodeIgnoredBody(body, fields, "request only advances the settlement card layout");
            case "SELECT_CARD": return DecodeFixed(body, 2, fields, diagnostics, ("cardType", "u8", 0), ("cardIndex", "u8", 1));
            case "EPLP_COMMAND": return DecodeFixed(body, 2, fields, diagnostics, ("state", "u8", 0), ("option", "u8", 1));
            case "MAILBOX_SEND": return DecodeMailboxSend(body, fields, diagnostics, multi: false);
            case "MULTI_MAILBOX_SEND": return DecodeMailboxSend(body, fields, diagnostics, multi: true);
            case "CREATURE_SCRIPT_MESSAGE": return DecodeCreatureScriptMessage(body, fields, diagnostics);
            case "CHARACTER_STATISTIC": return DecodeIgnoredBody(body, fields, "death-respawn acknowledgement uses active run timer state; bytes are logged only");
            case "DEATH_TOWER_STAGE_CMD": return DecodeDeathTowerStageCommand(body, fields, diagnostics);
            case "OVERFLOW_INFO": return DecodeOverflowInfo(body, fields, diagnostics);
            case "CHANGE_ANOTHER_SKILL_TREE": return DecodeSkillTreeSwitch(body, fields, diagnostics);
            case "ONE_TO_ONE_CHAT_STATE": return DecodeIgnoredBody(body, fields, "current server records the chat-state packet without consuming fields");
            case "UPGRADE_CARGO": return DecodeIgnoredBody(body, fields, "personal cargo upgrade is selected from persisted inventory state");
            case "SAVE_CHARACTER_OPTION": return DecodeRawPersisted(body, fields, "characterOptionBlob");
            case "IMAGE_COMMUNICATION_EQUIPMENT_USE": return DecodeFixed(body, 0, fields, diagnostics);
            case "INFORM_NOTICE": return DecodeSceneUniqueId(body, fields, diagnostics);
            case "VERIFY_CREATURE_QUEST": return DecodeIgnoredBody(body, fields, "pet evolution quest verification uses current character state");
            case "COMBO_SKILL_INFO": return DecodeComboSkillInfo(body, fields, diagnostics);
            case "COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET": return DecodeComboQuickSlotReset(body, fields);
            case "SET_CLONE_TITLE": return DecodeFixed(body, 4, fields, diagnostics, ("cloneTitleItemId", "i32", 0));
            case "RAID_DO_BEHAVIOR": return DecodeFixed(body, 8, fields, diagnostics, ("targetObjectId", "u32", 0), ("behaviorId", "u32", 4));
            case "START_RAID": return DecodeFixed(body, 0, fields, diagnostics);
            case "LOAD_EXTEND_CHARACS": return DecodeIgnoredBody(body, fields, "compatibility request returns a fixed two-byte response");
            case "DAILY_CHALLENGE_REWARD": return DecodeFixed(body, 4, fields, diagnostics, ("groupIndex", "i32", 0));
            case "PREMIUM_SERVICE": return DecodeIgnoredBody(body, fields, "premium-service response is built from account and character state");
            case "ADD_EQUIPMENT_EFFECT": return DecodeEquipmentEffect(body, fields, diagnostics);
            case "RENT_EQUIPMENT_ITEM": return DecodeRentalEquipment(body, fields, diagnostics);
            case "CHARGE_RENTPOINT": return DecodeAtLeast(body, 19, fields, diagnostics, ("purchaseCount", "u16", 17));
            case "SELECT_COLLECTBOX": return DecodeCollectionBoxQuery(body, fields, diagnostics);
            case "DELETE_ITEM": return DecodeDeleteItem(body, fields, diagnostics);
            case "LOGIN": return DecodeLogin(body, fields, diagnostics);
            case "SET_UDP_IP_PORT": return DecodeUdpEndpoint(body, fields, diagnostics);
            case "SET_PARTY_INFO": return DecodeSetPartyInfo(body, fields, diagnostics);
            case "SELECT_DUNGEON": return DecodeSelectDungeon(body, fields, diagnostics);
            case "DIE_MONSTER": return DecodeDieMonster(body, fields, diagnostics);
            case "GET_ITEM": return DecodeFixed(body, 2, fields, diagnostics, ("srcSlot", "u16", 0));
            case "BOSS_DIE_CHECK": return DecodeFixed(body, 4, fields, diagnostics, ("userId", "u16", 0), ("bossSequence", "u16", 2));
            case "MOVE_MAP": return DecodeMoveMap(body, fields, diagnostics);
            case "DROP_ITEM": return DecodeFixed(body, 11, fields, diagnostics,
                ("positionX", "u16", 0), ("positionY", "u16", 2), ("listType", "u8", 4),
                ("slotIndex", "i16", 5), ("count", "i32", 7));
            case "INCREASE_STATUS": return DecodeFixed(body, 2, fields, diagnostics, ("slotIndex", "i16", 0));
            case "ACCEPT_QUEST":
            case "GIVEUP_QUEST": return DecodeQuestWire(body, fields, diagnostics,
                ("questId", "u16", 0));
            case "SET_QUEST_TRIGGER": return DecodeQuestWire(body, fields, diagnostics,
                ("questId", "u16", 0), ("triggerType", "u8", 2), ("increment", "bool8", 3));
            case "FINISH_QUEST": return DecodeQuestWire(body, fields, diagnostics,
                ("questId", "u16", 0), ("rewardSelection", "u16", 2),
                ("completionCount", "u16", 4), ("sentinel", "u16", 6));
            case "SAVE_QUEST_NOTIFY": return DecodeCountedInt32(body, fields, diagnostics, "questIds");
            case "UPGRADE_ITEM": return DecodeItemUpgrade(body, fields, diagnostics);
            case "RESET_ITEM_ATTR": return DecodeFixed(body, 8, fields, diagnostics,
                ("targetSlotIndex", "i16", 0), ("targetItemTemplateId", "i32", 2), ("materialSlotIndex", "i16", 6));
            case "DISJOINT_ITEM": return DecodeAtLeast(body, 5, fields, diagnostics,
                ("targetSlotIndex", "i16", 0), ("itemSpace", "u8", 2), ("disjointItemSlotIndex", "i16", 3),
                ("contextValue", "i32?", 5));
            case "USE_LOTTERY_ITEM": return DecodeFixed(body, 4, fields, diagnostics, ("phase", "u16", 0), ("slotIndex", "i16", 2));
            case "PURIFY_ITEM": return DecodeFixed(body, 12, fields, diagnostics,
                ("targetSlotIndex", "i16", 0), ("targetItemTemplateId", "i32", 2),
                ("materialSlotIndex", "i16", 6), ("materialItemTemplateId", "i32", 8));
            case "INVEST_ITEM_AMPLIFY_OPTION": return DecodeFixed(body, 14, fields, diagnostics,
                ("action", "u8", 0), ("targetSlotIndex", "i16", 1), ("targetItemTemplateId", "i32", 3),
                ("materialSlotIndex", "i16", 7), ("materialItemTemplateId", "i32", 9), ("selectedOption", "u8", 13));
            case "DISJOINT_AVATAR": return DecodeAtLeast(body, 2, fields, diagnostics,
                ("slotIndex", "i16", 0), ("expectedItemTemplateId", "i32?", 2));
            case "COMPOUND_EMBLEM": return DecodeEmblemCompound(body, fields, diagnostics);
            case "ENCHANT_BY_BEAD": return DecodeFixed(body, 6, fields, diagnostics,
                ("beadListType", "u8", 0), ("beadSlotIndex", "i16", 1),
                ("targetListType", "u8", 3), ("targetSlotIndex", "i16", 4));
            case "USE_LIMIT_CUBE": return DecodeFixed(body, 8, fields, diagnostics,
                ("targetSlotIndex", "i16", 0), ("targetItemId", "i32", 2), ("cubeSlotIndex", "i16", 6));
            case "USE_TITLE_CHANGE_ITEM": return DecodeFixed(body, 4, fields, diagnostics,
                ("sourceSlotIndex", "i16", 0), ("targetSlotIndex", "i16", 2));
            case "AVATAR_OPTION_CHANGE": return DecodeFixed(body, 13, fields, diagnostics,
                ("sourceSlotIndex", "i16", 0), ("sourceItemId", "i32", 2),
                ("targetSlotIndex", "i16", 6), ("targetItemId", "i32", 8), ("abilityNo", "u8", 12));
            case "SECRET_SHOP_BUY_ITEM": return DecodeFixed(body, 8, fields, diagnostics, ("itemId", "i32", 0), ("requestedCount", "i32", 4));
            case "SECRET_SHOP_OPEN_CLOSE": return DecodeFixed(body, 1, fields, diagnostics, ("open", "bool8", 0));
            case "PARTY_TELEPORT": return DecodeFixed(body, 7, fields, diagnostics,
                ("townId", "u8", 0), ("areaId", "u8", 1), ("x", "i16", 2), ("y", "i16", 4), ("direction", "u8", 6));
            case "TELEPORT": return DecodeFixed(body, 8, fields, diagnostics,
                ("type", "i16", 0), ("itemTemplateId", "i32", 2), ("reserved", "u8", 6), ("targetTownId", "u8", 7));
            case "ENTER_EXPERT_JOB_STORE": return DecodeFixed(body, 2, fields, diagnostics, ("ownerUserId", "u16", 0));
            case "CREATE_EXPERT_JOB_STORE": return DecodeCreateExpertJobStore(body, fields, diagnostics);
            case "REQUEST_DISJOINT_ITEM":
            case "REPAIR_DISJOINT_MACHINE": return DecodeFixed(body, 5, fields, diagnostics,
                ("ownerUserId", "u16", 0), ("targetSlotIndex", "i16", 2), ("itemSpace", "u8", 4));
            case "EXPERT_EXTRACTION": return DecodeFixed(body, 6, fields, diagnostics,
                ("extractorType", "u8", 0), ("extractorSlotIndex", "i16", 1),
                ("targetListType", "u8", 3), ("targetSlotIndex", "i16", 4));
            case "COMPOUND_ITEM_BY_EXPERT_JOB": return DecodeFixed(body, 8, fields, diagnostics,
                ("recipeItemId", "i32", 0), ("requestedCount", "u16", 4), ("cardSlotIndex", "i16", 6));
            case "TOURNAMENT_REWARD_SELECT": return DecodeFixed(body, 2, fields, diagnostics, ("cardType", "u8", 0), ("cardIndex", "u8", 1));
            case "SELECT_ULTIMATE_DIFFICULTY": return DecodeFixed(body, 1, fields, diagnostics, ("difficulty", "u8", 0));
            case "DIE_BLOOD_MONSTER": return DecodeCountedUInt16(body, fields, diagnostics, "sequenceIds");
            case "SUMMON_MONSTER": return DecodeFixed(body, 19, fields, diagnostics,
                ("sequenceId", "u16", 0), ("monsterCode", "i32", 2), ("stateId", "i32", 6),
                ("mapId", "i32", 10), ("positionX", "u16", 14), ("positionY", "u16", 16), ("matchCount", "u8", 18));
            case "SEA_CHASE_MINI_GAME_RESULT": return DecodeAtLeast(body, 4, fields, diagnostics, ("result", "i32", 0));
            case "REJOIN_DUNGEON": return DecodeAtLeast(body, 8, fields, diagnostics,
                ("partyId", "i32", 0), ("targetParticipantUserId", "i32", 4));
            case "CANCEL_REJOIN_DUNGEON": return DecodeAtLeast(body, 4, fields, diagnostics, ("partyId", "i32", 0));
            case "MAKE_PVP_ROOM": return DecodeMakePvpRoom(body, fields, diagnostics);
            case "ENTER_PVP_ROOM": return DecodeEnterPvpRoom(body, fields, diagnostics);
            case "DIE_PVP_CHARACTER": return DecodeFixed(body, 2, fields, diagnostics, ("reportedDeadUserId", "u16", 0));
            case "SET_PVP_READY_STATE": return DecodeFixed(body, 1, fields, diagnostics, ("isReady", "bool8", 0));
            case "SET_PVP_SEAT_STATE": return DecodeFixed(body, 2, fields, diagnostics, ("seat", "u8", 0), ("seatState", "u8", 1));
            case "SET_PVP_TEAM_MODE": return DecodeFixed(body, 1, fields, diagnostics, ("battleMode", "u8", 0));
            case "PVP_TIME_OUT": return DecodeFixed(body, 32, fields, diagnostics,
                ("value0", "i32", 0), ("value1", "i32", 4), ("value2", "i32", 8), ("value3", "i32", 12),
                ("value4", "i32", 16), ("value5", "i32", 20), ("value6", "i32", 24), ("value7", "i32", 28));
            case "CONNECT_P2P_PVP": return DecodeSeatStatusList(body, fields, diagnostics);
            case "UPGRADE_CHRONICLE": return DecodeFixed(body, 19, fields, diagnostics,
                ("ticketSlotIndex", "i16", 0), ("ticketItemTemplateId", "i32", 2),
                ("targetSlotIndex", "i16", 6), ("targetItemTemplateId", "i32", 8),
                ("reserved", "u8", 12), ("materialSlotIndex", "i16", 13), ("materialItemTemplateId", "i32", 15));
            case "ENCHANT_3RD_CHRONICLE_ITEM": return DecodeFixed(body, 14, fields, diagnostics,
                ("materialSlotIndex", "i16", 0), ("materialItemTemplateId", "i32", 2),
                ("materialPadding", "u8", 6), ("targetSlotIndex", "i16", 7),
                ("targetItemTemplateId", "i32", 9), ("optionNo", "u8", 13));
            case "UPGRADE_ITEM_SEPARATE": return DecodeSeparateUpgrade(body, fields, diagnostics);
            case "INCREASE_CHANCE_LOTTERY_RESET": return DecodeFixed(body, 21, fields, diagnostics,
                ("slotIndex", "i16", 13), ("itemTemplateId", "i32", 17));
            case "USE_ENCHANT_STORE": return DecodeFixed(body, 13, fields, diagnostics,
                ("ownerUserId", "u16", 0), ("recipeItemId", "i32", 2), ("mode", "u8", 6),
                ("targetListType", "u8", 7), ("targetSlotIndex", "i16", 8),
                ("cardListType", "u8", 10), ("cardSlotIndex", "i16", 11));
            case "USE_RANDOMBOX_ITEM": return DecodeMagicBox(body, fields, diagnostics, single: true);
            case "USE_RANDOMBOX_ITEM_EXPAND": return DecodeMagicBox(body, fields, diagnostics, single: false);
            case "CHECK_DOUBLE_CHARACTER_NAME": return DecodeDStringOnly(body, fields, diagnostics, "name", Encoding.UTF8);
            case "OPEN_CERAPACKAGE": return DecodeCeraPackage(body, fields, diagnostics);
            case "UPGRADE_CARD": return DecodeFixed(body, 7, fields, diagnostics,
                ("listType", "u8", 0), ("targetSlot", "i16", 1),
                ("materialCount", "i16", 3), ("materialSlot", "i16", 5));
            default:
                diagnostics.Add($"{name} is supported by the server but has no standalone semantic schema yet");
                return "opaque";
        }
    }

    private static string DecodeIgnoredBody(
        byte[] body,
        Dictionary<string, object?> fields,
        string semantic)
    {
        fields["serverHandling"] = "body-ignored";
        fields["semantic"] = semantic;
        return body.Length == 0 ? "empty-body-ignored" : "body-ignored";
    }

    private static string DecodeRawPersisted(
        byte[] body,
        Dictionary<string, object?> fields,
        string fieldName)
    {
        fields["serverHandling"] = "raw-persisted";
        fields[fieldName] = Convert.ToHexString(body);
        return "raw-persisted";
    }

    private static string DecodeOpaqueLength(
        byte[] body,
        int exactLength,
        Dictionary<string, object?> fields,
        List<string> diagnostics,
        string fieldName)
    {
        if (body.Length != exactLength)
            diagnostics.Add($"expected exactly {exactLength} opaque bytes, got {body.Length}");
        fields["serverHandling"] = "length-validated-opaque-acknowledgement";
        fields[fieldName] = Convert.ToHexString(body);
        return "opaque-fixed-length";
    }

    private static string DecodeRequestPeer(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 3)
        {
            diagnostics.Add("REQUEST_PEER requires targetUid:u16 and requestType:u8");
            return "invalid-peer-request";
        }
        fields["targetUserId"] = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var requestType = body[2];
        fields["requestType"] = requestType;
        fields["requestSemantic"] = requestType switch
        {
            0 => "party-invite",
            1 => "trade-invite",
            2 => "pvp-room-invite",
            _ => "unsupported",
        };
        if (body.Length >= 7)
            fields["peerId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3, 4));
        if (body.Length > 7)
            fields["trailingHex"] = Convert.ToHexString(body.AsSpan(7));
        if (requestType == 2 && body.Length != 7)
            diagnostics.Add("pvp-room invite variant requires exactly 7 bytes");
        return requestType switch
        {
            0 => "party-invite",
            1 => "trade-invite",
            2 => "pvp-room-invite",
            _ => "unknown-peer-request",
        };
    }

    private static string DecodeRepairEquipment(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 5)
        {
            diagnostics.Add("REPAIR_EQUIPMENT requires at least 5 bytes");
            return "invalid-repair-request";
        }
        fields["inventoryType"] = body[0];
        fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1, 2));
        fields["repairItemSlot"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
        if (body.Length >= 6) fields["autoRepair"] = body[5] == 1;
        if (body.Length >= 7) fields["reserved"] = body[6];
        if (body.Length >= 8) fields["quickRepair"] = body[7] == 1;
        if (body.Length > 8) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(8));
        return body.Length >= 6 && body[5] == 1
            ? "auto-repair"
            : body.Length >= 8 && body[7] == 1
                ? "quick-repair"
                : "manual-repair";
    }

    private static string DecodeSetPlayResult(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length > 10)
        {
            fields["clientRankPoint"] = body[10];
            fields["rankPointOffset"] = 10;
            return "rank-presentation";
        }
        fields["clientRankPoint"] = 0;
        diagnostics.Add("body has no byte at +0x0A; server uses rank point 0");
        return "rank-defaulted";
    }

    private static string DecodeMailboxSend(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics,
        bool multi)
    {
        var offset = 0;
        if (!TryReadDString(body, ref offset, out var receiverName))
        {
            diagnostics.Add("mailbox request has an invalid receiver-name dstr");
            return multi ? "invalid-multi-mail" : "invalid-single-mail";
        }
        fields["receiverName"] = receiverName;
        if (!TryReadInt32(body, ref offset, out var gold))
        {
            diagnostics.Add("mailbox request is missing gold:i32");
            return multi ? "invalid-multi-mail" : "invalid-single-mail";
        }
        fields["gold"] = gold;
        ushort attachmentCount = 1;
        if (multi && !TryReadUInt16(body, ref offset, out attachmentCount))
        {
            diagnostics.Add("multi-mail request is missing attachmentCount:u16");
            return "invalid-multi-mail";
        }
        fields["attachmentCount"] = attachmentCount;
        if (attachmentCount > 10)
            diagnostics.Add($"attachment count {attachmentCount} exceeds server limit 10");

        var attachments = new List<object>();
        for (var index = 0; index < Math.Min(attachmentCount, (ushort)10); index++)
        {
            byte itemType = 0;
            if ((!multi || index > 0) && !TryReadByte(body, ref offset, out itemType))
            {
                diagnostics.Add($"attachment[{index}] is missing itemType:u8");
                break;
            }
            if (!TryReadUInt16(body, ref offset, out var slot)
                || !TryReadInt32(body, ref offset, out var itemId)
                || !TryReadInt32(body, ref offset, out var count))
            {
                diagnostics.Add($"attachment[{index}] is truncated");
                break;
            }
            attachments.Add(new { itemType, slot, itemId, count });
        }
        fields["attachments"] = attachments;
        if (offset < body.Length)
        {
            var beforeText = offset;
            if (TryReadDString(body, ref offset, out var text))
                fields["text"] = text;
            else
            {
                offset = beforeText;
                diagnostics.Add("mailbox text dstr is malformed; remaining bytes returned as tail");
            }
        }
        if (offset < body.Length)
            fields["tailHex"] = Convert.ToHexString(body.AsSpan(offset));
        fields["consumedBytes"] = offset;
        return multi ? "multi-attachment-mail" : "single-attachment-mail";
    }

    private static string DecodeCreatureScriptMessage(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 11)
        {
            diagnostics.Add("CREATURE_SCRIPT_MESSAGE requires an 11-byte prefix");
            return "invalid-creature-script-message";
        }
        fields["mode"] = body[0];
        fields["targetUniqueId"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1, 2));
        fields["characterId"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(7, 4));
        fields["messageLength"] = length;
        if (length < 0 || length > 256 || 11 + length > body.Length)
        {
            diagnostics.Add("message dstr length is invalid");
            return "invalid-creature-script-message";
        }
        fields["message"] = Encoding.UTF8.GetString(body, 11, length);
        var offset = 11 + length;
        if (body[0] is 1 or 7)
        {
            if (!TryReadDString(body, ref offset, out var targetName))
                diagnostics.Add("targeted creature message is missing target-name dstr");
            else
                fields["targetName"] = targetName;
        }
        if (offset < body.Length) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(offset));
        return body[0] is 1 or 7 ? "targeted-creature-message" : "creature-message";
    }

    private static string DecodeDeathTowerStageCommand(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 1)
        {
            diagnostics.Add("DEATH_TOWER_STAGE_CMD requires operation:u8");
            return "invalid-stage-command";
        }
        fields["operation"] = body[0];
        fields["operationSemantic"] = body[0] switch { 1 => "fight-start", 2 => "stage-clear", _ => "unknown" };
        if (body.Length > 1) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(1));
        return body[0] switch { 1 => "fight-start", 2 => "stage-clear", _ => "unknown-stage-operation" };
    }

    private static string DecodeOverflowInfo(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.SequenceEqual(new byte[] { 0x01, 0x99, 0x02 }))
        {
            fields["operation"] = "close-raid-create-popup";
            fields["raidCommandType"] = 0x0299;
            return "raid-create-popup-close";
        }
        if (body.SequenceEqual(new byte[] { 0x01, 0x1B, 0x00 }))
        {
            fields["operation"] = "confirm-lottery-overflow";
            return "lottery-overflow-confirm";
        }
        diagnostics.Add("OVERFLOW_INFO candidates require fixed bodies 01-99-02 or 01-1B-00");
        return "unknown-overflow-operation";
    }

    private static string DecodeSkillTreeSwitch(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        byte raw;
        if (body.Length == 1) raw = body[0];
        else if (body.Length == 2) raw = body[1];
        else
        {
            diagnostics.Add("CHANGE_ANOTHER_SKILL_TREE expects 1 byte or a 2-byte legacy form");
            return "session-default-skill-tree";
        }
        fields["wireSkillTreeIndex"] = raw;
        fields["normalizedSkillTreeIndex"] = raw switch { 0 => 0, 1 or 2 => 1, _ => 0 };
        return body.Length == 1 ? "direct-index" : "legacy-prefixed-index";
    }

    private static string DecodeSceneUniqueId(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 2)
        {
            diagnostics.Add("INFORM_NOTICE requires at least a 2-byte scene unique id");
            return "invalid-scene-id";
        }
        var raw = body.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(body)
            : BinaryPrimitives.ReadUInt16LittleEndian(body);
        fields["rawSceneValue"] = raw;
        fields["sceneUniqueId"] = (ushort)(raw & 0xFFFF);
        if (body.Length > 4) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(4));
        return body.Length >= 4 ? "scene-id-u32" : "scene-id-u16";
    }

    private static string DecodeComboSkillInfo(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length == 0)
        {
            diagnostics.Add("COMBO_SKILL_INFO requires a non-empty persisted blob");
            return "invalid-combo-skill-blob";
        }
        fields["serverHandling"] = "raw-persisted-with-record-preview";
        fields["page"] = body[0] == 1 ? 1 : 0;
        var records = new List<object>();
        var offset = 0;
        var first = true;
        while (offset < body.Length)
        {
            var headerSize = first ? 4 : 3;
            if (offset + headerSize > body.Length) break;
            var skillId = first ? body[offset + 1] : body[offset];
            var lengthOffset = first ? offset + 2 : offset + 1;
            var commandLength = (body[lengthOffset] << 8) | body[lengthOffset + 1];
            offset += headerSize;
            if ((!first && commandLength <= 0) || offset + commandLength > body.Length) break;
            records.Add(new
            {
                skillId,
                commandLength,
                commandHex = Convert.ToHexString(body.AsSpan(offset, commandLength)),
            });
            offset += commandLength;
            first = false;
        }
        fields["records"] = records;
        fields["consumedBytes"] = offset;
        if (offset < body.Length) fields["unparsedTailHex"] = Convert.ToHexString(body.AsSpan(offset));
        return "dark-knight-combo-records";
    }

    private static string DecodeComboQuickSlotReset(byte[] body, Dictionary<string, object?> fields)
    {
        fields["page"] = body.Length > 0 && body[0] == 1 ? 1 : 0;
        if (body.Length > 1) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(1));
        return body.Length == 0 ? "default-page-reset" : "page-reset";
    }

    private static string DecodeEquipmentEffect(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 21)
        {
            diagnostics.Add("ADD_EQUIPMENT_EFFECT requires at least 21 bytes");
            return "invalid-equipment-effect";
        }
        fields["targetListType"] = body[12];
        fields["targetSlotIndex"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(13, 4));
        var sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(17, 4));
        if (sourceSlot is < 0 or > 500)
        {
            sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8, 4));
            fields["sourceSlotFallbackOffset"] = 8;
        }
        else
        {
            fields["sourceSlotFallbackOffset"] = 17;
        }
        fields["sourceSlotIndex"] = sourceSlot;
        if (body.Length > 21) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(21));
        return "explicit-equipment-effect-target";
    }

    private static string DecodeRentalEquipment(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length < 21)
        {
            diagnostics.Add("RENT_EQUIPMENT_ITEM requires at least 21 bytes");
            return "invalid-rental-equipment";
        }
        fields["shopWeaponId"] = BinaryPrimitives.ReadUInt32LittleEndian(body);
        fields["reservedHex"] = Convert.ToHexString(body.AsSpan(4, 9));
        fields["inventoryTemplateId"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(13, 4));
        fields["rentalDays"] = body[17];
        fields["starCostHalf"] = body[18];
        fields["priceTier"] = body[19];
        fields["reservedTail"] = body[20];
        if (body.Length > 21) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(21));
        return "rental-weapon-request";
    }

    private static string DecodeCollectionBoxQuery(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length == 0)
        {
            fields["boxIndex"] = 0;
            diagnostics.Add("empty SELECT_COLLECTBOX defaults to box index 0");
            return "default-box";
        }
        fields["boxIndex"] = body[^1];
        if (body.Length > 1) fields["prefixHex"] = Convert.ToHexString(body.AsSpan(0, body.Length - 1));
        return "tail-index";
    }

    private static string DecodeDeleteItem(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics)
    {
        if (body.Length == 4)
            return DecodeFixed(body, 4, fields, diagnostics, ("slotIndex", "i16", 0), ("itemCount", "i16", 2)) == "fixed-layout"
                ? "simple-legacy"
                : "simple-legacy";
        if (body.Length >= 15 && body[1] is >= 1 and <= 100)
        {
            fields["listType"] = body[0];
            var count = body[1];
            fields["entryCount"] = count;
            var entries = new List<object>();
            var offset = 2;
            for (var index = 0; index < count && offset + 12 <= body.Length; index++, offset += 12)
            {
                entries.Add(new
                {
                    operationType = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset, 2)),
                    slotIndex = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 2, 2)),
                    itemId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset + 4, 4)),
                    deleteCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset + 8, 4)),
                });
            }
            fields["entries"] = entries;
            fields["consumedBytes"] = offset;
            if (entries.Count != count) diagnostics.Add($"extended DELETE_ITEM declares {count} entries but only {entries.Count} complete records fit");
            if (offset < body.Length) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(offset));
            return "extended-array";
        }
        if (body.Length >= 5)
        {
            fields["listType"] = body[0];
            fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1, 2));
            fields["itemCount"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
            if (body.Length > 5) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(5));
            return "simple-list-prefixed";
        }
        diagnostics.Add("DELETE_ITEM requires 4 bytes, a list-prefixed 5-byte form, or an extended array form");
        return "invalid-delete-item";
    }

    private static bool TryReadByte(byte[] body, ref int offset, out byte value)
    {
        value = 0;
        if (offset < 0 || offset >= body.Length) return false;
        value = body[offset++];
        return true;
    }

    private static bool TryReadUInt16(byte[] body, ref int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset + 2 > body.Length) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
        offset += 2;
        return true;
    }

    private static bool TryReadInt32(byte[] body, ref int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > body.Length) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadDString(byte[] body, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadInt32(body, ref offset, out var length)
            || length < 0
            || length > 1024 * 1024
            || offset + length > body.Length)
        {
            return false;
        }
        value = Encoding.UTF8.GetString(body, offset, length).TrimEnd('\0');
        offset += length;
        return true;
    }

    private static string DecodeQuestWire(
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics,
        params (string Name, string Type, int Offset)[] payloadSchema)
    {
        var payloadWidth = payloadSchema.Select(field => field.Offset + FieldWidth(field.Type)).DefaultIfEmpty(0).Max();
        var payloadOffset = body.Length >= payloadWidth + 2 ? 2 : 0;
        fields["echoPrefixLength"] = payloadOffset;
        if (payloadOffset == 2)
        {
            fields["echoPrefixHex"] = Convert.ToHexString(body.AsSpan(0, 2));
            diagnostics.Add("Quest wire request includes the 2-byte echo prefix stripped by QuestManager");
        }
        if (body.Length < payloadOffset + payloadWidth)
            diagnostics.Add($"quest payload requires {payloadWidth} bytes after echo prefix, got {body.Length - payloadOffset}");
        foreach (var field in payloadSchema)
        {
            var width = FieldWidth(field.Type);
            var offset = payloadOffset + field.Offset;
            if (offset + width <= body.Length)
                fields[field.Name] = ReadField(body, field.Type, offset);
        }
        fields["businessPayloadOffset"] = payloadOffset;
        return "quest-wire-with-optional-echo";
    }

    private static string DecodeCeraPackage(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length >= 3)
        {
            var avatarCount = body[2];
            var avatarLength = 3 + avatarCount * 5;
            if (avatarCount is > 0 and <= 32 && body.Length >= avatarLength)
            {
                fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body);
                fields["choiceCount"] = avatarCount;
                var choices = new List<object>();
                for (var index = 0; index < avatarCount; index++)
                {
                    var offset = 3 + index * 5;
                    choices.Add(new
                    {
                        itemTemplateId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4)),
                        optionValue = body[offset + 4],
                    });
                }
                fields["choices"] = choices;
                if (body.Length > avatarLength) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(avatarLength));
                return "avatar-package";
            }
        }

        if (body.Length >= 9)
        {
            fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body);
            fields["selectionContext"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(2, 2));
            fields["selectedItemTemplateId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4));
            fields["selectionFlag"] = body[8];
            if (body.Length > 9) fields["selectionTailHex"] = Convert.ToHexString(body.AsSpan(9));
            return "selectable-or-general-package";
        }

        diagnostics.Add("OPEN_CERAPACKAGE matches neither avatar-package nor selectable-package minimum layout");
        return "unresolved-package-variant";
    }

    private static string DecodeDieMonster(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 2) { diagnostics.Add("DIE_MONSTER requires at least 2 bytes"); return "die-monster"; }
        fields["localIndex"] = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (body.Length >= 4) fields["userId"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        if (body.Length > 20)
        {
            var attackCount = body[20];
            fields["attackCount"] = attackCount;
            var flagOffset = 21 + attackCount * 10 + 6;
            fields["flagOffset"] = flagOffset;
            if (flagOffset - 1 < body.Length) fields["isCapture"] = body[flagOffset - 1] != 0;
            if (flagOffset < body.Length) fields["isPassiveObject"] = body[flagOffset] == 1;
        }
        return "die-monster";
    }

    private static string DecodeEmblemCompound(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 1) { diagnostics.Add("COMPOUND_EMBLEM requires a count byte"); return "emblem-inputs"; }
        var count = body[0];
        fields["count"] = count;
        if (body.Length != 1 + count * 6) diagnostics.Add($"COMPOUND_EMBLEM expects {1 + count * 6} bytes, got {body.Length}");
        var items = new List<object>();
        for (var index = 0; index < count && 1 + index * 6 + 6 <= body.Length; index++)
        {
            var offset = 1 + index * 6;
            items.Add(new
            {
                itemTemplateId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4)),
                slotIndex = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 4, 2)),
            });
        }
        fields["inputs"] = items;
        return "emblem-inputs";
    }

    private static string DecodeCreateExpertJobStore(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 15) { diagnostics.Add("CREATE_EXPERT_JOB_STORE requires at least 15 bytes"); return "expert-store-create"; }
        fields["kind"] = body[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1, 4));
        fields["nameLength"] = length;
        if (length < 0 || body.Length != 15 + length) { diagnostics.Add("expert store name length mismatch"); return "expert-store-create"; }
        fields["nameBytesHex"] = Convert.ToHexString(body.AsSpan(5, length));
        var offset = 5 + length;
        fields["cost"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
        fields["positionX"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 4, 2));
        fields["positionY"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 6, 2));
        fields["direction"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset + 8, 2));
        return "expert-store-create";
    }

    private static string DecodeMakePvpRoom(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var roomNameType)) { diagnostics.Add("MAKE_PVP_ROOM missing roomNameType"); return "pvp-room-create"; }
        fields["roomNameType"] = roomNameType;
        if (roomNameType == 0)
        {
            if (!reader.TryReadDString(Encoding.UTF8, out var roomName)) { diagnostics.Add("MAKE_PVP_ROOM invalid room name dstr"); return "pvp-room-create"; }
            fields["roomName"] = roomName;
        }
        if (!reader.TryReadInt16(out var mapIndex) || !reader.TryReadByte(out var passwordFlag))
        {
            diagnostics.Add("MAKE_PVP_ROOM truncated after room name"); return "pvp-room-create";
        }
        fields["mapIndex"] = mapIndex;
        fields["hasPassword"] = passwordFlag != 0;
        if (passwordFlag == 1)
        {
            if (!reader.TryReadDString(Encoding.UTF8, out var password)) { diagnostics.Add("MAKE_PVP_ROOM invalid password dstr"); return "pvp-room-create"; }
            fields["password"] = password;
        }
        if (reader.TryReadByte(out var special))
        {
            fields["specialBattleModeRaw"] = special;
            fields["battleMode"] = special == 1 ? 6 : 2;
        }
        if (reader.Remaining != 0) diagnostics.Add($"MAKE_PVP_ROOM has {reader.Remaining} trailing bytes");
        return "pvp-room-create";
    }

    private static string DecodeEnterPvpRoom(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadUInt16(out var roomId) || !reader.TryReadByte(out var passwordFlag))
        {
            diagnostics.Add("ENTER_PVP_ROOM requires roomId and password flag"); return "pvp-room-enter";
        }
        fields["roomId"] = roomId;
        fields["hasPassword"] = passwordFlag != 0;
        if (passwordFlag == 1)
        {
            if (!reader.TryReadDString(Encoding.UTF8, out var password)) diagnostics.Add("ENTER_PVP_ROOM invalid password dstr");
            else fields["password"] = password;
        }
        if (reader.Remaining != 0) diagnostics.Add($"ENTER_PVP_ROOM has {reader.Remaining} trailing bytes");
        return "pvp-room-enter";
    }

    private static string DecodeSeatStatusList(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 1) { diagnostics.Add("CONNECT_P2P_PVP requires count byte"); return "seat-status-list"; }
        var count = body[0];
        fields["count"] = count;
        if (body.Length != 1 + count * 2) diagnostics.Add($"CONNECT_P2P_PVP expects {1 + count * 2} bytes, got {body.Length}");
        var statuses = new List<object>();
        for (var index = 0; index < count && 1 + index * 2 + 2 <= body.Length; index++)
            statuses.Add(new { seat = body[1 + index * 2], status = body[2 + index * 2] });
        fields["statuses"] = statuses;
        return "seat-status-list";
    }

    private static string DecodeSeparateUpgrade(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 15) { diagnostics.Add("UPGRADE_ITEM_SEPARATE requires at least 15 bytes"); return "separate-upgrade"; }
        fields["targetListType"] = body[0];
        fields["targetSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1, 2));
        fields["targetItemTemplateId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3, 4));
        fields["materialSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(7, 2));
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(9, 4));
        fields["itemNameLength"] = nameLength;
        if (nameLength > 0 && 13 + nameLength < body.Length)
        {
            fields["itemName"] = Encoding.UTF8.GetString(body, 13, nameLength);
            fields["confirmationVariant"] = body[13 + nameLength];
        }
        else diagnostics.Add("UPGRADE_ITEM_SEPARATE name length mismatch");
        return "separate-upgrade";
    }

    private static string DecodeMagicBox(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, bool single)
    {
        if (single)
        {
            if (body.Length < 3) { diagnostics.Add("single random box request requires at least 3 bytes"); return "single-random-box"; }
            fields["rawListType"] = body[0];
            fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1, 2));
            if (body.Length >= 5) fields["materialSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
            return "single-random-box";
        }
        return DecodeAtLeast(body, 15, fields, diagnostics,
            ("rawListType", "u8", 0), ("slotIndex", "i16", 1), ("itemTemplateId", "i32", 3),
            ("materialSlotIndex", "i16", 7), ("materialItemTemplateId", "i32", 9), ("requestedCount", "u16", 13));
    }

    private static string DecodeDStringOnly(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string fieldName, Encoding encoding)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadDString(encoding, out var value)) diagnostics.Add($"invalid {fieldName} dstr");
        else fields[fieldName] = value;
        if (reader.Remaining != 0) diagnostics.Add($"{reader.Remaining} trailing bytes after {fieldName}");
        return "dstr";
    }

    private static string DecodeCountedInt32(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string fieldName)
    {
        if (body.Length < 1) { diagnostics.Add("counted i32 list requires count byte"); return "counted-i32-list"; }
        var count = body[0];
        fields["count"] = count;
        if (body.Length != 1 + count * 4) diagnostics.Add($"counted i32 list expects {1 + count * 4} bytes, got {body.Length}");
        fields[fieldName] = Enumerable.Range(0, Math.Min(count, (body.Length - 1) / 4))
            .Select(index => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1 + index * 4, 4))).ToArray();
        return "counted-i32-list";
    }

    private static DecodedBody DecodeUserInfo(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 1)
        {
            diagnostics.Add("USERINFO requires subtype byte at body offset 0");
            return new DecodedBody("unresolved", fields);
        }
        var subtype = body[0];
        fields["subtype"] = subtype;
        return subtype switch
        {
            0 => DecodeUserInfoSubtype0(body, fields, diagnostics),
            1 => DecodeUserInfoSubtype1(body, fields, diagnostics),
            2 => DecodeUserInfoSubtype2(body, fields, diagnostics),
            3 => DecodeUserInfoSubtype3(body, fields, diagnostics),
            _ => UnknownUserInfoSubtype(subtype, body, fields, diagnostics),
        };
    }

    private static DecodedBody DecodeUserInfoRequestedVariant(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string requestedVariant)
    {
        var subtype = requestedVariant switch
        {
            "subtype0" or "subtype0-character-state" => 0,
            "subtype1" or "subtype1-stats-equipment-skills" => 1,
            "subtype2" or "subtype2-character-roster" => 2,
            "subtype3" or "subtype3-inspect-player" => 3,
            _ => -1,
        };
        if (subtype < 0)
        {
            diagnostics.Add($"requested USERINFO variant '{requestedVariant}' was not found");
            fields["candidateVariants"] = new[] { "subtype0-character-state", "subtype1-stats-equipment-skills", "subtype2-character-roster", "subtype3-inspect-player" };
            return new DecodedBody("unknown-requested-variant", fields);
        }
        if (body.Length == 0 || body[0] != subtype)
            diagnostics.Add($"requested USERINFO variant subtype{subtype} does not match body discriminator {(body.Length == 0 ? "missing" : body[0].ToString())}");
        return subtype switch
        {
            0 => DecodeUserInfoSubtype0(body, fields, diagnostics),
            1 => DecodeUserInfoSubtype1(body, fields, diagnostics),
            2 => DecodeUserInfoSubtype2(body, fields, diagnostics),
            _ => DecodeUserInfoSubtype3(body, fields, diagnostics),
        };
    }

    private static DecodedBody DecodeUserInfoSubtype0(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        reader.TryReadByte(out _);
        if (!reader.TryReadUInt16(out var count) || !reader.TryReadUInt16(out var userId) || !reader.TryReadDString(Encoding.UTF8, out var name))
        {
            diagnostics.Add("USERINFO subtype 0 header is truncated");
            return new DecodedBody("subtype0-character-state", fields);
        }
        fields["recordCount"] = count;
        fields["userId"] = userId;
        fields["name"] = name;
        if (reader.TryReadByte(out var job)) fields["job"] = job;
        if (reader.TryReadByte(out var growType)) fields["growType"] = growType;
        if (reader.TryReadByte(out var level)) fields["level"] = level;
        if (reader.TryReadByte(out var pvpGrade)) fields["pvpGrade"] = pvpGrade;
        if (reader.TryReadByte(out var pvpRatingGrade)) fields["pvpRatingGrade"] = pvpRatingGrade;
        if (reader.TryReadByte(out var userState)) fields["userState"] = userState;
        if (!reader.TryReadByte(out var appearanceCount))
        {
            diagnostics.Add("USERINFO subtype 0 has no appearance count");
            return new DecodedBody("subtype0-character-state", fields);
        }
        fields["appearanceCount"] = appearanceCount;
        var appearances = new List<Dictionary<string, object?>>();
        for (var index = 0; index < appearanceCount; index++)
        {
            if (!reader.TryReadByte(out var slot) || !reader.TryReadInt32(out var displayItemId) ||
                !reader.TryReadInt32(out var expansionLength) || !reader.TryReadBytes(4, out var expansionData) ||
                !reader.TryReadByte(out var state) || !reader.TryReadInt32(out var linkItemId) ||
                !reader.TryReadUInt32(out var enchantValue) || !reader.TryReadByte(out var flag20))
            {
                diagnostics.Add($"USERINFO subtype 0 appearance entry {index} is truncated");
                break;
            }
            appearances.Add(new Dictionary<string, object?>
            {
                ["slot"] = slot, ["displayItemId"] = displayItemId, ["expansionLength"] = expansionLength,
                ["expansionDataHex"] = Convert.ToHexString(expansionData), ["state"] = state,
                ["linkItemId"] = linkItemId, ["enchantValue"] = enchantValue, ["flag20"] = flag20,
            });
        }
        fields["appearances"] = appearances;
        DecodeUserInfoSubtype0Tail(reader, fields, diagnostics);
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail);
        return new DecodedBody("subtype0-character-state", fields);
    }

    private static DecodedBody DecodeUserInfoSubtype1(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        DecodeUserInfoCommonPrefix(body, fields, diagnostics, out var reader);
        if (reader is null) return new DecodedBody("subtype1-stats-equipment-skills", fields);
        if (!DecodeUserInfoStatsAndEquipment(reader, fields, diagnostics))
            return new DecodedBody("subtype1-stats-equipment-skills", fields);
        ReadUInt32Field(reader, fields, diagnostics, "cloneTitleItemId");
        ReadUInt32Field(reader, fields, diagnostics, "nameTagItemId");
        ReadUInt32Field(reader, fields, diagnostics, "nameTagExpireTime");
        ReadByteField(reader, fields, diagnostics, "skillTreeIndex");
        fields["skillPages"] = new[]
        {
            DecodeUserInfoSkillPage(reader, diagnostics, 0),
            DecodeUserInfoSkillPage(reader, diagnostics, 1),
        };
        ReadByteField(reader, fields, diagnostics, "equippedCreatureLevel");
        fields["dimensions"] = DecodeUserInfoDimensions(reader, diagnostics);
        ReadByteField(reader, fields, diagnostics, "dimensionFlag1");
        ReadByteField(reader, fields, diagnostics, "dimensionFlag2");
        ReadByteField(reader, fields, diagnostics, "dimensionFlag3");
        ReadByteField(reader, fields, diagnostics, "dimensionFlag4");
        fields["pvpResults"] = DecodeUserInfoPvpResults(reader, diagnostics);
        ReadByteField(reader, fields, diagnostics, "manageLevel");
        fields["specialRewardQuestIds"] = DecodeCountedUInt32(reader, diagnostics, "special reward quest");
        ReadByteField(reader, fields, diagnostics, "flagByte");
        ReadUInt32Field(reader, fields, diagnostics, "guildPowerWar");
        ReadUInt32Field(reader, fields, diagnostics, "serverTimestamp");
        ReadUInt16Field(reader, fields, diagnostics, "questShopCount");
        ReadUInt32Field(reader, fields, diagnostics, "progress1");
        ReadUInt32Field(reader, fields, diagnostics, "progress2");
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var remainder)) fields["trailingHex"] = Convert.ToHexString(remainder);
        return new DecodedBody("subtype1-stats-equipment-skills", fields);
    }

    private static DecodedBody DecodeUserInfoSubtype2(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        reader.TryReadByte(out _);
        if (!reader.TryReadUInt16(out var slotLimit) || !reader.TryReadUInt16(out var gateOrCount2) ||
            !reader.TryReadByte(out var manageLevel) || !reader.TryReadInt32(out var totalPoint) ||
            !reader.TryReadUInt16(out var unknown16) || !reader.TryReadInt32(out var unknown32) ||
            !reader.TryReadUInt16(out var characterCount))
        {
            diagnostics.Add("USERINFO subtype 2 roster header is truncated");
            return new DecodedBody("subtype2-character-roster", fields);
        }
        fields["slotLimit"] = slotLimit;
        fields["gateOrCount2"] = gateOrCount2;
        fields["manageLevel"] = manageLevel;
        fields["totalPoint"] = totalPoint;
        fields["unknown16"] = unknown16;
        fields["unknown32"] = unknown32;
        fields["characterCount"] = characterCount;
        var characters = new List<Dictionary<string, object?>>();
        for (var index = 0; index < characterCount; index++)
        {
            var entry = DecodeUserInfoRosterEntry(reader, diagnostics, index);
            if (entry is null) break;
            characters.Add(entry);
        }
        fields["characters"] = characters;
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var roster)) fields["trailingHex"] = Convert.ToHexString(roster);
        return new DecodedBody("subtype2-character-roster", fields);
    }

    private static DecodedBody DecodeUserInfoSubtype3(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        DecodeUserInfoCommonPrefix(body, fields, diagnostics, out var reader);
        if (reader is not null)
        {
            if (DecodeUserInfoStatsAndEquipment(reader, fields, diagnostics))
            {
                ReadUInt32Field(reader, fields, diagnostics, "cloneTitleItemId");
                ReadUInt32Field(reader, fields, diagnostics, "nameTagItemId");
                ReadUInt32Field(reader, fields, diagnostics, "nameTagExpireTime");
                ReadByteField(reader, fields, diagnostics, "skillTreeIndex");
                fields["skillPages"] = new[]
                {
                    DecodeUserInfoSkillPage(reader, diagnostics, 0),
                    DecodeUserInfoSkillPage(reader, diagnostics, 1),
                };
                ReadByteField(reader, fields, diagnostics, "equippedCreatureLevel");
                var context = new Dictionary<string, object?>(StringComparer.Ordinal);
                ReadUInt32Field(reader, context, diagnostics, "value0");
                ReadUInt32Field(reader, context, diagnostics, "value1");
                ReadUInt32Field(reader, context, diagnostics, "value2");
                ReadByteField(reader, context, diagnostics, "flag0");
                ReadByteField(reader, context, diagnostics, "flag1");
                ReadByteField(reader, context, diagnostics, "flag2");
                ReadByteField(reader, context, diagnostics, "flag3");
                fields["inspectContext"] = context;
                ReadUInt32Field(reader, fields, diagnostics, "helpAbuseRatio");
                ReadUInt16Field(reader, fields, diagnostics, "personalPowerWarPoint");
                ReadUInt32Field(reader, fields, diagnostics, "guildPowerWar");
                ReadDStringField(reader, fields, diagnostics, "guildName", Encoding.UTF8);
                ReadByteField(reader, fields, diagnostics, "guildLevel");
                ReadByteField(reader, fields, diagnostics, "reservedAfterGuild0");
                ReadByteField(reader, fields, diagnostics, "reservedAfterGuild1");
                ReadByteField(reader, fields, diagnostics, "manageLevel");
                ReadByteField(reader, fields, diagnostics, "flagByte");
                fields["specialRewardQuestIds"] = DecodeCountedUInt32(reader, diagnostics, "special reward quest");
                ReadUInt16Field(reader, fields, diagnostics, "questShopCount");
                ReadByteField(reader, fields, diagnostics, "inspectionRecordCount");
            }
            fields["consumedBytes"] = reader.Offset;
            if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var payload)) fields["trailingHex"] = Convert.ToHexString(payload);
        }
        return new DecodedBody("subtype3-inspect-player", fields);
    }

    private static DecodedBody UnknownUserInfoSubtype(byte subtype, byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        diagnostics.Add($"USERINFO subtype {subtype} has no known schema; candidates are 0, 1, 2, and 3");
        AddScalarPreview(fields, body);
        return new DecodedBody($"unresolved-subtype-{subtype}", fields);
    }

    private static void DecodeUserInfoCommonPrefix(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, out PacketReader? reader)
    {
        reader = new PacketReader(body);
        reader.TryReadByte(out _);
        if (!reader.TryReadUInt16(out var count) || !reader.TryReadUInt16(out var userId))
        {
            diagnostics.Add("USERINFO common subtype header is truncated");
            reader = null;
            return;
        }
        fields["recordCount"] = count;
        fields["userId"] = userId;
    }

    private static void DecodeUserInfoSubtype0Tail(PacketReader reader, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        ReadUInt32Field(reader, fields, diagnostics, "cloneTitleItemId");
        ReadByteField(reader, fields, diagnostics, "forging");
        ReadByteField(reader, fields, diagnostics, "creatureField2");
        ReadByteField(reader, fields, diagnostics, "creatureField3");
        ReadByteField(reader, fields, diagnostics, "creatureField4");
        ReadUInt32Field(reader, fields, diagnostics, "nameTagItemId");
        ReadUInt32Field(reader, fields, diagnostics, "nameTagExpireTime");
        ReadByteField(reader, fields, diagnostics, "stamina");
        ReadUInt32Field(reader, fields, diagnostics, "fatiguePenalty");
        ReadByteField(reader, fields, diagnostics, "isEventCharacter");
        ReadUInt32Field(reader, fields, diagnostics, "equippedCreatureItemId");
        ReadDStringField(reader, fields, diagnostics, "equippedCreatureName", Encoding.UTF8);
        ReadByteField(reader, fields, diagnostics, "equippedCreatureAliveState");
        ReadByteField(reader, fields, diagnostics, "isPremiumPcRoom");
        ReadByteField(reader, fields, diagnostics, "serverGroupId");
        ReadUInt32Field(reader, fields, diagnostics, "blackCount");
        ReadByteField(reader, fields, diagnostics, "guildLevel");
        ReadDStringField(reader, fields, diagnostics, "guildName", Encoding.UTF8);
        ReadUInt32Field(reader, fields, diagnostics, "chaosPoint");
        ReadByteField(reader, fields, diagnostics, "constantOne");
        ReadByteField(reader, fields, diagnostics, "disguiseKind");
        ReadByteField(reader, fields, diagnostics, "isDisguised");
        ReadByteField(reader, fields, diagnostics, "expertJobType");
        ReadUInt32Field(reader, fields, diagnostics, "expertJobExperience");
        ReadByteField(reader, fields, diagnostics, "reservedExpertByte");
        ReadUInt32Field(reader, fields, diagnostics, "reservedExpertValue");
        ReadUInt16Field(reader, fields, diagnostics, "reservedExpertShort");
        ReadByteField(reader, fields, diagnostics, "isHardcoreMode");
        ReadByteField(reader, fields, diagnostics, "isHardcoreDead");
        ReadUInt16Field(reader, fields, diagnostics, "hardcoreDeathCount");
        ReadUInt32Field(reader, fields, diagnostics, "progressA");
        ReadUInt32Field(reader, fields, diagnostics, "progressB");
        ReadByteField(reader, fields, diagnostics, "userStateBits");
        ReadUInt32Field(reader, fields, diagnostics, "chatBanEndTime");
        ReadByteField(reader, fields, diagnostics, "displayPercent");
        ReadUInt16Field(reader, fields, diagnostics, "fatigueUpdate");
        ReadByteField(reader, fields, diagnostics, "returnUserFlag");
        ReadUInt16Field(reader, fields, diagnostics, "channelDisplayMode");
        ReadByteField(reader, fields, diagnostics, "channelType");
        ReadUInt16Field(reader, fields, diagnostics, "moodValue");
        ReadByteField(reader, fields, diagnostics, "skillTreeIndex");
        ReadByteField(reader, fields, diagnostics, "isReturnUser");
        ReadByteField(reader, fields, diagnostics, "linkSlotEnabled");
        ReadByteField(reader, fields, diagnostics, "linkTypeA");
        ReadByteField(reader, fields, diagnostics, "linkTypeB");
        ReadUInt16Field(reader, fields, diagnostics, "emotionIndex");
        ReadByteField(reader, fields, diagnostics, "actionByte");
        ReadUInt16Field(reader, fields, diagnostics, "fatigueDisplayUpdate");
        ReadByteField(reader, fields, diagnostics, "costumeFlag");
        ReadByteField(reader, fields, diagnostics, "auraFlag");
        ReadByteField(reader, fields, diagnostics, "petDisplayFlag");
        ReadByteField(reader, fields, diagnostics, "titleDisplayFlag");
        ReadUInt32Field(reader, fields, diagnostics, "pvpStatA");
        ReadByteField(reader, fields, diagnostics, "pvpWinStreak");
        ReadByteField(reader, fields, diagnostics, "pvpLoseStreak");
        ReadUInt32Field(reader, fields, diagnostics, "pvpRankPoint");
        ReadByteField(reader, fields, diagnostics, "trailingByte");
    }

    private static bool DecodeUserInfoStatsAndEquipment(PacketReader reader, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var before = diagnostics.Count;
        ReadUInt32Field(reader, fields, diagnostics, "characterExperience");
        ReadInt32Field(reader, fields, diagnostics, "statBlockMarker");
        ReadUInt32Field(reader, fields, diagnostics, "hpMax");
        ReadUInt32Field(reader, fields, diagnostics, "mpMax");
        ReadInt16Field(reader, fields, diagnostics, "physicalAttack");
        ReadInt16Field(reader, fields, diagnostics, "physicalDefense");
        ReadInt16Field(reader, fields, diagnostics, "magicalAttack");
        ReadInt16Field(reader, fields, diagnostics, "magicalDefense");
        ReadInt16Field(reader, fields, diagnostics, "fireResistance");
        ReadInt16Field(reader, fields, diagnostics, "waterResistance");
        ReadInt16Field(reader, fields, diagnostics, "darkResistance");
        ReadInt16Field(reader, fields, diagnostics, "lightResistance");
        var reservedStats = new List<ushort>();
        for (var index = 0; index < 17; index++)
        {
            if (!reader.TryReadUInt16(out var value))
            {
                diagnostics.Add($"USERINFO reserved stat[{index}] is truncated");
                return false;
            }
            reservedStats.Add(value);
        }
        fields["reservedStats"] = reservedStats;
        ReadUInt32Field(reader, fields, diagnostics, "inventoryLimit");
        ReadUInt16Field(reader, fields, diagnostics, "hpRegenSpeed");
        ReadUInt16Field(reader, fields, diagnostics, "mpRegenSpeed");
        ReadUInt32Field(reader, fields, diagnostics, "moveSpeed");
        ReadUInt16Field(reader, fields, diagnostics, "attackSpeed");
        ReadUInt16Field(reader, fields, diagnostics, "castSpeed");
        ReadUInt16Field(reader, fields, diagnostics, "hitRecovery");
        ReadUInt16Field(reader, fields, diagnostics, "jumpPower");
        ReadUInt32Field(reader, fields, diagnostics, "weight");
        ReadByteField(reader, fields, diagnostics, "statLevel");
        ReadByteField(reader, fields, diagnostics, "extraEquipmentSlotState");
        if (!reader.TryReadByte(out var equipmentCount))
        {
            diagnostics.Add("USERINFO equipment count is truncated");
            return false;
        }
        fields["equipmentCount"] = equipmentCount;
        var equipment = new List<Dictionary<string, object?>>();
        for (var index = 0; index < equipmentCount; index++)
        {
            var entry = DecodeUserInfoEquippedEntry(reader, diagnostics, index);
            if (entry is null) return false;
            equipment.Add(entry);
        }
        fields["equipment"] = equipment;
        return diagnostics.Count == before;
    }

    private static Dictionary<string, object?>? DecodeUserInfoEquippedEntry(PacketReader reader, List<string> diagnostics, int index)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!reader.TryReadByte(out var slot) || !reader.TryReadInt32(out var itemId)
            || !reader.TryReadUInt32(out var value) || !reader.TryReadByte(out var attr)
            || !reader.TryReadUInt16(out var durability) || !reader.TryReadUInt32(out var clearAvatarOrSeal)
            || !reader.TryReadInt32(out var enchantCardId) || !reader.TryReadByte(out var enchantUpgradeCount)
            || !reader.TryReadByte(out var amplifyType) || !reader.TryReadUInt16(out var amplifyValue))
        {
            diagnostics.Add($"USERINFO equipment entry {index} core is truncated");
            return null;
        }
        result["slot"] = slot;
        result["itemId"] = itemId;
        result["value"] = value;
        result["attr"] = attr;
        result["durability"] = durability;
        result["clearAvatarOrSeal"] = clearAvatarOrSeal;
        result["enchantCardId"] = enchantCardId;
        result["enchantUpgradeCount"] = enchantUpgradeCount;
        result["amplifyType"] = amplifyType;
        result["amplifyValue"] = amplifyValue;

        if (slot <= 10)
        {
            if (!reader.TryReadInt32(out var socketLength) || socketLength < 0 || !reader.TryReadBytes(socketLength, out var socketBytes)
                || !reader.TryReadInt32(out var colorBlockLength) || colorBlockLength < 4
                || !reader.TryReadUInt16(out var color1) || !reader.TryReadUInt16(out var color2))
            {
                diagnostics.Add($"USERINFO equipment entry {index} avatar block is truncated");
                return null;
            }
            result["avatarSocketLength"] = socketLength;
            result["avatarSocketHex"] = Convert.ToHexString(socketBytes);
            result["avatarColorBlockLength"] = colorBlockLength;
            result["avatarColor1"] = color1;
            result["avatarColor2"] = color2;
            if (colorBlockLength > 4 && !reader.TryReadBytes(colorBlockLength - 4, out _))
            {
                diagnostics.Add($"USERINFO equipment entry {index} avatar color extension is truncated");
                return null;
            }
        }
        if (slot == 24)
        {
            if (!reader.TryReadUInt32(out var creatureMarker))
            {
                diagnostics.Add($"USERINFO equipment entry {index} creature marker is truncated");
                return null;
            }
            result["creatureMarker"] = creatureMarker;
        }

        if (!reader.TryReadByte(out var chronicleCount))
        {
            diagnostics.Add($"USERINFO equipment entry {index} chronicle count is truncated");
            return null;
        }
        var chronicles = new List<object>();
        for (var option = 0; option < chronicleCount; option++)
        {
            if (!reader.TryReadInt32(out var optionId) || !reader.TryReadByte(out var job)
                || !reader.TryReadByte(out var growType) || !reader.TryReadByte(out var equipmentType)
                || !reader.TryReadByte(out var optionNo))
            {
                diagnostics.Add($"USERINFO equipment entry {index} chronicle option {option} is truncated");
                return null;
            }
            chronicles.Add(new { optionId, job, growType, equipmentType, optionNo });
        }
        result["chronicleOptions"] = chronicles;
        if (!reader.TryReadInt32(out var expireTime) || !reader.TryReadByte(out var emblemCount))
        {
            diagnostics.Add($"USERINFO equipment entry {index} expiry/emblem header is truncated");
            return null;
        }
        result["expireTime"] = expireTime;
        var emblems = new List<int>();
        for (var emblem = 0; emblem < emblemCount; emblem++)
        {
            if (!reader.TryReadInt32(out var emblemId))
            {
                diagnostics.Add($"USERINFO equipment entry {index} emblem {emblem} is truncated");
                return null;
            }
            emblems.Add(emblemId);
        }
        result["emblemIds"] = emblems;
        if (!reader.TryReadUInt16(out var rune) || !reader.TryReadByte(out var randomCount))
        {
            diagnostics.Add($"USERINFO equipment entry {index} rune/random header is truncated");
            return null;
        }
        result["rune"] = rune;
        var randomOptions = new List<object>();
        for (var random = 0; random < randomCount; random++)
        {
            if (!reader.TryReadByte(out var type) || !reader.TryReadByte(out var value1) || !reader.TryReadByte(out var value2))
            {
                diagnostics.Add($"USERINFO equipment entry {index} random option {random} is truncated");
                return null;
            }
            randomOptions.Add(new { type, value1, value2 });
        }
        result["randomOptions"] = randomOptions;
        if (randomCount > 0)
        {
            if (!reader.TryReadByte(out var randomState) || !reader.TryReadByte(out var changedIndex))
            {
                diagnostics.Add($"USERINFO equipment entry {index} random option state is truncated");
                return null;
            }
            result["randomOptionState"] = randomState;
            result["randomOptionChangedIndex"] = changedIndex;
            if (changedIndex != 0xFF)
            {
                if (!reader.TryReadByte(out var changeState) || !reader.TryReadByte(out var changeType)
                    || !reader.TryReadByte(out var changeValue1) || !reader.TryReadByte(out var changeValue2))
                {
                    diagnostics.Add($"USERINFO equipment entry {index} random change record is truncated");
                    return null;
                }
                result["randomOptionChange"] = new { changeState, changeType, changeValue1, changeValue2 };
            }
        }
        if (!reader.TryReadByte(out var genuineUpgrade) || !reader.TryReadByte(out var emancipateLevel)
            || !reader.TryReadByte(out var tradeRestriction) || !reader.TryReadUInt16(out var tailUnknown0)
            || !reader.TryReadByte(out var tailUnknown1) || !reader.TryReadByte(out var tailUnknown2)
            || !reader.TryReadByte(out var tailUnknown3) || !reader.TryReadByte(out var remainUseCount)
            || !reader.TryReadByte(out var tailReserved))
        {
            diagnostics.Add($"USERINFO equipment entry {index} tail is truncated");
            return null;
        }
        result["genuineUpgrade"] = genuineUpgrade;
        result["emancipateEquipmentLevel"] = emancipateLevel;
        result["tradeRestriction"] = tradeRestriction;
        result["tailUnknown0"] = tailUnknown0;
        result["tailUnknown1"] = tailUnknown1;
        result["tailUnknown2"] = tailUnknown2;
        result["tailUnknown3"] = tailUnknown3;
        result["remainUseCount"] = remainUseCount;
        result["tailReserved"] = tailReserved;
        return result;
    }

    private static object DecodeUserInfoSkillPage(PacketReader reader, List<string> diagnostics, int pageIndex)
    {
        if (!reader.TryReadByte(out var count))
        {
            diagnostics.Add($"USERINFO skill page {pageIndex} count is truncated");
            return new { pageIndex, count = 0, entries = Array.Empty<object>() };
        }
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadUInt16(out var skillId) || !reader.TryReadByte(out var level))
            {
                diagnostics.Add($"USERINFO skill page {pageIndex} entry {index} is truncated");
                break;
            }
            entries.Add(new { skillId, level });
        }
        return new { pageIndex, count, entries };
    }

    private static object[] DecodeUserInfoDimensions(PacketReader reader, List<string> diagnostics)
    {
        if (!reader.TryReadByte(out var count))
        {
            diagnostics.Add("USERINFO dimension count is truncated");
            return Array.Empty<object>();
        }
        var values = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadUInt32(out var key) || !reader.TryReadByte(out var value1) || !reader.TryReadByte(out var value2))
            {
                diagnostics.Add($"USERINFO dimension {index} is truncated");
                break;
            }
            values.Add(new { key, value1, value2 });
        }
        return values.ToArray();
    }

    private static object[] DecodeUserInfoPvpResults(PacketReader reader, List<string> diagnostics)
    {
        if (!reader.TryReadByte(out var count))
        {
            diagnostics.Add("USERINFO PvP result count is truncated");
            return Array.Empty<object>();
        }
        var values = new List<object>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadUInt32(out var value32) || !reader.TryReadUInt16(out var value16A) || !reader.TryReadUInt16(out var value16B))
            {
                diagnostics.Add($"USERINFO PvP result {index} is truncated");
                break;
            }
            values.Add(new { value32, value16A, value16B });
        }
        return values.ToArray();
    }

    private static uint[] DecodeCountedUInt32(PacketReader reader, List<string> diagnostics, string semantic)
    {
        if (!reader.TryReadUInt32(out var rawCount))
        {
            diagnostics.Add($"USERINFO {semantic} count is truncated");
            return Array.Empty<uint>();
        }
        var available = reader.Remaining / 4;
        var count = rawCount > int.MaxValue ? int.MaxValue : (int)rawCount;
        if (count > available)
        {
            diagnostics.Add($"USERINFO {semantic} count {rawCount} exceeds remaining payload capacity {available}");
            count = available;
        }
        var values = new uint[count];
        for (var index = 0; index < count; index++) reader.TryReadUInt32(out values[index]);
        return values;
    }

    private static Dictionary<string, object?>? DecodeUserInfoRosterEntry(PacketReader reader, List<string> diagnostics, int index)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!reader.TryReadUInt16(out var slotIndex) || !reader.TryReadDString(Encoding.UTF8, out var name)
            || !reader.TryReadByte(out var reserved0) || !reader.TryReadByte(out var reserved1)
            || !reader.TryReadByte(out var job) || !reader.TryReadByte(out var growType) || !reader.TryReadByte(out var level)
            || !reader.TryReadUInt32(out var honorLevel) || !reader.TryReadUInt32(out var honorExperience)
            || !reader.TryReadUInt16(out var honorReserved) || !reader.TryReadByte(out var appearanceCount))
        {
            diagnostics.Add($"USERINFO subtype 2 character {index} header is truncated");
            return null;
        }
        result["slotIndex"] = slotIndex;
        result["name"] = name;
        result["reserved0"] = reserved0;
        result["reserved1"] = reserved1;
        result["job"] = job;
        result["growType"] = growType;
        result["level"] = level;
        result["honorLevel"] = honorLevel;
        result["honorExperience"] = honorExperience;
        result["honorReserved"] = honorReserved;
        result["appearanceCount"] = appearanceCount;
        var appearances = new List<object>();
        for (var appearanceIndex = 0; appearanceIndex < appearanceCount; appearanceIndex++)
        {
            if (!reader.TryReadByte(out var appearanceSlot) || !reader.TryReadInt32(out var displayItemId)
                || !reader.TryReadInt32(out var expansionLength) || !reader.TryReadBytes(4, out var expansionData)
                || !reader.TryReadByte(out var state) || !reader.TryReadInt32(out var linkItemId)
                || !reader.TryReadUInt32(out var enchantValue) || !reader.TryReadByte(out var flag20))
            {
                diagnostics.Add($"USERINFO subtype 2 character {index} appearance {appearanceIndex} is truncated");
                return null;
            }
            appearances.Add(new { slot = appearanceSlot, displayItemId, expansionLength, expansionDataHex = Convert.ToHexString(expansionData), state, linkItemId, enchantValue, flag20 });
        }
        result["appearances"] = appearances;
        if (!reader.TryReadUInt32(out var cloneTitleItemId) || !reader.TryReadBytes(4, out var tailBytes4)
            || !reader.TryReadUInt32(out var nameTagItemId) || !reader.TryReadUInt32(out var nameTagExpireTime)
            || !reader.TryReadByte(out var stamina) || !reader.TryReadUInt32(out var fatiguePenalty)
            || !reader.TryReadByte(out var tailFlag21) || !reader.TryReadByte(out var tailFlag22)
            || !reader.TryReadByte(out var tailByte23) || !reader.TryReadByte(out var displayStateBits)
            || !reader.TryReadByte(out var tailFlag25) || !reader.TryReadByte(out var tailByte26)
            || !reader.TryReadByte(out var tailByte27) || !reader.TryReadByte(out var tailFlag28)
            || !reader.TryReadByte(out var tailFlag29) || !reader.TryReadByte(out var tailFlag30)
            || !reader.TryReadByte(out var tailFlag31))
        {
            diagnostics.Add($"USERINFO subtype 2 character {index} fixed tail is truncated");
            return null;
        }
        result["cloneTitleItemId"] = cloneTitleItemId;
        result["reservedTailBytes0To3Hex"] = Convert.ToHexString(tailBytes4);
        result["nameTagItemId"] = nameTagItemId;
        result["nameTagExpireTime"] = nameTagExpireTime;
        result["stamina"] = stamina;
        result["fatiguePenalty"] = fatiguePenalty;
        result["tailFlag21"] = tailFlag21;
        result["tailFlag22"] = tailFlag22;
        result["tailByte23"] = tailByte23;
        result["displayStateBits"] = displayStateBits;
        result["tailFlag25"] = tailFlag25;
        result["tailByte26"] = tailByte26;
        result["tailByte27"] = tailByte27;
        result["tailFlag28"] = tailFlag28;
        result["tailFlag29"] = tailFlag29;
        result["tailFlag30"] = tailFlag30;
        result["tailFlag31"] = tailFlag31;
        return result;
    }

    private static void ReadByteField(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name)
    {
        if (reader.TryReadByte(out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:u8 is truncated");
    }

    private static void ReadUInt16Field(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name)
    {
        if (reader.TryReadUInt16(out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:u16 is truncated");
    }

    private static void ReadInt16Field(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name)
    {
        if (reader.TryReadInt16(out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:i16 is truncated");
    }

    private static void ReadUInt32Field(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name)
    {
        if (reader.TryReadUInt32(out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:u32 is truncated");
    }

    private static void ReadInt32Field(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name)
    {
        if (reader.TryReadInt32(out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:i32 is truncated");
    }

    private static void ReadDStringField(PacketReader reader, IDictionary<string, object?> fields, List<string> diagnostics, string name, Encoding encoding)
    {
        if (reader.TryReadDString(encoding, out var value)) fields[name] = value;
        else diagnostics.Add($"USERINFO field {name}:dstr is truncated or malformed");
    }

    private static DecodedBody DecodeCommandResponse(PacketTypeDefinition definition, byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string? requestedVariant)
    {
        if (body.Length == 0) return new DecodedBody("empty-response", fields);

        var manual = definition.EnumName switch
        {
            "CARD_SELECT_RIGHT_STATE" => DecodeCardLayoutResponse(body, fields, diagnostics),
            "TOURNAMENT_REWARD_SELECT" => DecodeTournamentRewardSelection(body, fields, diagnostics),
            "SET_CLONE_TITLE" => DecodeCloneTitleResponse(body, fields, diagnostics),
            "BUY_CERASHOP_ITEM" => DecodeCeraShopPurchaseResponse(body, fields, diagnostics),
            "PREMIUM_SERVICE" => DecodePremiumServiceResponse(body, fields, diagnostics),
            "SAVE_GAME_OPTION_1" => DecodeRentalCatalogResponse(body, fields, diagnostics),
            "SELECT_CARD" => DecodeCardInfoResponse(body, fields, diagnostics),
            "CHANGE_TUTORIAL_FLAG" => DecodeTutorialRewardResponse(body, fields, diagnostics),
            "SUMMON_MONSTER" => DecodeSummonMonsterResponse(body, fields, diagnostics),
            "QUERY_CHARAC_INFO_MAILBOX" => DecodeMailboxCharacterQueryResponse(body, fields, diagnostics),
            "SKILL_COMMAND_CUSTOMIZING" => DecodeSkillCommandEchoResponse(body, fields, diagnostics),
            "GET_EXPAND_EXP_GAGE_REWARD" => DecodeGrowthCapsuleClaimResponse(body, fields, diagnostics),
            "TOURNAMENT_REWARD_SELECT_STATE" => DecodeTournamentSelectionRights(body, fields, diagnostics),
            "SELECT_CHARACTER" => DecodeSelectCharacterResponse(body, fields, diagnostics),
            "BUY_ITEM" => DecodeBuyItemResponse(body, fields, diagnostics),
            "INVEST_ITEM_AMPLIFY_OPTION" => DecodeInvestAmplifyResponse(body, fields, diagnostics),
            "COMPOUND_ITEM" => DecodeCompoundItemResponse(body, fields, diagnostics),
            "RESET_ITEM_ATTR" => DecodeResetItemAttributeResponse(body, fields, diagnostics),
            "SECRET_SHOP_BUY_ITEM" => DecodeSecretShopBuyResponse(body, fields, diagnostics),
            "USE_STACKABLE" => DecodeUseStackableResponse(body, fields, diagnostics),
            "USE_LOTTERY_ITEM" => DecodeLotteryItemResponse(body, fields, diagnostics),
            "UPGRADE_CHRONICLE" => DecodeChronicleGrowthResponse(body, fields, diagnostics),
            "CHARGE_RENTPOINT" => DecodeChargeRentPointResponse(body, fields, diagnostics),
            "MOVE_ITEMSPACE" => DecodeMoveItemSpaceResponse(body, fields, diagnostics),
            "CRANE_START_USE" => DecodeCraneStartResponse(body, fields, diagnostics),
            "DISJOINT_ITEM" => DecodeDisjointItemResponse(body, fields, diagnostics),
            "USE_BOOSTER_ITEM" => DecodeSelectablePackageResponse(body, fields, diagnostics),
            "BIND_PLUS" => DecodeAvatarCompoundSetResponse(body, fields, diagnostics),
            "REQUEST_CHARAC_SKILL_INFO" => DecodeCharacterSkillListResponse(body, fields, diagnostics),
            "UPGRADE_ITEM" => DecodeItemUpgradeResponse(body, fields, diagnostics),
            "REPAIR_EQUIPMENT" => DecodeRepairEquipmentResponse(body, fields, diagnostics),
            "USE_RANDOMBOX_ITEM_EXPAND" => DecodeMagicBoxResponse(body, fields, diagnostics, batch: true),
            "ENCHANT_3RD_CHRONICLE_ITEM" => DecodeChronicleRefineResponse(body, fields, diagnostics),
            "COMPOUND_AVATAR" => DecodeAvatarCompoundResponse(body, fields, diagnostics),
            "DELETE_ITEM" => DecodeDeleteItemResponse(body, fields, diagnostics),
            "USE_RANDOMBOX_ITEM" => DecodeMagicBoxResponse(body, fields, diagnostics, batch: false),
            "DISJOINT_AVATAR" => DecodeAvatarDisjointResponse(body, fields, diagnostics),
            "REQUEST_DISJOINT_ITEM" => DecodeExpertDisjointResponse(body, fields, diagnostics),
            "REPAIR_DISJOINT_MACHINE" or "REPAIR_EXPERT_JOB_STORE" => DecodeExpertRepairResponse(body, fields, diagnostics),
            "UPGRADE_DISJOINT_MACHINE" => DecodeExpertUpgradeResponse(body, fields, diagnostics),
            "USE_ENCHANT_STORE" => DecodeExpertEnchantResponse(body, fields, diagnostics),
            "COMPOUND_ITEM_BY_EXPERT_JOB" => DecodeExpertCompoundResponse(body, fields, diagnostics),
            "GIVEUP_EXPERT_JOB" => DecodeExpertGiveupResponse(body, fields, diagnostics),
            "CREATE_EXPERT_JOB_STORE" => DecodeStatusAckResponse(body, fields, diagnostics, "create-store"),
            "ENTER_EXPERT_JOB_STORE" => DecodeExpertEnterResponse(body, fields, diagnostics),
            "ENTER_PVP_ROOM" => DecodePvpEnterResponse(body, fields, diagnostics),
            "DAILY_CHALLENGE_REWARD" => DecodeDailyChallengeRewardResponse(body, fields, diagnostics),
            _ => null,
        };
        if (manual is not null) return manual;

        // Outbound builders are intentionally kept as independent variants. For the
        // common command acknowledgements, body[0] is a status byte; for richer
        // builders the status/length combination identifies a schema below.
        var variants = definition.Variants;
        if (definition.EnumName == "BUY_SKILL" && body[0] == 1 && body.Length >= 7)
        {
            fields["status"] = body[0];
            fields["success"] = true;
            fields["skillTree"] = body[1];
            fields["remainSp"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
            fields["remainTp"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
            var count = body.Length >= 7 ? body[6] : 0;
            fields["entryCount"] = count;
            var entries = new List<object>();
            var offset = 7;
            for (var index = 0; index < count && offset + 5 <= body.Length; index++, offset += 5)
            {
                entries.Add(new
                {
                    slot = body[offset],
                    skillId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset + 1, 2)),
                    level = body[offset + 3],
                    hasCommand = body[offset + 4] != 0,
                });
            }
            fields["entries"] = entries;
            if (offset != body.Length) diagnostics.Add($"BUY_SKILL success body has {body.Length - offset} trailing bytes");
            return new DecodedBody("buy-skill-success", fields);
        }

        if (definition.EnumName == "BUY_SKILL" && body[0] == 0)
        {
            if (body.Length != 2) diagnostics.Add($"BUY_SKILL error response expects 2 bytes, got {body.Length}");
            fields["status"] = body[0];
            if (body.Length >= 2) fields["errorCode"] = body[1];
            return new DecodedBody("buy-skill-error", fields);
        }

        if (definition.EnumName == "CERA" && body.Length >= 13 && body[0] == 1)
        {
            fields["status"] = body[0];
            fields["success"] = true;
            fields["cera"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1, 4));
            fields["tokenCera"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5, 4));
            fields["happyTokenCera"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(9, 4));
            return new DecodedBody("Build-success", fields);
        }

        var builderDecoded = DecodeOutboundVariants(definition, body, fields, diagnostics, requestedVariant);
        if (builderDecoded is not null) return builderDecoded;

        fields["status"] = body[0];
        if (body[0] == 0 && body.Length >= 2) fields["errorCode"] = body[1];
        if (body[0] == 1) fields["success"] = true;
        if (variants.Length > 0)
            fields["sourceVariants"] = variants.Select(variant => variant.Name).Distinct().ToArray();
        if (body.Length > 2) fields["payloadAfterStatusHex"] = Convert.ToHexString(body.AsSpan(1));
        return new DecodedBody(body[0] == 0 ? "error-response" : body[0] == 1 ? "success-response" : "response", fields);
    }

    private static DecodedBody DecodeCardLayoutResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 17) diagnostics.Add($"CARD_SELECT_RIGHT_STATE expects 17 bytes, got {body.Length}");
        if (body.Length > 0) fields["status"] = body[0];
        fields["cardRights"] = Enumerable.Range(0, Math.Min(8, Math.Max(0, (body.Length - 1) / 2)))
            .Select(index => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1 + index * 2, 2))).ToArray();
        return new DecodedBody("card-layout", fields);
    }

    private static DecodedBody DecodeTournamentRewardSelection(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var status)) diagnostics.Add("TOURNAMENT_REWARD_SELECT is missing status");
        fields["status"] = status;
        var cardTypes = new List<object>();
        for (var type = 0; type < 2; type++)
        {
            if (!reader.TryReadByte(out var count)) { diagnostics.Add($"tournament card type {type} count is truncated"); break; }
            var selections = new byte[Math.Min(count, reader.Remaining)];
            for (var index = 0; index < selections.Length; index++) reader.TryReadByte(out selections[index]);
            if (selections.Length != count) diagnostics.Add($"tournament card type {type} expects {count} selections, got {selections.Length}");
            cardTypes.Add(new { type, count, selections });
        }
        fields["cardTypes"] = cardTypes;
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail);
        return new DecodedBody("selection-state", fields);
    }

    private static DecodedBody DecodeCloneTitleResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 5) diagnostics.Add($"SET_CLONE_TITLE response expects 5 bytes, got {body.Length}");
        if (body.Length > 0) fields["status"] = body[0];
        if (body.Length >= 5) fields["cloneTitleItemId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1, 4));
        return new DecodedBody("clone-title-ack", fields);
    }

    private static DecodedBody DecodeCeraShopPurchaseResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 22) diagnostics.Add($"BUY_CERASHOP_ITEM response requires at least 22 bytes, got {body.Length}");
        if (body.Length == 0) return new DecodedBody("purchase-invalid", fields);
        fields["status"] = body[0];
        fields["success"] = body[0] == 1;
        if (body.Length >= 2) fields[body[0] == 0 ? "errorCode" : "resultOption"] = body[1];
        if (body.Length >= 22)
        {
            fields["category"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2, 4));
            fields["commodityNo"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(6, 4));
            fields["value0"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(10, 4));
            fields["value1"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            fields["value2"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18, 4));
        }
        if (body[0] == 0)
        {
            if (body.Length != 22) diagnostics.Add($"BUY_CERASHOP_ITEM error response expects 22 bytes, got {body.Length}");
            return new DecodedBody("purchase-error", fields);
        }
        if (body.Length < 24) return new DecodedBody("purchase-success", fields);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(22, 2));
        fields["extraCount"] = count;
        var entries = new List<object>();
        var offset = 24;
        for (var index = 0; index < count && offset + 8 <= body.Length; index++, offset += 8)
            entries.Add(new { itemId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4)), value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4, 4)) });
        fields["extraItems"] = entries;
        if (entries.Count != count) diagnostics.Add($"BUY_CERASHOP_ITEM expects {count} extra entries, got {entries.Count}");
        if (offset < body.Length) fields["trailingHex"] = Convert.ToHexString(body.AsSpan(offset));
        return new DecodedBody("purchase-success", fields);
    }

    private static DecodedBody DecodePremiumServiceResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 77) diagnostics.Add($"PREMIUM_SERVICE response expects 77 bytes, got {body.Length}");
        if (body.Length > 0) fields["status"] = body[0];
        if (body.Length >= 3) fields["serviceType"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1, 2));
        if (body.Length >= 77)
        {
            fields["serviceDataHex"] = Convert.ToHexString(body.AsSpan(3, 74));
            fields["contractSlots"] = Enumerable.Range(0, 8).Select(slot => new
            {
                slot,
                premiumType = 580 + slot,
                expireTime = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3 + 6 + slot * 9, 4)),
                usedCount = 10 + slot * 9 + 4 <= 74
                    ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3 + 10 + slot * 9, 4))
                    : (int?)null,
            }).ToArray();
        }
        return new DecodedBody("premium-service-state", fields);
    }

    private static DecodedBody DecodeRentalCatalogResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 138) diagnostics.Add($"SAVE_GAME_OPTION_1 rental catalog expects 138 bytes, got {body.Length}");
        if (body.Length < 4) return new DecodedBody("rental-catalog", fields);
        var length = BinaryPrimitives.ReadInt32LittleEndian(body);
        fields["catalogLength"] = length;
        if (length != 134) diagnostics.Add($"rental catalog length prefix is {length}, expected 134");
        if (body.Length >= 138)
        {
            fields["luckyStar"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14, 2));
            fields["purchaseMarker"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(40, 2));
            fields["purchaseCount"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(120, 2));
            fields["catalogHex"] = Convert.ToHexString(body.AsSpan(4, 134));
        }
        return new DecodedBody("rental-catalog", fields);
    }

    private static DecodedBody DecodeCardInfoResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 33) diagnostics.Add($"SELECT_CARD response requires at least 33 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var status)) return new DecodedBody("card-info-standard", fields);
        fields["status"] = status;
        var records = new List<object>();
        for (var index = 0; index < 8; index++)
        {
            if (index >= 4)
            {
                if (!reader.TryReadBytes(4, out var reserved)) { diagnostics.Add($"card record {index} is truncated"); break; }
                records.Add(new { index, reservedHex = Convert.ToHexString(reserved) });
                continue;
            }
            if (index > 0)
            {
                if (!reader.TryReadBytes(4, out var reserved)) { diagnostics.Add($"card record {index} is truncated"); break; }
                records.Add(new { index, reservedHex = Convert.ToHexString(reserved) });
                continue;
            }
            if (!reader.TryReadByte(out var freeState) || !reader.TryReadByte(out var paidState) || !reader.TryReadByte(out var rewardCount))
            {
                diagnostics.Add("card record 0 header is truncated");
                break;
            }
            object? paidReward = null;
            if (paidState == 0)
            {
                if (!reader.TryReadUInt32(out var reservedValue) || !reader.TryReadInt32(out var gold)
                    || !reader.TryReadUInt32(out var itemId) || !reader.TryReadInt32(out var itemCount))
                {
                    diagnostics.Add("paid card reward details are truncated");
                    break;
                }
                paidReward = new { reservedValue, gold, itemId, itemCount };
            }
            if (!reader.TryReadByte(out var tail)) { diagnostics.Add("card record 0 tail is truncated"); break; }
            records.Add(new { index, freeState, paidState, rewardCount, paidReward, tail });
        }
        fields["records"] = records;
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var extra)) fields["trailingHex"] = Convert.ToHexString(extra);
        return new DecodedBody("card-info-standard", fields);
    }

    private static DecodedBody DecodeTutorialRewardResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 2) { diagnostics.Add("CHANGE_TUTORIAL_FLAG response requires status and count"); return new DecodedBody("tutorial-reward-ack", fields); }
        fields["status"] = body[0];
        var count = body[1];
        fields["rewardCount"] = count;
        if (body.Length != 2 + count * 10) diagnostics.Add($"tutorial reward response expects {2 + count * 10} bytes, got {body.Length}");
        fields["rewards"] = Enumerable.Range(0, Math.Min(count, Math.Max(0, (body.Length - 2) / 10))).Select(index =>
        {
            var offset = 2 + index * 10;
            return new
            {
                slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)),
                itemId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 2, 4)),
                count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 6, 4)),
            };
        }).ToArray();
        return new DecodedBody("tutorial-reward-ack", fields);
    }

    private static DecodedBody DecodeSummonMonsterResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 15) diagnostics.Add($"SUMMON_MONSTER response expects 15 bytes, got {body.Length}");
        ReadSchema(body, fields,
            ("result", "u8", 0), ("state", "i32", 1), ("count", "u8", 5),
            ("runtimeKey", "u16", 6), ("monsterCode", "i32", 8), ("mode", "u8", 12), ("parameter", "u16", 13));
        return new DecodedBody("summon-create-response", fields);
    }

    private static DecodedBody DecodeMailboxCharacterQueryResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var status)) { diagnostics.Add("QUERY_CHARAC_INFO_MAILBOX response is empty"); return new DecodedBody("query-invalid", fields); }
        fields["status"] = status;
        if (status == 0)
        {
            if (!reader.TryReadByte(out var errorCode)) diagnostics.Add("mailbox character query error code is truncated");
            else fields["errorCode"] = errorCode;
            if (body.Length != 2) diagnostics.Add($"mailbox character query error expects 2 bytes, got {body.Length}");
            return new DecodedBody("query-error", fields);
        }
        if (!reader.TryReadDString(Encoding.UTF8, out var name) || !reader.TryReadUInt16(out var level)
            || !reader.TryReadByte(out var job) || !reader.TryReadByte(out var growType) || !reader.TryReadByte(out var reserved))
            diagnostics.Add("mailbox character query success body is truncated");
        else
        {
            fields["name"] = name; fields["level"] = level; fields["job"] = job;
            fields["growType"] = growType; fields["reserved"] = reserved;
        }
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail);
        return new DecodedBody("query-success", fields);
    }

    private static DecodedBody DecodeSkillCommandEchoResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 5) diagnostics.Add("SKILL_COMMAND_CUSTOMIZING response requires status plus request record body");
        if (body.Length == 0) return new DecodedBody("command-record-echo", fields);
        fields["status"] = body[0];
        var recordsBody = body.AsSpan(1).ToArray();
        fields["page"] = recordsBody.Length > 0 && recordsBody[0] == 1 ? 1 : 0;
        var records = new List<object>();
        var offset = 0;
        var first = true;
        while (offset < recordsBody.Length)
        {
            var headerSize = first ? 4 : 3;
            if (offset + headerSize > recordsBody.Length) { diagnostics.Add("skill command record header is truncated"); break; }
            var skillId = first ? recordsBody[offset + 1] : recordsBody[offset];
            var lengthOffset = first ? offset + 2 : offset + 1;
            var length = (recordsBody[lengthOffset] << 8) | recordsBody[lengthOffset + 1];
            offset += headerSize;
            if (offset + length > recordsBody.Length) { diagnostics.Add("skill command bytes are truncated"); break; }
            records.Add(new { skillId, commandHex = Convert.ToHexString(recordsBody.AsSpan(offset, length)) });
            offset += length;
            first = false;
        }
        fields["records"] = records;
        fields["consumedBytes"] = 1 + offset;
        return new DecodedBody("command-record-echo", fields);
    }

    private static DecodedBody DecodeGrowthCapsuleClaimResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 0) { diagnostics.Add("growth capsule response is empty"); return new DecodedBody("claim-invalid", fields); }
        fields["result"] = body[0];
        fields["success"] = body[0] == 0;
        if (body[0] != 0)
        {
            if (body.Length != 1) diagnostics.Add($"growth capsule failure expects 1 byte, got {body.Length}");
            return new DecodedBody("claim-failure", fields);
        }
        if (body.Length != 13) diagnostics.Add($"growth capsule success expects 13 bytes, got {body.Length}");
        if (body.Length >= 13)
        {
            fields["reserved"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1, 4));
            fields["itemId"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
            fields["itemCount"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(9, 4));
        }
        return new DecodedBody("claim-success", fields);
    }

    private static DecodedBody DecodeTournamentSelectionRights(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 9) diagnostics.Add($"TOURNAMENT_REWARD_SELECT_STATE expects 9 bytes, got {body.Length}");
        if (body.Length > 0) fields["status"] = body[0];
        if (body.Length >= 9)
        {
            fields["cardTypes"] = Enumerable.Range(0, 2).Select(type => new
            {
                type,
                partySlots = body.AsSpan(1 + type * 4, 4).ToArray(),
            }).ToArray();
        }
        return new DecodedBody("selection-rights", fields);
    }

    private static DecodedBody DecodeSelectCharacterResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadByte(out var status)) { diagnostics.Add("SELECT_CHARACTER response is empty"); return new DecodedBody("select-invalid", fields); }
        fields["status"] = status;
        if (status == 0)
        {
            if (reader.TryReadByte(out var errorCode)) fields["errorCode"] = errorCode;
            else diagnostics.Add("SELECT_CHARACTER error code is truncated");
            if (body.Length != 2) diagnostics.Add($"SELECT_CHARACTER error expects 2 bytes, got {body.Length}");
            return new DecodedBody("select-error", fields);
        }
        ReadInt32Field(reader, fields, diagnostics, "accountRegistrationTime");
        ReadInt32Field(reader, fields, diagnostics, "characterCreatedTime");
        ReadInt16Field(reader, fields, diagnostics, "uniqueId");
        ReadInt16Field(reader, fields, diagnostics, "totalFatigue");
        ReadInt16Field(reader, fields, diagnostics, "fatigueLimit");
        ReadInt16Field(reader, fields, diagnostics, "usedFatigue");
        if (!reader.TryReadByte(out var premiumCount)) { diagnostics.Add("SELECT_CHARACTER premium count is truncated"); return new DecodedBody("select-success", fields); }
        fields["premiumCount"] = premiumCount;
        var premiums = new List<object>();
        for (var index = 0; index < premiumCount; index++)
        {
            if (!reader.TryReadByte(out var type) || !reader.TryReadBytes(8, out var endTime)) { diagnostics.Add($"SELECT_CHARACTER premium {index} is truncated"); break; }
            premiums.Add(new { type, endTimeHex = Convert.ToHexString(endTime) });
        }
        fields["premiums"] = premiums;
        ReadInt32Field(reader, fields, diagnostics, "cera");
        var quests = new List<object>();
        for (var index = 0; index < 30; index++)
        {
            if (!reader.TryReadUInt16(out var questId) || !reader.TryReadUInt32(out var triggerValue)) { diagnostics.Add($"SELECT_CHARACTER quest slot {index} is truncated"); break; }
            quests.Add(new { slot = index, questId, triggerValue });
        }
        fields["activeQuestSlots"] = quests;
        var notifyIds = new List<int>();
        for (var index = 0; index < 4; index++)
        {
            if (!reader.TryReadInt32(out var value)) { diagnostics.Add($"SELECT_CHARACTER quest notify slot {index} is truncated"); break; }
            notifyIds.Add(value);
        }
        fields["questNotifyIds"] = notifyIds;
        ReadByteField(reader, fields, diagnostics, "characterSlotIndex");
        ReadByteField(reader, fields, diagnostics, "tutorialFlag");
        if (!reader.TryReadByte(out var tutorialCount)) diagnostics.Add("SELECT_CHARACTER tutorial flag count is truncated");
        else
        {
            fields["tutorialFlagCount"] = tutorialCount;
            var flags = new byte[Math.Min(tutorialCount, reader.Remaining)];
            for (var index = 0; index < flags.Length; index++) reader.TryReadByte(out flags[index]);
            fields["tutorialFlagIndexes"] = flags;
            if (flags.Length != tutorialCount) diagnostics.Add($"SELECT_CHARACTER expects {tutorialCount} tutorial flags, got {flags.Length}");
        }
        ReadUInt16Field(reader, fields, diagnostics, "fatigueBattery");
        ReadUInt16Field(reader, fields, diagnostics, "fatigueGrownUpBuff");
        ReadByteField(reader, fields, diagnostics, "tradePunishFlag");
        ReadUInt16Field(reader, fields, diagnostics, "extraField86Jp");
        if (reader.TryReadBytes(8, out var reserved)) fields["reserved8Hex"] = Convert.ToHexString(reserved); else diagnostics.Add("SELECT_CHARACTER reserved8 is truncated");
        ReadByteField(reader, fields, diagnostics, "tutorialSkippable");
        ReadUInt16Field(reader, fields, diagnostics, "postTutorialValue");
        if (reader.TryReadBytes(22, out var tail)) fields["reservedTailHex"] = Convert.ToHexString(tail); else diagnostics.Add("SELECT_CHARACTER reserved tail is truncated");
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var extra)) fields["trailingHex"] = Convert.ToHexString(extra);
        return new DecodedBody("select-success", fields);
    }

    private static DecodedBody DecodeBuyItemResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "buy-error" };
        var reader = new PacketReader(body);
        reader.TryReadByte(out var status); fields["status"] = status;
        ReadInt32Field(reader, fields, diagnostics, "updatedGold");
        ReadInt32Field(reader, fields, diagnostics, "updatedSp");
        ReadInt32Field(reader, fields, diagnostics, "reservedCurrency");
        ReadInt32Field(reader, fields, diagnostics, "updatedCoin");
        ReadInt16Field(reader, fields, diagnostics, "slotIndex");
        ReadInt32Field(reader, fields, diagnostics, "itemTemplateId");
        ReadInt32Field(reader, fields, diagnostics, "instanceValue");
        ReadUInt16Field(reader, fields, diagnostics, "durability");
        ReadByteField(reader, fields, diagnostics, "attr");
        ReadUInt16Field(reader, fields, diagnostics, "reservedItem16");
        ReadInt32Field(reader, fields, diagnostics, "expireTime");
        if (reader.TryReadBytes(11, out var reserved)) fields["reservedItemTailHex"] = Convert.ToHexString(reserved); else diagnostics.Add("BUY_ITEM reserved item tail is truncated");
        if (!reader.TryReadByte(out var count)) { diagnostics.Add("BUY_ITEM cost item count is truncated"); return new DecodedBody("buy-success", fields); }
        fields["costItemCount"] = count;
        fields["costItems"] = DecodeInt32PairList(reader, count, diagnostics, "BUY_ITEM cost item", "itemTemplateId", "newStackCount");
        FinishReader(reader, fields);
        return new DecodedBody("buy-success", fields);
    }

    private static DecodedBody DecodeInvestAmplifyResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "amplify-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadByteField(reader, fields, diagnostics, "action");
        ReadInt16Field(reader, fields, diagnostics, "materialSlotIndex");
        ReadInt32Field(reader, fields, diagnostics, "materialRemainingCount");
        ReadInt16Field(reader, fields, diagnostics, "targetSlotIndex");
        ReadByteField(reader, fields, diagnostics, "amplifyType");
        ReadUInt16Field(reader, fields, diagnostics, "amplifyValue");
        if (fields.TryGetValue("action", out var action) && Convert.ToByte(action) == 2)
            ReadByteField(reader, fields, diagnostics, "amplifyLevel");
        FinishReader(reader, fields);
        return new DecodedBody("amplify-success", fields);
    }

    private static DecodedBody DecodeCompoundItemResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "compound-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        if (!reader.TryReadByte(out var deletedCount)) { diagnostics.Add("COMPOUND_ITEM deleted count is truncated"); return new DecodedBody("compound-success", fields); }
        fields["deletedEntries"] = DecodeSlotCountEntries(reader, deletedCount, diagnostics, includeListType: true, "COMPOUND_ITEM deleted");
        if (!reader.TryReadByte(out var rewardCount)) { diagnostics.Add("COMPOUND_ITEM reward count is truncated"); return new DecodedBody("compound-success", fields); }
        var rewards = new List<object>();
        for (var index = 0; index < rewardCount; index++)
        {
            if (!reader.TryReadByte(out var listType) || !reader.TryReadInt16(out var slotIndex)
                || !reader.TryReadInt32(out var itemTemplateId) || !reader.TryReadInt32(out var count)
                || !reader.TryReadBytes(21, out var reserved))
            { diagnostics.Add($"COMPOUND_ITEM reward {index} is truncated"); break; }
            rewards.Add(new { listType, slotIndex, itemTemplateId, count, reservedHex = Convert.ToHexString(reserved) });
        }
        fields["rewards"] = rewards;
        FinishReader(reader, fields);
        return new DecodedBody("compound-success", fields);
    }

    private static DecodedBody DecodeResetItemAttributeResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 12)
        {
            fields["targetSlotIndex"] = BinaryPrimitives.ReadInt32LittleEndian(body);
            fields["targetItemId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4));
            fields["resultCode"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8, 4));
            return new DecodedBody("wax-reseal-result", fields);
        }
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "quality-reset-error" };
        if (body.Length != 10) diagnostics.Add($"RESET_ITEM_ATTR quality success expects 10 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("targetItemId", "i32", 1), ("listType", "u8", 5), ("targetSlotIndex", "i32", 6));
        return new DecodedBody("quality-reset-success", fields);
    }

    private static DecodedBody DecodeSecretShopBuyResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "secret-shop-error" };
        if (body.Length != 30) diagnostics.Add($"SECRET_SHOP_BUY_ITEM success expects 30 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("updatedGold", "i32", 1), ("assignedSlot", "u16", 5),
            ("itemId", "i32", 7), ("itemValue", "i32", 11), ("extData0", "u8", 15), ("durability", "u16", 16),
            ("requiredItemId", "i32", 18), ("costItemRemainingCount", "i32", 22), ("offerRemainingCount", "i32", 26));
        return new DecodedBody("secret-shop-success", fields);
    }

    private static DecodedBody DecodeUseStackableResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 12 && body[0] == 1)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("slotIndex", "i16", 1), ("listType", "u8", 3), ("instanceValue", "i32", 4), ("itemCode", "i32", 8));
            return new DecodedBody("stackable-success", fields);
        }
        if (body.Length == 11 && body[0] == 0)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("errorCode", "u8", 1), ("listType", "u8", 2), ("instanceValue", "i32", 3), ("itemCode", "i32", 7));
            return new DecodedBody(body[1] == 0 ? "practice-success" : "stackable-error", fields);
        }
        diagnostics.Add($"USE_STACKABLE response expects 12-byte success or 11-byte error/practice body, got {body.Length}");
        AddScalarPreview(fields, body);
        return new DecodedBody("stackable-unresolved", fields);
    }

    private static DecodedBody DecodeLotteryItemResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 13)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("sourceSlotIndex", "i16", 1), ("reserved", "u16", 3), ("previewItemId", "i32", 5), ("previewItemId2", "i32", 9));
            return new DecodedBody(body[0] == 0 ? "lottery-error" : "phase-start", fields);
        }
        if (body.Length is 22 or 52)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("sourceSlotIndex", "i16", 1), ("rewardSlotIndex", "i16", 3),
                ("itemId", "i32", 5), ("displayValue", "i32", 9), ("durability", "u16", 13), ("attr", "u8", 15),
                ("amplifyType", "u8", 16), ("amplifyValue", "u16", 17));
            var tailOffset = body.Length == 52 ? 49 : 19;
            if (body.Length == 52) fields["equipmentSocketExtensionHex"] = Convert.ToHexString(body.AsSpan(19, 30));
            fields["inventoryTailHex"] = Convert.ToHexString(body.AsSpan(tailOffset, 3));
            return new DecodedBody(body.Length == 52 ? "common-equipment-result" : "common-stackable-result", fields);
        }
        if (body.Length == 129)
        {
            fields["status"] = body[0]; fields["sourceSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1, 2));
            fields["avatarEntry126Hex"] = Convert.ToHexString(body.AsSpan(3, 126));
            return new DecodedBody("avatar-result", fields);
        }
        diagnostics.Add($"USE_LOTTERY_ITEM has unknown response length {body.Length}"); AddScalarPreview(fields, body);
        return new DecodedBody("lottery-unresolved", fields);
    }

    private static DecodedBody DecodeChronicleGrowthResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "chronicle-growth-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadByteField(reader, fields, diagnostics, "growthSucceeded");
        if (!reader.TryReadByte(out var count)) { diagnostics.Add("UPGRADE_CHRONICLE consumption count is truncated"); return new DecodedBody("chronicle-growth-success", fields); }
        fields["consumptions"] = DecodeSlotCountEntries(reader, count, diagnostics, true, "UPGRADE_CHRONICLE consumption");
        FinishReader(reader, fields);
        return new DecodedBody("chronicle-growth-success", fields);
    }

    private static DecodedBody DecodeChargeRentPointResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "rent-point-error" };
        if (body.Length < 22) diagnostics.Add($"CHARGE_RENTPOINT success expects at least 22 bytes, got {body.Length}");
        fields["status"] = body[0];
        if (body.Length >= 17) fields["totalLuckyStar"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(13, 4));
        if (body.Length >= 22) fields["changeCount"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18, 4));
        if (body.Length > 1) fields["requestEchoHex"] = Convert.ToHexString(body.AsSpan(1));
        return new DecodedBody("rent-point-success", fields);
    }

    private static DecodedBody DecodeMoveItemSpaceResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 4 && body[0] == 0)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("errorCode", "u8", 1), ("sourceListType", "u8", 2), ("destinationListType", "u8", 3));
            return new DecodedBody("move-error", fields);
        }
        if (body.Length != 11) diagnostics.Add($"MOVE_ITEMSPACE success expects 11 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("sourceListType", "u8", 1), ("sourceSlotIndex", "i16", 2),
            ("moveValue", "i32", 4), ("destinationListType", "u8", 8), ("destinationSlotIndex", "i16", 9));
        return new DecodedBody("move-success", fields);
    }

    private static DecodedBody DecodeCraneStartResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "crane-start-error" };
        if (body.Length != 31) diagnostics.Add($"CRANE_START_USE success expects 31 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("machineId", "u16", 1), ("materialRemainingCount", "u32", 3));
        fields["displayCatalogIndexes"] = Enumerable.Range(0, Math.Min(6, Math.Max(0, (body.Length - 7) / 4)))
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7 + index * 4, 4))).ToArray();
        return new DecodedBody("crane-start-success", fields);
    }

    private static DecodedBody DecodeDisjointItemResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "disjoint-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadInt16Field(reader, fields, diagnostics, "targetSlotIndex"); ReadByteField(reader, fields, diagnostics, "itemSpace");
        if (!reader.TryReadByte(out var count)) { diagnostics.Add("DISJOINT_ITEM material count is truncated"); return new DecodedBody("disjoint-success", fields); }
        fields["materials"] = DecodeReward10List(reader, count, diagnostics, "DISJOINT_ITEM material"); FinishReader(reader, fields);
        return new DecodedBody("disjoint-success", fields);
    }

    private static DecodedBody DecodeSelectablePackageResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 1) { fields["status"] = body[0]; return new DecodedBody(body[0] == 1 ? "success-ack" : "package-error-short", fields); }
        if (body[0] == 0) return DecodeSimpleErrorBody(body, fields, diagnostics, "package-error");
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadInt16Field(reader, fields, diagnostics, "sourceSlotIndex"); ReadInt32Field(reader, fields, diagnostics, "reserved0"); ReadInt32Field(reader, fields, diagnostics, "reserved1");
        if (!reader.TryReadUInt16(out var count)) { diagnostics.Add("USE_BOOSTER_ITEM granted item count is truncated"); return new DecodedBody("package-success", fields); }
        fields["grantedItems"] = DecodeInt32PairList(reader, count, diagnostics, "package grant", "itemTemplateId", "displayCount"); FinishReader(reader, fields);
        return new DecodedBody("package-success", fields);
    }

    private static DecodedBody DecodeAvatarCompoundSetResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body[0] == 0) return DecodeSimpleErrorBody(body, fields, diagnostics, "avatar-set-error");
        if (body.Length != 59) diagnostics.Add($"BIND_PLUS success expects 59 bytes, got {body.Length}");
        fields["status"] = body[0]; if (body.Length < 35) return new DecodedBody("avatar-set-success", fields);
        fields["headerHex"] = Convert.ToHexString(body.AsSpan(1, 8));
        fields["newSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(9, 2));
        fields["newItemId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(11, 4));
        fields["abilityNo"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(15, 2));
        fields["resultCount"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(17, 2));
        fields["consumedSlots"] = Enumerable.Range(0, 8).Select(index => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(19 + index * 2, 2))).ToArray();
        fields["reservedTailHex"] = Convert.ToHexString(body.AsSpan(35));
        return new DecodedBody("avatar-set-success", fields);
    }

    private static DecodedBody DecodeCharacterSkillListResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "skill-list-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadUInt16Field(reader, fields, diagnostics, "requestEcho"); ReadByteField(reader, fields, diagnostics, "reserved0"); ReadByteField(reader, fields, diagnostics, "reserved1");
        if (!reader.TryReadByte(out var count)) { diagnostics.Add("REQUEST_CHARAC_SKILL_INFO skill count is truncated"); return new DecodedBody("skill-list-success", fields); }
        var skills = new List<object>();
        for (var index = 0; index < count; index++)
        { if (!reader.TryReadByte(out var reserved) || !reader.TryReadUInt16(out var skillId) || !reader.TryReadByte(out var level)) { diagnostics.Add($"skill list entry {index} is truncated"); break; } skills.Add(new { reserved, skillId, level }); }
        fields["skills"] = skills; FinishReader(reader, fields); return new DecodedBody("skill-list-success", fields);
    }

    private static DecodedBody DecodeItemUpgradeResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "upgrade-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadByteField(reader, fields, diagnostics, "method"); ReadInt16Field(reader, fields, diagnostics, "materialSlotIndex"); ReadInt32Field(reader, fields, diagnostics, "materialRemainingCount");
        ReadInt16Field(reader, fields, diagnostics, "optionalTicketSlotIndex"); ReadByteField(reader, fields, diagnostics, "reserved0"); ReadByteField(reader, fields, diagnostics, "oldLevel");
        ReadByteField(reader, fields, diagnostics, "resultCode"); ReadByteField(reader, fields, diagnostics, "newLevel"); ReadByteField(reader, fields, diagnostics, "reserved1");
        ReadInt16Field(reader, fields, diagnostics, "targetSlotIndex"); ReadInt16Field(reader, fields, diagnostics, "ticketSlotEcho");
        if (fields.TryGetValue("resultCode", out var resultCode) && Convert.ToByte(resultCode) == 3)
        { if (!reader.TryReadByte(out var count)) diagnostics.Add("UPGRADE_ITEM destruction reward count is truncated"); else fields["destroyRewards"] = DecodeReward10List(reader, count, diagnostics, "UPGRADE_ITEM destroy reward"); }
        FinishReader(reader, fields); return new DecodedBody("upgrade-success", fields);
    }

    private static DecodedBody DecodeRepairEquipmentResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "repair-error" };
        if (body.Length != 10) diagnostics.Add($"REPAIR_EQUIPMENT success expects 10 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("updatedGold", "i32", 1), ("inventoryType", "u8", 5), ("slotIndex", "i16", 6), ("reserved", "i16", 8));
        return new DecodedBody("repair-success", fields);
    }

    private static DecodedBody DecodeMagicBoxResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, bool batch)
    {
        if (body.Length == 1 && body[0] == 0) { fields["status"] = body[0]; return new DecodedBody("magic-box-error-short", fields); }
        if (body.Length == 2 && body[0] == 0) return DecodeSimpleErrorBody(body, fields, diagnostics, "magic-box-error");
        if (!batch && body.Length == 3 && body[0] == 1 && body[1] == 0xFF)
        { fields["status"] = body[0]; fields["clientType"] = body[1]; fields["hasDoubleRewards"] = false; return new DecodedBody("magic-box-silent-completion", fields); }
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadByteField(reader, fields, diagnostics, "clientType"); ReadByteField(reader, fields, diagnostics, "hasDoubleRewards");
        if (batch) ReadUInt16Field(reader, fields, diagnostics, "consumedSourceCount");
        ReadInt16Field(reader, fields, diagnostics, "sourceSlotIndex"); ReadInt16Field(reader, fields, diagnostics, "materialSlotIndex");
        if (!reader.TryReadUInt16(out var primaryCount)) diagnostics.Add("magic box primary reward count is truncated"); else fields["primaryRewards"] = DecodeMagicBoxRewardRows(reader, primaryCount, diagnostics);
        if (batch)
        {
            ReadUInt16Field(reader, fields, diagnostics, "reservedBetweenLists");
            if (!reader.TryReadUInt16(out var doubleCount)) diagnostics.Add("magic box double reward count is truncated"); else fields["doubleRewards"] = DecodeMagicBoxRewardRows(reader, doubleCount, diagnostics);
        }
        FinishReader(reader, fields); return new DecodedBody(batch ? "magic-box-batch-success" : "magic-box-single-success", fields);
    }

    private static DecodedBody DecodeChronicleRefineResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "chronicle-refine-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        ReadInt16Field(reader, fields, diagnostics, "materialSlotIndex"); ReadInt16Field(reader, fields, diagnostics, "materialRemainingCount"); ReadByteField(reader, fields, diagnostics, "refineSucceeded");
        if (fields.TryGetValue("refineSucceeded", out var succeeded) && Convert.ToByte(succeeded) == 0)
        { ReadByteField(reader, fields, diagnostics, "reserved"); ReadInt16Field(reader, fields, diagnostics, "destroyedTargetSlotIndex"); if (!reader.TryReadByte(out var count)) diagnostics.Add("chronicle refine failure reward count is truncated"); else fields["failureRewards"] = DecodeReward10List(reader, count, diagnostics, "chronicle refine reward"); }
        FinishReader(reader, fields); return new DecodedBody("chronicle-refine-success", fields);
    }

    private static DecodedBody DecodeAvatarCompoundResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body[0] == 0) return DecodeSimpleErrorBody(body, fields, diagnostics, "avatar-compound-error");
        if (body.Length != 131) diagnostics.Add($"COMPOUND_AVATAR success expects 131 bytes, got {body.Length}");
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status;
        if (!reader.TryReadByte(out var deleteCount)) { diagnostics.Add("avatar compound delete count is truncated"); return new DecodedBody("avatar-compound-success", fields); }
        fields["deletedEntries"] = DecodeSlotCountEntries(reader, deleteCount, diagnostics, true, "avatar compound deleted");
        var rewards = new List<object>();
        for (var index = 0; index < 2; index++)
        { if (!reader.TryReadInt16(out var slot) || !reader.TryReadInt32(out var itemId) || !reader.TryReadInt32(out var value) || !reader.TryReadUInt16(out var abilityNo) || !reader.TryReadInt32(out var expansionLength) || expansionLength < 0 || !reader.TryReadBytes(expansionLength, out var expansion) || !reader.TryReadInt32(out var colorLength) || colorLength < 0 || !reader.TryReadBytes(colorLength, out var colors)) { diagnostics.Add($"avatar compound reward {index} is truncated"); break; } rewards.Add(new { slot, itemId, value, abilityNo, expansionHex = Convert.ToHexString(expansion), colorHex = Convert.ToHexString(colors) }); }
        fields["rewards"] = rewards; FinishReader(reader, fields); return new DecodedBody("avatar-compound-success", fields);
    }

    private static DecodedBody DecodeDeleteItemResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length == 3 && body[0] == 0)
        { ReadSchema(body, fields, ("status", "u8", 0), ("errorCode", "u8", 1), ("listType", "u8", 2)); return new DecodedBody("delete-error", fields); }
        if (body.Length != 11) diagnostics.Add($"DELETE_ITEM success expects 11 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("listType", "u8", 1), ("entryCount", "u8", 2), ("slotIndex", "i16", 3), ("appliedCount", "i32", 5), ("reserved", "i16", 9));
        return new DecodedBody("delete-success", fields);
    }

    private static DecodedBody DecodeAvatarDisjointResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "avatar-disjoint-error" };
        var reader = new PacketReader(body); reader.TryReadByte(out var status); fields["status"] = status; ReadInt16Field(reader, fields, diagnostics, "sourceSlotIndex");
        if (!reader.TryReadUInt16(out var count)) diagnostics.Add("DISJOINT_AVATAR material count is truncated"); else fields["materials"] = DecodeReward10List(reader, count, diagnostics, "avatar disjoint material");
        FinishReader(reader, fields); return new DecodedBody("avatar-disjoint-success", fields);
    }

    private static DecodedBody DecodeStatusAckResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string semantic)
    {
        if (body.Length == 1 && body[0] == 1)
        {
            fields["status"] = body[0];
            return new DecodedBody("success-ack", fields);
        }
        return DecodeSimpleErrorBody(body, fields, diagnostics, "error-ack");
    }

    private static DecodedBody DecodeExpertDisjointResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "disjoint-error" };
        var reader = new PacketReader(body); ReadByteField(reader, fields, diagnostics, "status");
        ReadInt16Field(reader, fields, diagnostics, "targetSlotIndex"); ReadByteField(reader, fields, diagnostics, "itemSpace");
        if (!reader.TryReadByte(out var count)) diagnostics.Add("expert disjoint material count is truncated");
        else fields["materials"] = DecodeReward10List(reader, count, diagnostics, "expert disjoint material");
        ReadInt32Field(reader, fields, diagnostics, "requesterGold"); ReadInt32Field(reader, fields, diagnostics, "endurance");
        FinishReader(reader, fields); return new DecodedBody("disjoint-success", fields);
    }

    private static DecodedBody DecodeExpertRepairResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "repair-error" };
        if (body.Length != 9) diagnostics.Add($"expert repair success expects 9 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("gold", "i32", 1), ("endurance", "i32", 5));
        return new DecodedBody("repair-success", fields);
    }

    private static DecodedBody DecodeExpertUpgradeResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "upgrade-error" };
        if (body.Length != 13) diagnostics.Add($"expert upgrade success expects 13 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("gold", "i32", 1), ("grade", "i32", 5), ("endurance", "i32", 9));
        return new DecodedBody("upgrade-success", fields);
    }

    private static DecodedBody DecodeExpertEnchantResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "enchant-error" };
        if (body.Length != 11) diagnostics.Add($"expert enchant success expects 11 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("enchantSucceeded", "u8", 1), ("finalExperience", "u32", 2), ("reserved", "u8", 6), ("endurance", "i32", 7));
        return new DecodedBody("enchant-success", fields);
    }

    private static DecodedBody DecodeExpertCompoundResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "compound-error" };
        var reader = new PacketReader(body); ReadByteField(reader, fields, diagnostics, "status");
        if (!reader.TryReadByte(out var count)) diagnostics.Add("expert compound output count is truncated");
        else
        {
            var outputs = new List<object>();
            for (var i = 0; i < count; i++)
            {
                if (!reader.TryReadInt32(out var itemId) || !reader.TryReadInt32(out var itemCount)) { diagnostics.Add($"expert compound output {i} is truncated"); break; }
                outputs.Add(new { itemId, count = itemCount });
            }
            fields["outputs"] = outputs;
        }
        ReadInt32Field(reader, fields, diagnostics, "successCount"); ReadInt32Field(reader, fields, diagnostics, "failureCount"); ReadByteField(reader, fields, diagnostics, "reserved");
        FinishReader(reader, fields); return new DecodedBody("compound-success", fields);
    }

    private static DecodedBody DecodeExpertGiveupResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "giveup-error" };
        if (body.Length != 6) diagnostics.Add($"expert giveup success expects 6 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("currentGold", "i32", 1), ("giveupCount", "u8", 5));
        return new DecodedBody("giveup-success", fields);
    }

    private static DecodedBody DecodeExpertEnterResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "error-ack" };
        if (body.Length == 11)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("kind", "u8", 1), ("machineGrade", "u8", 2), ("cost", "i32", 3), ("endurance", "i32", 7));
            return new DecodedBody("disjoint-enter-success", fields);
        }
        if (body.Length == 8)
        {
            ReadSchema(body, fields, ("status", "u8", 0), ("kind", "u8", 1), ("ownerUserId", "u16", 2), ("endurance", "i32", 4));
            return new DecodedBody("enchant-enter-success", fields);
        }
        diagnostics.Add($"expert enter success expects 8 or 11 bytes, got {body.Length}");
        AddScalarPreview(fields, body);
        return new DecodedBody("enter-success", fields);
    }

    private static DecodedBody DecodePvpEnterResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "error-ack" };
        if (body.Length != 9) diagnostics.Add($"PVP enter success expects 9 bytes, got {body.Length}");
        fields["status"] = body.Length > 0 ? body[0] : (byte)0;
        fields["readyStates"] = body.Skip(1).Take(8).ToArray();
        return new DecodedBody("enter-success", fields);
    }

    private static DecodedBody DecodeDailyChallengeRewardResponse(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (TryDecodeError(body, fields, diagnostics, out var error)) return error with { Variant = "claim-error" };
        if (body.Length != 9) diagnostics.Add($"DAILY_CHALLENGE_REWARD success expects 9 bytes, got {body.Length}");
        ReadSchema(body, fields, ("status", "u8", 0), ("groupIndex", "i32", 1), ("reserved", "i32", 5));
        return new DecodedBody("claim-success", fields);
    }

    private static bool TryDecodeError(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, out DecodedBody decoded)
    {
        if (body.Length >= 1 && body[0] == 0 && body.Length <= 2)
        { decoded = DecodeSimpleErrorBody(body, fields, diagnostics, "error-response"); return true; }
        decoded = null!; return false;
    }

    private static DecodedBody DecodeSimpleErrorBody(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string variant)
    {
        if (body.Length < 1) diagnostics.Add("response status is truncated"); else fields["status"] = body[0];
        if (body.Length >= 2) fields["errorCode"] = body[1];
        if (body.Length > 2) fields["errorTailHex"] = Convert.ToHexString(body.AsSpan(2));
        return new DecodedBody(variant, fields);
    }

    private static object[] DecodeSlotCountEntries(PacketReader reader, int count, List<string> diagnostics, bool includeListType, string semantic)
    {
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        {
            byte listType = 0;
            if (includeListType && !reader.TryReadByte(out listType)) { diagnostics.Add($"{semantic} {index} list type is truncated"); break; }
            if (!reader.TryReadInt16(out var slotIndex) || !reader.TryReadInt32(out var itemCount)) { diagnostics.Add($"{semantic} {index} is truncated"); break; }
            entries.Add(new { listType, slotIndex, itemCount });
        }
        return entries.ToArray();
    }

    private static object[] DecodeReward10List(PacketReader reader, int count, List<string> diagnostics, string semantic)
    {
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        { if (!reader.TryReadInt16(out var slotIndex) || !reader.TryReadInt32(out var itemTemplateId) || !reader.TryReadInt32(out var countValue)) { diagnostics.Add($"{semantic} {index} is truncated"); break; } entries.Add(new { slotIndex, itemTemplateId, count = countValue }); }
        return entries.ToArray();
    }

    private static object[] DecodeInt32PairList(PacketReader reader, int count, List<string> diagnostics, string semantic, string firstName, string secondName)
    {
        var entries = new List<object>();
        for (var index = 0; index < count; index++)
        { if (!reader.TryReadInt32(out var first) || !reader.TryReadInt32(out var second)) { diagnostics.Add($"{semantic} {index} is truncated"); break; } entries.Add(new Dictionary<string, object?> { [firstName] = first, [secondName] = second }); }
        return entries.ToArray();
    }

    private static object[] DecodeMagicBoxRewardRows(PacketReader reader, int count, List<string> diagnostics)
    {
        var rows = new List<object>();
        for (var index = 0; index < count; index++)
        { if (!reader.TryReadInt16(out var slot) || !reader.TryReadInt32(out var itemId) || !reader.TryReadInt32(out var displayCount) || !reader.TryReadBytes(21, out var reserved)) { diagnostics.Add($"magic box reward {index} is truncated"); break; } rows.Add(new { slot, itemId, displayCount, reservedHex = Convert.ToHexString(reserved) }); }
        return rows.ToArray();
    }

    private static void FinishReader(PacketReader reader, Dictionary<string, object?> fields)
    {
        fields["consumedBytes"] = reader.Offset;
        if (reader.Remaining > 0 && reader.TryReadBytes(reader.Remaining, out var tail)) fields["trailingHex"] = Convert.ToHexString(tail);
    }

    private static DecodedBody? DecodeOutboundVariants(
        PacketTypeDefinition definition,
        byte[] body,
        Dictionary<string, object?> fields,
        List<string> diagnostics,
        string? requestedVariant)
    {
        var variants = definition.Variants.Where(item => item.Schema is not null || !string.IsNullOrWhiteSpace(item.FixedBodyHex)).ToArray();
        if (variants.Length == 0) return null;

        PacketVariant[] candidates;
        if (!string.IsNullOrWhiteSpace(requestedVariant))
        {
            var selected = variants.FirstOrDefault(item => item.Name.Equals(requestedVariant, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                fields["candidateVariants"] = variants.Select(ToVariantCandidate).ToArray();
                diagnostics.Add($"requested outbound variant '{requestedVariant}' was not found");
                return new DecodedBody("unknown-requested-variant", fields);
            }
            candidates = new[] { selected };
        }
        else
        {
            candidates = variants.Where(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.FixedBodyHex)
                    && !Convert.ToHexString(body).Equals(item.FixedBodyHex, StringComparison.OrdinalIgnoreCase))
                    return false;
                var schema = item.Schema;
                if (schema is null) return !string.IsNullOrWhiteSpace(item.FixedBodyHex);
                if (schema.ExactLength.HasValue && schema.ExactLength.Value != body.Length) return false;
                if (schema.MinimumLength.HasValue && body.Length < schema.MinimumLength.Value) return false;
                return true;
            }).ToArray();
        }

        if (candidates.Length == 0)
        {
            fields["candidateVariants"] = variants.Select(ToVariantCandidate).ToArray();
            diagnostics.Add($"no outbound builder variant accepts body length {body.Length}");
            AddScalarPreview(fields, body);
            return new DecodedBody("unresolved-outbound-variant", fields);
        }
        if (candidates.Length > 1 && string.IsNullOrWhiteSpace(requestedVariant))
        {
            fields["candidateVariants"] = candidates.Select(ToVariantCandidate).ToArray();
            diagnostics.Add($"body matches multiple outbound builder variants for {definition.EnumName}; select one explicitly using variant");
            AddScalarPreview(fields, body);
            return new DecodedBody("ambiguous-outbound-variant", fields);
        }

        var selectedVariant = candidates[0];
        fields["selectedVariant"] = selectedVariant.Name;
        fields["variantDiscriminator"] = selectedVariant.Discriminator;
        if (selectedVariant.Schema is not null)
        {
            var schema = selectedVariant.Schema;
            foreach (var field in schema.Fields)
            {
                var width = FieldWidth(field.Type);
                if (width == 0 || field.Offset + width > body.Length)
                {
                    if (!field.Optional) diagnostics.Add($"outbound field {field.Name} at +0x{field.Offset:X} is truncated");
                    continue;
                }
                fields[field.Name] = ReadField(body, field.Type, field.Offset);
            }
            fields["schemaConfidence"] = selectedVariant.Confidence;
            return new DecodedBody(selectedVariant.Name, fields);
        }
        fields["fixedBodyHex"] = selectedVariant.FixedBodyHex;
        return new DecodedBody(selectedVariant.Name, fields);
    }

    private static string DecodeLogin(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadDString(Encoding.ASCII, out var mid) || !reader.TryReadDString(Encoding.ASCII, out var password))
        {
            diagnostics.Add("LOGIN requires two ASCII dstr fields");
            return "login-request";
        }
        fields["mId"] = mid;
        fields["passwordHash"] = password;
        fields["consumedBytes"] = reader.Offset;
        return "login-request";
    }

    private static string DecodeUdpEndpoint(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 15) { diagnostics.Add("SET_UDP_IP_PORT requires at least 15 bytes"); return "udp-endpoint"; }
        fields["natType"] = body[0];
        fields["innerIpv4"] = new IPAddress(body.AsSpan(1, 4)).ToString();
        fields["outerIpv4"] = new IPAddress(body.AsSpan(5, 4)).ToString();
        fields["port"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(9, 2));
        fields["mtu"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11, 4));
        return "udp-endpoint";
    }

    private static string DecodeSetPartyInfo(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 1) { diagnostics.Add("SET_PARTY_INFO requires at least one byte"); return "party-info"; }
        fields["titleIndex"] = body[0];
        if (body.Length > 1) fields["userMax"] = body[1];
        if (body.Length >= 4) fields["dungeonIndex"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        if (body.Length > 4) fields["dungeonDifficulty"] = body[4];
        return "party-info";
    }

    private static string DecodeSelectDungeon(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
        => DecodeFixed(body, 5, fields, diagnostics, ("dungeonId", "u16", 0), ("difficulty", "u8", 2), ("flag1", "u8", 3), ("flag2", "u8", 4));

    private static string DecodeMoveMap(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length != 64) { diagnostics.Add($"MOVE_MAP expects 64 bytes, got {body.Length}"); return "move-map"; }
        fields["nextX"] = body[0]; fields["nextY"] = body[1];
        fields["pathPositionX"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2, 4));
        fields["pathPositionY"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6, 4));
        fields["moveMode"] = body[10]; fields["trapBits"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(11, 2));
        fields["memberMapClearValues"] = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(13 + i * 2, 2))).ToArray();
        fields["memberMapElapsedValues"] = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(29 + i * 4, 4))).ToArray();
        fields["clientTimingToken"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(61, 2));
        fields["clientStateFlag"] = body[63];
        return "move-map";
    }

    private static string DecodeItemUpgrade(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 16) { diagnostics.Add("UPGRADE_ITEM requires at least 16 bytes"); return "item-upgrade"; }
        fields["method"] = BinaryPrimitives.ReadUInt16LittleEndian(body);
        fields["targetSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(2, 2));
        fields["targetItemTemplateId"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4));
        fields["materialSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(8, 2));
        fields["optionalTicketSlotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(10, 2));
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12, 4));
        fields["targetItemNameLength"] = length;
        if (length >= 0 && 16 + length <= body.Length) fields["targetItemName"] = Encoding.UTF8.GetString(body, 16, length);
        else diagnostics.Add("UPGRADE_ITEM name length exceeds body");
        return "item-upgrade";
    }

    private static string DecodeCountedUInt16(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics, string fieldName)
    {
        if (body.Length < 1) { diagnostics.Add("counted u16 list requires count byte"); return "counted-u16-list"; }
        var count = body[0];
        fields["count"] = count;
        if (body.Length != 1 + count * 2) diagnostics.Add($"counted u16 list expects {1 + count * 2} bytes, got {body.Length}");
        fields[fieldName] = Enumerable.Range(0, Math.Min(count, (body.Length - 1) / 2))
            .Select(i => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1 + i * 2, 2))).ToArray();
        return "counted-u16-list";
    }

    private static string DecodeFixed(byte[] body, int length, Dictionary<string, object?> fields, List<string> diagnostics, params (string Name, string Type, int Offset)[] schema)
    {
        if (body.Length != length) diagnostics.Add($"expected {length} bytes, got {body.Length}");
        ReadSchema(body, fields, schema);
        return "fixed-layout";
    }

    private static string DecodeAtLeast(byte[] body, int length, Dictionary<string, object?> fields, List<string> diagnostics, params (string Name, string Type, int Offset)[] schema)
    {
        if (body.Length < length) diagnostics.Add($"expected at least {length} bytes, got {body.Length}");
        ReadSchema(body, fields, schema);
        return "variable-layout";
    }

    private static void ReadSchema(byte[] body, Dictionary<string, object?> fields, params (string Name, string Type, int Offset)[] schema)
    {
        foreach (var field in schema)
        {
            var optional = field.Type.EndsWith('?');
            var type = optional ? field.Type[..^1] : field.Type;
            var width = type switch { "u8" or "bool8" => 1, "u16" or "i16" => 2, "u32" or "i32" => 4, _ => 0 };
            if (width == 0 || field.Offset + width > body.Length) continue;
            fields[field.Name] = type switch
            {
                "u8" => body[field.Offset],
                "bool8" => body[field.Offset] != 0,
                "u16" => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(field.Offset, 2)),
                "i16" => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(field.Offset, 2)),
                "u32" => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(field.Offset, 4)),
                "i32" => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(field.Offset, 4)),
                _ => null,
            };
        }
    }

    internal static int FieldWidth(string type) => type switch
    {
        "u8" or "i8" or "bool8" => 1,
        "u16" or "i16" => 2,
        "u32" or "i32" => 4,
        "u64" or "i64" => 8,
        _ => 0,
    };

    internal static object? ReadField(byte[] body, string type, int offset) => type switch
    {
        "u8" => body[offset],
        "i8" => unchecked((sbyte)body[offset]),
        "bool8" => body[offset] != 0,
        "u16" => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)),
        "i16" => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset, 2)),
        "u32" => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4)),
        "i32" => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4)),
        "u64" => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(offset, 8)),
        "i64" => BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(offset, 8)),
        _ => null,
    };

    private static void ReadNamedI16(PacketReader reader, Dictionary<string, object?> fields, params string[] names)
    {
        foreach (var name in names) if (reader.TryReadInt16(out var value)) fields[name] = value;
    }

    private static Dictionary<string, object?> BaseFields(byte[] body) => new(StringComparer.Ordinal)
    {
        ["bodyLength"] = body.Length,
        ["rawHex"] = Convert.ToHexString(body),
    };

    private static void AddScalarPreview(Dictionary<string, object?> fields, byte[] body)
    {
        var preview = new List<Dictionary<string, object?>>();
        for (var offset = 0; offset < Math.Min(body.Length, 32); offset++)
        {
            var item = new Dictionary<string, object?> { ["offset"] = offset, ["u8"] = body[offset] };
            if (offset + 2 <= body.Length)
            {
                item["u16le"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
                item["i16le"] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset, 2));
            }
            if (offset + 4 <= body.Length)
            {
                item["u32le"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
                item["i32le"] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
            }
            preview.Add(item);
        }
        fields["scalarPreview"] = preview;
    }
}

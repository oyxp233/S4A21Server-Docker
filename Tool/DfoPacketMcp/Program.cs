using System.Text.Json;
using DfoPacketMcp.Mcp;
using DfoPacketMcp.Protocol;

var root = AppContext.BaseDirectory;
if (args.Contains("--export-protocol", StringComparer.OrdinalIgnoreCase))
{
    var sourceRoot = args.SkipWhile(item => !item.Equals("--source-root", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
        ?? root;
    var output = args.SkipWhile(item => !item.Equals("--output", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
        ?? Path.Combine(root, "Protocol", "protocol-catalog.json");
    var versionPath = Path.Combine(sourceRoot, "VERSION");
    var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "development";
    ProtocolCatalog.ExportStandalone(ProtocolCatalog.LoadLegacy(sourceRoot), output, version);
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, output = Path.GetFullPath(output), version }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
var catalog = ProtocolCatalog.Load(root);
var tools = new PacketToolService(catalog);

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    return RunSelfTest(catalog, tools);
}

if (args.Contains("--coverage", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(tools.GetCoverage(), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

await new StdioMcpServer(tools).RunAsync();
return 0;

static int RunSelfTest(ProtocolCatalog catalog, PacketToolService tools)
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    var decoder = new PacketDecoder(catalog);
    var login = catalog.Find(PacketFlow.ClientToServer, PacketKind.Cmd, "LOGIN")!;
    var loginFields = JsonDocument.Parse("""{"mId":"tester","passwordHash":"abc123"}""").RootElement;
    var loginBody = PacketEncoder.EncodeBody(login, null, loginFields);
    var inbound = PacketDecoder.Encode(1, login.Type, loginBody, PacketTransport.Ingress);
    var parsedLogin = decoder.Decode(inbound, PacketTransport.Ingress);
    Require(parsedLogin.Flow == PacketFlow.ClientToServer, "login flow mismatch");
    Require(parsedLogin.Definition?.Name == "C2S_CMD_LOGIN_REQUEST", "login direction-specific name mismatch");
    Require((string?)parsedLogin.Fields["mId"] == "tester", "login field mismatch");

    var exitResponse = catalog.Find(PacketFlow.ServerToClient, PacketKind.Cmd, "EXIT")!;
    var response = PacketDecoder.Encode(1, exitResponse.Type, new byte[] { 1 }, PacketTransport.Egress);
    var parsedResponse = decoder.Decode(response, PacketTransport.Egress);
    Require(parsedResponse.Flow == PacketFlow.ServerToClient, "response flow mismatch");
    Require(parsedResponse.Definition?.Name == "S2C_CMD_EXIT_RESPONSE", "response direction-specific name mismatch");
    Require(parsedResponse.Variant == "BuildSuccessAck", "response variant mismatch");

    var userInfo = catalog.Find(PacketFlow.ServerToClient, PacketKind.Noti, "USERINFO")!;
    Require(userInfo.Variants.Any(item => item.Name == "subtype0-character-state"), "USERINFO manual variants are not attached to the catalog");
    Require(userInfo.Variants.Any(item => item.Name == "subtype3-inspect-player"), "USERINFO subtype 3 variant is missing from the catalog");
    foreach (var subtype in new byte[] { 0, 1, 2, 3 })
    {
        byte[] body = subtype switch
        {
            0 => new byte[] { 0, 1, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            1 => new byte[] { 1, 1, 0, 7, 0 },
            2 => new byte[] { 2, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            _ => new byte[] { 3, 1, 0, 7, 0 },
        };
        var packet = PacketDecoder.Encode(0, userInfo.Type, body, PacketTransport.Egress);
        var parsed = decoder.Decode(packet, PacketTransport.Egress);
        Require(parsed.Definition?.Name == "S2C_NOTI_USERINFO", "USERINFO name mismatch");
        Require(parsed.Variant.StartsWith($"subtype{subtype}", StringComparison.Ordinal), $"USERINFO subtype {subtype} discriminator mismatch");
    }

    var unknownUserInfoDiagnostics = new List<string>();
    var unknownUserInfo = PacketSchemaRegistry.Decode(userInfo, new byte[] { 9, 1, 2 }, unknownUserInfoDiagnostics);
    Require(unknownUserInfo.Variant == "unresolved-subtype-9", "USERINFO unknown subtype must remain unresolved");
    Require(unknownUserInfoDiagnostics.Count > 0, "USERINFO unknown subtype must emit diagnostics");

    var mismatchDiagnostics = new List<string>();
    PacketSchemaRegistry.Decode(userInfo, new byte[] { 1, 1, 0, 7, 0 }, mismatchDiagnostics, "subtype0-character-state");
    Require(mismatchDiagnostics.Any(item => item.Contains("does not match body discriminator", StringComparison.Ordinal)), "explicit USERINFO subtype mismatch must be diagnosed");

    var sharedTypeC2s = catalog.Find(PacketFlow.ClientToServer, PacketKind.Cmd, "0x0131")!;
    var sharedTypeS2c = catalog.Find(PacketFlow.ServerToClient, PacketKind.Cmd, "0x0131")!;
    Require(sharedTypeC2s.Name == "C2S_CMD_CREATE_ACCOUNT_CARGO_REQUEST", "same-type C2S name mismatch");
    Require(sharedTypeS2c.Name == "S2C_CMD_CREATE_ACCOUNT_CARGO_RESPONSE", "same-type S2C name mismatch");
    Require(sharedTypeS2c.Variants.Length >= 2, "S2C 0x0131 should preserve success and error body variants");

    var errorBodyDiagnostics = new List<string>();
    var decodedErrorBody = PacketSchemaRegistry.Decode(sharedTypeS2c, new byte[] { 0, 4 }, errorBodyDiagnostics);
    Require(decodedErrorBody.Variant == "inline-bytes", "S2C 0x0131 error variant selection mismatch");
    Require((byte)decodedErrorBody.Fields["errorCode"]! == 4, "S2C 0x0131 error field mismatch");
    var successBodyDiagnostics = new List<string>();
    var decodedSuccessBody = PacketSchemaRegistry.Decode(sharedTypeS2c, new byte[] { 1 }, successBodyDiagnostics);
    Require(decodedSuccessBody.Variant == "inline-bytes-2", "S2C 0x0131 success variant selection mismatch");

    var mercenaryReturn = catalog.Find(PacketFlow.ClientToServer, PacketKind.Cmd, "MERCENARY_RETURN")!;
    var mercenaryFields = JsonDocument.Parse("""{"purpose":2,"characterId":12345}""").RootElement;
    var mercenaryBody = PacketEncoder.EncodeBody(mercenaryReturn, "MercenaryExpeditionHandler.HandleReturn", mercenaryFields);
    var mercenaryDiagnostics = new List<string>();
    var decodedMercenary = PacketSchemaRegistry.Decode(mercenaryReturn, mercenaryBody, mercenaryDiagnostics, "MercenaryExpeditionHandler.HandleReturn");
    Require((byte)decodedMercenary.Fields["purpose"]! == 2, "inferred variant purpose round-trip mismatch");
    Require((int)decodedMercenary.Fields["characterId"]! == 12345, "inferred variant character id round-trip mismatch");

    var recvPacket = PacketDecoder.Encode(1, login.Type, loginBody, PacketTransport.Ingress);
    var sendPacket = PacketDecoder.Encode(1, sharedTypeS2c.Type, new byte[] { 1 }, PacketTransport.Egress);
    var captureText = $"RECV cmd=0x01 type=0x{login.Type:X4}\nraw: {Convert.ToHexString(recvPacket)}\nSEND cmd=0x01 type=0x{sharedTypeS2c.Type:X4}\nraw: {Convert.ToHexString(sendPacket)}";
    var captureArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { text = captureText })).RootElement;
    using var captureDocument = JsonDocument.Parse(JsonSerializer.Serialize(tools.DecodeCapture(captureArguments)));
    Require(captureDocument.RootElement.GetProperty("count").GetInt32() == 2, "mixed SEND/RECV capture count mismatch");

    static void RequireRoundTrip(ProtocolCatalog catalog, string packet, string variant, string json, string expectedVariant)
    {
        var definition = catalog.Find(PacketFlow.ServerToClient, PacketKind.Cmd, packet)
            ?? throw new InvalidOperationException($"missing S2C CMD {packet}");
        using var document = JsonDocument.Parse(json);
        var encoded = PacketEncoder.EncodeBody(definition, variant, document.RootElement);
        var diagnostics = new List<string>();
        var decoded = PacketSchemaRegistry.Decode(definition, encoded, diagnostics, variant);
        Require(decoded.Variant == expectedVariant, $"{packet} round-trip variant mismatch: {decoded.Variant}");
        Require(!diagnostics.Any(item => item.Contains("truncated", StringComparison.OrdinalIgnoreCase)), $"{packet} round-trip truncated: {string.Join("; ", diagnostics)}");
    }

    static void RequireNotiRoundTrip(ProtocolCatalog catalog, string packet, string variant, string json, string expectedVariant)
    {
        var definition = catalog.Find(PacketFlow.ServerToClient, PacketKind.Noti, packet)
            ?? throw new InvalidOperationException($"missing S2C NOTI {packet}");
        using var document = JsonDocument.Parse(json);
        var encoded = PacketEncoder.EncodeBody(definition, variant, document.RootElement);
        var diagnostics = new List<string>();
        var decoded = PacketSchemaRegistry.Decode(definition, encoded, diagnostics, variant);
        Require(decoded.Variant == expectedVariant, $"{packet} NOTI round-trip variant mismatch: {decoded.Variant}");
        Require(!diagnostics.Any(item => item.Contains("truncated", StringComparison.OrdinalIgnoreCase)), $"{packet} NOTI round-trip truncated: {string.Join("; ", diagnostics)}");
    }

    RequireRoundTrip(catalog, "CARD_SELECT_RIGHT_STATE", "card-layout", """{"status":1,"cardRights":[1,65535,65535,65535,65535,65535,65535,65535]}""", "card-layout");
    RequireRoundTrip(catalog, "TOURNAMENT_REWARD_SELECT", "selection-state", """{"status":1,"cardTypes":[{"selections":[0,1]},{"selections":[255]}]}""", "selection-state");
    RequireRoundTrip(catalog, "SET_CLONE_TITLE", "clone-title-ack", """{"status":1,"cloneTitleItemId":123}""", "clone-title-ack");
    RequireRoundTrip(catalog, "BUY_CERASHOP_ITEM", "purchase-success", """{"success":true,"category":-1,"commodityNo":77,"extraItems":[]}""", "purchase-success");
    RequireRoundTrip(catalog, "PREMIUM_SERVICE", "premium-service-state", $"{{\"status\":1,\"serviceType\":1,\"serviceDataHex\":\"{new string('0', 148)}\"}}", "premium-service-state");
    RequireRoundTrip(catalog, "SAVE_GAME_OPTION_1", "rental-catalog", $"{{\"catalogHex\":\"{new string('0', 268)}\",\"luckyStar\":9,\"purchaseMarker\":3,\"purchaseCount\":2}}", "rental-catalog");
    RequireRoundTrip(catalog, "SELECT_CARD", "card-info-standard", """{"status":1,"records":[{"index":0,"freeState":255,"paidState":255,"tail":0}]}""", "card-info-standard");
    RequireRoundTrip(catalog, "CHANGE_TUTORIAL_FLAG", "tutorial-reward-ack", """{"status":1,"rewards":[{"slot":2,"itemId":100,"count":3}]}""", "tutorial-reward-ack");
    RequireRoundTrip(catalog, "SUMMON_MONSTER", "summon-create-response", """{"result":1,"state":2,"count":1,"runtimeKey":3,"monsterCode":4,"mode":5,"parameter":6}""", "summon-create-response");
    RequireRoundTrip(catalog, "QUERY_CHARAC_INFO_MAILBOX", "query-success", """{"name":"target","level":70,"job":1,"growType":2}""", "query-success");
    RequireRoundTrip(catalog, "SKILL_COMMAND_CUSTOMIZING", "command-record-echo", """{"status":1,"page":0,"records":[{"skillId":7,"commandHex":"AABB"}]}""", "command-record-echo");
    RequireRoundTrip(catalog, "GET_EXPAND_EXP_GAGE_REWARD", "claim-success", """{"success":true,"itemId":1234,"itemCount":2}""", "claim-success");
    RequireRoundTrip(catalog, "BUY_SKILL", "buy-skill-success", """{"success":true,"skillTree":1,"remainSp":12,"remainTp":3,"entries":[{"slot":0,"skillId":44,"level":2,"hasCommand":true}]}""", "buy-skill-success");
    RequireRoundTrip(catalog, "TOURNAMENT_REWARD_SELECT_STATE", "selection-rights", """{"status":1,"cardTypes":[{"partySlots":[1,1,255,255]},{"partySlots":[1,255,255,255]}]}""", "selection-rights");
    RequireRoundTrip(catalog, "SELECT_CHARACTER", "select-success", """{"status":1,"uniqueId":7,"fatigueLimit":188,"premiums":[],"activeQuestSlots":[],"questNotifyIds":[],"characterSlotIndex":0,"tutorialFlagIndexes":[],"reserved8Hex":"0000000000000000","reservedTailHex":"00000000000000000000000000000000000000000000"}""", "select-success");
    RequireRoundTrip(catalog, "BUY_ITEM", "buy-success", """{"updatedGold":10,"slotIndex":2,"itemTemplateId":100,"costItems":[{"itemTemplateId":200,"newStackCount":3}]}""", "buy-success");
    RequireRoundTrip(catalog, "INVEST_ITEM_AMPLIFY_OPTION", "amplify-success", """{"action":2,"materialSlotIndex":1,"materialRemainingCount":4,"targetSlotIndex":2,"amplifyType":3,"amplifyValue":5,"amplifyLevel":7}""", "amplify-success");
    RequireRoundTrip(catalog, "COMPOUND_ITEM", "compound-success", """{"deletedEntries":[{"listType":1,"slotIndex":2,"itemCount":3}],"rewards":[{"listType":1,"slotIndex":4,"itemTemplateId":5,"count":6}]}""", "compound-success");
    RequireRoundTrip(catalog, "RESET_ITEM_ATTR", "wax-reseal-result", """{"targetSlotIndex":2,"targetItemId":3,"resultCode":1}""", "wax-reseal-result");
    RequireRoundTrip(catalog, "SECRET_SHOP_BUY_ITEM", "secret-shop-success", """{"updatedGold":10,"assignedSlot":2,"itemId":3,"itemValue":4,"requiredItemId":-1}""", "secret-shop-success");
    RequireRoundTrip(catalog, "USE_STACKABLE", "stackable-success", """{"slotIndex":2,"listType":1,"instanceValue":3,"itemCode":4}""", "stackable-success");
    RequireRoundTrip(catalog, "USE_STACKABLE", "stackable-error", """{"status":0,"errorCode":23,"listType":1,"instanceValue":3,"itemCode":4}""", "stackable-error");
    RequireRoundTrip(catalog, "USE_LOTTERY_ITEM", "phase-start", """{"sourceSlotIndex":2,"previewItemId":100,"previewItemId2":100}""", "phase-start");
    RequireRoundTrip(catalog, "UPGRADE_CHRONICLE", "chronicle-growth-success", """{"growthSucceeded":true,"consumptions":[{"listType":1,"slotIndex":2,"itemCount":3}]}""", "chronicle-growth-success");
    RequireRoundTrip(catalog, "CHARGE_RENTPOINT", "rent-point-success", """{"totalLuckyStar":9,"changeCount":2}""", "rent-point-success");
    RequireRoundTrip(catalog, "MOVE_ITEMSPACE", "move-success", """{"sourceListType":1,"sourceSlotIndex":2,"moveValue":3,"destinationListType":2,"destinationSlotIndex":4}""", "move-success");
    RequireRoundTrip(catalog, "CRANE_START_USE", "crane-start-success", """{"machineId":1,"materialRemainingCount":2,"displayCatalogIndexes":[1,2,3,4,5,6]}""", "crane-start-success");
    RequireRoundTrip(catalog, "DISJOINT_ITEM", "disjoint-success", """{"targetSlotIndex":2,"itemSpace":1,"materials":[{"slotIndex":3,"itemTemplateId":4,"count":5}]}""", "disjoint-success");
    RequireRoundTrip(catalog, "USE_BOOSTER_ITEM", "package-success", """{"sourceSlotIndex":2,"grantedItems":[{"itemTemplateId":3,"displayCount":4}]}""", "package-success");
    RequireRoundTrip(catalog, "BIND_PLUS", "avatar-set-success", """{"headerHex":"0100030001000000","newSlotIndex":2,"newItemId":3,"abilityNo":4,"resultCount":1,"consumedSlots":[1,2,3,4,5,6,7,8],"reservedTailHex":"000000000000000000000000000000000000000000000000"}""", "avatar-set-success");
    RequireRoundTrip(catalog, "REQUEST_CHARAC_SKILL_INFO", "skill-list-success", """{"requestEcho":7,"skills":[{"reserved":0,"skillId":8,"level":2}]}""", "skill-list-success");
    RequireRoundTrip(catalog, "UPGRADE_ITEM", "upgrade-success", """{"method":1,"materialSlotIndex":2,"materialRemainingCount":3,"optionalTicketSlotIndex":-1,"oldLevel":4,"resultCode":1,"newLevel":5,"targetSlotIndex":6,"ticketSlotEcho":-1}""", "upgrade-success");
    RequireRoundTrip(catalog, "REPAIR_EQUIPMENT", "repair-success", """{"updatedGold":10,"inventoryType":1,"slotIndex":2}""", "repair-success");
    RequireRoundTrip(catalog, "USE_RANDOMBOX_ITEM_EXPAND", "magic-box-batch-success", """{"clientType":1,"consumedSourceCount":1,"sourceSlotIndex":2,"materialSlotIndex":-1,"primaryRewards":[{"slot":-1,"itemId":3,"displayCount":4}],"doubleRewards":[]}""", "magic-box-batch-success");
    RequireRoundTrip(catalog, "ENCHANT_3RD_CHRONICLE_ITEM", "chronicle-refine-success", """{"materialSlotIndex":1,"materialRemainingCount":2,"refineSucceeded":true}""", "chronicle-refine-success");
    RequireRoundTrip(catalog, "COMPOUND_AVATAR", "avatar-compound-success", """{"deletedEntries":[{"listType":1,"slotIndex":2,"itemCount":1},{"listType":1,"slotIndex":3,"itemCount":1},{"listType":0,"slotIndex":4,"itemCount":1}],"rewards":[{"slot":5,"itemId":6,"value":0,"abilityNo":7},{"slot":-1,"itemId":0,"value":0,"abilityNo":7}]}""", "avatar-compound-success");
    RequireRoundTrip(catalog, "DELETE_ITEM", "delete-success", """{"listType":1,"entryCount":1,"slotIndex":2,"appliedCount":3}""", "delete-success");
    RequireRoundTrip(catalog, "USE_RANDOMBOX_ITEM", "magic-box-single-success", """{"clientType":1,"sourceSlotIndex":2,"materialSlotIndex":-1,"primaryRewards":[{"slot":-1,"itemId":3,"displayCount":4}]}""", "magic-box-single-success");
    RequireRoundTrip(catalog, "DISJOINT_AVATAR", "avatar-disjoint-success", """{"sourceSlotIndex":2,"materials":[{"slotIndex":3,"itemTemplateId":4,"count":5}]}""", "avatar-disjoint-success");
    RequireRoundTrip(catalog, "REQUEST_DISJOINT_ITEM", "disjoint-success", """{"targetSlotIndex":2,"itemSpace":1,"materials":[{"slotIndex":3,"itemTemplateId":4,"count":5}],"requesterGold":100,"endurance":90}""", "disjoint-success");
    RequireRoundTrip(catalog, "REPAIR_DISJOINT_MACHINE", "repair-success", """{"gold":100,"endurance":90}""", "repair-success");
    RequireRoundTrip(catalog, "UPGRADE_DISJOINT_MACHINE", "upgrade-success", """{"gold":100,"grade":2,"endurance":90}""", "upgrade-success");
    RequireRoundTrip(catalog, "USE_ENCHANT_STORE", "enchant-success", """{"enchantSucceeded":true,"finalExperience":1234,"endurance":90}""", "enchant-success");
    RequireRoundTrip(catalog, "COMPOUND_ITEM_BY_EXPERT_JOB", "compound-success", """{"outputs":[{"itemId":4,"count":2}],"successCount":1,"failureCount":0}""", "compound-success");
    RequireRoundTrip(catalog, "GIVEUP_EXPERT_JOB", "giveup-success", """{"currentGold":100,"giveupCount":2}""", "giveup-success");
    RequireRoundTrip(catalog, "CREATE_EXPERT_JOB_STORE", "success-ack", """{"status":1}""", "success-ack");
    RequireRoundTrip(catalog, "ENTER_EXPERT_JOB_STORE", "disjoint-enter-success", """{"kind":1,"machineGrade":2,"cost":100,"endurance":90}""", "disjoint-enter-success");
    RequireRoundTrip(catalog, "ENTER_EXPERT_JOB_STORE", "enchant-enter-success", """{"kind":2,"ownerUserId":7,"endurance":90}""", "enchant-enter-success");
    RequireRoundTrip(catalog, "ENTER_PVP_ROOM", "enter-success", """{"readyStates":[1,0,0,0,0,0,0,0]}""", "enter-success");
    RequireRoundTrip(catalog, "DAILY_CHALLENGE_REWARD", "claim-success", """{"groupIndex":2,"reserved":0}""", "claim-success");
    RequireNotiRoundTrip(catalog, "USER_UDP_IP_PORT", "peer-endpoint-roster", """{"members":[{"userId":7,"innerIpv4":"127.0.0.1","outerIpv4":"10.0.0.1","port":10000,"accountId":8,"natType":0,"mtu":1500,"characterAttribute":0}]}""", "peer-endpoint-roster");
    RequireNotiRoundTrip(catalog, "GET_ITEM", "pickup-item", """{"sourceSceneSlot":1,"pickerActorId":2,"destinationSlot":3,"moveFlag":7}""", "pickup-item");
    RequireNotiRoundTrip(catalog, "GET_ITEM", "pickup-gold", """{"sourceSceneSlot":1,"pickerActorId":2,"goldAmount":100,"extraGold":5}""", "pickup-gold");
    RequireNotiRoundTrip(catalog, "USER_STATE", "user-state-list", """{"users":[{"userId":7,"userState":1}]}""", "user-state-list");
    RequireNotiRoundTrip(catalog, "PARTY_INFO", "party-info-and-roster", """{"partyId":9,"title":"party","userMax":4,"slots":[{"slot":0,"userId":7}]}""", "party-info-and-roster");
    RequireNotiRoundTrip(catalog, "ENTER_SELECT_DUNGEON", "enter-select-dungeon", """{"users":[{"userId":7,"state":0}],"towerOfDespairFloor":3}""", "enter-select-dungeon");
    RequireNotiRoundTrip(catalog, "REQUEST_PEER", "party-invite", """{"inviterUserId":7,"peerToken":8,"partyValues":[0,0,0]}""", "party-invite");
    RequireNotiRoundTrip(catalog, "REQUEST_PEER", "trade-invite", """{"inviterUserId":7,"peerToken":8,"inviterCreateTime":9}""", "trade-invite");
    RequireNotiRoundTrip(catalog, "REQUEST_PEER", "pvp-room-invite", """{"inviterUserId":7,"peerToken":8}""", "pvp-room-invite");
    RequireNotiRoundTrip(catalog, "PARTY_MEMBER_REALTIME_INFO", "party-realtime-list", """{"members":[{"userId":7,"hpPercent":100,"isHelpAbuseParty":false,"slotIndex":0}]}""", "party-realtime-list");
    RequireNotiRoundTrip(catalog, "AREA_USERS", "area-user-roster", """{"townId":1,"areaId":2,"users":[{"userId":7,"x":10,"y":20,"direction":1,"state":0}]}""", "area-user-roster");
    RequireNotiRoundTrip(catalog, "UPDATE_ITEM_LIST", "common-entry-updates", """{"listType":0,"entries":[{"slotIndex":2,"itemTemplateId":100,"value":3,"attribute":1,"durability":99,"marker16":-1,"chronicleOptions":[{"optionId":7,"job":1,"firstGrowType":2,"equipmentType":3,"optionNo":4}],"randomOptions":[{"type":1,"value1":2,"value2":3}],"sortLockFlag":1}]}""", "common-entry-updates");
    RequireNotiRoundTrip(catalog, "UPDATE_ITEM_LIST", "avatar-entry-updates", $"{{\"listType\":1,\"entries\":[{{\"slotIndex\":3,\"itemTemplateId\":101,\"value\":300,\"jewelSocketHex\":\"{new string('0', 60)}\",\"color1\":4,\"color2\":5}}]}}", "avatar-entry-updates");
    RequireNotiRoundTrip(catalog, "ITEM_LIST", "common-item-list", """{"listType":0,"listParam":56,"entries":[{"slotIndex":1,"itemTemplateId":200,"value":9}]}""", "common-item-list");
    RequireNotiRoundTrip(catalog, "ITEM_LIST", "pet-item-list", """{"listType":7,"entries":[{"slotIndex":4,"itemTemplateId":201,"value":10}]}""", "pet-item-list");
    RequireNotiRoundTrip(catalog, "ITEM_LIST", "account-cargo-item-list", """{"listType":12,"selectionKey":2,"money":1234,"entries":[{"slotIndex":5,"itemTemplateId":202,"value":11}]}""", "account-cargo-item-list");
    RequireNotiRoundTrip(catalog, "CREATURE_ITEM_LIST", "creature-item-list", """{"entries":[{"creatureKey":1,"field04":40,"modeFlag":1,"progressValue32":1234,"mode1Field0A":2,"mode1Field0B":3,"fieldAfterValue32":40,"creatureTextUtf8":"pet","tailFlag":7}]}""", "creature-item-list");
    RequireNotiRoundTrip(catalog, "ITEM_LOCK_LIST", "equipment-item-lock-list", """{"entries":[{"listType":0,"slotIndex":2,"state":1},{"listType":1,"slotIndex":3,"state":2,"remainingSeconds":60}]}""", "equipment-item-lock-list");
    RequireNotiRoundTrip(catalog, "CREATURE_STATE", "runtime-state-pair", """{"creatureKey":1,"stateValue":2}""", "runtime-state-pair");
    RequireNotiRoundTrip(catalog, "CREATURE_STATE", "creature-entry-refresh", """{"entry":{"creatureKey":1,"field04":40,"modeFlag":0,"progressValue32":1234,"fieldAfterValue32":40,"tailFlag":7}}""", "creature-entry-refresh");
    RequireNotiRoundTrip(catalog, "CREATURE_SCRIPT_MESSAGE", "creature-script-broadcast", """{"mode":3,"senderUserId":7,"serverGroup":0,"messageUtf8":"hello"}""", "creature-script-broadcast");
    RequireNotiRoundTrip(catalog, "SKILLINFO", "two-page-skill-info", """{"pages":[{"headerValue":10,"entries":[{"slot":1,"skillId":100,"level":2,"extraValues":[3,4]}]},{"headerValue":20,"entries":[]}],"tail0":30,"tail1":40}""", "two-page-skill-info");
    RequireNotiRoundTrip(catalog, "COMBO_SKILL_INFO", "dark-knight-combo-pages", """{"reserved":0,"pages":[{"pageIndex":0,"roots":[{"rootSkillId":100,"childSkillIds":[101,102]}]},{"pageIndex":1,"roots":[]}]}""", "dark-knight-combo-pages");
    RequireNotiRoundTrip(catalog, "DUNGEON_INFO", "dungeon-info", """{"dungeonId":100,"difficulty":2,"bossX":3,"bossY":4,"extraPairGroups":[{"pairs":[{"first":1,"second":2}]}],"hellPartyEnabled":1,"value1":12,"packetSeed":4294967295}""", "dungeon-info");
    RequireNotiRoundTrip(catalog, "START_MAP", "start-map-revisit", """{"x":1,"y":2,"randomSeed":3,"roomStateValue":1,"partyMemberIndex":255}""", "start-map-revisit");
    RequireNotiRoundTrip(catalog, "START_MAP", "start-map-standard", """{"x":1,"y":2,"randomSeed":3,"mapIndex":4,"monsters":[],"extraEntries":[],"ridableGroups":[],"partyMemberIndex":255}""", "start-map-standard");
    RequireNotiRoundTrip(catalog, "CLEAR_DUNGEON_REWARD", "clear-reward", $"{{\"clearBaseExp\":10,\"bonusExpSlots\":[],\"postBaseSlots\":[],\"scoreSlots\":[],\"freeCardItemId\":0,\"freeCardGold\":20,\"freeCardSeatFlagsHex\":\"{new string('0', 14)}\",\"buffTable0Hex\":\"{new string('0', 16)}\",\"buffTable1Hex\":\"{new string('0', 16)}\",\"monsterExp\":30}}", "clear-reward");
    RequireNotiRoundTrip(catalog, "DIE_MONSTER", "monster-death-drops", $"{{\"monsterSequenceId\":7,\"drops\":[],\"fixedTailHex\":\"0000FF00\"}}", "monster-death-drops");
    RequireNotiRoundTrip(catalog, "DEATH_TOWER_INFO", "tower-info", """{"dungeonId":7,"endStage":10,"randomBuffType":11}""", "tower-info");
    RequireNotiRoundTrip(catalog, "START_DEATH_TOWER_MAP", "tower-stage-map", """{"currentStage":1,"randomSeed":2,"mapId":3,"monsters":[],"items":[]}""", "tower-stage-map");
    RequireNotiRoundTrip(catalog, "DEATH_TOWER_STATE_RANKING", "tower-ranking", """{"clearTimeMilliseconds":100,"clearedFloorCount":5,"dungeonId":7,"groups":[]}""", "tower-ranking");
    RequireNotiRoundTrip(catalog, "DEATH_TOWER_STATE_REWARD", "tower-reward", """{"rewardExp":100,"groups":[]}""", "tower-reward");
    RequireNotiRoundTrip(catalog, "DEATH_TOWER_STATE_EPLP", "tower-eplp-state", """{"allMembersHaveRequiredItem":1}""", "tower-eplp-state");
    RequireNotiRoundTrip(catalog, "BLOOD_DUNGEON_STATE_RANKING", "blood-ranking", """{"playTimeMilliseconds":1,"currentRound":2,"bestTimeMilliseconds":3,"bestRound":4,"maxRound":5,"rewardExperience":6}""", "blood-ranking");
    RequireNotiRoundTrip(catalog, "BLOOD_DUNGEON_STATE_REWARD", "blood-reward", $"{{\"currentRound\":1,\"maxRound\":5,\"rewards\":[],\"groupTailHex\":\"000000\"}}", "blood-reward");
    RequireNotiRoundTrip(catalog, "BLOOD_MONSTER_SPAWN", "blood-monster-wave", """{"monsters":[],"tailValue":0}""", "blood-monster-wave");
    RequireNotiRoundTrip(catalog, "START_BLOOD_MAP", "blood-map-revisit", """{"x":1,"y":2,"seed":3}""", "blood-map-revisit");
    RequireNotiRoundTrip(catalog, "START_BLOOD_MAP", "blood-map-standard", """{"x":1,"y":2,"seed":3,"mapId":4}""", "blood-map-standard");
    RequireNotiRoundTrip(catalog, "BLOOD_ROUND_INTERVAL_TIME", "blood-round-interval", """{"round":2,"intervalMilliseconds":3000}""", "blood-round-interval");
    RequireNotiRoundTrip(catalog, "HELL_PARTY_MONSTER_INFO", "hell-party-monster-levels", """{"entries":[{"actorId":7,"level":100}]}""", "hell-party-monster-levels");
    RequireNotiRoundTrip(catalog, "DUNGEON_PERMISSION", "permission-list", """{"entries":[{"dungeonId":100,"clearState":1},{"dungeonId":200,"clearState":0}]}""", "permission-list");
    RequireNotiRoundTrip(catalog, "GAME_OPTION", "account-game-options", """{"mainGameOptionHex":"0102","quickchatBank0Hex":"0304","quickchatBank1Hex":"0506"}""", "account-game-options");
    RequireNotiRoundTrip(catalog, "LOAD_COOLTIME_ITEM_INFO", "cooltime-item-values", """{"entries":[{"itemId":100,"value":10}]}""", "cooltime-item-values");
    RequireNotiRoundTrip(catalog, "LOAD_EFFECT_ITEM_INFO", "effect-item-values", """{"entries":[{"itemId":200,"value":20}]}""", "effect-item-values");
    RequireNotiRoundTrip(catalog, "HOTKEY_OPTION", "account-hotkeys", """{"keyType":1,"hotkeysHex":"01000200"}""", "account-hotkeys");
    RequireNotiRoundTrip(catalog, "COLLECT_BOX", "collection-box-state", """{"boxIndex":2,"version":1,"remainSeconds":3600,"statusFlags":0,"itemIds":[100,200]}""", "collection-box-state");
    RequireNotiRoundTrip(catalog, "INCREASE_CHANCE_LOTTERY_ALL", "increase-chance-all-state", """{"activeState":2,"currentItemTemplateId":1234,"newRewardIndex":5,"records":[{"itemTemplateId":1234,"claimedRewardIndexes":[0,4]}]}""", "increase-chance-all-state");
    RequireNotiRoundTrip(catalog, "RAID_SET_SYMBOL", "symbol-table", """{"entries":[{"symbolId":110,"value":1},{"symbolId":111,"value":0}]}""", "symbol-table");
    RequireNotiRoundTrip(catalog, "RAID_DUNGEON_PARTICIPATION_INFO", "participation-enter", """{"targetId":7,"op":0,"memberUserIds":[10,11]}""", "participation-enter");
    RequireNotiRoundTrip(catalog, "RAID_DUNGEON_PARTICIPATION_INFO", "participation-exit", """{"targetId":7,"op":2,"memberUserIds":[10]}""", "participation-exit");
    RequireNotiRoundTrip(catalog, "RAID_WAITING_LIST", "waiting-member-list", """{"entries":[{"userId":7,"partyIndex":2}]}""", "waiting-member-list");
    RequireNotiRoundTrip(catalog, "RAID_ENTRY_COST_INFO", "entry-cost-statuses", """{"entries":[{"userId":7,"ready":true,"ownedCount":3}]}""", "entry-cost-statuses");
    RequireNotiRoundTrip(catalog, "RAID_REWARD_LIST", "reward-list", """{"rewardType":1,"entries":[{"userId":7,"cardType":1,"flags":0,"itemId":100,"quantity":2}]}""", "reward-list");
    RequireNotiRoundTrip(catalog, "RAID_BUFF_SYSTEM", "buff-status-groups", """{"groups":[{"buffType":1,"entries":[{"partyIndex":0,"userId":7,"activeUntilTimestamp":100,"cooldownUntilTimestamp":200}]}]}""", "buff-status-groups");
    RequireNotiRoundTrip(catalog, "RAID_MONSTER_HP", "monster-situation-status", """{"entries":[{"situationIndex":1,"memberIds":[7],"usedCoinCount":2,"runtimeValues":[100]}]}""", "monster-situation-status");

    var comboC2s = catalog.Find(PacketFlow.ClientToServer, PacketKind.Cmd, "COMBO_SKILL_INFO");
    var comboS2c = catalog.Find(PacketFlow.ServerToClient, PacketKind.Noti, "COMBO_SKILL_INFO");
    Require(comboC2s is not null && comboS2c is not null, "COMBO_SKILL_INFO direction definitions are missing");
    Require(comboC2s!.Type != comboS2c!.Type, "COMBO_SKILL_INFO command and notification types must remain distinct");
    Require(comboC2s.Kind == PacketKind.Cmd && comboS2c.Kind == PacketKind.Noti, "COMBO_SKILL_INFO direction/kind isolation mismatch");

    var compareArguments = JsonDocument.Parse("""{"rawA":"002F021D000000000000000000000002000C222E1600005A0B34230000","rawB":"002F021D000000000000000000000002000C222E1600005A0B34230001"}""").RootElement;
    using (var compareDocument = JsonDocument.Parse(JsonSerializer.Serialize(tools.ComparePackets(compareArguments))))
    {
        var comparison = compareDocument.RootElement;
        Require(comparison.GetProperty("opcodeEqual").GetBoolean(), "packet comparison opcode equality mismatch");
        Require(comparison.GetProperty("changedByteCount").GetInt32() == 1, "packet comparison changed-byte count mismatch");
        Require(comparison.GetProperty("semanticFieldDiffs").EnumerateArray()
            .Any(item => item.GetProperty("path").GetString() == "entries[1].monsterCode"),
            "packet comparison semantic field diff is missing");
        var packetA = comparison.GetProperty("packetA");
        Require(packetA.GetProperty("packetLayout").GetProperty("headerLength").GetInt32() == 15,
            "packet comparison egress envelope layout mismatch");
    }

    using (var coverageDocument = JsonDocument.Parse(JsonSerializer.Serialize(tools.GetCoverage())))
    {
        var s2cCmd = coverageDocument.RootElement.GetProperty("groups").EnumerateArray()
            .Single(item => item.GetProperty("flow").GetString() == "s2c" && item.GetProperty("kind").GetString() == "cmd");
    Require(s2cCmd.GetProperty("supported").GetInt32() == 128, "S2C CMD supported coverage mismatch");
    Require(s2cCmd.GetProperty("structured").GetInt32() == 128, "S2C CMD structured coverage is incomplete");
        Require(s2cCmd.GetProperty("partial").GetInt32() == 0, "S2C CMD partial coverage must be zero");
        Require(s2cCmd.GetProperty("rawFallback").GetInt32() == 0, "S2C CMD raw fallback must be zero");
    }

    var supportedInbound = catalog.Types.Count(item => item.Flow == PacketFlow.ClientToServer && item.Kind == PacketKind.Cmd && item.Supported);
    Require(supportedInbound == 210, $"expected 210 inbound CMD types, got {supportedInbound}");
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, supportedInbound, totalDefinitions = catalog.Types.Count }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

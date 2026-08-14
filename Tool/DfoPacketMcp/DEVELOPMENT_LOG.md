### 2026-08-13 Milestone 8 - Extended Notification Coverage

- Version advanced to `0.2.5`.
- Added `ExtendedNotificationCodec.cs` for the remaining 18 notification definitions previously reported as raw/partial. Decode coverage is complete; Daily Challenge and Tournament reward encoders support round-trip fields, while complex APC/tag/Raid modify tails remain conservatively exposed as raw/opaque fields.
- Kept conservative diagnostics and raw tails for nested or builder-dependent sections; no unsupported bytes are silently discarded.
- Verification passed: build, self-test, coverage, and standalone runtime loading.
- Coverage snapshot: C2S CMD `210 supported` with `100 structured / 70 inferred / 14 ignoredBody / 9 lengthOnly / 17 empty`; S2C CMD `117 structured`; S2C NOTI `101 structured / 0 partial / 0 rawFallback`.
- Variant count increased to `755`; all variant selection remains keyed by flow, kind, type, name, and discriminator.
# DfoPacketMcp Development Log

## 2026-08-13

### Requirements Recorded

- The tool must be standalone and must not reference or start `DfoServer`.
- Current server-supported packets, approximately 200 inbound commands, must be inventoried and decoded.
- Inbound and outbound packets with the same type are separate protocol definitions.
- CMD and NOTI packet namespaces are separate even when their numeric type is equal.
- A single flow/kind/type may have multiple incompatible body structures.
- Variant selection must use body discriminators such as subtype, length, or fixed values.
- Packet names, semantic field names, decoded parameters, and encode callbacks must be exposed through MCP.
- Tool implementation progress and releases must be logged continuously.

### Inventory

- `CmdPacketType`: 1,086 enum entries.
- `NotiPacketType`: 1,036 enum entries.
- `GameProtocolHandler` inbound registrations: 210 unique resolvable CMD types.
- Resolved static outbound types: 190 unique flow/kind/type definitions.
- Dynamic outbound envelope sites requiring contextual resolution: 187.
- Existing dedicated request parser files: 50+ parser/model files across login, inventory, dungeon, party, PvP, quest, town, lottery, Cera Shop, and expert-job areas.

### Confirmed Envelope Rules

- Client-to-server ingress uses a 13-byte header.
- Server-to-client egress uses a 15-byte envelope.
- The server writes `length = bodyLength + 15` for egress.
- Direction must be explicit when a byte sequence is ambiguous.

### Confirmed Polymorphism

- `S2C NOTI 0x0002 USERINFO` uses body byte `+0x00` as a subtype discriminator.
- Confirmed variants include subtype 0, 1, 2, and 3.
- Subtype 0 is character state/appearance, subtype 1 is stats/equipment/skills, subtype 2 is the account character roster, and subtype 3 is inspect-player data.

### Implementation State

- Protocol enum JSON generation: complete.
- Inbound registration evidence generation: complete.
- Outbound envelope evidence generation: partial; static sites are resolved and dynamic sites are retained for audit.
- Direction/kind/type/variant data model: implemented.
- Structured schema registry: active with manual and source-inferred schemas.
- MCP stdio transport: implemented.
- Verification: build, self-test, and JSON-RPC smoke test passing.

### 2026-08-13 Milestone 1

- Build: 0 warnings, 0 errors.
- Self-test: passed.
- MCP initialize, tools/list, and tools/call smoke test: passed.
- Direction isolation test: the same CMD type resolves to separate C2S request and S2C response definitions.
- `S2C NOTI 0x0002 USERINFO` discriminator test: subtype 0, 1, 2, and 3 routes passed.
- Current C2S CMD coverage across 210 supported types:
  - 58 manually confirmed structured schemas;
  - 68 handler/parser-inferred field schemas;
  - 14 handlers that explicitly ignore body bytes;
  - 7 length-only schemas;
  - 17 explicitly empty requests;
  - 46 unresolved request bodies.
- Current statically resolved outbound definitions:
  - 89 S2C CMD response types;
  - 101 S2C NOTI types;
  - 187 dynamic outbound sites retained for contextual resolution.

### Accuracy Policy

- A field is named semantically only when the current source contains parser, builder, handler, test, or documentation evidence.
- Undocumented bytes remain explicitly named `unknown`, `reserved`, or `raw`; the tool does not invent semantics.
- Ambiguous variants return candidate variants and diagnostics instead of silently choosing one.

### 2026-08-13 Milestone 2 - Coverage Correction

- Re-ran the source generator after fixing multi-line registrations, handler-context separation, and dynamic outbound type propagation.
- Corrected C2S CMD coverage across all 210 registered types:
  - 100 manually confirmed structured schemas;
  - 70 handler/parser-inferred field schemas;
  - 14 handlers that explicitly ignore request bytes;
  - 9 length-only schemas;
  - 17 explicitly empty requests;
  - 0 opaque registered requests.
- Corrected statically resolved outbound inventory:
  - 117 S2C CMD definitions;
  - 101 S2C NOTI definitions;
  - 218 total flow/kind/type definitions;
  - 112 unresolved call sites: 110 dynamic-type and 2 dynamic-command.
- The older Milestone 1 counts are retained as historical measurements and are superseded by this milestone.

### 2026-08-13 Milestone 3 - Outbound Schema Extraction

- Added fixed builder write-sequence extraction for `WriteByte`, `WriteInt16`, `WriteUInt16`, `WriteInt32`, `WriteUInt32`, `WriteZeroBytes`, and dstr length prefixes.
- Added exact fixed-body matching for constant inline byte arrays.
- Restricted automatic fixed-layout extraction to straight-line builders; conditional, looping, and dynamic byte-copy builders remain explicit unresolved variants.
- Attached USERINFO subtype 0/1/2/3 manual variants to the S2C NOTI catalog entry.
- Added outbound variant decoding and encoding using schema-backed variants.
- Added explicit ambiguity responses when multiple S2C variants accept the same body.
- Current generated outbound evidence:
  - 432 distinct builder variants;
  - 205 variants with generated schema evidence;
  - 48 variants with exact fixed-body discriminators.
- Current definition-level structured coverage:
  - 87 of 117 S2C CMD definitions;
  - 27 of 101 S2C NOTI definitions;
  - remaining 30 CMD and 74 NOTI definitions are accurately reported as raw fallback.
- Verification completed:
  - protocol generation succeeded;
  - Debug build succeeded with 0 warnings and 0 errors;
  - self-test passed;
  - direction isolation, same-direction multi-variant selection, USERINFO unknown/mismatched subtype handling, inferred encode/decode round-trip, and mixed SEND/RECV capture tests passed.

### Release Record

- Version: `0.2.0`.
- Date: `2026-08-13`.
- Runtime dependency rule: published MCP remains independent of `DfoServer`; only protocol regeneration reads server source.
- Completion statement: C2S registered packet classification is complete; complex S2C field-level parsing is still in progress and must not be reported as fully complete.

### 2026-08-13 Milestone 4 - Inventory, Pet, and Skill Notifications

- Version advanced to `0.2.1`.
- Added independent manual codecs in `Protocol/InventoryPetSkillNotificationCodec.cs`; runtime still has no `DfoServer` project dependency.
- Added `UPDATE_ITEM_LIST` variants for common 84-byte and avatar 126-byte entries.
- Added `ITEM_LIST` variants for common, avatar, pet, and account-cargo list headers.
- Added field-level `CREATURE_ITEM_LIST` parsing with variable-length creature text, plus `CREATURE_STATE` dual variants:
  - `runtime-state-pair`: creature key and state value, exact 8 bytes.
  - `creature-entry-refresh`: one variable creature entry without a list count.
- Added `ITEM_LOCK_LIST` state-dependent records, `CREATURE_SCRIPT_MESSAGE` dstr payloads, two-page `SKILLINFO`, and Dark Knight `COMBO_SKILL_INFO` page/root/child records.
- Added self-tests covering all new variants and direction/type isolation for C2S versus S2C `COMBO_SKILL_INFO`.
- Verification: build passed with 0 warnings and 0 errors; self-test passed.
- Coverage snapshot: C2S CMD 100 structured / 70 inferred / 14 ignoredBody / 9 lengthOnly / 17 empty / 0 opaque; S2C CMD 117 structured; S2C NOTI 61 structured / 3 partial / 37 raw fallback.

### 2026-08-13 Milestone 5 - Dungeon, Death Tower, and Blood Altar Notifications

- Version advanced to `0.2.2`.
- Added `Protocol/DungeonNotificationCodec.cs` with source-backed field codecs for 16 S2C NOTI names.
- Added explicit `START_MAP` standard/revisit and `START_BLOOD_MAP` standard/revisit variants.
- Added counted monster/drop/reward records for dungeon, Death Tower, and Blood Altar packets; unknown bytes remain `reserved`, `raw`, or fixed-tail fields.
- Added round-trip self-tests for every new manual variant.
- Verification: Debug build passed with 0 warnings and 0 errors; self-test passed.
- Coverage snapshot: C2S CMD 100 structured / 70 inferred / 14 ignoredBody / 9 lengthOnly / 17 empty / 0 opaque; S2C CMD 117 structured; S2C NOTI 71 structured / 3 partial / 27 raw fallback.

### 2026-08-13 Milestone 6 - Initialization and Account State Notifications

- Version advanced to `0.2.3`.
- Added `Protocol/InitNotificationCodec.cs` for permission lists, account game options, cooldown/effect item values, account hotkeys, collection-box state, and increase-chance lottery state.
- Added source-backed encode/decode round-trip coverage for all seven new notification codecs.
- Runtime remains independent of `DfoServer`; only generated JSON source evidence contains server paths.

### 2026-08-13 Milestone 7 - Raid Notifications

- Version advanced to `0.2.4`.
- Added `Protocol/RaidNotificationCodec.cs` for seven Raid notification types.
- Added explicit `RAID_DUNGEON_PARTICIPATION_INFO` enter/exit variants selected by `op`.
- Added round-trip tests for symbol tables, participation lists, waiting members, entry costs, rewards, buff groups, and monster situation rows.
- Final verification for this milestone: Debug/Release build passed with 0 warnings and 0 errors; self-test passed; publish output is independent of `DfoServer`; MCP stdio smoke test passed.
- Coverage snapshot: C2S CMD 100 structured / 70 inferred / 14 ignoredBody / 9 lengthOnly / 17 empty / 0 opaque; S2C CMD 117 structured; S2C NOTI 83 structured / 3 partial / 15 raw fallback.
- At this milestone, the static S2C NOTI raw/partial set is complete. The historical dynamic-site inventory still contains 112 call sites (110 dynamic-type, 2 dynamic-command); codecs added later cover stable layouts but do not claim all dynamic sites are statically resolved.

### 2026-08-13 Milestone 9 - Dynamic Outbound CMD Audit

- Added standalone codecs for dynamic outbound CMD sites with stable wire layouts: expert-job store/disjoint/repair/upgrade/enchant/compound/give-up, PvP room enter, and daily challenge reward acknowledgements.
- Added explicit variants for status/error bodies and the two expert-store enter layouts.
- Updated coverage assertions and round-trip self-tests; S2C CMD coverage is now `128 structured / 0 partial / 0 rawFallback`.
- The unresolved-site inventory is intentionally retained as a historical audit artifact; adding a codec does not claim all dynamic call sites are statically resolved.

### 2026-08-13 Milestone 10 - Dynamic CMD Registry Integration

- Registered the new dynamic CMD codecs as standalone S2C definitions and added MCP encode/decode callbacks for each variant.
- Corrected `ENTER_EXPERT_JOB_STORE` enchant-store success variant to exact 8-byte body (`status + kind + ownerUserId + endurance`) and added a round-trip regression test.
- Final verification: Debug build, self-test, coverage, and Release publish passed; corrected the enchant-store enter body to its exact 8-byte layout; publish output contains no server assembly dependency.


### 2026-08-13 Milestone 10 - Coverage Verification

- Verified final standalone build and self-test after notification and dynamic CMD codecs.
- Coverage snapshot: C2S CMD 210 supported (100 structured, 70 inferred, 14 ignoredBody, 9 lengthOnly, 17 empty); S2C CMD 128 structured; S2C NOTI 101 structured; no partial/raw fallback in supported groups.





### 2026-08-13 Milestone 11 - Random Runtime Simulation and Native Log Extraction

- Updated `PacketToolService.decode_capture` to accept native `game-packets-*.log` lines with timestamps, `command=0xNN`, `type=0xNNNN`, and `raw:` payloads.
- Redacted or truncated raw markers are skipped or reported with diagnostics; they are never treated as valid complete packets.
- Ran random `encode_packet -> decode_packet` simulations across C2S CMD, S2C CMD, and S2C NOTI, including `USERINFO` subtype and multi-record notification variants.
- Verified direct decoding from `D:\DXF_ServerS4A12B\TestServer\Logs\game-packets-20260814-011820-22844-c5d4f51b9ffe43a2905dbb44f04d16ed.log`.


### 2026-08-13 Milestone 12 - Opcode Layout and Raw Comparison

- Version advanced to `0.2.8`.
- Added `compare_packets` so an AI can provide two raw packets and receive byte-range, envelope, opcode, variant, and semantic-field differences directly.
- Added full envelope segment maps and absolute/body-relative byte offsets to `decode_packet` output.
- Clarified the protocol model: envelopes share one of two fixed layouts, while body layout is selected by `flow + kind + opcode + variant` and must not be merged across directions or variants.
- Added a self-test mapping a single changed byte in `MINIMAP_ICON_INFO` to the decoded `entries[1].monsterCode` field.


### 2026-08-13 Milestone 13 - Source-Independent Protocol Snapshot

- Version advanced to `0.2.9`.
- Consolidated all runtime protocol definitions into `protocol-catalog.json`; development extraction JSON remains generation-only.
- Added `protocol-manifest.json` SHA-256 integrity enforcement.
- Removed server source paths and builder expressions from the runtime snapshot.
- Verified the published directory outside `D:\86JP-main` without a `Server` directory.
- Verified initialize, protocol coverage, raw comparison, and tamper rejection from the external directory.

### 2026-08-13 Milestone 14 - Chinese README Reconstruction

- Rebuilt `README.md` as UTF-8 Chinese documentation and removed the corrupted mixed-encoding content.
- Documented the direction-aware `flow + kind + opcode + variant` key and separate 13-byte C2S / 15-byte S2C envelope layouts.
- Added standalone protocol snapshot, MCP client setup, all eight tools, raw decode/compare, native log extraction, protocol regeneration, coverage limits, and deployment checks.
- Kept version `0.2.9` because this milestone changes documentation only and does not change runtime or protocol behavior.


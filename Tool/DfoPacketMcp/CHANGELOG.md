## 0.2.9 - 2026-08-13

### Added

- Added a single standalone `Protocol/protocol-catalog.json` runtime snapshot containing all direction/kind/opcode/variant definitions.
- Added `Protocol/protocol-manifest.json` with format/version/count metadata and SHA-256 integrity verification.
- Added `--export-protocol` to rebuild the source-independent runtime snapshot from development protocol files.

### Changed

- Runtime now loads protocol data relative to the executable directory and requires no repository or server source tree.
- Release publish includes only the standalone catalog and manifest, not development extraction/audit JSON files.
- Server source paths, builder expressions, and field source paths are removed from the standalone snapshot.

### Verification

- Copied the published directory outside the repository and successfully ran initialize, coverage, and packet comparison.
- Confirmed coverage reports `sourceIndependent: true` and the catalog SHA-256.
- Confirmed a one-byte catalog modification is rejected during startup because the manifest hash no longer matches.
## 0.2.8 - 2026-08-13

### Added

- Added `compare_packets` for direct comparison of two AI-provided raw packets.
- Added complete 13-byte ingress and 15-byte egress envelope layouts with absolute offsets, widths, little-endian types, raw hex, decoded values, and byte tables.
- Added byte-range, envelope-header, opcode, variant, and semantic-field comparisons.
- Added optional per-packet transport/variant selection and automatic transport detection when flow is omitted.

### Changed

- `decode_packet` now returns `packetLayout` and accepts an optional explicit variant.
- Raw packet bytes are retained exactly as supplied instead of being reconstructed for layout output.

### Verification

- Added regression coverage proving that a one-byte raw change is mapped to `entries[1].monsterCode` for `S2C NOTI 0x022F MINIMAP_ICON_INFO`.
- Debug build and self-test passed.
## 0.2.7 - 2026-08-13

### Added

- Native extraction support for `game-packets-*.log` files with timestamps, `command=0xNN`, `type=0xNNNN`, and `raw:` lines.
- Random runtime simulation coverage using `encode_packet` followed by `decode_packet` across C2S CMD, S2C CMD, and S2C NOTI variants.

### Verification

- Native log smoke test decoded 12 records directly from the TestServer log without preprocessing.
- Build, self-test, coverage, and Release publish passed.
- Coverage: C2S CMD 210 supported, S2C CMD 128 structured, S2C NOTI 101 structured.
## 0.2.6 - 2026-08-13

### Added

- Added `Protocol/ExtendedNotificationCodec.cs` and connected it to the standalone runtime.
- Completed field-level codecs for the former S2C NOTI raw/partial set, including quest lists, expert-job state, Blood Altar exit-ready, secret shop NPC/items, Tower of Despair APC/reward, title-book categories, tournament info/map/rewards, buff lists, minimap icons, daily challenge state, server broadcast variants, tag-character records, and Raid modify operation variants.
- Preserved explicit variants for same type with different builders and discriminators.

### Verification

- Debug build: 0 warnings, 0 errors.
- Self-test: passed.
- Coverage: S2C NOTI `101 structured / 0 partial / 0 rawFallback`; variants increased from 733 to 755.
- Runtime remains independent of `DfoServer`; only source evidence paths reference server code.




### Dynamic CMD Audit (0.2.6)

- Added standalone S2C CMD codecs for expert-job/store, PvP-enter, and daily-challenge dynamic outbound bodies.
- Coverage: S2C CMD 128 structured / 0 partial / 0 rawFallback.






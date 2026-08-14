# DfoPacketMcp

`DfoPacketMcp` 是一个独立运行的 DFO 封包分析 MCP，用于快速排查客户端与服务端之间的游戏协议。AI 可以直接提供完整 raw、body 或抓包日志，由工具按方向、包类型、opcode 和 variant 定位协议定义，解析字段语义，并对两条 raw 做逐字节和字段级对比。

当前版本：`0.2.9`  
版本记录日期：`2026-08-13`

## 核心规则

每个封包定义使用以下四维键唯一标识：

```text
flow + kind + opcode/type + variant
```

- `flow`: `c2s`（客户端到服务端）或 `s2c`（服务端到客户端）。
- `kind`: `cmd` 或 `noti`。
- `opcode/type`: 16 位封包序号，文档和结果中通常显示为 `0xNNNN`。
- `variant`: 同一方向、同一 kind、同一 opcode 下的具体 body 结构。

不能只按 opcode 或包名合并协议：

- 相同 opcode 在入站和出站可以对应不同包名、不同字段结构。
- 相同包名和 opcode 可以同时用于入站和出站，但两侧定义仍然独立。
- 相同 `flow + kind + opcode` 可以存在多个不兼容结构，例如由 subtype、状态、长度或固定值决定的 variant。
- `S2C NOTI 0x0002 USERINFO` 已按 subtype 分离多个结构。
- variant 无法从 raw 唯一判断时，工具会返回候选项和诊断信息，不会静默套用某个结构。此时应显式传入 `variant`。

## Envelope 结构

Envelope 是固定头部；opcode 对应的业务字段位于 body。所有多字节整数均按 little-endian 读取。

### C2S 入站：13 字节

| 绝对偏移 | 长度 | 字段 | 类型 | 说明 |
| ---: | ---: | --- | --- | --- |
| `0x00` | 1 | `commandClass` | `u8` | `0` 为 noti，`1` 为 cmd；当前服务端注册的 C2S 为 cmd |
| `0x01` | 2 | `opcodeType` | `u16le` | opcode/type |
| `0x03` | 4 | `packetLength` | `u32le` | 完整封包长度，包含 13 字节头部 |
| `0x07` | 4 | `firstControl` | `u32le` | 校验或控制字段 |
| `0x0B` | 2 | `sequence` | `u16le` | 入站序列号 |
| `0x0D` | 可变 | `body` | `bytes` | opcode 对应的业务结构 |

### S2C 出站：15 字节

| 绝对偏移 | 长度 | 字段 | 类型 | 说明 |
| ---: | ---: | --- | --- | --- |
| `0x00` | 1 | `commandClass` | `u8` | `0` 为 noti，`1` 为 cmd |
| `0x01` | 2 | `opcodeType` | `u16le` | opcode/type |
| `0x03` | 4 | `packetLength` | `u32le` | 完整封包长度，包含 15 字节头部 |
| `0x07` | 4 | `firstControl` | `u32le` | 校验或控制字段 |
| `0x0B` | 4 | `secondControl` | `u32le` | 出站控制字段 |
| `0x0F` | 可变 | `body` | `bytes` | opcode 对应的业务结构 |

`decode_packet` 会同时返回：

- 头部每个分段的绝对偏移、长度、类型、原始十六进制和值。
- body 每个字节的绝对偏移和 body 相对偏移。
- 已确认 schema 字段的名称、偏移、宽度、类型和值。
- 匹配到的包名、variant、语义字段和诊断信息。

自动判断 13/15 字节 envelope 可能存在歧义。已知方向时应传入 `flow` 和 `transport`，不要只依赖自动判断。

## 独立协议快照

运行时协议已合并为两个文件：

```text
Protocol/
├── protocol-catalog.json
└── protocol-manifest.json
```

- `protocol-catalog.json`：包含方向、kind、opcode、包名、variant、schema 和语义元数据。
- `protocol-manifest.json`：包含格式版本、协议版本、定义数量和目录 SHA-256。
- 当前 `protocol-catalog.json` SHA-256：`1DB08FA640D286705B0FB2C254216A5F04661406DA6BF516E59B7344C19ED738`。
- 快照不包含服务端源码路径、builder 表达式或字段源码路径。

运行时通过 `AppContext.BaseDirectory` 从可执行文件旁加载 `Protocol` 目录，不搜索仓库，不读取或启动 `Server`、`DfoServer`，也不依赖生成脚本和服务端 DLL。目录缺失、内容损坏或 SHA-256 不匹配时会拒绝启动。

协议维护阶段仍需读取服务端源码来重新提取定义；生成后的发布目录可以完全脱离服务端项目运行。

## 构建与验证

需要 .NET 10 SDK。在仓库根目录 `D:\86JP-main` 执行：

```powershell
dotnet build Tool\DfoPacketMcp\DfoPacketMcp.csproj -c Debug
dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj -- --self-test
dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj -- --coverage
```

启动 stdio MCP：

```powershell
dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj
```

发布独立目录：

```powershell
dotnet publish Tool\DfoPacketMcp\DfoPacketMcp.csproj -c Release -o Tool\DfoPacketMcp\publish --no-restore
```

发布后必须整体复制 `publish` 目录，保留其中的 `Protocol/protocol-catalog.json` 和 `Protocol/protocol-manifest.json`。

## MCP 客户端配置

开发态：

```json
{
  "mcpServers": {
    "dfo-packet": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "D:\\86JP-main\\Tool\\DfoPacketMcp\\DfoPacketMcp.csproj"
      ]
    }
  }
}
```

发布态：

```json
{
  "mcpServers": {
    "dfo-packet": {
      "command": "D:\\86JP-main\\Tool\\DfoPacketMcp\\publish\\DfoPacketMcp.exe",
      "args": []
    }
  }
}
```

## MCP 工具

| 工具 | 用途 |
| --- | --- |
| `list_packets` | 按 `flow`、`kind`、名称或 opcode 查询定义，不合并相同数字序号 |
| `describe_packet` | 查看一个方向化封包的语义、状态、schema 和全部 variant |
| `decode_packet` | 解析完整的 13 字节入站或 15 字节出站 raw |
| `compare_packets` | 对比两条完整 raw 的 envelope、opcode、variant、字节区间和语义字段 |
| `decode_body` | 按显式 `flow/kind/packet/variant` 只解析 body |
| `encode_packet` | 使用语义字段回调或原始 body 构造完整封包 |
| `decode_capture` | 解析抓包文本或本地 `game-packets-*.log` 文件 |
| `protocol_coverage` | 返回按方向和 kind 划分的实时覆盖率及快照信息 |

`packet` 参数可使用十六进制 opcode、十进制 opcode、枚举名或完整方向化名称。raw 可以由 AI 直接通过 hex 或 Base64 提供，不要求先落盘。

## 单包完整解析

下面是一条 `S2C NOTI 0x022F MINIMAP_ICON_INFO` 完整 raw：

```json
{
  "flow": "s2c",
  "transport": "egress",
  "hex": "002F021D000000000000000000000002000C222E1600005A0B34230000"
}
```

调用 `decode_packet` 后，重点查看：

- `packetLayout.headerLength`：当前 envelope 为 15 字节。
- `packetLayout.segments`：完整头部和 body 布局。
- `packetLayout.segments[].fields`：字段的绝对偏移与 body 相对偏移。
- `name`、`variant`、`fields`：包名、实际结构和语义参数。
- `diagnostics`：长度异常、variant 歧义或未确认字段。

## 两条 raw 直接对比

AI 可以直接向 `compare_packets` 提供两条 raw：

```json
{
  "flow": "s2c",
  "transport": "egress",
  "rawA": "002F021D000000000000000000000002000C222E1600005A0B34230000",
  "rawB": "002F021D000000000000000000000002000C222E1600005A0B34230001"
}
```

该示例只修改最后一个字节。结果会给出：

- `byteDiffs`：变化字节的绝对偏移、长度、A/B 十六进制和所属区域。
- `headerDiffs`：envelope 字段差异。
- `opcodeEqual`、`variantEqual`：是否为同一 opcode 和同一 body 结构。
- `semanticFieldDiffs`：本例变化会定位到 `entries[1].monsterCode`。
- `comparisonDiagnostics`：跨 13/15 字节 envelope、不同 opcode 或不同 variant 时的解释。

如果两条 raw 方向或 envelope 不同，可分别传 `transportA`、`transportB`。同一 opcode 存在多种结构且无法自动判定时，可分别传 `variantA`、`variantB`。

## 只解析或构造 body

只解析 body 时必须给出完整协议键，避免错误套用另一个方向或结构：

```json
{
  "flow": "s2c",
  "kind": "noti",
  "packet": "0x0002",
  "variant": "subtype0-character-state",
  "bodyHex": "..."
}
```

构造封包时可以提供 `fields`，也可以由 AI 直接提供 `bodyHex` 或 `bodyBase64`：

```json
{
  "flow": "s2c",
  "kind": "cmd",
  "packet": "ENTER_PVP_ROOM",
  "variant": "enter-success",
  "fields": {
    "readyStates": [1, 0, 0, 0, 0, 0, 0, 0]
  },
  "transport": "egress"
}
```

## 解析原生日志

`decode_capture` 支持包含 `SEND`/`RECV`、`command=0xNN`、`type=0xNNNN` 和下一行 `raw:` 的原生日志。可直接传入本地路径：

```json
{
  "path": "D:\\DXF_ServerS4A12B\\TestServer\\Logs\\game-packets-20260814-011820-22844-c5d4f51b9ffe43a2905dbb44f04d16ed.log",
  "limit": 100
}
```

也可以由 AI 读取 `D:\DXF_ServerS4A12B\TestServer\Logs\game-packets-*.log` 后，将抽取的日志文本放入 `text`。被截断或脱敏的 raw 会跳过或产生诊断，不会当作完整封包解析。

## 重新生成协议

仅在维护协议目录时执行，生成阶段需要仓库内的服务端源码：

```powershell
powershell -ExecutionPolicy Bypass -File Tool\DfoPacketMcp\generate-protocol.ps1

dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj -- `
  --export-protocol `
  --source-root Tool\DfoPacketMcp `
  --output Tool\DfoPacketMcp\Protocol\protocol-catalog.json

dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj -- --self-test
dotnet run --project Tool\DfoPacketMcp\DfoPacketMcp.csproj -- --coverage
dotnet publish Tool\DfoPacketMcp\DfoPacketMcp.csproj -c Release -o Tool\DfoPacketMcp\publish --no-restore
```

`--export-protocol` 会同时更新 `protocol-catalog.json` 和对应 manifest 校验信息。生成、测试、覆盖率和发布任一步失败时，不应发布新快照。

## 当前覆盖率

`0.2.9` 的当前快照：

| 方向与类型 | 支持数 | 状态 |
| --- | ---: | --- |
| C2S CMD | 210 | `100 structured / 70 inferred / 14 ignoredBody / 9 lengthOnly / 17 empty / 0 opaque` |
| S2C CMD | 128 | `128 structured / 0 partial / 0 rawFallback` |
| S2C NOTI | 101 | `101 structured / 0 partial / 0 rawFallback` |

已支持 variant 总数为 `778`，其中 `362` 个带 schema；有 `67` 个受支持定义包含多个 schema variant。

覆盖率必须按状态理解：

- `structured`：存在已确认的字段级结构。
- `inferred`：依据处理器或解析器推导，仍应结合诊断核对。
- `ignoredBody`：服务端处理器明确忽略 body，不表示未知字节已有语义。
- `lengthOnly`：只确认长度约束，尚无完整字段语义。
- `empty`：已确认请求 body 为空。

因此不能声称 210 个 C2S CMD 的 body 都已完成字段级语义化。工具不会为缺少证据的字节虚构业务含义，未确认部分保留为 `unknown`、`reserved` 或 `raw`。

## 独立部署检查

1. 使用 Release 配置发布完整目录。
2. 确认 `DfoPacketMcp.exe` 旁存在 `Protocol/protocol-catalog.json` 和 `Protocol/protocol-manifest.json`。
3. 不复制 `Server`、`DfoServer`、生成脚本或服务端 DLL。
4. 在目标目录执行 `DfoPacketMcp.exe --coverage`，确认 `sourceIndependent` 为 `true` 且 SHA-256 与 manifest 一致。
5. 执行 `DfoPacketMcp.exe --self-test` 后再接入 MCP 客户端。
6. 将协议快照、`CHANGELOG.md`、`DEVELOPMENT_LOG.md` 和 `VERSION` 一并纳入发布与排障记录。

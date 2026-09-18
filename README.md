# CodingFire for Windows

Turn your AI coding token burn into a pixel campfire on the desktop.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%207%20%7C%208%20%7C%2010%20%7C%2011-lightgrey)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-3.5%20%2F%20CLR%202.0-512BD4)](#requirements)
[![Release](https://img.shields.io/github/v/release/wangsrGit119/codingfire-win)](../../releases)

**English** · [简体中文](#简体中文)

A small always-on-top campfire that reads the token usage logs your AI coding
tools already write to disk. The faster you burn tokens, the bigger the fire.

- **Fire intensity = current token burn rate.** Several clients at once still
  share a single fire; their rates are summed
- **Hover for today's usage card** with a live tok/s reading and the current tier
- **Read-only.** No network, no uploads, and prompts or code are never read
- **Zero runtime dependencies** - one `.exe` plus one `.config`

## Requirements

| OS | Needs |
|---|---|
| Windows 11 / 10 | Nothing - .NET 4.x is in-box |
| Windows 8 / 8.1 | Nothing - .NET 4.5 is in-box |
| Windows 7 SP1 | Nothing - .NET 3.5.1 is in-box |

The binary targets .NET Framework 3.5 / CLR 2.0 and declares both `v4.0` and
`v2.0.50727` under `supportedRuntime`, so a single build covers Win7 SP1 through
Win11. It is compiled `/platform:x86` and runs on 32- and 64-bit Windows via WoW64.

## Download and run

Grab the latest zip from [Releases](../../releases), unpack it anywhere and run
`CodingFire.exe`.

- **Hover the fire** - today's usage card, live tok/s, current tier
- **Drag it** - move it around, the position is remembered
- **Right-click / double-click the tray icon** - menu and statistics console

With no data yet the fire stays in an "embers" state. That is normal.

## Supported data sources

**23 read-only local sources (22 tools).** Nothing is uploaded, and no prompt or
source file is ever read - only token counters and file paths.

- **From the original macOS app** - Claude Code, Codex, Grok, Pi, Amp
- **Aligned with [juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage)** -
  WorkBuddy, CodeBuddy, Qoder, Qwen Code, Kimi, GitHub Copilot CLI, ZCode,
  OpenCode, Gemini CLI, Droid, Cline / Roo Code / Kilo Code, DeepSeek Harness,
  Command Code, OpenClaw, Every Code

WorkBuddy's China and international builds are separate installations with
separate home directories (`~/.workbuddy` and `~/.workbuddy-ai`), so they are
counted separately and shown as `WorkBuddy (CN)` / `WorkBuddy (INTL)`.

Every source accepts an environment variable to override its log root
(`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `WORKBUDDY_HOME`, `WORKBUDDY_AI_HOME`, ...).
See `src\Data\Adapters*.cs`.

**Counting rules.** Only billable tokens are counted; cache reads and writes are
kept as separate columns and `reasoning` is not double-counted. Cumulative
sources are tracked by high-water mark per event id, so restarts never
double-count. ZCode subagent messages are attributed to their own source.

**Deliberately not collected.** Cursor (usage only exists behind its cloud API),
Kiro / Antigravity / QwenWork (no real token metadata locally - the reference
implementations estimate from character counts), Trae (SQLCipher encrypted), and
a few tools whose log schema could not be verified.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 3.5 target -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Net4    # .NET 4.x target -> dist-net4\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # build, then launch
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.0   # zip, tag, release
```

The compiler is discovered in this order: a local `_tools\roslyn-4.8.0\`
(optional, enables C# 12), then the in-box .NET 4.x `csc.exe`. The 3.5 target
additionally needs `Reference Assemblies\...\v3.5\System.Core.dll`, which ships
with Visual Studio or the Build Tools.

## Data and privacy

Everything lives in `%APPDATA%\CodingFire\`:

| File | Purpose |
|---|---|
| `usage.ndjson` | Event store, 45-day retention |
| `cursors.json` | Per-file read offsets |
| `settings.json` | Size, position, language, colors |
| `codingfire.log` | Errors only |

Set `CODINGFIRE_DATA_DIR` to use a portable data directory instead.

Headless self-checks:

```powershell
CodingFire.exe --dump report.txt   # statistics report
CodingFire.exe --render out-dir    # render each fire tier to PNG
```

## Credits

A Windows rewrite of the macOS app **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)**
by [@wdkwdkwdk](https://github.com/wdkwdkwdk) (MIT). The project idea and the core
algorithms are carried over: the pixel fire, the hover card's live rate estimate,
the TPM tier anchors, and the high-water-mark ingestion used for cumulative
sources. Data-source semantics are aligned with
[juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage).

Only the platform layer differs: Swift to C# / WinForms / GDI, SpriteKit particles
to a hand-written pixel heat field, SQLite to NDJSON (dropping the native
dependency), and `NSWindow` to an `UpdateLayeredWindow` layered window.

## License

MIT - see [LICENSE](./LICENSE).

---

# 简体中文

> [English](#codingfire-for-windows) · **简体中文**

把 AI 编程烧掉的 token 变成桌面上的一把像素篝火 —— 火势就是当前的 token 消耗速率。

- **火势 = 实时 token 消耗速率**；多客户端同时跑也只有一把火（火势加总）
- 鼠标悬停弹出当日用量卡片与实时 tok/s
- **全程只读本地日志**，不联网、不上传、不读 prompt 或代码内容
- 一个 exe + 一个 config，**零运行时依赖**

## 系统要求

| 系统 | 需要什么 |
|---|---|
| Windows 11 / 10 | 无，开箱即用（自带 .NET 4.x） |
| Windows 8 / 8.1 | 无，开箱即用（自带 .NET 4.5） |
| Windows 7 SP1 | 无，开箱即用（自带 .NET 3.5.1） |

产物以 .NET 3.5 / CLR 2.0 为目标，并在 `supportedRuntime` 里同时声明 `v4.0` 与
`v2.0.50727`，所以**一份产物覆盖 Win7 SP1 到 Win11**；`/platform:x86` 经 WoW64
通吃 32/64 位。

## 下载与运行

到 [Releases](../../releases) 下载最新 zip，解压到任意目录，双击 `CodingFire.exe`。

- **鼠标移到火上** —— 当日用量卡片、实时 tok/s、当前档位
- **拖动** —— 换位置（会记住）
- **右键 / 双击托盘图标** —— 菜单与统计控制台

首次启动没有数据时，火保持「余烬」状态，属正常。

## 支持的数据源

**23 个数据源（22 种工具），全部只读解析本地日志**，不联网、不上传、不读 prompt
或代码内容 —— 只取 token 数字与文件路径。

- **原版 macOS 就有** —— Claude Code、Codex、Grok、Pi、Amp
- **对齐 [juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage) 补齐** ——
  WorkBuddy、CodeBuddy、Qoder、Qwen Code、Kimi、GitHub Copilot CLI、ZCode、
  OpenCode、Gemini CLI、Droid、Cline / Roo Code / Kilo Code、DeepSeek Harness、
  Command Code、OpenClaw、Every Code

WorkBuddy 的国内版与国外版是两份独立安装、两个 home 目录（`~/.workbuddy` 与
`~/.workbuddy-ai`），因此**分开统计**，列表里显示为 `WorkBuddy（内）` /
`WorkBuddy（外）`。

每个来源都支持环境变量覆盖日志根目录（`CLAUDE_CONFIG_DIR`、`CODEX_HOME`、
`WORKBUDDY_HOME`、`WORKBUDDY_AI_HOME` …），详见源码 `src\Data\Adapters*.cs`。

**统计口径** —— 只计真实计费 token，缓存读/写单列，`reasoning` 不重复计；累计型
来源按「同 id 见过的最大总量」取差额入库，重启不重复计数；ZCode 内嵌子代理消息
归各自的源统计，避免重复。

**刻意不采集** —— Cursor（用量只在云端 API 后）、Kiro / Antigravity / QwenWork
（本地无真实 token 元数据，上游靠字符数估算）、Trae（SQLCipher 加密），以及若干
库结构未核实的工具 —— 宁可不收也不乱收。

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 3.5 目标 -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Net4    # .NET 4.x 目标 -> dist-net4\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # 构建后直接启动
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.0   # 打包 + 打 tag + 发布
```

编译器按顺序查找：本地 `_tools\roslyn-4.8.0\`（可选，启用 C# 12）→ 系统自带
.NET 4.x `csc.exe`。3.5 目标额外需要
`Reference Assemblies\...\v3.5\System.Core.dll`（随 Visual Studio 或 Build Tools 安装）。

## 数据放在哪

全部落在 `%APPDATA%\CodingFire\`：

| 文件 | 用途 |
|---|---|
| `usage.ndjson` | 事件库，保留 45 天 |
| `cursors.json` | 每个文件的读取游标 |
| `settings.json` | 尺寸、位置、语言、配色 |
| `codingfire.log` | 仅出错时写 |

设 `CODINGFIRE_DATA_DIR` 可切便携模式。

无界面自检：

```powershell
CodingFire.exe --dump 报告.txt     # 输出统计报告
CodingFire.exe --render 目录       # 把各档火势渲染成 PNG
```

## 致谢

本项目是 macOS 版 **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)**（作者
[@wdkwdkwdk](https://github.com/wdkwdkwdk)，MIT 协议）的 Windows 客户端重写。
沿用了它的项目思路与核心算法 —— 像素火势、悬停卡片的实时速率估算、TPM 锚点分档、
累计型来源取高水位差额的入库策略。数据源口径同时对齐
[juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage)。

差异只在平台这一层：Swift → C# / WinForms / GDI，SpriteKit 粒子 → 手写像素热场，
SQLite → NDJSON（去掉 native 依赖），NSWindow → `UpdateLayeredWindow` 分层窗。

## 协议

MIT —— 同 macOS 版 [TinyFire](https://github.com/wdkwdkwdk/tinyfire)。详见 [LICENSE](./LICENSE)。

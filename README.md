# CodingFire for Windows

Turn your AI coding token burn into a pixel campfire on the desktop.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%208%20%7C%2010%20%7C%2011-lightgrey)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-4.x%20%2F%20CLR%204.0-512BD4)](#requirements)
[![Release](https://img.shields.io/github/v/release/wangsrGit119/codingfire-win)](../../releases)

**English** · [简体中文](README.zh-CN.md)

**Multi-platform build** (64-bit Windows, Linux, macOS) -
**[wangsrGit119/codingfire](https://github.com/wangsrGit119/codingfire)**

<p align="center">
  <img src="assets/example_01.gif" width="344" alt="CodingFire - campfire with a green-tinted flame">
  <img src="assets/example_02.gif" width="344" alt="CodingFire - campfire with the classic orange flame">
</p>

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
| Windows 7 | Unsupported |

The binary targets .NET Framework 4.x / CLR 4.0 because the statistics console uses
the WinForms Chart control. It is compiled `/platform:x86` and runs on 32- and
64-bit Windows via WoW64.

This build covers Windows only. On Linux, macOS or 64-bit Windows, use the
multi-platform build: **[CodingFire](https://github.com/wangsrGit119/codingfire)**.

## Download and run

Grab the latest zip from [Releases](../../releases), unpack it anywhere and run
`CodingFire.exe`.

- **Hover the fire** - today's usage card, live tok/s, current tier
- **Drag it** - move it around, the position is remembered
- **Right-click / double-click the tray icon** - menu and statistics console
- **Starts with Windows by default** - turn it off any time from the tray menu

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
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 4.x target -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # build, then launch
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.0   # zip, tag, release
```

The compiler is discovered in this order: a local `_tools\roslyn-4.8.0\`
(optional, enables C# 12), then the in-box .NET 4.x `csc.exe`.

## Data and privacy

Everything lives in `%APPDATA%\CodingFire\`:

| File | Purpose |
|---|---|
| `usage.ndjson` | Event store, 45-day retention |
| `cursors.json` | Per-file read offsets |
| `settings.json` | Size, position, language, colors, autostart |
| `codingfire.log` | Errors only |

Set `CODINGFIRE_DATA_DIR` to use a portable data directory instead.

The one thing written outside that folder is a single
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry for the autostart
toggle. It needs no admin rights, and it is deleted again the moment you turn
autostart off.

Headless self-checks:

```powershell
CodingFire.exe --dump report.txt   # statistics report
CodingFire.exe --render out-dir    # render each fire tier to PNG
```

## Credits

A Windows rewrite of the macOS app **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)**
by [@wdkwdkwdk](https://github.com/wdkwdkwdk) (MIT). Thanks for the original idea
and the core algorithms. Data-source semantics are aligned with
[juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage).

## License

MIT - see [LICENSE](./LICENSE).

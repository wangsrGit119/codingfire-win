# CodingFire for Windows

[English](README.md) · **简体中文**

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%208%20%7C%2010%20%7C%2011-lightgrey)](#系统要求)
[![.NET](https://img.shields.io/badge/.NET-4.x%20%2F%20CLR%204.0-512BD4)](#系统要求)
[![Release](https://img.shields.io/github/v/release/wangsrGit119/codingfire-win)](../../releases)

<p align="center">
  <img src="assets/example_01.gif" width="344" alt="CodingFire —— 偏绿的火苗">
  <img src="assets/example_02.gif" width="344" alt="CodingFire —— 经典橙色火苗">
</p>

把 AI 编程烧掉的 token 变成桌面上的一把像素篝火 —— 火势就是当前的 token 消耗速率。

一个常驻桌面顶层的小篝火，读取 AI 编程工具本来就写在磁盘上的 token 用量日志。
烧得越快，火越旺。

- **火势 = 实时 token 消耗速率**；多客户端同时跑也只有一把火（火势加总）
- 鼠标悬停弹出当日用量卡片与实时 tok/s
- **全程只读本地日志**，不联网、不上传、不读 prompt 或代码内容
- 一个 exe + 一个 config，**零运行时依赖**

## 系统要求

| 系统 | 需要什么 |
|---|---|
| Windows 11 / 10 | 无，开箱即用（自带 .NET 4.x） |
| Windows 8 / 8.1 | 无，开箱即用（自带 .NET 4.5） |
| Windows 7 | 不再支持 |

产物以 .NET 4.x / CLR 4.0 为目标，统计控制台使用 WinForms Chart 控件；`/platform:x86`
经 WoW64 通吃 32/64 位。

## 下载与运行

到 [Releases](../../releases) 下载最新 zip，解压到任意目录，双击 `CodingFire.exe`。

- **鼠标移到火上** —— 当日用量卡片、实时 tok/s、当前档位
- **拖动** —— 换位置（会记住）
- **右键 / 双击托盘图标** —— 菜单与统计控制台
- **默认开机自启** —— 不想要的话在托盘菜单里取消勾选即可

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
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 4.x 目标 -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # 构建后直接启动
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.0   # 打包 + 打 tag + 发布
```

编译器按顺序查找：本地 `_tools\roslyn-4.8.0\`（可选，启用 C# 12）→ 系统自带
.NET 4.x `csc.exe`。

## 数据放在哪

全部落在 `%APPDATA%\CodingFire\`：

| 文件 | 用途 |
|---|---|
| `usage.ndjson` | 事件库，保留 45 天 |
| `cursors.json` | 每个文件的读取游标 |
| `settings.json` | 尺寸、位置、语言、配色、开机自启 |
| `codingfire.log` | 仅出错时写 |

设 `CODINGFIRE_DATA_DIR` 可切便携模式。

唯一写在这个目录之外的东西，是「开机自启」在
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的一条值。
它不需要管理员权限，关掉自启时会立刻被删掉。

无界面自检：

```powershell
CodingFire.exe --dump 报告.txt     # 输出统计报告
CodingFire.exe --render 目录       # 把各档火势渲染成 PNG
```

## 致谢

本项目是 macOS 版 **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)**（作者
[@wdkwdkwdk](https://github.com/wdkwdkwdk)，MIT 协议）的 Windows 版本，
感谢原作者给出的创意与核心算法。数据源口径对齐
[juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage)。

## 协议

MIT —— 详见 [LICENSE](./LICENSE)。

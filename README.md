# TinyFire for Windows

> **致谢 / Credits**
>
> 本项目是 macOS 版 **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)**（作者 [@wdkwdkwdk](https://github.com/wdkwdkwdk)，MIT）的 Windows 客户端重写。
> 原版用 Swift + SwiftUI + SpriteKit 把本地 AI 编程工具的 token 用量变成桌面上的一把像素篝火。
> 本仓库**沿用了它的项目思路与核心算法** —— 像素火势、悬停卡片的实时速率估算、TPM 锚点分档、
> 累计型来源取高水位差额的入库策略，均按上游逻辑实现；数据源口径同时对齐
> [juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage)。
>
> 差异只在平台这一层：Swift → C# / WinForms / GDI，SpriteKit 粒子 → 手写像素热场，
> SQLite → NDJSON（去掉 native 依赖），NSWindow → `UpdateLayeredWindow` 分层窗。

---

## 项目思路

让「烧 token」变得**肉眼可见、安静、不打扰**：

- 一只像素篝火常驻桌面，**火势 = 当前 token 消耗速率**；多客户端同时跑也只有一把火（火势加总）
- 悬停弹出当日用量卡片，实时速率取「60 秒窗口实测」与「当前火势反推」的较大值
- 全程只读本地日志，**不联网、不上传、不读 prompt 或代码内容**
- **零运行时依赖** —— 一个 exe + 一个 config 就能跑

## 怎么跑

解压 release，双击 `TinyFire.exe`。鼠标移到火上 = 用量卡片，拖动 = 换位置（会记住），
右键/双击托盘 = 菜单与控制台。首次启动没有数据时火保持「余烬」状态，属正常。

---

## 本仓库（Windows 客户端）的特性

| 特性 | 说明 |
|---|---|
| **单文件免安装** | ~170 KB 的 exe + `.config`，不带任何 DLL / 运行时 |
| **一份产物覆盖 Win7→Win11** | .NET 3.5 / CLR 2.0 目标 + `supportedRuntime` 双声明，Win7 走自带 CLR 2.0，Win8+ 自动落到 CLR 4；`/platform:x86` 经 WoW64 通吃 32/64 位 |
| **手写像素热场** | `PixelFireEngine`（28×36 热场：热扩散 + 上升 + 边缘衰减）替代 SpriteKit 粒子 |
| **真·分层窗口** | `UpdateLayeredWindow` + `WS_EX_LAYERED \| TOOLWINDOW \| NOACTIVATE`，逐像素 alpha 与点击穿透 |
| **手写只读 SQLite** | `SqliteReader.cs` 直接读表 b-tree / 溢出页 / WAL，解析 `CREATE TABLE` 取列名，**不执行任何 SQL** |
| **23 个数据源** | 5 种原版就有的 + 17 种对齐 juejin-usage 补齐 + WorkBuddy 国外版（详见下） |
| **多源配色** | 颜色按来源占比分横向色带，可设可见来源上限（默认前 5，避免糊成花布） |
| **沉默期衰减** | AI 停止 3 秒后火焰衰减时间常数从 14s 降到 5s，5–10 秒内明显熄灭 |
| **i18n** | 英语 / 简体中文 / 日语 / 韩语 |
| **无界面自检** | `TinyFire.exe --dump 报告.txt` 输出统计报告；`--render 目录` 把各档火势渲染成 PNG |

---

## 支持的数据源

**23 个数据源（22 种工具），全部只读解析本地日志**，不联网、不上传、不读 prompt 或代码内容。

- **原版就有**：Claude Code、Codex、Grok、Pi、Amp
- **对齐 juejin-usage 补齐**：WorkBuddy、CodeBuddy、Qoder、Qwen Code、Kimi、GitHub Copilot CLI、ZCode、
  OpenCode、Gemini CLI、Droid、Cline / Roo Code / Kilo Code、DeepSeek Harness、Command Code、OpenClaw、Every Code

**国内版 / 国外版分开统计的工具**：WorkBuddy 的国内版与国外版是两份独立安装，日志根目录不同
（`~/.workbuddy` 与 `~/.workbuddy-ai`），文件格式一模一样，所以拆成两个数据源分别计数 ——
列表里显示为 `WorkBuddy（内）` / `WorkBuddy（外）`，一眼能看出是哪边在烧 token。

每个来源都支持环境变量覆盖日志根目录（如 `CLAUDE_CONFIG_DIR`、`CODEX_HOME`、`WORKBUDDY_HOME`、
`WORKBUDDY_AI_HOME` …），详见源码 `src\Data\Adapters*.cs`。

**统计口径**：只计真实计费 token（缓存读/写单列，`reasoning` 不重复计）；累计型来源取「同 id 见过的最大总量」
把差额算作增量，重启不重复计数；ZCode 内嵌子代理消息归各自的源统计，避免重复。

**刻意不采集**：Cursor（用量只在云端 API 后）、Kiro / Antigravity / QwenWork（本地无真实 token 元数据，
上游靠字符数估算）、Trae（SQLCipher 加密），以及若干库结构未核实的工具 —— 宁可不收也不乱收。

---

## 系统要求

| 系统 | 需要什么 |
|---|---|
| Windows 11 / 10 | 无，开箱即用（自带 .NET 4.x） |
| Windows 8 / 8.1 | 无，开箱即用（自带 .NET 4.5） |
| Windows 7 SP1 | 无，开箱即用（自带 .NET 3.5.1） |

## 数据放在哪

`%APPDATA%\TinyFire\` —— `usage.ndjson`（事件库，保留 45 天）、`cursors.json`（读取游标）、
`settings.json`、`tinyfire.log`（仅出错时写）。设 `TINYFIRE_DATA_DIR` 可切便携模式。

---

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 3.5 目标 -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Net4    # .NET 4.x 目标 -> dist-net4\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # 构建后直接启动
```

编译器按顺序找：本地 `_tools\roslyn-4.8.0\`（可选，启用 C# 12）→ 系统自带 .NET 4.x `csc.exe`。
3.5 目标需要 `Reference Assemblies\...\v3.5\System.Core.dll`（随 Visual Studio / Build Tools 安装）。

**源码结构**

```
src\
  Program.cs          入口 + --dump / --render 自检模式
  Core\               Json（手写解析器）· Model（数据模型/分档/配色）· Settings
                      · UsageStore（NDJSON 事件库）· L10n（四语言）
  Data\               Adapters / Adapters2（23 个来源）· SqliteReader（手写只读）
                      · JsonlReader（增量读取）· UsageMonitor（4 秒轮询调度）
  Fire\               PixelFireEngine（像素热场）· FireStateMachine（火势状态机）· Palette
  Ui\                 LayeredWindow · FlameForm（悬浮窗+悬停卡片）· ConsoleForm（控制台）
                      · CampfireRenderer · TrayIconArt · TinyFireApp
```

---

## 协议

MIT —— 同 macOS 版 [TinyFire](https://github.com/wdkwdkwdk/tinyfire)。详见 [LICENSE](./LICENSE)。

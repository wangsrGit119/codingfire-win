# TinyFire for Windows

> **致谢 / Credits**
>
> 本项目是对 macOS 版 **[TinyFire](https://github.com/wdkwdkwdk/tinyfire)** 的 Windows 客户端重写。
> 原项目由 [@wdkwdkwdk](https://github.com/wdkwdkwdk) 用 Swift + SwiftUI + SpriteKit 写成（MIT 协议），
> 把本地 AI 编程工具的 token 用量变成桌面上的一把像素篝火。本仓库**完整保留了它的项目思路与核心算法**：
> 像素火势、悬停卡片的实时速率估算、TPM 锚点分档、累计型来源取高水位差额的入库策略，全部按上游逻辑实现。
>
> 改动只在 Windows 平台这一层：把 Swift 换成 C# / WinForms / GDI、把 SQLite 换成 NDJSON、
> 把 SpriteKit 粒子换成手写像素热场、把 UpdateLayeredWindow 透明分层窗 + GDI 合成搬过来。
> 数据源口径与原版及 [juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage) 对齐。

---

## 项目思路

让「烧 token」这件事变得**肉眼可见、安静、不打扰**：

- 一只像素篝火常驻桌面右下角，**火势 = 当前 token 消耗速率**
- 多源多客户端同时跑时只有一把火（**火势 = 总和**），但颜色按来源占比横向分带
- 鼠标悬停弹出当日用量卡片，**实时速率 (tok/s)** 与「窗口内实测 / 当前火势反推」两条线取较大值
- 全程只读本地日志，**不联网、不上传、不读 prompt 或代码内容**（只取 token 数字与文件路径）
- **零运行时依赖** —— 一个 exe + 一个 config 即可运行，连 SQLite 客户端都不用带

---

## 怎么跑

下载 release 包，解压，双击 `TinyFire.exe`。托盘会出现小火苗，桌面上出现悬浮的篝火。

- **鼠标移到火上** → 弹出今日用量卡片（含实时 tok/s 与分档火势）
- **拖动** → 换位置（位置会记住）
- **右键托盘图标** → 显示/隐藏、暂停动画、火焰大小、界面语言、重置位置、统计控制台、退出
- **双击托盘图标** → 打开统计控制台

首次启动如果没检测到任何工具的数据，火会保持「余烬」状态 —— 这是正常的，说明还没有用量。

---

## 本仓库（Windows 客户端）的特性

### 1. 单文件 + 免安装

- 单一可执行文件（`TinyFire.exe`，~170 KB）+ 单一配置（`TinyFire.exe.config`），**不依赖任何运行时 / DLL / 安装包**
- **.NET 3.5 / CLR 2.0** 目标 + `supportedRuntime` 双声明 —— Win7 SP1 用自带的 CLR 2.0，Win8/10/11 自动落到自带的 CLR 4.0，**一份产物覆盖 Win7 SP1 到 Win11**
- `/platform:x86` 走 WoW64，32/64 位系统都能跑

### 2. 用 WinForms + GDI 重写了 macOS 端的整套栈

| macOS 原版（致敬） | Windows 本仓库 |
|---|---|
| Swift + SwiftUI | C# + WinForms |
| SpriteKit 粒子 | 手写 `PixelFireEngine`（28×36 热场，热扩散 + 上升 + 边缘衰减） |
| NSWindow / 透明背景 | `UpdateLayeredWindow` 真·分层窗口（`WS_EX_LAYERED \| TOOLWINDOW \| NOACTIVATE`，点击穿透加 `TRANSPARENT`） |
| 程序化生成图标（Swift） | `TrayIconArt.cs` —— GDI 画的托盘图标与窗图标 |
| SQLite | `UsageStore.cs` —— 追加式 NDJSON + 内存 id 索引 + 文件游标（base64 半行），**去掉 native sqlite3.dll 依赖** |
| Sparkle 自动更新 | 无（手动 release） |

### 3. 手写 SQLite 读取器（守住零依赖）

原版 + juejin-usage 里有好几个工具把用量记在 SQLite（ZCode / OpenCode / Qoder IDE / WorkBuddy 回退路径）。为了继续免安装，**手写了一个只读的最小 SQLite 读取器**（`src\Data\SqliteReader.cs`）：

- 表 b-tree 走叶子 0x0D / 内部 0x05 页面
- 支持溢出页
- WAL 只叠加已提交帧（前缀判断）
- 从 `CREATE TABLE` 文本里解析列名
- **不执行任何 SQL**、不写、不建索引

本机实测可正确读出 46 MB 的 `opencode.db`（带 5 MB WAL）和 8.6 MB 的 Qoder IDE `local.db`。

### 4. 22 种数据源，全部只读本地日志

> 完整列表与日志路径见 [支持的数据源](#支持的数据源) 一节。
>
> 5 个原版 macOS 就有的第一梯队（Claude Code / Codex / Grok / Pi / Amp）+ 17 个对齐 juejin-usage 补齐的第二梯队。

### 5. 多源配色（横向色带 + 上限）

- 多源 = 一把更大的火（**火势加总**，颜色用占比）
- 色带位置 = `UsageSources.All` 枚举序（Claude 最左…），带宽 ∝ 权重
- 安静时底火仍带今天主力的身份色（窗口内总量 <= 50 token 时退回到今日分来源占比）
- 横向只有 28 列 —— **色带可见来源上限** 已实现：`ColumnWeights` 按权重降序保留前 N 个再归一化（默认 5，0 = 不限），设置键 `colorSources`

### 6. 实时速率的确定性核心

悬停卡片右下角的 `tok/s` 算法与 macOS 1.1.16 **逐字一致**（`min(320, max(实测, 火势反推))` + 非对称平滑 + 多频抖动）：

- **窗口 60 秒**，每条事件封顶 15,000 token
- **显示上限 320 tok/s** —— 不被异常尖峰吓到
- 升 0.9s / 降 2.8s 的非对称平滑
- ±8–14% 的多频抖动（让数字「活着」但不跳）
- **迟到事件不参与速率**（不伪造「当下正在烧」），但仍会点亮火焰
- **沉默期衰减**：AI 停止 3 秒后，火焰 τ 从 14s 降到 5s，**5–10 秒内明显熄灭**（不再「不消费还在烧」）

三个纯函数已拆成 `internal` 供夹具断言：`RateFromInflows(now)` / `RateFromFlame()` / `ComputeInstantRate(now)`。

### 7. i18n

英语 / 简体中文 / 日语 / 韩语，由 `src\Core\L10n.cs` 维护。

### 8. 自检模式

```powershell
.\dist\TinyFire.exe --dump 报告.txt      # 扫描本机真实日志，输出统计报告
.\dist\TinyFire.exe --render 目录        # 把各档火势 + 配色 + 悬停卡片 + 托盘图标渲染成 PNG
```

`--dump` 不写 `%APPDATA%`，把游标和数据目录都隔离到参数路径下，便于在没有本机真实数据的机器上跑。

---

## 系统要求

| 系统 | 需要什么 |
|---|---|
| Windows 11 / 10 | 无，开箱即用（系统自带 .NET 4.x） |
| Windows 8 / 8.1 | 无，开箱即用（系统自带 .NET 4.5） |
| Windows 7 SP1 | 无，开箱即用（系统自带 .NET 3.5.1） |

`dist\TinyFire.exe` 是 **.NET 3.5 / CLR 2.0** 目标，配合同目录的 `.config` 声明双运行时。
Win7 SP1 用它自带的 CLR 2.0，Win8/10/11 用自带的 CLR 4。一份产物覆盖全部。

> 可选：`dist-net4\TinyFire.exe` 是等价的 .NET 4.x / CLR 4.0 版本（`-Net4` 开关），仅供 CLR 2.0 出问题时的排查对照。
> 正常情况**用 `dist\` 那个**。

---

## 数据放在哪

`%APPDATA%\TinyFire\`

| 文件 | 内容 |
|---|---|
| `usage.ndjson` | 用量事件（按 id 幂等，保留 45 天） |
| `cursors.json` | 各日志文件的读取游标 |
| `settings.json` | 设置 |
| `tinyfire.log` | 仅出错时写的排查日志 |

原版用 SQLite；本版改用「追加式 NDJSON + 内存索引」，为了去掉 native `sqlite3.dll` 依赖，
让产物真正做到零外部依赖。设置里没有任何遥测开关 —— 因为这个程序不联网。

想换数据目录可以设 `TINYFIRE_DATA_DIR`（便携模式）。

---

## 支持的数据源

共 **22 种**，全部**只读解析本地日志**，不联网、不上传、不读 prompt 或代码内容
（只读 token 数字与文件路径）。

### 第一梯队（macOS 原版就有）

| 来源 | 日志位置 | 环境变量覆盖 |
|---|---|---|
| Claude Code | `%USERPROFILE%\.claude\projects\**\*.jsonl` | `CLAUDE_CONFIG_DIR` |
| Codex | `%USERPROFILE%\.codex\sessions\**\*.jsonl` | `CODEX_HOME` |
| Grok | `%USERPROFILE%\.grok\sessions\**\updates.jsonl` + `logs\unified.jsonl` | `GROK_HOME` |
| Pi | `%USERPROFILE%\.pi\agent\sessions\**\*.jsonl` | `PI_AGENT_DIR` |
| Amp | `%USERPROFILE%\.local\share\amp\threads\**\*.json`（或 `%APPDATA%\amp`） | `AMP_DATA_DIR` |

### 第二梯队（相对 [juejin-cn/juejin-usage](https://github.com/juejin-cn/juejin-usage) 补齐）

| 来源 | 日志位置 | 环境变量覆盖 |
|---|---|---|
| WorkBuddy | `~\.workbuddy\projects\**\*.jsonl`；无明细时回退 `workbuddy.db` 的 `session_usage` | `WORKBUDDY_HOME` |
| CodeBuddy | `~\.codebuddy\projects\**\*.jsonl` | `CODEBUDDY_HOME` |
| Qoder | CLI/Work：`~\.qoder\projects`、`~\.qoderwork\projects`<br>IDE：`%APPDATA%\Qoder\SharedClientCache\cache\db\local.db` | `AI_USAGE_QODER_ROOTS`<br>`AI_USAGE_QODER_IDE_ROOTS` |
| Qwen Code | `~\.qwen\tmp\**\*.jsonl` | `QWEN_TMP_DIR` |
| Kimi | 新版 `~\.kimi-code\sessions\**\wire.jsonl`；旧版 `~\.kimi\sessions\...\wire.jsonl` | `KIMI_CODE_HOME`、`KIMI_HOME` |
| GitHub Copilot CLI | `~\.copilot\session-state\<会话>\events.jsonl` | `COPILOT_HOME` |
| ZCode | `~\.zcode\cli\db\db.sqlite` 的 `message` 表 | `ZCODE_HOME` |
| OpenCode | `~\.local\share\opencode\opencode.db`；旧版 `storage\message\*.json` | `OPENCODE_HOME`、`XDG_DATA_HOME` |
| Gemini CLI | `~\.gemini\tmp\<项目>\chats\session-*.json` 与 `*.jsonl` | `GEMINI_HOME` |
| Droid | `~\.factory\sessions\**\*.settings.json` | `DROID_SESSIONS_DIR`、`FACTORY_DIR` |
| Cline / Roo Code / Kilo Code | `%APPDATA%\<编辑器>\User\globalStorage\<扩展 id>\tasks\<任务>\ui_messages.json` | `AI_USAGE_CLINE_ROOTS`、`AI_USAGE_ROOCODE_ROOTS`、`AI_USAGE_KILOCODE_ROOTS` |
| DeepSeek Harness | `~\.dsh\sessions\**\session.jsonl` | `DSH_HOME` |
| Command Code | `~\.commandcode\projects\**\*.jsonl` | `AI_USAGE_COMMANDCODE_ROOTS` |
| OpenClaw | `~\.openclaw\agents\<id>\sessions\*.jsonl` | `OPENCLAW_STATE_DIR` |
| Every Code | `~\.code\sessions` 与 `~\.code\archived_sessions` | `AI_USAGE_EVERY_CODE_HOME`、`CODE_HOME` |

> VS Code 系那几个插件默认会依次找 Code / Code - Insiders / VSCodium / Cursor / Windsurf /
> Trae / Trae CN / CodeBuddy 这些宿主目录，所以装哪个编辑器都能命中。

### 统计口径

- 只计真实计费的 token；缓存读、缓存写单独拆成两列，`reasoning` 不重复计入
- **累计型来源**（Claude Code 的流式快照、OpenCode / ZCode / Gemini CLI / Droid /
  DeepSeek Harness / Command Code / Every Code）只取「同一 id 见过的最大总量」，
  把差额算作增量 —— 重启后不会重复计数
- **Copilot** 一次会话收尾会把多个模型的账写在一起，本程序合并成一条事件，不按模型拆账
- **ZCode** 内嵌的 Claude / Codex / Gemini 子代理消息由各自的源统计，
  ZCode 自己只收 `providerID` 不是 anthropic / openai / google 的部分，避免重复计数

### 刻意不采集的工具

不是漏了，是判断过不值得记：

| 工具 | 原因 |
|---|---|
| Cursor | 用量只在 Dashboard API 后面，本地没有 token 数字 |
| Kiro / Antigravity / QwenWork | 本地日志里没有真实 token 元数据，上游是靠「字符数 ÷ 4」估算的；估出来的数字只会让火焰变成噪音 |
| Trae | 本地库是 SQLCipher 加密的 |
| Mimo / Kilo CLI / Hermes / Goose / Zed / Warp | 库结构未逐一核实，宁可不收也不乱收 |

---

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 3.5 目标 -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Net4    # .NET 4.x 目标 -> dist-net4\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # 构建后直接启动
```

`build.ps1` 自动按下列顺序找编译器（首个匹配即用）：

1. `_tools\roslyn-4.8.0\...` —— 可选，本地放的 Roslyn 4.8（启用 C# 12 语法糖）
2. `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` —— 系统自带的 .NET 4 64 位编译器
3. `%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe` —— 32 位回退

3.5 目标需要 `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.5\System.Core.dll`，
它随 Visual Studio / Build Tools 一起安装；没装的话脚本会给出下载链接。

`/platform:x86` 是刻意的 —— 32 位经 WoW64 通吃 32/64 位系统，一份产物覆盖全部。

### 源码结构

```
src\
  Program.cs              入口；--dump / --render 两个无界面自检模式
  Core\
    Json.cs               手写 JSON 解析器（不依赖 System.Text.Json / System.Web）
    Model.cs              数据模型、火势分档与配色
    Settings.cs           设置、路径约定、原子写入、日志
    UsageStore.cs         用量事件库（NDJSON + 内存索引 + 文件游标）
    L10n.cs               英语 / 简体中文 / 日语 / 韩语
  Data\
    Adapters.cs           基础适配器 + Claude Code / Codex / Grok / Pi / Amp
    Adapters2.cs          其余 17 种工具的适配器（口径对齐 juejin-usage）
    SqliteReader.cs       手写只读 SQLite 读取器（表 b-tree + 溢出页 + WAL）
    JsonlReader.cs        JSONL 增量读取（字节游标 + 自愈）
    UsageMonitor.cs       4 秒轮询调度、去重入库、喂火
  Fire\
    PixelFireEngine.cs    像素热场火焰引擎
    FireStateMachine.cs   火势状态机（强度 / 燃料 / 余温 / 沉默期衰减）
    Palette.cs            火焰色带生成
  Ui\
    LayeredWindow.cs      UpdateLayeredWindow 实现的真·透明分层窗口
    FlameForm.cs          悬浮篝火窗 + 悬停卡片
    ConsoleForm.cs        统计控制台
    CampfireRenderer.cs   火焰合成渲染
    TrayIconArt.cs        程序化生成的托盘/窗口图标
    TinyFireApp.cs        装配与托盘菜单
tests\
  fixtures.py             为 22 种来源各造一份「schema 与真实格式一致」的合成日志
  fixtures_expected.json  每个来源期望的 token 总量
  FixtureMain.cs          编译时与 src\*.cs 一起编进测试 exe，访问 internal 适配器
  run-fixtures.ps1        编译 + 跑夹具
  check-fixtures.py       断言各来源 token 总量
  check-rate.py           断言悬停速率的确定性核心（剥离抖动后的窗口/反查路径）
```

### 跑测试套件

```powershell
# 1. 生成夹具树（_fixtures/）和期望值（fixtures_expected.json）
python tests\fixtures.py

# 2. 编译 src\*.cs + tests\FixtureMain.cs，跑夹具，写出 _fixtures_got.txt
powershell -ExecutionPolicy Bypass -File tests\run-fixtures.ps1

# 3. 断言每个来源的 token 总量匹配期望（全过才退出 0）
python tests\check-fixtures.py

# 4. 断言悬停速率的 6 项确定性核心
python tests\check-rate.py
```

`check-*.py` 在输入文件缺失或为空时以退出码 2 明确失败 —— 不能假绿。

---

## 已知边界

- **Win7 兼容性是静态结论，未在真机验证。** `win7check` 只做 PE 头 / CLR 头 / 导入表分析
  （子系统 4.0 ≤ 6.1、x86、ILONLY、仅导入 `mscoree.dll`、CLR 头 2.5、元数据 v2.0.50727）。
  真机验收清单见下。
- 不采集 Cursor 及只提供估算值的几家（见上）。
- 第二梯队里有 7 种工具在本机没有安装，只做了夹具级验证（合成语料），没有真实数据对账。
- `parseDatabase` 对 SQLite 是「全表顺序读 + 靠事件 id 幂等」，几十 MB 的库首轮会慢一点；
  内容未变时会跳过，所以只在库真正变动时才重读。
- 音频开关在原版里存在，本版保留设置项但未接音效资源。

### Win7 真机验收清单（尚未执行）

1. 在**未安装 .NET 4.x** 的 Win7 SP1 上双击 `dist\TinyFire.exe`（配合 `.config` 同目录）
2. 在 64 位 Win7 上同样验证一次（走 WoW64）
3. 中文路径下的 `%APPDATA%\TinyFire` 读写正常
4. 缺件场景：目录不存在 / 日志为空 / 只有部分来源时，火与卡片不崩、不误报
5. 日志正被占用时（Claude Code 正在写）增量读取不丢事件
6. 休眠唤醒、锁屏解锁后能重新扫描并补上期间的用量

---

## 协议

MIT —— 同 macOS 版 [TinyFire](https://github.com/wdkwdkwdk/tinyfire)。详见 [LICENSE](./LICENSE)。

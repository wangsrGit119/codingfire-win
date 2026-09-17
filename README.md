# TinyFire for Windows

macOS 版 [TinyFire](https://github.com/wdkwdkwdk/tinyfire) 的 Windows 重写版 —— 一只悬浮在桌面上的像素篝火，
火势由你本地 AI 编程工具的 token 用量驱动。

用 C# 从零重写（原版是 Swift + SwiftUI + SpriteKit），不依赖任何运行时/安装包，
**免安装、绿色、单文件**（一个 exe + 一个 config）。

---

## 怎么跑

双击 `dist\TinyFire.exe` 即可。托盘会出现一把小火苗，桌面上出现悬浮的篝火。

- 鼠标移到火上 → 弹出今日用量卡片
- 拖动 → 换位置（位置会记住）
- 右键托盘图标 → 显示/隐藏、暂停动画、火焰大小、界面语言、重置位置、统计控制台、退出
- 双击托盘图标 → 打开统计控制台

首次启动如果没检测到任何工具的数据，火会保持「余烬」状态 —— 这是正常的，说明还没有用量。

> 卡片右下角那个 `tok/s` 是**估算的实时速率**：窗口 60 秒，取「窗口内实测」与「当前火势反推」
> 两者中的较大值，再叠一点抖动让数字保持活性。单条事件最多计 15,000 token，显示上限 320 tok/s。
> 它表示的是「此刻烧得有多旺」，不是精确的吞吐计量；不想要可在控制台关掉「悬停显示实时速率」。
> 它只吃增量（累计型来源已做过差），迟到写出的旧日志不会伪造出一个当下的读数。

> 多个工具同开时火焰只有一把，但颜色按来源占比横向分带：哪个工具烧得多，它的颜色就占得宽。
> 来源一多色带会糊成花布（10 个以上基本看不出主色），所以控制台设置里有「**色带来源数**」：
> 只显示占比最大的前 N 个来源（默认 5，其余颜色并入背景不再单列），选「不限」恢复原行为。

## 系统要求

| 系统 | 需要什么 |
|---|---|
| Windows 10 / 11 | 无，开箱即用（系统自带 .NET 4.x） |
| Windows 8 / 8.1 | 无，开箱即用（系统自带 .NET 4.5） |
| Windows 7 SP1 | 无，开箱即用（系统自带 .NET 3.5.1） |

`dist\TinyFire.exe` 是 **.NET 3.5 / CLR 2.0** 目标，配合同目录的 `.config` 声明双运行时：
Win7 SP1 用它自带的 CLR 2.0，Win8/10/11 用自带的 CLR 4。一份产物覆盖全部。

> `dist-net4\TinyFire.exe` 是等价的 .NET 4.x / CLR 4.0 版本，仅供 CLR 2.0 出问题时的排查对照，
> 正常情况**用 `dist\` 那个**。两者实测输出完全一致。

## 支持的数据源

共 **22 种**，全部是**只读解析本地日志**，不联网、不上传、不读 prompt 或代码内容
（只读 token 数字与文件路径）。

### 第一梯队（原版 macOS 就有）

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

- 只计真实计费的 token；缓存读、缓存写单独拆成两列，`reasoning` 不重复计入。
- **累计型来源**（Claude Code 的流式快照、OpenCode / ZCode / Gemini CLI / Droid /
  DeepSeek Harness / Command Code / Every Code）只取「同一 id 见过的最大总量」，
  把差额算作增量 —— 重启后不会重复计数。
- **Copilot** 一次会话收尾会把多个模型的账写在一起，本程序合并成一条事件，不按模型拆账。
- **ZCode** 内嵌的 Claude / Codex / Gemini 子代理消息由各自的源统计，
  ZCode 自己只收 `providerID` 不是 anthropic / openai / google 的部分，避免重复计数。

### 刻意不采集的工具

不是漏了，是判断过不值得记：

| 工具 | 原因 |
|---|---|
| Cursor | 用量只在 Dashboard API 后面，本地没有 token 数字 |
| Kiro / Antigravity / QwenWork | 本地日志里没有真实 token 元数据，上游是靠「字符数 ÷ 4」估算的；估出来的数字只会让火焰变成噪音 |
| Trae | 本地库是 SQLCipher 加密的 |
| Mimo / Kilo CLI / Hermes / Goose / Zed / Warp | 库结构未逐一核实，宁可不收也不乱收 |

### SQLite 支持

第二梯队里有好几个工具把用量记在 SQLite 里。为了守住「零外部依赖、免安装单文件」，
本程序手写了一个**只读**的最小 SQLite 读取器（`src\Data\SqliteReader.cs`）：
只走表 b-tree（叶子 0x0D / 内部 0x05）、支持溢出页、支持 WAL（只叠加已提交帧）、
从 `CREATE TABLE` 文本里解析列名，**不执行任何 SQL**、不写、不建索引。
本机实测可正确读出 46 MB 的 `opencode.db`（带 5 MB WAL）和 8.6 MB 的 Qoder IDE `local.db`。

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

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # .NET 3.5 目标 -> dist\
powershell -ExecutionPolicy Bypass -File build.ps1 -Net4    # .NET 4.x 目标 -> dist-net4\
powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # 构建后直接启动
```

编译器优先用 `_tools\roslyn-4.8.0\`（Roslyn 4.8，支持 C# 12），没有则退回系统自带的编译器。
`/platform:x86` 是刻意的 —— 32 位经 WoW64 通吃 32/64 位系统，一份产物覆盖全部。
构建末尾会自动跑 `win7check` 静态检查，不合格直接构建失败。

### 源码结构

```
src\
  Program.cs            入口；--dump / --render 两个无界面自检模式
  Core\
    Json.cs             手写 JSON 解析器（不依赖 System.Text.Json / System.Web）
    Model.cs            数据模型、火势分档与配色混合
    Settings.cs         设置、路径约定、原子写入、日志
    UsageStore.cs       用量事件库（NDJSON + 内存索引 + 文件游标）
    L10n.cs             英语 / 简体中文 / 日语 / 韩语
  Data\
    Adapters.cs         基础适配器 + Claude Code / Codex / Grok / Pi / Amp
    Adapters2.cs        其余 17 种工具的适配器（口径对齐 juejin-usage）
    SqliteReader.cs     手写只读 SQLite 读取器（表 b-tree + 溢出页 + WAL）
    JsonlReader.cs      JSONL 增量读取（字节游标 + 自愈）
    UsageMonitor.cs     4 秒轮询调度、去重入库、喂火
  Fire\
    PixelFireEngine.cs  像素热场火焰引擎
    FireStateMachine.cs 火势状态机（强度 / 燃料 / 余温）
    Palette.cs          火焰色带与各来源配色
  Ui\
    LayeredWindow.cs    UpdateLayeredWindow 实现的真·透明分层窗口
    FlameForm.cs        悬浮篝火窗 + 悬停卡片
    ConsoleForm.cs      统计控制台
    CampfireRenderer.cs 火焰合成渲染
    TrayIconArt.cs      程序化生成的托盘/窗口图标
    TinyFireApp.cs      装配与托盘菜单
```

### 自检模式

```powershell
.\dist\TinyFire.exe --dump 报告.txt     # 扫描本地日志，输出统计报告
.\dist\TinyFire.exe --render 目录       # 把各档火势渲染成 PNG
```

### 验证

```powershell
.\dist\TinyFire.exe --dump 报告.txt      # 扫描本机真实日志（也可直接给目录）
.\dist\TinyFire.exe --render 目录        # 各档火势 + 混合配色 + 悬停卡片 + 托盘图标 PNG
```

三层测试，都不动真实数据：

1. **夹具套件**（`_dev\`）—— 为 22 种来源各造一份「schema 与真实格式一致」的合成日志，
   断言每个适配器算出的 token 总量。跑法：

   ```powershell
   python _dev\_fixtures.py                     # 生成夹具与期望值
   powershell -File _dev\_fixtest.ps1           # 编译 + 运行
   python _dev\_fixtest_check.py                # 断言各来源（全过才退出 0）
   python _dev\_ratetest_check.py               # 断言悬停速率的确定性核心
   ```

   这套夹具在开发期抓到过三个真 bug：SQLite 读取器在「表少到 sqlite_master 根就是第 1 页」
   的小库上漏读所有表名；Gemini 的 `.jsonl` 分支因为返回空表而被当成「已认领」，永远读不到；
   VS Code 三家插件在共享根目录下会互相认领对方的 `ui_messages.json`。

2. **悬停速率的确定性核心** —— 卡片上那个 tok/s 叠了刻意的抖动，显示值不可复现，
   所以断言的是**平滑之前**的候选值：60 秒滑动窗口、每条封顶 15,000 token、
   显示上限 320 tok/s、`min(上限, max(实测, 火势反推))`，外加「迟到事件不参与速率，
   但仍然点亮火焰」。两个断言脚本在输入文件缺失时以退出码 2 明确失败，不会假绿。

3. **真实数据对账** —— 用独立的 Python 实现重新解析本机日志，与 C# 落库的事件逐 id 比对。
   本机跑出的结论：WorkBuddy 640 条共享事件 0 处数值不一致；OpenCode 1261 条、
   Qoder IDE 170 条，事件集合与总量完全一致。

`_make_testdata.py` 是最早那版五点源的模拟语料，保留作参考。

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

//
//  L10n.cs — TinyFire for Windows
//
//  四种语言 + 跟随系统。键名沿用 macOS 版 Localizable.xcstrings 的命名习惯。
//

using System;
using System.Collections.Generic;
using System.Globalization;

namespace TinyFire.Core
{
    public enum AppLanguage { System, English, ChineseSimplified, Japanese, Korean }

    public static class L10n
    {
        // 语言列顺序：en, zh-Hans, ja, ko
        private const int EN = 0, ZH = 1, JA = 2, KO = 3;

        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // ---- 应用 ----
            { "app.name",          new[] { "TinyFire", "TinyFire", "TinyFire", "TinyFire" } },
            { "app.tagline",       new[] {
                "If you're burning tokens anyway, light a real fire.",
                "既然都在烧 token，不如真的生一把火。",
                "どうせトークンを燃やすなら、本物の火を灯そう。",
                "토큰을 태우는 김에, 진짜 불을 피워보세요." } },

            // ---- 档位 ----
            { "tier.hush",    new[] { "Hush",    "微火", "微火", "잔불" } },
            { "tier.glow",    new[] { "Glow",    "小火", "小火", "작은 불" } },
            { "tier.crackle", new[] { "Crackle", "中火", "中火", "중간 불" } },
            { "tier.roar",    new[] { "Roar",    "大火", "大火", "큰 불" } },
            { "tier.blaze",   new[] { "Blaze",   "烈火", "烈火", "맹렬한 불" } },

            // ---- 阶段 ----
            { "phase.unlit",  new[] { "Unlit", "未点燃", "未点火", "미점화" } },
            { "phase.flame",  new[] { "Burning", "燃烧中", "燃焼中", "연소 중" } },
            { "phase.ember",  new[] { "Embers", "余烬", "残り火", "잔불" } },
            { "phase.out",    new[] { "Out", "已熄灭", "消火", "꺼짐" } },

            // ---- 尺寸 ----
            { "size.small",   new[] { "Small",  "小", "小", "작게" } },
            { "size.medium",  new[] { "Medium", "中", "中", "중간" } },
            { "size.large",   new[] { "Large",  "大", "大", "크게" } },

            // ---- 数据源状态 ----
            { "source.state.ok",          new[] { "Connected",     "已连接",   "接続済み",   "연결됨" } },
            { "source.state.notFound",    new[] { "Not found",     "未找到",   "見つかりません", "찾을 수 없음" } },
            { "source.state.noPermission",new[] { "No permission", "无权限",   "権限なし",   "권한 없음" } },
            { "source.state.unsupported", new[] { "Unsupported",   "不支持",   "非対応",     "지원 안 함" } },
            { "source.state.readError",   new[] { "Read error",    "读取失败", "読み取りエラー", "읽기 오류" } },

            // ---- 悬停卡片 ----
            { "hover.today",    new[] { "Today",         "今日 Tokens", "本日", "오늘" } },
            { "hover.rate",     new[] { "Rate",          "速率",       "レート", "속도" } },
            { "hover.tokensS",  new[] { "tok/s",         "token/秒",   "tok/s", "tok/s" } },
            { "hover.updated",  new[] { "Updated",       "更新于",     "更新", "업데이트" } },
            { "hover.estimate", new[] { "estimated",     "估算",       "推定", "추정" } },
            { "hover.none",     new[] { "No supported local usage logs found yet.",
                                        "尚未发现支持的本地用量记录。",
                                        "対応するローカル利用ログが見つかりません。",
                                        "지원되는 로컬 사용 로그를 찾지 못했습니다." } },

            // ---- 托盘菜单 ----
            { "menu.show",           new[] { "Show campfire",   "显示篝火",   "焚き火を表示", "모닥불 표시" } },
            { "menu.hideFlame",      new[] { "Hide campfire",   "隐藏篝火",   "焚き火を隠す", "모닥불 숨기기" } },
            { "menu.console",        new[] { "Console…",        "打开控制台…", "コンソール…", "콘솔 열기…" } },
            { "menu.resetPosition",  new[] { "Reset position",  "重置位置",   "位置をリセット", "위치 초기화" } },
            { "menu.togglePause",    new[] { "Pause / resume animation", "暂停 / 继续动画", "アニメーション一時停止", "애니메이션 일시정지" } },
            { "menu.size",           new[] { "Campfire size",   "火焰尺寸",   "焚き火サイズ", "모닥불 크기" } },
            { "menu.language",       new[] { "Language",        "语言",       "言語", "언어" } },
            { "menu.about",          new[] { "About TinyFire",  "关于 TinyFire", "TinyFire について", "TinyFire 정보" } },
            { "menu.quit",           new[] { "Quit",            "退出",       "終了", "종료" } },

            // ---- 控制台 ----
            { "console.title",       new[] { "TinyFire Console", "TinyFire 控制台", "TinyFire コンソール", "TinyFire 콘솔" } },
            { "console.tab.stats",   new[] { "Stats",    "统计",   "統計", "통계" } },
            { "console.tab.sources", new[] { "Sources",  "数据源", "データ源", "데이터 소스" } },
            { "console.tab.settings",new[] { "Settings", "设置",   "設定", "설정" } },
            { "console.tab.about",   new[] { "About",    "关于",   "情報", "정보" } },

            { "stats.today",     new[] { "Today total",  "今日总量",   "本日の合計", "오늘 합계" } },
            { "stats.bySource",  new[] { "By source",    "分来源",     "データ源別", "소스별" } },
            { "stats.breakdown", new[] { "Breakdown",    "用量构成",   "内訳", "구성" } },
            { "stats.timeline",  new[] { "Today timeline","今日时间线", "本日のタイムライン", "오늘 타임라인" } },
            { "stats.tier",      new[] { "Fire tier",    "当前火势",   "火力", "불 세기" } },
            { "stats.peak",      new[] { "Peak hour",    "峰值时段",   "ピーク時間", "최고 시간대" } },

            { "breakdown.input",      new[] { "Input",       "输入",     "入力",     "입력" } },
            { "breakdown.output",     new[] { "Output",      "输出",     "出力",     "출력" } },
            { "breakdown.cacheRead",  new[] { "Cache read",  "缓存读取", "キャッシュ読", "캐시 읽기" } },
            { "breakdown.cacheWrite", new[] { "Cache write", "缓存写入", "キャッシュ書", "캐시 쓰기" } },

            { "sources.rescan",  new[] { "Rescan",     "重新检测", "再スキャン", "다시 검사" } },
            { "sources.source",  new[] { "Source",     "来源",     "データ源",   "소스" } },
            { "sources.state",   new[] { "State",      "状态",     "状態",       "상태" } },
            { "sources.path",    new[] { "Path",       "路径",     "パス",       "경로" } },
            { "sources.today",   new[] { "Today",      "今日",     "本日",       "오늘" } },
            { "sources.lastRead",new[] { "Last read",  "最近读取", "最終読み取り", "마지막 읽기" } },
            { "sources.scanning",new[] { "Scanning…",  "扫描中…",  "スキャン中…", "검사 중…" } },

            { "settings.flame",        new[] { "Campfire",              "篝火",         "焚き火",       "모닥불" } },
            { "settings.flameSize",    new[] { "Size",                  "尺寸",         "サイズ",       "크기" } },
            { "settings.visible",      new[] { "Show on desktop",       "在桌面显示",   "デスクトップに表示", "바탕 화면에 표시" } },
            { "settings.paused",       new[] { "Pause animation",       "暂停动画",     "アニメーション停止", "애니메이션 일시정지" } },
            { "settings.reduceMotion", new[] { "Reduce motion",         "减少动态效果", "モーションを減らす", "모션 줄이기" } },
            { "settings.showRate",     new[] { "Show live rate on hover","悬停显示实时速率", "ホバー時にレート表示", "호버 시 속도 표시" } },
            { "settings.language",     new[] { "Language",              "语言",         "言語",         "언어" } },
            { "settings.colorSources", new[] { "Flame color",            "火焰颜色",     "炎の色",       "불꽃 색상" } },
            { "settings.colorSourcesAll", new[] { "All",                "不限",         "すべて",       "전체" } },
            { "settings.lang.system",  new[] { "System",                "跟随系统",     "システム",     "시스템" } },
            { "settings.night",        new[] { "Night",                 "夜间",         "夜",           "밤" } },
            { "settings.colors",       new[] { "Flame color",           "火焰颜色",     "炎の色",       "불꽃 색상" } },
            { "settings.colorsHint",   new[] { "Choose a preset or click ... to pick a custom color.",
                                               "选择预设色或点击 ... 自定义颜色。",
                                               "プリセットを選ぶか「...」でカスタム色を選択します。",
                                               "프리셋을 선택하거나 ...를 클릭하여 사용자 지정 색상을 선택합니다." } },
            { "settings.flameColor",    new[] { "Flame Color",           "火焰颜色",     "炎の色",       "불꽃 색상" } },
            { "settings.flameColorHint",new[] { "Current color preview",  "当前颜色预览", "現在の色プレビュー", "현재 색상 미리보기" } },
            { "settings.flameCustom",  new[] { "Custom color...",       "自定义颜色...", "カスタムカラー...", "사용자 지정 색..." } },
            { "settings.resetColors",  new[] { "Reset colors",          "恢复默认配色", "色をリセット", "색 초기화" } },
            { "settings.resetPosition",new[] { "Reset to bottom-right", "重置到右下角", "右下に戻す",   "오른쪽 아래로 초기화" } },
            { "settings.preview",      new[] { "Preview",               "火势预览",     "プレビュー",   "미리보기" } },
            { "settings.live",         new[] { "Live",                  "实时",         "ライブ",       "실시간" } },

            { "about.privacy", new[] { "TinyFire only reads local usage logs. Nothing is uploaded.",
                                       "TinyFire 只读取本机日志，用量数据不会上传。",
                                       "TinyFire はローカルのログのみを読み取ります。アップロードは行いません。",
                                       "TinyFire는 로컬 로그만 읽습니다. 어떤 데이터도 업로드하지 않습니다." } },
            { "about.origin",  new[] { "A Windows port of wdkwdkwdk/tinyfire (macOS, MIT), rebuilt in C# / WinForms.",
                                       "本项目是 wdkwdkwdk/tinyfire（macOS，MIT 协议）的 Windows 版本，用 C# / WinForms 重写。",
                                       "wdkwdkwdk/tinyfire（macOS, MIT）の Windows 版を C# / WinForms で再実装。",
                                       "wdkwdkwdk/tinyfire(macOS, MIT)의 Windows 포트로 C# / WinForms로 재작성했습니다." } },
            { "about.powered", new[] { ".NET Framework 3.5 (CLR 2.0) · no installer · runs on Windows 7 SP1 and later",
                                       ".NET Framework 3.5（CLR 2.0）· 免安装 · Windows 7 SP1 及以上可直接运行",
                                       ".NET Framework 3.5（CLR 2.0）· インストーラ不要 · Windows 7 SP1 以降で動作",
                                       ".NET Framework 3.5(CLR 2.0) · 설치 불필요 · Windows 7 SP1 이상에서 실행" } },
            { "about.sources", new[] { "Aggregates 22 local tools: Claude Code, Codex, Grok, Pi, Amp, WorkBuddy, CodeBuddy, Qoder, Qwen Code, Kimi, GitHub Copilot, ZCode, OpenCode, Gemini CLI, Droid, Cline, Roo Code, Kilo Code, DeepSeek Harness, Command Code, OpenClaw and Every Code.",
                                       "共聚合 22 种本地工具：Claude Code、Codex、Grok、Pi、Amp、WorkBuddy、CodeBuddy、Qoder、Qwen Code、Kimi、GitHub Copilot、ZCode、OpenCode、Gemini CLI、Droid、Cline、Roo Code、Kilo Code、DeepSeek Harness、Command Code、OpenClaw、Every Code。",
                                       "22 種類のローカルツールを集約します: Claude Code, Codex, Grok, Pi, Amp, WorkBuddy, CodeBuddy, Qoder, Qwen Code, Kimi, GitHub Copilot, ZCode, OpenCode, Gemini CLI, Droid, Cline, Roo Code, Kilo Code, DeepSeek Harness, Command Code, OpenClaw, Every Code。",
                                       "22개 로컬 도구를 집계합니다: Claude Code, Codex, Grok, Pi, Amp, WorkBuddy, CodeBuddy, Qoder, Qwen Code, Kimi, GitHub Copilot, ZCode, OpenCode, Gemini CLI, Droid, Cline, Roo Code, Kilo Code, DeepSeek Harness, Command Code, OpenClaw, Every Code." } },
            { "about.gap",     new[] { "Deliberately not collected: Cursor (its usage only exists behind a Dashboard API) and the tools that expose nothing but a character-count estimate (Kiro, Antigravity, QwenWork). Estimates would turn the flame into noise.",
                                       "刻意不采集：Cursor（它的用量只存在于 Dashboard API 后面），以及只提供「按字符数估算」的工具（Kiro、Antigravity、QwenWork）——估出来的数字只会让火焰变成噪音。",
                                       "意図的に収集しません: Cursor（Dashboard API 経由のみ）と、文字数からの推定値しか出さないツール（Kiro, Antigravity, QwenWork）。推定値は炎をノイズに変えてしまいます。",
                                       "의도적으로 수집하지 않음: Cursor(Dashboard API 전용)와 문자 수 추정치만 제공하는 도구(Kiro, Antigravity, QwenWork). 추정치는 불꽃을 노이즈로 만듭니다." } },

            // ---- 单位 ----
            { "unit.tokens", new[] { "tokens", "tokens", "tokens", "tokens" } },
            { "tier.label",  new[] { "Tier", "档位", "段階", "단계" } },
        };

        private static AppLanguage _current = AppLanguage.System;
        private static string _resolved = "en";

        public static event Action LanguageChanged;

        public static AppLanguage Current
        {
            get { return _current; }
            set
            {
                if (_current == value) return;
                _current = value;
                _resolved = Resolve(value);
                var h = LanguageChanged;
                if (h != null) h();
            }
        }

        public static string ResolvedCode { get { return _resolved; } }

        private static string Resolve(AppLanguage lang)
        {
            switch (lang)
            {
                case AppLanguage.English: return "en";
                case AppLanguage.ChineseSimplified: return "zh-Hans";
                case AppLanguage.Japanese: return "ja";
                case AppLanguage.Korean: return "ko";
                default:
                    {
                        string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                        switch (two)
                        {
                            case "zh": return "zh-Hans";
                            case "ja": return "ja";
                            case "ko": return "ko";
                            default: return "en";
                        }
                    }
            }
        }

        private static int Column()
        {
            switch (_resolved)
            {
                case "zh-Hans": return ZH;
                case "ja": return JA;
                case "ko": return KO;
                default: return EN;
            }
        }

        public static string T(string key)
        {
            string[] row;
            if (!Table.TryGetValue(key, out row)) return key;
            return row[Column()];
        }

        /// <summary>把数字压成 1.2k / 3.4M 这类紧凑写法。</summary>
        public static string Compact(long value)
        {
            double v = value;
            string suffix = "";
            if (Math.Abs(v) >= 1e9) { v /= 1e9; suffix = "B"; }
            else if (Math.Abs(v) >= 1e6) { v /= 1e6; suffix = "M"; }
            else if (Math.Abs(v) >= 1e3) { v /= 1e3; suffix = "k"; }
            else return value.ToString(CultureInfo.InvariantCulture);

            if (v >= 100) return v.ToString("0", CultureInfo.InvariantCulture) + suffix;
            if (v >= 10) return v.ToString("0.0", CultureInfo.InvariantCulture) + suffix;
            return v.ToString("0.00", CultureInfo.InvariantCulture) + suffix;
        }

        public static string Grouped(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}

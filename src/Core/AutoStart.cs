//
//  AutoStart.cs — CodingFire for Windows
//
//  开机自启。这是整个程序**唯一**碰注册表的地方：设置本身仍然只落在
//  %APPDATA%\CodingFire\settings.json 里，注册表里只有「要不要开机启动」这一条。
//
//  为什么用 HKCU 而不是 HKLM / 启动文件夹：
//    · HKCU\...\Run 是每用户的，不需要管理员权限，也不会弹 UAC；
//    · 关掉就是把这条值删掉，用户自己能看懂、能手动清理；
//    · 启动文件夹要造 .lnk，在 .NET 3.5 上要么拉 COM(WSH) 要么手写二进制格式，
//      都比写一条字符串脆弱。
//

using System;
using System.Reflection;
using Microsoft.Win32;

namespace CodingFire.Core
{
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>注册表里的值名。固定用它，不要跟着 exe 改名而变，否则旧值会残留。</summary>
        private const string ValueName = "CodingFire";

        /// <summary>注册表里现在是否真的挂着自启项（以注册表为准，不以设置为准）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string v = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("autostart read failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把自启项设成 <paramref name="desired"/> 状态。
        /// 已经是目标状态就什么都不写（避免每次启动都无谓地动注册表）。
        /// 返回 false 表示写失败（组策略、权限、注册表被锁），调用方据此回滚设置。
        /// </summary>
        public static bool Apply(bool desired)
        {
            try
            {
                if (desired)
                {
                    string exe = ExecutablePath();
                    if (string.IsNullOrEmpty(exe))
                    {
                        Log.Warn("autostart skipped: cannot resolve executable path");
                        return false;
                    }

                    // 带引号，路径里有空格（C:\Program Files\…）也不会被拆成两个参数
                    string wanted = "\"" + exe + "\"";

                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (key == null) return false;
                        if (string.Equals(key.GetValue(ValueName) as string, wanted, StringComparison.Ordinal))
                            return true;   // exe 没挪窝，已经是这条，不用重写
                        key.SetValue(ValueName, wanted, RegistryValueKind.String);
                    }
                }
                else
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (key == null) return true;                       // 键都没有，等于已经关掉
                        if (key.GetValue(ValueName) == null) return true;   // 值本来就不在
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("autostart write failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>当前 exe 的完整路径。取不到就返回 null，调用方不要瞎写注册表。</summary>
        private static string ExecutablePath()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc)) return loc;
                }
            }
            catch (Exception) { }

            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    if (p.MainModule != null)
                    {
                        string f = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(f)) return f;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }
    }
}

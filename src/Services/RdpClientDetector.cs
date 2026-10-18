using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace MyRemote.Services
{
    /// <summary>
    /// 探测本机可用的 RDP 客户端控件（mstscax.dll 中的 MsRdpClient* ActiveX）。
    ///
    /// 背景（实机踩坑）：
    /// 不同 Windows 版本/裁剪（如 Win10 IoT LTSC）注册的控件 CLSID 各不相同，
    /// 写死单一 CLSID 会在部分机器上抛 0x80040154（REGDB_E_CLASSNOTREG，“没有注册类”）。
    ///
    /// 策略：
    /// 1) 先按内置候选表（原版 CLSID 优先，其后按版本从高到低）逐个尝试实例化；
    /// 2) 全部失败时扫描注册表，把所有名称含 "RDP Client Control" 的 CLSID 按版本排序再试。
    /// 探测在独立 STA 线程执行（与 UI 线程同套间模型，避免 MTA 下的行为差异），结果按进程缓存。
    /// </summary>
    public static class RdpClientDetector
    {
        private static readonly object Sync = new();
        private static bool _done;
        private static string _clsid = "";

        /// <summary>探测到的可用控件 CLSID；没有可用控件时返回空串。</summary>
        public static string DetectedClsid
        {
            get
            {
                if (!_done) Detect();
                return _clsid;
            }
        }

        /// <summary>本机是否有可用的 RDP 客户端控件。</summary>
        public static bool IsAvailable => DetectedClsid.Length > 0;

        /// <summary>
        /// 清除探测缓存，让下次访问重新探测。
        /// 用途：注册表修复（regsvr32 mstscax.dll）、安装组件后需要立即复检。
        /// </summary>
        public static void Reset()
        {
            lock (Sync) { _done = false; _clsid = ""; }
        }

        /// <summary>
        /// 候选 CLSID 表。说明：
        /// - 首位为原版程序使用的 MsRdpClient9 NSFS 变体——在已注册它的系统上保持原有行为；
        /// - 其余按“NotSafeForScripting 变体（桌面内嵌首选）→ 可再分发型标准控件”，版本从高到低。
        /// 实际可用性以运行期实例化测试为准（不同系统注册情况不同）。
        /// </summary>
        private static readonly string[] Preferred =
        {
            "301B94BA-5D25-4A12-BFFE-3B6E7A6167FD", // MsRdpClient9 NotSafeForScripting（原版）
            // —— NotSafeForScripting 变体，版本从高到低 ——
            "3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A", // version 13
            "1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8", // version 12
            "A0C63C30-F08D-4AB4-907C-34905D770C7D", // version 11
            "8B918B82-7985-4C24-89DF-C33AD2BBFBCD", // version 10
            "A3BC03A0-041D-42E3-AD22-882B7865C9C5", // version 9
            "54D38BF7-B1EF-4479-9674-1BD6EA465258", // version 8
            "D2EA46A7-C2BF-426B-AF24-E19C44456399", // version 7
            "4EB2F086-C818-447E-B32C-C51CE2B30D31", // version 6
            "6AE29350-321B-42BE-BBE5-12FB5270C0DE", // version 5
            "ACE575FD-1FCF-4074-9401-EBAB990FA9DE", // version 4
            "3523C2FB-4031-44E4-9A3B-F1E94986EE7F", // version 3
            "7CACBD7B-0D99-468F-AC33-22E495C0AFE5", // version 2
            // —— 可再分发型（redistributable）标准控件，版本从高到低 ——
            "945EE98E-B376-4EC2-B2E5-64C9410F93B7", // version 13
            "22A7E88C-5BF5-4DE6-B687-60F7331DF190", // version 12
            "C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24", // version 11
            "301B94BA-5D25-4A12-BFFE-3B6E7A616585", // version 10
            "5F681803-2900-4C43-A1CC-CF405404A676", // version 9
            "A9D7038D-B5ED-472E-9C47-94BEA90A5910", // version 8
            "7390F3D8-0439-4C05-91E3-CF5CB290C3D0", // version 7
            "4EB89FF4-7F78-4A0F-8B8D-2BF02E94E4B2", // version 6
            "4EDCB26C-D24C-4E72-AF07-B576699AC0DE", // version 5
            "54CE37E0-9834-41AE-9896-4DAB69DC022B", // version 5a
            "6A6F4B83-45C5-4CA9-BDD9-0D81C12295E4", // version 4a
            "7584C670-2274-4EFB-B00B-D6AABA6D3850", // version 4
            "9059F30F-4EB1-4BD2-9FDC-36F43A218F4A", // version 3
            "971127BB-259F-48C2-BD75-5F97A3331551", // version 3a
            "791FA017-2DE3-492E-ACC5-53C67A2B94D0", // version 1
        };

        /// <summary>测试指定 CLSID 能否被实例化（COM 类工厂可用且创建成功）。</summary>
        public static bool CanInstantiate(string clsid)
        {
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid(clsid));
                if (type == null) return false;
                var obj = Activator.CreateInstance(type);
                if (obj == null) return false;
                try { Marshal.ReleaseComObject(obj); } catch { }
                return true;
            }
            catch { return false; }
        }

        private static void Detect()
        {
            lock (Sync)
            {
                if (_done) return;
                _done = true;

                // 在独立 STA 线程上探测：RDP 控件是 STA 组件，
                // 保证探测环境的套间模型与真实使用（UI 线程）一致。
                var thread = new Thread(() =>
                {
                    try { _clsid = DetectCore(); }
                    catch { _clsid = ""; }
                })
                {
                    IsBackground = true,
                    Name = "RdpClsidProbe"
                };
                try { thread.SetApartmentState(ApartmentState.STA); } catch { }
                thread.Start();
                thread.Join(20000);
            }
        }

        private static string DetectCore()
        {
            // 1) 内置候选表
            foreach (var c in Preferred)
            {
                if (CanInstantiate(c)) return c;
            }

            // 2) 兜底：扫描注册表（覆盖未来新增版本）
            foreach (var c in ScanRegistryClsIds())
            {
                if (Preferred.Contains(c, StringComparer.OrdinalIgnoreCase)) continue;
                if (CanInstantiate(c)) return c;
            }

            return "";
        }

        /// <summary>
        /// 扫描 HKCR\CLSID，收集所有显示名含 "RDP Client Control" 的控件，按版本号降序排列。
        /// </summary>
        private static List<string> ScanRegistryClsIds()
        {
            var list = new List<(int ver, string clsid)>();
            try
            {
                using var root = Registry.ClassesRoot.OpenSubKey("CLSID");
                if (root == null) return new List<string>();
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(name);
                        var display = key?.GetValue("") as string;
                        if (string.IsNullOrEmpty(display)) continue;
                        if (display.IndexOf("RDP Client Control", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var digits = Regex.Match(display, @"version\s+(\d+)", RegexOptions.IgnoreCase);
                        var ver = digits.Success && int.TryParse(digits.Groups[1].Value, out var v) ? v : 0;
                        list.Add((ver, name.Trim('{', '}')));
                    }
                    catch { }
                }
            }
            catch { }
            return list.OrderByDescending(x => x.ver).Select(x => x.clsid).ToList();
        }
    }
}
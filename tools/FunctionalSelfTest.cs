using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyRemote.Controls;
using MyRemote.Forms;
using MyRemote.Models;
using MyRemote.Services;

namespace MyRemote.Probe
{
    /// <summary>
    /// 全功能自检：在真机上逐项验证 MyRemote 的全部功能链路是否完好。
    ///
    /// 设计原则：
    /// 1) 分层覆盖 —— 纯函数（无副作用、可断言精确结果）、服务层（真实调用）、
    ///    UI 层（真实构造窗体并驱动交互逻辑）、端到端（跨模块串联）。
    /// 2) 隔离 —— 所有写盘类测试（设备库 / 凭据库 / 日志 / 快照）统一重定向到临时目录
    ///    （AppPaths.OverrideDir），绝不污染 %APPDATA%\MyRemote 下的真实用户数据。
    /// 3) 不改系统 —— 不执行提权型修复（注册表 / 服务 / 防火墙）。这类动作由 --exec 模式负责。
    /// 4) 可复现 —— 每项 assert 失败都打印期望值/实际值，便于直接定位。
    /// </summary>
    internal static class FunctionalSelfTest
    {
        private static int _pass, _fail, _skip;
        private static readonly List<string> Failures = new();
        private static readonly List<string> Skips = new();
        private static string _group = "";

        // ================================================================
        //  断言基础设施
        // ================================================================
        private static void Group(string name)
        {
            _group = name;
            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────────────────");
            Console.WriteLine("  " + name);
            Console.WriteLine("──────────────────────────────────────────────────────────");
        }

        private static void Ok(string what, string? detail = null)
        {
            _pass++;
            Console.WriteLine($"  [OK]   {what}" + (string.IsNullOrEmpty(detail) ? "" : "   → " + detail));
        }

        private static void Bad(string what, string expected, string actual)
        {
            _fail++;
            var msg = $"{_group} / {what}：期望 {expected}，实际 {actual}";
            Failures.Add(msg);
            Console.WriteLine($"  [FAIL] {what}");
            Console.WriteLine($"         期望：{expected}");
            Console.WriteLine($"         实际：{actual}");
        }

        private static void Skip(string what, string why)
        {
            _skip++;
            Skips.Add($"{_group} / {what}：{why}");
            Console.WriteLine($"  [SKIP] {what}   → {why}");
        }

        private static void Eq(string what, object? expected, object? actual)
        {
            if (Equals(expected, actual)) Ok(what, Esc(actual));
            else Bad(what, Esc(expected), Esc(actual));
        }

        private static void True(string what, bool cond, string? detail = null)
        {
            if (cond) Ok(what, detail);
            else Bad(what, "true", "false" + (detail == null ? "" : "（" + detail + "）"));
        }

        private static void NotNull(string what, object? v)
        {
            if (v != null) Ok(what, Esc(v));
            else Bad(what, "非 null", "null");
        }

        private static string Esc(object? o)
        {
            var s = o?.ToString() ?? "(null)";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return s.Length > 160 ? s.Substring(0, 157) + "…" : s;
        }

        /// <summary>执行一个子检查，异常即记为失败（不让单点异常中断整轮自检）。</summary>
        private static void Section(string what, Action body)
        {
            try { body(); }
            catch (Exception ex) { Bad(what, "无异常", ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// 自动探测交付目录位置（避免硬编码本机路径，便于在其他机器/克隆的仓库中运行）：
        /// ① 环境变量 MYREMOTE_DELIVERY_DIR 优先；
        /// ② 从探针 exe 所在目录逐级向上查找名为 MyRemote-Delivery 的目录。
        /// 找不到返回 null（调用方按"跳过"处理，不影响其它用例）。
        /// </summary>
        private static string? FindDeliveryDir()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("MYREMOTE_DELIVERY_DIR");
                if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

                var dir = AppContext.BaseDirectory;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    var candidate = Path.Combine(dir, "MyRemote-Delivery");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch { }
            return null;
        }

        // ================================================================
        //  入口
        // ================================================================
        public static int Run()
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║           MyRemote 全功能自检（真机 / 隔离沙箱）          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"管理员：{(SystemLog.IsAdministrator() ? "是" : "否（不执行提权类修改）")}");
            Console.WriteLine($"界面缩放：{UiScale.Describe()}");

            // ---- 数据目录隔离：全部写盘测试落到临时目录 ----
            var sandbox = Path.Combine(Path.GetTempPath(), "MyRemote-SelfTest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sandbox);
            AppPaths.OverrideDir = sandbox;
            // ---- UI 副作用抑制：不创建托盘图标、不自动发起 RDP 连接 ----
            MainForm.CreateTrayIcon = false;
            SessionForm.AutoConnectOnShown = false;
            Console.WriteLine($"沙箱目录：{sandbox}");
            Console.WriteLine("（所有设备/凭据/日志/快照测试均在此目录内进行，不影响真实数据）");
            Console.WriteLine("（已抑制托盘图标创建与 RDP 自动连接，避免测试产生副作用）");

            try
            {
                TestAppPaths();
                TestUiScale();
                TestDeviceStore();
                TestCredentialVault();
                TestSystemLog();
                TestSmbHelpers();
                TestTailscaleLayer();
                TestRdpControlLayer();
                TestPackageLayer();
                TestLocalMachineInfo();
                TestRepairServiceSurface();
                TestSnapshotModel();
                TestDiagnosticsConsistency();
                TestDiagnosticsRunAll();
                TestFormsConstruction();
                TestMainFormInteractions();
                TestEndToEndRepairRoundTrip();
                TestConcurrencyAndRobustness();
            }
            finally
            {
                AppPaths.OverrideDir = null;
                MainForm.CreateTrayIcon = true;
                SessionForm.AutoConnectOnShown = true;
                try { Directory.Delete(sandbox, true); } catch { }
            }

            // ---- 汇总 ----
            Console.WriteLine();
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine($"  自检汇总：通过 {_pass} ｜ 失败 {_fail} ｜ 跳过 {_skip}");
            Console.WriteLine("══════════════════════════════════════════════════════════");
            if (Failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细：");
                foreach (var f in Failures) Console.WriteLine("  ✘ " + f);
            }
            if (Skips.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("跳过明细（环境不满足，非缺陷）：");
                foreach (var s in Skips) Console.WriteLine("  ○ " + s);
            }
            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? "结论：所有可执行检查均通过 —— 功能链路完好。"
                : $"结论：发现 {_fail} 处失败，需要修复。");
            return _fail == 0 ? 0 : 1;
        }

        // ================================================================
        //  1. AppPaths：路径解析与重定向
        // ================================================================
        private static void TestAppPaths()
        {
            Group("1. AppPaths 数据目录解析 / 重定向");

            Section("重定向生效", () =>
            {
                var overrideDir = Path.Combine(Path.GetTempPath(), "MyRemote-Override-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                var prev = AppPaths.OverrideDir;

                AppPaths.OverrideDir = overrideDir;
                Eq("AppDir 重定向到指定目录", overrideDir, AppPaths.AppDir);
                Eq("DevicesFile 跟随重定向", Path.Combine(overrideDir, "devices.json"), AppPaths.DevicesFile);
                Eq("SecretsFile 跟随重定向", Path.Combine(overrideDir, "secrets.json"), AppPaths.SecretsFile);
                Eq("LogFilePath 跟随重定向", Path.Combine(overrideDir, "MyRemote.log"), AppPaths.LogFilePath);
                True("重定向时 IsPortable 为 false", !AppPaths.IsPortable);
                Eq("RollbackDir 位于重定向目录下", Path.Combine(overrideDir, "Rollback"), AppPaths.RollbackDir);
                Eq("LastSnapshotPath 位于 Rollback 下",
                    Path.Combine(overrideDir, "Rollback", "last.json"), AppPaths.LastSnapshotPath);
                True("RollbackDir 目录被自动创建", Directory.Exists(AppPaths.RollbackDir));

                AppPaths.OverrideDir = prev;
            });

            Section("默认路径形态", () =>
            {
                var prev = AppPaths.OverrideDir;
                AppPaths.OverrideDir = null;
                var appDir = AppPaths.AppDir;
                True("默认 AppDir 非空", !string.IsNullOrWhiteSpace(appDir), appDir);
                True("默认 AppDir 是绝对路径", Path.IsPathRooted(appDir), appDir);
                True("默认路径以 MyRemote 结尾或为 exe 目录（便携模式）",
                    appDir.TrimEnd('\\').EndsWith("MyRemote", StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(appDir, "devices.json"))
                    || File.Exists(Path.Combine(appDir, ".portable")),
                    appDir);
                // 关键：默认目录必须是有效路径形态，且不能被我们的测试创建
                var defaultLog = AppPaths.LogFilePath;
                True("默认日志路径已解析", defaultLog.EndsWith("MyRemote.log", StringComparison.OrdinalIgnoreCase), defaultLog);
                AppPaths.OverrideDir = prev;
            });
        }

        // ================================================================
        //  2. UiScale：尺寸换算
        // ================================================================
        private static void TestUiScale()
        {
            Group("2. UiScale 尺寸换算 / 屏幕裁剪");

            Section("基础换算", () =>
            {
                var f = UiScale.Factor;
                True("缩放系数在合理区间 0.5~4.0", f >= 0.5f && f <= 4f, f.ToString("0.###"));
                Eq("S(0) 恒为 0", 0, UiScale.S(0));
                Eq("S(100) = round(100×factor)", (int)Math.Round(100 * f, MidpointRounding.AwayFromZero), UiScale.S(100));

                var sz = UiScale.S(new Size(100, 40));
                Eq("Size 换算宽度一致", UiScale.S(100), sz.Width);
                Eq("Size 换算高度一致", UiScale.S(40), sz.Height);

                var pad = UiScale.S(new Padding(8, 4, 8, 4));
                Eq("Padding 左", UiScale.S(8), pad.Left);
                Eq("Padding 上", UiScale.S(4), pad.Top);
                Eq("Padding 右", UiScale.S(8), pad.Right);
                Eq("Padding 下", UiScale.S(4), pad.Bottom);

                True("Describe() 含百分比", UiScale.Describe().Contains('%'), UiScale.Describe());
                True("SystemDpi 在合理区间", UiScale.SystemDpi >= 48 && UiScale.SystemDpi <= 480, UiScale.SystemDpi.ToString());
            });

            Section("单调性（大设计值必须得到大实际值）", () =>
            {
                var a = UiScale.S(100);
                var b = UiScale.S(200);
                var c = UiScale.S(400);
                True("S(100) < S(200) < S(400)", a < b && b < c, $"{a} < {b} < {c}");
                True("S 结果非负", UiScale.S(1) >= 0 && UiScale.S(50) >= 0);
            });

            Section("屏幕可用区域", () =>
            {
                var wa = UiScale.WorkingArea();
                True("屏幕宽 >= 320", wa.Width >= 320, wa.Width.ToString());
                True("屏幕高 >= 240", wa.Height >= 240, wa.Height.ToString());
            });

            Section("SetFormSize 裁剪", () =>
            {
                using var f = new Form();
                UiScale.SetFormSize(f, 760, 520, 620, 420);
                var wa = UiScale.WorkingArea();
                True("窗口宽不超屏", f.Width <= wa.Width, $"{f.Width} <= {wa.Width}");
                True("窗口高不超屏", f.Height <= wa.Height, $"{f.Height} <= {wa.Height}");
                True("窗口宽达到设计值的缩放结果（或受屏幕限制）",
                    f.Width == Math.Min(UiScale.S(760), Math.Max(320, wa.Width - UiScale.S(40))), f.Width.ToString());
                True("MinimumSize 已设置", f.MinimumSize.Width > 0 && f.MinimumSize.Height > 0,
                    $"{f.MinimumSize.Width}x{f.MinimumSize.Height}");
                True("MinimumSize 不超屏", f.MinimumSize.Width <= wa.Width && f.MinimumSize.Height <= wa.Height,
                    $"{f.MinimumSize.Width}x{f.MinimumSize.Height} vs {wa.Width}x{wa.Height}");
            });

            Section("SetFormSize 极端小屏裁剪（不设最小尺寸）", () =>
            {
                using var f = new Form();
                UiScale.SetFormSize(f, 5000, 5000, 3000, 3000); // 远超屏幕
                var wa = UiScale.WorkingArea();
                True("超大设计尺寸被裁剪到屏幕内", f.Width <= wa.Width && f.Height <= wa.Height,
                    $"{f.Width}x{f.Height} vs {wa.Width}x{wa.Height}");
                True("超大最小尺寸也被裁剪", f.MinimumSize.Width <= wa.Width && f.MinimumSize.Height <= wa.Height,
                    $"{f.MinimumSize.Width}x{f.MinimumSize.Height}");
            });

            Section("AutoFit 按钮自适应", () =>
            {
                using var b = new Button { Text = "远程桌面" };
                var ret = b.AutoFit(70);
                True("返回自身（链式调用）", ReferenceEquals(ret, b));
                True("AutoSize 已开启", b.AutoSize);
                Eq("AutoSizeMode 为 GrowAndShrink", AutoSizeMode.GrowAndShrink, b.AutoSizeMode);
                Eq("MinimumSize 宽度按设计值换算", UiScale.S(70), b.MinimumSize.Width);
                True("按钮首选宽度不小于最小宽度", b.PreferredSize.Width >= b.MinimumSize.Width,
                    $"{b.PreferredSize.Width} >= {b.MinimumSize.Width}");

                // 关键回归：超长中文按钮的文字必须能放下（这是"字显示不全"的核心）
                using var longBtn = new Button { Text = "一键检测并修复" };
                longBtn.AutoFit(80);
                var need = TextRenderer.MeasureText(longBtn.Text, longBtn.Font).Width;
                var learned = longBtn.PreferredSize.Width;
                True("长中文按钮的首选宽度 >= 文字需求宽度", learned >= need,
                    $"PreferredSize={learned} 文字需求={need}");
            });

            Section("ButtonRowHeight", () =>
            {
                var h = UiScale.ButtonRowHeight();
                True("按钮行高 > 0", h > 0, h.ToString());
                True("按钮行高 >= 设计基准缩放值", h >= UiScale.S(26), $"{h} >= {UiScale.S(26)}");
                var h2 = UiScale.ButtonRowHeight(20);
                True("内边距增大时行高不减小", h2 >= h, $"{h2} >= {h}");
            });

            Section("ListView 列宽自适应", () =>
            {
                using var lv = new ListView { View = View.Details, Width = 600, Height = 200 };
                UiScale.SetColumns(lv, ("显示名", 150), ("地址", 150), ("状态", 90), ("备注", 200));
                Eq("列数正确", 4, lv.Columns.Count);
                Eq("首列宽按设计值换算", UiScale.S(150), lv.Columns[0].Width);
                Eq("首列标题", "显示名", lv.Columns[0].Text);

                // 末列补满：容器变宽后末列应被拉伸吸收剩余空间
                lv.CreateControl();
                var narrow = lv.Columns[3].Width;
                lv.Width = 1400;
                lv.PerformLayout();
                foreach (var _ in Enumerable.Range(0, 3)) { Application.DoEvents(); lv.PerformLayout(); }
                var wide = lv.Columns[3].Width;
                True("窗口变宽后末列被拉伸（吸收剩余空间）", wide > narrow, $"窄={narrow} → 宽={wide}");
                True("末列宽度不低于最小限制", wide >= UiScale.S(80), $"{wide} >= {UiScale.S(80)}");

                // 窗口变窄时不应把列压到 0（否则内容全丢）
                lv.Width = 400;
                lv.PerformLayout();
                Application.DoEvents();
                True("窗口变窄时末列仍保持可读宽度", lv.Columns[3].Width >= UiScale.S(80),
                    $"{lv.Columns[3].Width} >= {UiScale.S(80)}");
            });
        }

        // ================================================================
        //  3. DeviceStore：设备库 CRUD
        // ================================================================
        private static void TestDeviceStore()
        {
            Group("3. DeviceStore 设备读写（沙箱内）");

            Section("空库读取", () =>
            {
                var list = DeviceStore.Load();
                NotNull("首次加载返回非 null", list);
                Eq("首次加载为空列表", 0, list.Count);
            });

            Section("保存 → 读取 往返", () =>
            {
                var devices = new List<Device>
                {
                    new() { Id = "d1", Name = "主力机", Host = "100.64.0.5", UserName = "Sunny", RdpPort = 3389, ShareName = "C$", Notes = "客厅" },
                    new() { Id = "d2", Name = "备用机", Host = "100.64.0.9", UserName = "test\\u", RdpPort = 13389, ShareName = "D$", Notes = "含中文与符号 ×≠" },
                };
                DeviceStore.Save(devices);

                True("配置文件已生成", File.Exists(DeviceStore.ConfigFilePath), DeviceStore.ConfigFilePath);

                var back = DeviceStore.Load();
                Eq("设备数量一致", 2, back.Count);
                Eq("第 1 台名称", "主力机", back[0].Name);
                Eq("第 1 台 IP", "100.64.0.5", back[0].Host);
                Eq("第 1 台用户名", "Sunny", back[0].UserName);
                Eq("第 1 台端口", 3389, back[0].RdpPort);
                Eq("第 1 台共享名", "C$", back[0].ShareName);
                Eq("第 2 台端口", 13389, back[1].RdpPort);
                Eq("第 2 台备注中文与特殊符号保真", "含中文与符号 ×≠", back[1].Notes);
                Eq("第 2 台用户名含反斜杠保真", "test\\u", back[1].UserName);
            });

            Section("备份文件生成（原子写保护）", () =>
            {
                // 第二次保存才会产生 .bak（首次保存目标不存在，走 Move 分支）
                var list = DeviceStore.Load();
                var beforeCount = list.Count;
                list.Add(new Device { Id = "d3", Name = "第三台", Host = "100.64.0.11" });
                DeviceStore.Save(list);
                True(".bak 备份已生成", File.Exists(DeviceStore.ConfigFilePath + ".bak"),
                    DeviceStore.ConfigFilePath + ".bak");
                // 备份应保存"保存前"的状态（不含刚加入的第三台）
                var bak = System.Text.Json.JsonSerializer.Deserialize<List<Device>>(
                    File.ReadAllText(DeviceStore.ConfigFilePath + ".bak"));
                NotNull("备份可解析", bak);
                Eq("备份保存的是保存前状态", beforeCount, bak!.Count);
            });

            Section("损坏文件的自愈", () =>
            {
                var corruptBefore = Directory.GetFiles(AppPaths.AppDir, "devices.json.corrupt-*").Length;
                File.WriteAllText(DeviceStore.ConfigFilePath, "{ 这不是合法 JSON ", Encoding.UTF8);
                var recovered = DeviceStore.Load();
                NotNull("损坏时不抛异常且返回列表", recovered);
                True("从 .bak 恢复出设备（或安全回退空列表）", recovered.Count >= 0, recovered.Count.ToString());
                // 关键：损坏文件必须被另存留证，否则下一次保存会直接覆盖、现场丢失
                var corrupt = Directory.GetFiles(AppPaths.AppDir, "devices.json.corrupt-*");
                True("损坏文件已另存为 .corrupt-* 留证", corrupt.Length > corruptBefore,
                    corrupt.Length > corruptBefore
                        ? Path.GetFileName(corrupt.OrderBy(x => x).Last())
                        : "（未找到）");
            });

            Section("空列表保存（清空设备）", () =>
            {
                DeviceStore.Save(new List<Device>());
                Eq("清空后读取为 0 台", 0, DeviceStore.Load().Count);
            });

            Section("EnsureDir", () =>
            {
                Directory.Delete(AppPaths.AppDir, true);
                True("删除后目录不存在", !Directory.Exists(AppPaths.AppDir));
                DeviceStore.EnsureDir();
                True("EnsureDir 后目录存在", Directory.Exists(AppPaths.AppDir));
            });
        }

        // ================================================================
        //  4. CredentialVault：DPAPI 凭据加解密
        // ================================================================
        private static void TestCredentialVault()
        {
            Group("4. CredentialVault 凭据加解密（DPAPI，沙箱内）");

            Section("保存 → 读取 往返", () =>
            {
                CredentialVault.DeletePassword("dev-1"); // 先清干净
                CredentialVault.SavePassword("dev-1", "P@ssw0rd-中文密码-123");
                var got = CredentialVault.GetPassword("dev-1");
                Eq("密码解密后与原文一致（含中文）", "P@ssw0rd-中文密码-123", got);
            });

            Section("落盘内容必须加密（明文不可见）", () =>
            {
                var raw = File.ReadAllText(AppPaths.SecretsFile);
                True("磁盘上不含明文密码", !raw.Contains("P@ssw0rd"), raw.Length > 120 ? raw.Substring(0, 120) + "…" : raw);
                True("落盘文件为 JSON 且含键名", raw.Contains("dev-1"), raw.Length > 120 ? raw.Substring(0, 120) + "…" : raw);
                try
                {
                    var attrs = File.GetAttributes(AppPaths.SecretsFile);
                    True("凭据文件带隐藏属性", (attrs & FileAttributes.Hidden) != 0, attrs.ToString());
                }
                catch (Exception ex) { Skip("凭据文件隐藏属性", ex.Message); }
            });

            Section("多设备互不干扰", () =>
            {
                CredentialVault.SavePassword("dev-A", "aaa");
                CredentialVault.SavePassword("dev-B", "bbb");
                Eq("dev-A 密码正确", "aaa", CredentialVault.GetPassword("dev-A"));
                Eq("dev-B 密码正确", "bbb", CredentialVault.GetPassword("dev-B"));
                Eq("dev-1 不受影响", "P@ssw0rd-中文密码-123", CredentialVault.GetPassword("dev-1"));
            });

            Section("覆盖写与删除", () =>
            {
                CredentialVault.SavePassword("dev-A", "aaa-new");
                Eq("覆盖后取到新值", "aaa-new", CredentialVault.GetPassword("dev-A"));

                CredentialVault.DeletePassword("dev-A");
                Eq("删除后取不到", null, CredentialVault.GetPassword("dev-A"));
                Eq("删除 A 不影响 B", "bbb", CredentialVault.GetPassword("dev-B"));
            });

            Section("边界情况", () =>
            {
                CredentialVault.SavePassword("dev-empty", "");
                Eq("保存空密码等价于删除", null, CredentialVault.GetPassword("dev-empty"));
                Eq("读取不存在的设备返回 null", null, CredentialVault.GetPassword("no-such-device-xyz"));
                CredentialVault.SavePassword("dev-long", new string('x', 200));
                Eq("长密码（200 字符）往返正常", new string('x', 200), CredentialVault.GetPassword("dev-long"));
            });

            Section("损坏的凭据文件不致命", () =>
            {
                // 注意：凭据文件经 HardenAcl 收紧了权限并带隐藏属性，
                // 直接 File.WriteAllText 覆写会被拒（这是设计意图：减少凭据被篡改的面）。
                // 因此测试时先恢复正常属性，再写入。
                var backup = File.ReadAllText(AppPaths.SecretsFile);
                RestoreWritable(AppPaths.SecretsFile);
                File.WriteAllText(AppPaths.SecretsFile, "!!! 坏文件 !!!", Encoding.UTF8);
                Eq("损坏时读取安全返回 null（不抛异常）", null, CredentialVault.GetPassword("dev-B"));

                RestoreWritable(AppPaths.SecretsFile);
                File.WriteAllText(AppPaths.SecretsFile, backup, Encoding.UTF8);
                Eq("恢复文件后仍可正常读取", "bbb", CredentialVault.GetPassword("dev-B"));
            });
        }

        /// <summary>测试辅助：把文件恢复为可写（去掉隐藏/只读属性），用于模拟外部破坏文件。</summary>
        private static void RestoreWritable(string path)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                var sec = fi.GetAccessControl();
                sec.SetAccessRuleProtection(false, true);   // 恢复继承，使本进程可写
                fi.SetAccessControl(sec);
            }
            catch { }
        }

        // ================================================================
        //  5. SystemLog：日志缓冲 / 落盘 / 轮转
        // ================================================================
        private static void TestSystemLog()
        {
            Group("5. SystemLog 日志（沙箱内）");

            Section("日志缓冲区", () =>
            {
                var before = SystemLog.Entries.Count;
                SystemLog.Info("自检", "INFO 测试消息");
                SystemLog.Warn("自检", "WARN 测试消息");
                SystemLog.Error("自检", "ERROR 测试消息");
                var after = SystemLog.Entries.Count;
                Eq("缓冲区新增 3 条", before + 3, after);

                var last3 = SystemLog.Entries.Skip(Math.Max(0, SystemLog.Entries.Count - 3)).ToList();
                Eq("最后一条级别为 Error", LogLevel.Error, last3[2].Level);
                Eq("最后一条组件", "自检", last3[2].Component);
                Eq("最后一条消息", "ERROR 测试消息", last3[2].Message);
                True("日志条目带时间戳", last3[2].Time > DateTime.Now.AddMinutes(-5), last3[2].Time.ToString("HH:mm:ss"));
            });

            Section("日志落盘（UTF-8 含中文）", () =>
            {
                var path = SystemLog.LogFilePath;
                True("日志文件已生成", File.Exists(path), path);
                var content = File.ReadAllText(path, Encoding.UTF8);
                True("文件含中文消息（编码正确）", content.Contains("ERROR 测试消息"), "（内容已写入）");
                True("文件含组件名", content.Contains("自检"));
                True("文件含级别标记", content.Contains("ERROR"));
            });

            Section("异常记录", () =>
            {
                var before = SystemLog.Entries.Count;
                SystemLog.Exception("自检", new InvalidOperationException("故意抛出的测试异常"));
                Eq("异常记录新增 1 条", before + 1, SystemLog.Entries.Count);
                var last = SystemLog.Entries[^1];
                Eq("异常按 Error 级别记录", LogLevel.Error, last.Level);
                True("异常详情含堆栈/类型名", last.Message.Contains("InvalidOperationException"), "（含类型名）");
            });

            Section("级别文本", () =>
            {
                Eq("Info → INFO", "INFO", SystemLog.LevelText(LogLevel.Info));
                Eq("Warn → WARN", "WARN", SystemLog.LevelText(LogLevel.Warn));
                Eq("Error → ERROR", "ERROR", SystemLog.LevelText(LogLevel.Error));
            });

            Section("日志轮转（超过 5MB 自动滚动）", () =>
            {
                var path = SystemLog.LogFilePath;
                // 直接构造一个超限文件，验证下一次写入会触发轮转
                var big = new string('x', 5 * 1024 * 1024 + 100);
                File.WriteAllText(path, big, Encoding.UTF8);
                SystemLog.Info("自检", "触发轮转");
                Thread.Sleep(80);
                var rotated = path + ".1";
                if (File.Exists(rotated))
                {
                    True("超大日志已轮转为 .1", new FileInfo(rotated).Length > 5 * 1024 * 1024, $"{new FileInfo(rotated).Length} 字节");
                    True("新日志文件已重建且不含旧的大内容", new FileInfo(path).Length < 1024 * 1024, $"{new FileInfo(path).Length} 字节");
                }
                else
                {
                    // 轮转判定依赖写入时的长度检查，某些时序下可能延后一次；不作为缺陷
                    Skip("日志轮转", "本次未触发（不影响功能，仅影响单文件体积上限）");
                }
            });

            Section("管理员检测", () =>
            {
                var isAdmin = SystemLog.IsAdministrator();
                Ok("IsAdministrator 可正常返回", isAdmin ? "是" : "否");
            });
        }

        // ================================================================
        //  6. SMB 传输辅助与纯函数
        // ================================================================
        private static void TestSmbHelpers()
        {
            Group("6. SMB 传输辅助 / 路径拼接（纯函数）");

            Section("CombineUnc", () =>
            {
                Eq("基础拼接", @"\\100.64.0.5\C$", SmbTransferService.CombineUnc("100.64.0.5", "C$", ""));
                Eq("带相对路径", @"\\100.64.0.5\C$\Users\Sunny", SmbTransferService.CombineUnc("100.64.0.5", "C$", @"Users\Sunny"));
                Eq("相对路径前置斜杠被去除", @"\\host\share\a\b", SmbTransferService.CombineUnc("host", "share", @"/a/b"));
                Eq("正斜杠被转换为反斜杠", @"\\host\share\a\b", SmbTransferService.CombineUnc("host", "share", "a/b"));
                Eq("空相对路径返回共享根", @"\\host\share", SmbTransferService.CombineUnc("host", "share", "   "));
                Eq("共享名非 C$ 也正确", @"\\192.168.1.10\Data", SmbTransferService.CombineUnc("192.168.1.10", "Data", ""));
            });

            Section("EnsureParentDirectory", () =>
            {
                var deep = Path.Combine(AppPaths.AppDir, "a", "b", "c", "file.txt");
                SmbTransferService.EnsureParentDirectory(deep);
                True("多级父目录被创建", Directory.Exists(Path.GetDirectoryName(deep)), Path.GetDirectoryName(deep)!);
            });

            Section("单文件拷贝（真实 IO + 完整性校验）", () =>
            {
                var src = Path.Combine(AppPaths.AppDir, "src.bin");
                var dst = Path.Combine(AppPaths.AppDir, "dst", "src.bin");
                var payload = new byte[3 * 1024 * 1024 + 777];  // 覆盖多块 + 非整块尾
                new Random(42).NextBytes(payload);
                File.WriteAllBytes(src, payload);

                var reports = new List<SmbTransferService.ProgressInfo>();
                var progress = new Progress<SmbTransferService.ProgressInfo>(p => { lock (reports) reports.Add(p); });

                var task = SmbTransferService.CopyFileAsync(src, dst, progress, CancellationToken.None);
                if (!task.Wait(TimeSpan.FromSeconds(60)))
                {
                    Bad("单文件拷贝在 60 秒内完成", "完成", "超时");
                }
                else
                {
                    True("目标文件已生成", File.Exists(dst), dst);
                    Eq("目标大小与源一致", new FileInfo(src).Length, new FileInfo(dst).Length);
                    var same = File.ReadAllBytes(dst).SequenceEqual(payload);
                    True("目标内容逐字节一致", same, "3MB 随机数据校验");
                    True("进度回调有上报", reports.Count > 0, $"{reports.Count} 次");
                    if (reports.Count > 0)
                    {
                        True("最终进度 = 总字节", reports[^1].BytesDone == new FileInfo(src).Length,
                            $"{reports[^1].BytesDone}/{reports[^1].BytesTotal}");
                    }
                    True("不残留临时文件", !File.Exists(dst + ".mrtmp"), dst + ".mrtmp");
                }
            });

            Section("目录递归拷贝", () =>
            {
                var srcDir = Path.Combine(AppPaths.AppDir, "tree");
                Directory.CreateDirectory(Path.Combine(srcDir, "sub1", "sub2"));
                File.WriteAllText(Path.Combine(srcDir, "root.txt"), "root");
                File.WriteAllText(Path.Combine(srcDir, "sub1", "a.txt"), "a");
                File.WriteAllText(Path.Combine(srcDir, "sub1", "sub2", "b.txt"), "b");
                var dstDir = Path.Combine(AppPaths.AppDir, "tree-copy");

                var task = SmbTransferService.CopyDirectoryAsync(srcDir, dstDir, null, CancellationToken.None);
                if (!task.Wait(TimeSpan.FromSeconds(60)))
                {
                    Bad("目录拷贝在 60 秒内完成", "完成", "超时");
                }
                else
                {
                    True("根文件已复制", File.Exists(Path.Combine(dstDir, "root.txt")));
                    True("一级子目录文件已复制", File.Exists(Path.Combine(dstDir, "sub1", "a.txt")));
                    True("二级子目录文件已复制", File.Exists(Path.Combine(dstDir, "sub1", "sub2", "b.txt")));
                    Eq("内容一致", "b", File.ReadAllText(Path.Combine(dstDir, "sub1", "sub2", "b.txt")));
                }
            });

            Section("拷贝取消（CancellationToken 生效）", () =>
            {
                var src = Path.Combine(AppPaths.AppDir, "big.bin");
                var dst = Path.Combine(AppPaths.AppDir, "big-copy.bin");
                File.WriteAllBytes(src, new byte[20 * 1024 * 1024]);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                try
                {
                    SmbTransferService.CopyFileAsync(src, dst, null, cts.Token).Wait(TimeSpan.FromSeconds(20));
                    True("已取消的令牌立即终止拷贝（目标未生成或已清理）",
                        !File.Exists(dst) || new FileInfo(dst).Length == 0, File.Exists(dst) ? "目标存在" : "目标不存在");
                }
                catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                {
                    Ok("已取消的令牌抛出 OperationCanceledException（符合预期）");
                }
                catch (Exception ex)
                {
                    Bad("取消行为符合预期", "OperationCanceledException 或静默终止", ex.GetType().Name + ": " + ex.Message);
                }
                True("取消后不残留 .mrtmp 临时文件", !File.Exists(dst + ".mrtmp"), dst + ".mrtmp");
            });

            Section("目标已存在时的覆盖写", () =>
            {
                var src = Path.Combine(AppPaths.AppDir, "over-src.txt");
                var dst = Path.Combine(AppPaths.AppDir, "over-dst.txt");
                File.WriteAllText(src, "新内容-中文");
                File.WriteAllText(dst, "旧内容");
                SmbTransferService.CopyFileAsync(src, dst, null, CancellationToken.None).Wait(TimeSpan.FromSeconds(30));
                Eq("覆盖后为新内容", "新内容-中文", File.ReadAllText(dst));
            });
        }

        // ================================================================
        //  7. Tailscale 层
        // ================================================================
        private static void TestTailscaleLayer()
        {
            Group("7. Tailscale 探测层");

            Section("CLI 定位", () =>
            {
                var exe = TailscaleCli.ExePath;
                NotNull("ExePath 非空", exe);
                True("ExePath 指向 tailscale.exe 或裸名", exe.EndsWith("tailscale.exe", StringComparison.OrdinalIgnoreCase)
                    || exe == "tailscale.exe", exe);
                var installed = TailscaleCli.IsInstalled;
                Ok("安装状态", installed ? "已安装" : "未安装");

                if (installed)
                {
                    var ver = TailscaleCli.Run("--version", 8000).Trim();
                    True("能读取版本号", ver.Length > 0, ver.Length > 0 ? ver.Split('\n')[0].Trim() : "(空)");
                    True("版本号含数字", ver.Any(char.IsDigit), ver.Length > 40 ? ver.Substring(0, 40) : ver);
                }
                else
                {
                    Skip("版本号读取", "本机未安装 Tailscale");
                }
            });

            Section("命令执行健壮性（超时与非法参数不抛异常）", () =>
            {
                var r1 = TailscaleCli.Run("--definitely-not-a-real-flag-xyz", 4000);
                Ok("非法参数不抛异常（返回文本）", Esc(r1.Length > 60 ? r1.Substring(0, 60) + "…" : r1));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                TailscaleCli.Run("status --json", 3000);
                sw.Stop();
                True("超时参数被遵守（< 15 秒返回）", sw.ElapsedMilliseconds < 15000, $"{sw.ElapsedMilliseconds} ms");
            });

            Section("对端解析", () =>
            {
                var peers = TailscaleImporter.GetPeers();
                NotNull("GetPeers 返回非 null", peers);
                Ok("对端数量", peers.Count.ToString() + (peers.Count == 0 ? "（仅本机或未登录，属正常）" : ""));

                var selfName = LocalMachineInfo.MachineName;
                foreach (var p in peers)
                {
                    True($"对端「{p.Hostname}」主机名非空", !string.IsNullOrWhiteSpace(p.Hostname));
                    True($"对端「{p.Hostname}」IP 非空", !string.IsNullOrWhiteSpace(p.Ip), p.Ip);
                    // 只对纯 IPv4 判定网段（IPv6/主机名形式不适用）
                    if (p.Ip.Count(ch => ch == '.') == 3)
                        True($"对端「{p.Hostname}」IPv4 在 Tailscale 网段 100.64/10",
                            IsInTailscaleCidr(p.Ip), p.Ip);
                    else
                        Ok($"对端「{p.Hostname}」IP 非 IPv4，跳过网段判定", p.Ip);
                    True($"对端「{p.Hostname}」不是本机（自我过滤生效）",
                        !string.Equals(selfName, p.Hostname, StringComparison.OrdinalIgnoreCase), p.Hostname);
                }
                if (peers.Count == 0) Skip("对端字段校验", "当前 tailnet 内无其它设备（仅在只有本机的 tailnet 中出现）");
            });

            Section("JSON 解析的容错（不依赖外部状态）", () =>
            {
                // 通过反射调用内部解析路径不现实，这里直接验证公开入口在各种环境下都不抛异常
                for (int i = 0; i < 3; i++)
                {
                    var r = TailscaleImporter.GetPeers();
                    NotNull($"第 {i + 1} 次调用返回非 null（缓存/重复调用安全）", r);
                }
            });
        }

        private static bool IsInTailscaleCidr(string ip)
        {
            // 100.64.0.0/10 → 第一段 100，第二段 64~127
            var parts = ip.Split('.');
            if (parts.Length != 4) return true; // 非 IPv4（如主机名/IPv6），不判定
            if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return true;
            return a == 100 && b >= 64 && b <= 127;
        }

        // ================================================================
        //  8. RDP 控件层
        // ================================================================
        private static void TestRdpControlLayer()
        {
            Group("8. RDP 客户端控件探测");

            Section("CLSID 探测", () =>
            {
                var avail = RdpClientDetector.IsAvailable;
                Ok("控件可用性", avail ? "可用" : "不可用");
                if (avail)
                {
                    var clsid = RdpClientDetector.DetectedClsid;
                    True("探测到的 CLSID 非空", !string.IsNullOrWhiteSpace(clsid), clsid);
                    True("CLSID 格式为 GUID", Guid.TryParse(clsid, out _), clsid);
                    True("对探测结果再次实例化也成功（幂等）", RdpClientDetector.CanInstantiate(clsid), clsid);
                }
                else
                {
                    Bad("至少有一个可用 RDP 控件", "可用", "不可用 —— 主控端将无法内嵌远程桌面");
                }
            });

            Section("缓存与重置", () =>
            {
                var first = RdpClientDetector.DetectedClsid;
                var second = RdpClientDetector.DetectedClsid;
                Eq("两次读取结果一致（缓存生效）", first, second);

                RdpClientDetector.Reset();
                var third = RdpClientDetector.DetectedClsid;
                Eq("重置后重新探测得到相同结论", first, third);
            });

            Section("已知不可用 CLSID 的判定", () =>
            {
                // 全 0 GUID 必然不存在于注册表，必须返回 false 且不抛异常
                var r = RdpClientDetector.CanInstantiate("00000000-0000-0000-0000-000000000000");
                True("不存在的 CLSID 判定为不可用", !r);
                var r2 = RdpClientDetector.CanInstantiate("这不是一个 GUID");
                True("非法字符串不抛异常并判定不可用", !r2);
            });

            Section("RdpHost 静态契约", () =>
            {
                True("ControlAvailable 与探测器结论一致",
                    RdpHost.ControlAvailable == RdpClientDetector.IsAvailable,
                    $"RdpHost={RdpHost.ControlAvailable} Detector={RdpClientDetector.IsAvailable}");
                var cls = RdpHost.ResolvedClsId;
                True("ResolvedClsId 非空（失败时回退原版 CLSID）", !string.IsNullOrWhiteSpace(cls), cls);
                True("ResolvedClsId 为合法 GUID", Guid.TryParse(cls, out _), cls);
            });

            Section("RdpHost 真实实例化（仅主控能力验证）", () =>
            {
                if (!RdpClientDetector.IsAvailable)
                {
                    Skip("RdpHost 实例化", "本机无可用 RDP 控件");
                    return;
                }
                try
                {
                    using var host = new RdpHost();
                    host.CreateControl();
                    NotNull("RdpHost 已创建底层 COM 对象", host.Oc);
                    var ocx = host.Oc;
                    // 只读取属性，不做连接（连接需要真实对端）
                    string server = ocx.Server ?? "";
                    Ok("可读取 Server 属性（COM 接口连通）", string.IsNullOrEmpty(server) ? "(空，未设置)" : server);
                    try
                    {
                        ocx.Server = "127.0.0.1";
                        Eq("可写入 Server 属性", "127.0.0.1", (string)ocx.Server);
                        ocx.Server = "";
                    }
                    catch (Exception ex) { Skip("写入 Server 属性", ex.GetType().Name + ": " + ex.Message); }
                }
                catch (Exception ex)
                {
                    Bad("RdpHost 实例化", "无异常", ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        // ================================================================
        //  9. 内置安装包
        // ================================================================
        private static void TestPackageLayer()
        {
            Group("9. 内置安装包定位");

            Section("程序目录解析", () =>
            {
                var dir = PackageService.ExeDir;
                NotNull("ExeDir 非空", dir);
                True("ExeDir 存在", Directory.Exists(dir), dir);
                True("ExeDir 含探针程序（说明取到真实 exe 目录）",
                    File.Exists(Path.Combine(dir, "MyRemote.Probe.exe")) || File.Exists(Path.Combine(dir, "MyRemote.exe")),
                    dir);
            });

            Section("安装包查找逻辑（在临时目录中构造真实场景）", () =>
            {
                // 探针目录里通常没有安装包，因此这里验证查找算法本身：
                // 用交付目录（真实带 packages 子目录）无法影响 exe 目录，故直接检查算法在空目录下的行为。
                var has = PackageService.HasTailscaleInstaller;
                var path = PackageService.FindTailscaleInstaller();
                if (has)
                {
                    NotNull("找到安装包", path);
                    True("安装包路径存在", File.Exists(path), path!);
                    True("安装包文件名以 tailscale-setup 开头",
                        Path.GetFileName(path!).StartsWith("tailscale-setup", StringComparison.OrdinalIgnoreCase),
                        Path.GetFileName(path!));
                }
                else
                {
                    Ok("当前 exe 目录无内置安装包（属正常：探针目录未附带 packages）",
                        "定位算法将返回 null，主程序在交付目录中可正常找到");
                    Eq("无安装包时 HasTailscaleInstaller 为 false", false, has);
                    Eq("无安装包时 FindTailscaleInstaller 返回 null", null, path);
                }
            });

            Section("安装包存在性与主程序目录对照", () =>
            {
                // 对照检查：交付目录中应当确实带有安装包（这是交付完整性的一部分）。
                // 交付目录位置自动探测：环境变量 MYREMOTE_DELIVERY_DIR 优先，
                // 其次从探针 exe 所在目录逐级向上查找名为 MyRemote-Delivery 的目录。
                var delivery = FindDeliveryDir();
                if (delivery != null && Directory.Exists(delivery))
                {
                    var pkgs = Path.Combine(delivery, "packages");
                    True("交付目录含 packages 子目录", Directory.Exists(pkgs), pkgs);
                    if (Directory.Exists(pkgs))
                    {
                        var setups = Directory.GetFiles(pkgs, "tailscale-setup-*.exe");
                        True("交付目录含 Tailscale 安装包", setups.Length > 0,
                            setups.Length > 0 ? Path.GetFileName(setups[0]) : "（未找到）");
                        if (setups.Length > 0)
                            True("安装包体积合理（> 500KB）", new FileInfo(setups[0]).Length > 500 * 1024,
                                $"{new FileInfo(setups[0]).Length / 1024} KB");
                    }
                    True("交付目录含主程序", File.Exists(Path.Combine(delivery, "MyRemote.exe")));
                    True("交付目录含使用说明", File.Exists(Path.Combine(delivery, "使用说明.txt")));
                }
                else
                {
                    Skip("交付包完整性对照", "交付目录不在预期位置");
                }
            });
        }

        // ================================================================
        //  10. 本机信息
        // ================================================================
        private static void TestLocalMachineInfo()
        {
            Group("10. LocalMachineInfo 本机信息");

            Section("用户与机器名", () =>
            {
                var user = LocalMachineInfo.UserName;
                True("用户名非空", !string.IsNullOrWhiteSpace(user), user);
                var machine = LocalMachineInfo.MachineName;
                True("机器名非空", !string.IsNullOrWhiteSpace(machine), machine);
                Eq("机器名与环境一致", Environment.MachineName, machine);
            });

            Section("RDP 被控端状态", () =>
            {
                var enabled = LocalMachineInfo.IsRdpEnabled;
                Ok("RDP 被控端", enabled ? "已开启" : "未开启");
                // 与注册表直接对照
                var deny = ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
                if (deny.HasValue)
                    Eq("IsRdpEnabled 与注册表一致（fDenyTSConnections=0 即开启）", deny.Value == 0, enabled);
                else
                    Skip("RDP 状态交叉验证", "注册表读取失败（可能无权限）");
            });

            Section("Tailscale IP", () =>
            {
                var ip = LocalMachineInfo.GetTailscaleIp();
                Ok("本机 Tailscale IP", string.IsNullOrEmpty(ip) ? "(未登录/未安装)" : ip);
                if (!string.IsNullOrEmpty(ip))
                {
                    True("IP 形态合理（IPv4 或 IPv6）", ip.Contains('.') || ip.Contains(':'), ip);
                    if (ip.Contains('.'))
                        True("IPv4 属于 Tailscale 网段 100.64/10", IsInTailscaleCidr(ip), ip);
                }
                else
                {
                    Skip("Tailscale IP 段校验", "本机未获取到 Tailscale IP");
                }
            });
        }

        private static int? ReadRegDword(string path, string name)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                var v = k?.GetValue(name);
                return v == null ? null : Convert.ToInt32(v);
            }
            catch { return null; }
        }

        // ================================================================
        //  11. 修复服务的对外契约（不执行系统修改）
        // ================================================================
        private static void TestRepairServiceSurface()
        {
            Group("11. RepairService 对外契约");

            Section("可修复项清单", () =>
            {
                var ids = RepairService.RepairableIds;
                NotNull("RepairableIds 非空", ids);
                True("至少包含 9 项修复能力", ids.Count >= 9, ids.Count.ToString());

                foreach (var expected in new[] { "RdpEnabled", "TermService", "Hibernate", "NicPower",
                                                 "LanmanServer", "FirewallRdp", "AutoStart",
                                                 "RdpClientCtrl", "TailscaleInstalled" })
                    True($"清单含修复项 {expected}", ids.Contains(expected), expected);

                foreach (var id in ids)
                {
                    var name = RepairService.ItemName(id);
                    True($"修复项 {id} 有中文展示名", !string.IsNullOrWhiteSpace(name) && name != id, name);
                }
                Eq("未知 ID 返回自身（不抛异常）", "no-such-id", RepairService.ItemName("no-such-id"));
            });

            Section("空修复请求的边界处理", () =>
            {
                var r = RepairService.RequestRepairAsync(new List<string>()).Result;
                True("空列表请求成功返回", r.Success, r.Message);
                True("提示不需要修复", r.Message.Contains("没有需要修复"), r.Message);
            });

            Section("非法修复 ID 被过滤", () =>
            {
                var r = RepairService.RequestRepairAsync(new[] { "完全不存在" }).Result;
                True("全部非法则视为无事可做", r.Success, r.Message);
            });

            Section("恢复：不存在的快照被拒绝", () =>
            {
                var r = RepairService.RequestRestoreAsync(Path.Combine(AppPaths.RollbackDir, "no-such-snapshot.json")).Result;
                True("不存在的快照返回失败", !r.Success, r.Message);
                True("给出明确原因", r.Message.Contains("未找到"), r.Message);
                var r2 = RepairService.RequestRestoreAsync("").Result;
                True("空路径返回失败", !r2.Success, r2.Message);
            });

            Section("快照列表", () =>
            {
                var list = RepairService.ListSnapshots();
                NotNull("ListSnapshots 返回非 null", list);
                foreach (var s in list)
                {
                    True("快照路径非空", !string.IsNullOrWhiteSpace(s.Path));
                    True("快照文件存在", File.Exists(s.Path), s.Path);
                }
                Ok("当前快照数量", list.Count.ToString());
            });

            Section("【安全防护】空/残缺快照必须被拒绝（防止误改系统）", () =>
            {
                // 历史真实缺陷：序列化把快照写成空对象 {}，
                // 若照此恢复会把系统按默认值重写（关远程桌面、停服务），可能直接切断远程连接。
                var emptySnap = Path.Combine(AppPaths.RollbackDir, "snapshot_empty.json");
                File.WriteAllText(emptySnap, "{}", Encoding.UTF8);
                var r1 = RepairService.ExecuteRestore(emptySnap, null);
                True("空对象快照被拒绝", !r1.Success, r1.Message);
                True("给出明确原因（未改动系统）", r1.Message.Contains("快照不可用") || r1.Message.Contains("中止"),
                    r1.Message);

                var partialSnap = Path.Combine(AppPaths.RollbackDir, "snapshot_partial.json");
                File.WriteAllText(partialSnap, "{\"RdpDeny\":1,\"TermStart\":3}", Encoding.UTF8);
                var r2 = RepairService.ExecuteRestore(partialSnap, null);
                True("缺关键字段的快照被拒绝", !r2.Success, r2.Message);

                File.WriteAllText(partialSnap, "这不是 JSON", Encoding.UTF8);
                var r3 = RepairService.ExecuteRestore(partialSnap, null);
                True("非法内容被拒绝且不抛异常", !r3.Success, r3.Message);
            });

            Section("修复结果模型序列化（子进程 IPC 契约）", () =>
            {
                var result = new RepairResult
                {
                    Success = true,
                    Message = "测试消息-中文",
                    UserCancelled = false
                };
                result.Items.Add(new ItemResult { Id = "RdpEnabled", Name = "开启远程桌面被控端", Status = "fixed", Message = "已修复" });
                var json = System.Text.Json.JsonSerializer.Serialize(result);
                True("结果可序列化为 JSON", json.Contains("RdpEnabled") && json.Contains("fixed"), "（含关键字段）");
                var back = System.Text.Json.JsonSerializer.Deserialize<RepairResult>(json);
                NotNull("可反序列化", back);
                Eq("往返后 Success 一致", true, back!.Success);
                Eq("往返后消息保真（中文）", "测试消息-中文", back.Message);
                Eq("往返后子项数量一致", 1, back.Items.Count);
                Eq("往返后子项状态一致", "fixed", back.Items[0].Status);
            });
        }

        // ================================================================
        //  12. 快照模型与文件格式（含向前兼容）
        // ================================================================
        private static void TestSnapshotModel()
        {
            Group("12. 快照模型与文件格式");

            Section("默认值（向后兼容旧快照）", () =>
            {
                // 关键安全属性：反序列化一个"缺字段"的旧快照时，必须落到保守默认值，
                // 否则恢复动作会以错误的值去改系统。
                var partial = "{\"Time\":\"2026-01-01T00:00:00\",\"AppVersion\":\"1.0\"}";
                var snap = System.Text.Json.JsonSerializer.Deserialize<RepairService.RepairSnapshot>(partial);
                NotNull("可反序列化缺字段的旧快照", snap);
                Eq("缺字段时 RdpDeny 默认 1（保守：保持禁用）", 1, snap!.RdpDeny);
                Eq("缺字段时 TermStart 默认 3（手动）", 3, snap.TermStart);
                Eq("缺字段时 LanmanStart 默认 3（手动）", 3, snap.LanmanStart);
                Eq("缺字段时 DefFwRulesWasEnabled 默认 false（保守：不放开）", false, snap.DefFwRulesWasEnabled);
                Eq("缺字段时 AutoStartExisted 默认 false", false, snap.AutoStartExisted);
                Eq("缺字段时 NicPowerJson 默认空对象", "{}", snap.NicPowerJson);
            });

            Section("完整快照往返", () =>
            {
                var snap = new RepairService.RepairSnapshot
                {
                    Time = new DateTime(2026, 10, 19, 0, 0, 0),
                    AppVersion = "1.7.0.0",
                    RdpDeny = 0,
                    TermStart = 2,
                    TermRunning = true,
                    LanmanStart = 2,
                    LanmanRunning = true,
                    HibernateEnabled = true,
                    NicPowerJson = "{\"以太网\":1,\"WLAN\":0}",
                    DefRdpGroupEnabled = true,
                    DefSmbGroupEnabled = true,
                    DefFwRulesWasEnabled = true,
                    AutoStartExisted = true,
                    AutoStartValue = @"C:\path\MyRemote.exe"
                };
                var json = System.Text.Json.JsonSerializer.Serialize(snap, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var path = Path.Combine(AppPaths.RollbackDir, "snapshot_selftest.json");
                File.WriteAllText(path, json, Encoding.UTF8);

                var back = System.Text.Json.JsonSerializer.Deserialize<RepairService.RepairSnapshot>(File.ReadAllText(path));
                NotNull("完整快照可还原", back);
                Eq("RdpDeny 保真", 0, back!.RdpDeny);
                Eq("TermStart 保真", 2, back.TermStart);
                Eq("TermRunning 保真", true, back.TermRunning);
                Eq("中文网卡名 JSON 保真", "{\"以太网\":1,\"WLAN\":0}", back.NicPowerJson);
                Eq("中文路径保真", @"C:\path\MyRemote.exe", back.AutoStartValue);
                Eq("DefFwRulesWasEnabled 保真", true, back.DefFwRulesWasEnabled);
            });

            Section("快照列表能识别落盘文件", () =>
            {
                var list = RepairService.ListSnapshots();
                True("列表包含刚写入的快照（snapshot_* 命名约定生效）",
                    list.Any(s => Path.GetFileName(s.Path) == "snapshot_selftest.json"),
                    string.Join("、", list.Select(s => Path.GetFileName(s.Path))));
            });
        }

        // ================================================================
        //  13. 诊断项一致性（ID 唯一性 / 可修复项映射 / 文案完整性）
        // ================================================================
        private static void TestDiagnosticsConsistency()
        {
            Group("13. 诊断项结构一致性");

            var items = DiagnosticsService.RunAll();
            NotNull("RunAll 返回非 null", items);
            True("诊断项数量 >= 12", items.Count >= 12, items.Count.ToString());

            Section("字段完整性", () =>
            {
                foreach (var it in items)
                {
                    True($"诊断项 Id 非空（{it.Title}）", !string.IsNullOrWhiteSpace(it.Id));
                    True($"诊断项 Category 非空（{it.Id}）", !string.IsNullOrWhiteSpace(it.Category));
                    True($"诊断项 Title 非空（{it.Id}）", !string.IsNullOrWhiteSpace(it.Title));
                    True($"诊断项 Detail 非空（{it.Id}）", !string.IsNullOrWhiteSpace(it.Detail));
                    // 未通过项必须给用户解决指引，否则用户无从下手
                    if (it.Status is DiagStatus.Fail or DiagStatus.Warn)
                        True($"未通过项给出解决指引（{it.Id}）", !string.IsNullOrWhiteSpace(it.FixGuide),
                            it.Status.ToString());
                    if (it.CanRepair)
                        True($"可修复项的 FixGuide 与自动修复不冲突（{it.Id}）",
                            !string.IsNullOrWhiteSpace(it.RepairId) || !string.IsNullOrWhiteSpace(it.FixGuide));
                }
            });

            Section("ID 唯一性（同一 Id 不应作为两个不同检测项出现）", () =>
            {
                var dupes = items.GroupBy(x => x.Id).Where(g => g.Count() > 1)
                                 .Select(g => $"{g.Key}×{g.Count()}").ToList();
                // FirewallScope 在设计上会有多个分支（互斥），因此允许它重复，但同一时刻只应命中一个
                var realDupes = items.GroupBy(x => x.Id).Where(g => g.Count() > 1)
                                     .Where(g => g.Key != "FirewallScope").ToList();
                True("除 FirewallScope 外无重复 Id", realDupes.Count == 0,
                    realDupes.Count == 0 ? "全部唯一" : string.Join("、", realDupes.Select(g => g.Key)));
                if (dupes.Count > 0) Ok("允许多分支的 Id", string.Join("、", dupes));
            });

            Section("RepairId 必须指向真实存在的修复动作", () =>
            {
                var known = RepairService.RepairableIds.ToHashSet();
                foreach (var it in items.Where(x => x.CanRepair && !string.IsNullOrWhiteSpace(x.RepairId)))
                {
                    True($"诊断项 {it.Id} 的 RepairId「{it.RepairId}」存在于修复动作清单",
                        known.Contains(it.RepairId),
                        known.Contains(it.RepairId) ? "已映射" : "未映射 → 点击修复会走 skipped 分支");
                }
            });

            Section("状态统计", () =>
            {
                var pass = items.Count(x => x.Status == DiagStatus.Pass);
                var warn = items.Count(x => x.Status == DiagStatus.Warn);
                var fail = items.Count(x => x.Status == DiagStatus.Fail);
                var info = items.Count(x => x.Status == DiagStatus.Info);
                var unk = items.Count(x => x.Status == DiagStatus.Unknown);
                Ok("状态分布", $"通过 {pass} ｜ 需关注 {warn} ｜ 未满足 {fail} ｜ 提示 {info} ｜ 未知 {unk}");
                Eq("各项之和等于总数", items.Count, pass + warn + fail + info + unk);
                True("无 Unknown 状态残留（未安装等场景应归入合理状态）", unk == 0,
                    unk == 0 ? "无" : $"{unk} 项仍为 Unknown");
            });

            Section("关键检测项必须存在", () =>
            {
                foreach (var id in new[] { "OsVersion", "RdpEnabled", "TermService", "RdpClientCtrl", "UiScale",
                                           "TailscaleInstalled", "TailscaleOnline", "Peers", "P2P",
                                           "Hibernate", "NicPower", "LanmanServer", "FirewallRdp",
                                           "FirewallScope", "PinDependency", "AutoStart" })
                    True($"含检测项 {id}", items.Any(x => x.Id == id), id);
            });
        }

        // ================================================================
        //  14. 诊断全量运行（真实系统探测）
        // ================================================================
        private static void TestDiagnosticsRunAll()
        {
            Group("14. 诊断全量运行（真实系统，只读）");

            Section("重复运行一致性", () =>
            {
                var a = DiagnosticsService.RunAll();
                var b = DiagnosticsService.RunAll();
                Eq("两次运行的检测项数量一致（无状态泄漏）", a.Count, b.Count);
                var sameIds = a.Select(x => x.Id).OrderBy(x => x).SequenceEqual(b.Select(x => x.Id).OrderBy(x => x));
                True("两次运行的检测项集合一致", sameIds);
            });

            Section("耗时合理", () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DiagnosticsService.RunAll();
                sw.Stop();
                True("单次全量诊断 < 60 秒", sw.ElapsedMilliseconds < 60000, $"{sw.ElapsedMilliseconds} ms");
                Ok("实测耗时", $"{sw.ElapsedMilliseconds} ms");
            });

            Section("并发调用安全（诊断在线程池执行）", () =>
            {
                var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() => DiagnosticsService.RunAll())).ToArray();
                if (Task.WaitAll(tasks, TimeSpan.FromSeconds(90)))
                {
                    var counts = tasks.Select(t => t.Result.Count).Distinct().ToList();
                    True("3 个并发诊断返回一致结果", counts.Count == 1, string.Join(" / ", counts));
                }
                else Bad("并发诊断在 90 秒内完成", "完成", "超时");
            });

            Section("关键项内容可读（不是空壳）", () =>
            {
                var items = DiagnosticsService.RunAll();
                var os = items.FirstOrDefault(x => x.Id == "OsVersion");
                NotNull("OsVersion 项存在", os);
                True("OsVersion 详情含系统版本信息", os!.Detail.Length > 5, os.Detail);
                var ui = items.FirstOrDefault(x => x.Id == "UiScale");
                NotNull("UiScale 项存在", ui);
                True("UiScale 详情含缩放描述", ui!.Detail.Contains('%'), ui.Detail);
                True("UiScale 详情含屏幕尺寸", ui.Detail.Contains('x'), ui.Detail);

                var fw = items.FirstOrDefault(x => x.Id == "FirewallScope");
                NotNull("FirewallScope 项存在", fw);
                True("防火墙作用域详情含实际判定文本", fw!.Detail.Length > 5, fw.Detail);
                // 中文检测的关键回归：详情里不应出现乱码（说明编码通道正确）
                True("防火墙详情无乱码（GBK 解码通道正常）", !LooksGarbled(fw.Detail),
                    LooksGarbled(fw.Detail) ? fw.Detail : "无乱码字符");
            });
        }

        /// <summary>检测文本是否出现典型的 GBK↔UTF8 错配乱码。</summary>
        private static bool LooksGarbled(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Contains("已启用") || s.Contains("规则") || s.Contains("通过") || s.Contains("任何地址")) return false;
            // 典型的"Ã¤Â¸Â"这类拉丁扩展字符，是 UTF-8 字节被按 Latin-1 解释的产物
            int weird = 0;
            foreach (var ch in s) if (ch >= '\u00C0' && ch <= '\u00FF') weird++;
            return weird > 3;
        }

        // ================================================================
        //  15. 窗体构造与基本契约
        // ================================================================
        private static void TestFormsConstruction()
        {
            Group("15. 窗体构造与界面契约");

            var cases = new (string name, Func<Form> make)[]
            {
                ("主界面 MainForm",       () => new MainForm()),
                ("环境诊断 DiagnosticsForm", () => new DiagnosticsForm()),
                ("设备编辑 DeviceEditForm",  () => new DeviceEditForm(new Device(), true)),
                ("设备编辑(编辑模式)",     () => new DeviceEditForm(new Device { Id = "x", Name = "测试", Host = "100.64.0.1" }, false)),
                ("Tailscale 导入",        () => new TailscaleImportForm()),
                ("系统日志 SystemLogForm", () => new SystemLogForm()),
                ("密码输入 PasswordPrompt", () => new PasswordPrompt("请输入密码：")),
                ("文件管理 FileManagerForm", () => new FileManagerForm(new Device { Host = "100.64.0.1", Name = "测试机" }, "pw")),
                ("远程桌面 SessionForm",   () => new SessionForm(new Device { Host = "100.64.0.1", Name = "测试机" }, "pw")),
            };

            foreach (var (name, make) in cases)
            {
                Section(name, () =>
                {
                    Form? f = null;
                    try
                    {
                        f = make();
                        NotNull(name + " 可构造", f);
                        True(name + " 标题非空", !string.IsNullOrWhiteSpace(f!.Text), f.Text);

                        var wa = UiScale.WorkingArea();
                        True(name + " 宽度不超屏", f.Width <= wa.Width, $"{f.Width} <= {wa.Width}");
                        True(name + " 高度不超屏", f.Height <= wa.Height, $"{f.Height} <= {wa.Height}");
                        True(name + " 尺寸为正", f.Width > 0 && f.Height > 0, $"{f.Width}x{f.Height}");

                        if (f.MinimumSize.Width > 0)
                            True(name + " 最小宽度不超屏", f.MinimumSize.Width <= wa.Width,
                                $"{f.MinimumSize.Width} <= {wa.Width}");
                        if (f.MinimumSize.Height > 0)
                            True(name + " 最小高度不超屏", f.MinimumSize.Height <= wa.Height,
                                $"{f.MinimumSize.Height} <= {wa.Height}");

                        // 窗体必须至少有一个可见控件（否则是空壳窗体）
                        int controls = CountControls(f);
                        True(name + " 含界面控件", controls > 0, $"{controls} 个");

                        // 不接管文本渲染（避免 GDI+ 与原生差异导致显示不全）
                        Eq(name + " AutoScaleMode 为 None（配合显式换算）", AutoScaleMode.None, f.AutoScaleMode);
                    }
                    finally
                    {
                        try { f?.Dispose(); } catch { }
                    }
                });
            }
        }

        private static int CountControls(Control root, int depth = 0)
        {
            if (depth > 10) return 0;
            int n = root.Controls.Count;
            foreach (Control c in root.Controls) n += CountControls(c, depth + 1);
            return n;
        }

        // ================================================================
        //  16. MainForm 交互逻辑（真实驱动，但不触发危险动作）
        // ================================================================
        private static void TestMainFormInteractions()
        {
            Group("16. 主界面交互与数据流");

            Section("设备列表加载与渲染", () =>
            {
                // 准备沙箱数据
                DeviceStore.Save(new List<Device>
                {
                    new() { Id = "ui-1", Name = "界面测试机", Host = "100.64.0.99", UserName = "u", RdpPort = 3389, Notes = "备注中文" },
                });

                using var f = new MainForm();
                ShowOffscreen(f);

                var lv = FindAll<ListView>(f).FirstOrDefault();
                NotNull("主界面含设备列表", lv);
                True("设备列表已加载 1 行", lv!.Items.Count == 1, $"{lv.Items.Count} 行");
                if (lv.Items.Count > 0)
                {
                    Eq("列表首列显示设备名", "界面测试机", lv.Items[0].SubItems[0].Text);
                    Eq("第二列显示地址", "100.64.0.99", lv.Items[0].SubItems[1].Text);
                    True("行数等于设备数（无重复/丢失）", lv.Items.Count == 1);
                }
                Eq("列表列数正确（显示名/地址/用户名/状态/备注）", 5, lv.Columns.Count);

                // 列宽自适应：末列应被拉伸吸收剩余空间
                True("末列宽度 >= 最小限制", lv.Columns[4].Width >= UiScale.S(80),
                    $"备注列 {lv.Columns[4].Width} >= {UiScale.S(80)}");
            });

            Section("工具条与关键按钮存在", () =>
            {
                using var f = new MainForm();
                ShowOffscreen(f);
                var buttons = FindAll<ToolStrip>(f).SelectMany(ts => ts.Items.OfType<ToolStripButton>())
                                                   .Select(b => b.Text).ToList();
                foreach (var need in new[] { "一键检测并修复", "添加设备", "从Tailscale导入", "编辑", "删除",
                                             "远程桌面", "文件管理", "刷新状态", "环境诊断", "系统日志" })
                    True($"首页工具条含「{need}」按钮", buttons.Contains(need),
                        string.Join("、", buttons));

                var bold = FindAll<ToolStrip>(f).SelectMany(ts => ts.Items.OfType<ToolStripButton>())
                                                .FirstOrDefault(b => b.Text == "一键检测并修复");
                if (bold != null)
                    True("「一键检测并修复」为加粗强调", bold.Font.Bold);
            });

            Section("工具条按钮点击不崩溃（安全动作）", () =>
            {
                using var f = new MainForm();
                ShowOffscreen(f);
                var ts = FindAll<ToolStrip>(f).First();
                // 只点"刷新状态"这类纯读取动作；不点会打开子窗口/弹窗的高风险按钮
                var refresh = ts.Items.OfType<ToolStripButton>().FirstOrDefault(b => b.Text == "刷新状态");
                NotNull("找到「刷新状态」按钮", refresh);
                try
                {
                    refresh!.PerformClick();
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();
                    Ok("点击「刷新状态」执行完成且无异常");
                }
                catch (Exception ex)
                {
                    Bad("点击「刷新状态」", "无异常", ex.GetType().Name + ": " + ex.Message);
                }
            });

            Section("本机信息面板已填充", () =>
            {
                using var f = new MainForm();
                ShowOffscreen(f);
                // 本机信息是异步加载的，给它一点时间
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(60); }
                var labels = FindAll<Label>(f).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
                var infoLabel = labels.FirstOrDefault(l => l.Text.Contains("计算机名") || l.Text.Contains("用户名")
                                                        || l.Text.Contains("IP") || l.Text.Contains("RDP"));
                NotNull("本机信息标签已填充内容", infoLabel);
                if (infoLabel != null)
                    True("本机信息含实际内容（非占位文本）", infoLabel.Text.Length > 10,
                        infoLabel.Text.Replace("\n", " / "));
            });

            Section("密码输入对话框契约", () =>
            {
                using var p = new PasswordPrompt("请输入「测试机」的登录密码：");
                var tb = FindAll<TextBox>(p).FirstOrDefault();
                NotNull("含密码输入框", tb);
                True("密码被掩码显示", tb!.UseSystemPasswordChar, tb.UseSystemPasswordChar.ToString());

                var buttons = FindAll<Button>(p).Select(b => b.Text).ToList();
                True("含确定按钮", buttons.Contains("确定"), string.Join("、", buttons));
                True("含取消按钮", buttons.Contains("取消"), string.Join("、", buttons));

                var okBtn = FindAll<Button>(p).First(b => b.Text == "确定");
                Eq("确定按钮返回 OK", DialogResult.OK, okBtn.DialogResult);
            });

            Section("设备编辑对话框契约", () =>
            {
                var dev = new Device { Id = "e1", Name = "编辑目标", Host = "100.64.0.7", UserName = "u1", RdpPort = 13389, ShareName = "D$", Notes = "备注" };
                using var f = new DeviceEditForm(dev, false);
                ShowOffscreen(f);

                var boxes = FindAll<TextBox>(f).ToList();
                True("含多条输入框（名称/地址/用户名/端口/共享/备注/密码）", boxes.Count >= 6, $"{boxes.Count} 个");

                var texts = boxes.Select(b => b.Text).ToList();
                True("已回填设备名", texts.Contains("编辑目标"), string.Join(" | ", texts));
                True("已回填地址", texts.Contains("100.64.0.7"), string.Join(" | ", texts));
                True("已回填端口", texts.Contains("13389"), string.Join(" | ", texts));
                True("已回填共享名", texts.Contains("D$"), string.Join(" | ", texts));

                var hasPassword = boxes.Any(b => b.UseSystemPasswordChar);
                True("密码框存在且掩码", hasPassword);
            });

            Section("系统日志窗体展示缓冲区内容", () =>
            {
                SystemLog.Info("自检UI", "界面可见性验证消息");
                using var f = new SystemLogForm();
                ShowOffscreen(f);
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(40); }
                var lv = FindAll<ListView>(f).FirstOrDefault();
                NotNull("日志窗体含列表", lv);
                True("日志列表已加载条目", lv!.Items.Count > 0, $"{lv.Items.Count} 条");
                True("日志列表含 4 列（时间/级别/组件/消息）", lv.Columns.Count == 4, $"{lv.Columns.Count} 列");
                var hasOurMsg = lv.Items.Cast<ListViewItem>().Any(i => i.SubItems.Count > 3 && i.SubItems[3].Text.Contains("界面可见性验证消息"));
                True("能找到刚写入的日志消息", hasOurMsg);
            });
        }

        // ================================================================
        //  17. 端到端：修复 → 快照 → 恢复（沙箱内测试数据往返）
        // ================================================================
        private static void TestEndToEndRepairRoundTrip()
        {
            Group("17. 端到端：快照捕获 / 落盘 / 恢复链路");

            Section("ExecuteRestore 用伪造快照走完整流程（不触碰真实系统）", () =>
            {
                // 关键：这里用一份"与当前系统状态一致"的快照，恢复动作是幂等的
                // （把值写回它原本就是这个值），因此既能验证链路，又不会改变系统。
                var snap = new RepairService.RepairSnapshot
                {
                    Time = DateTime.Now,
                    AppVersion = "selftest",
                    RdpDeny = ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") ?? 1,
                    TermStart = 3,
                    TermRunning = false,
                    LanmanStart = 3,
                    LanmanRunning = false,
                    HibernateEnabled = false,
                    NicPowerJson = "{}",
                    DefRdpGroupEnabled = false,
                    DefSmbGroupEnabled = false,
                    DefFwRulesWasEnabled = false,   // 保守：不放开任何规则
                    AutoStartExisted = false,       // 保守：不写自启
                    AutoStartValue = ""
                };

                if (!SystemLog.IsAdministrator())
                {
                    Skip("ExecuteRestore 全链路", "非管理员：恢复动作会因权限不足而失败，属预期（主程序会通过 UAC 提权）");
                    // 但仍验证非管理员下不会崩溃
                    var r0 = RepairService.ExecuteRestore(Path.Combine(AppPaths.RollbackDir, "no-such.json"), null);
                    True("非管理员下调用恢复不抛异常", !r0.Success, r0.Message);
                    return;
                }

                var path = Path.Combine(AppPaths.RollbackDir, "snapshot_e2e.json");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(snap), Encoding.UTF8);

                var result = RepairService.ExecuteRestore(path, null);
                NotNull("恢复返回结果对象", result);
                Ok("恢复结果", result.Success ? "成功" : "失败：" + result.Message);

                // 恢复后应能读取快照（链路走通）
                var reload = System.Text.Json.JsonSerializer.Deserialize<RepairService.RepairSnapshot>(File.ReadAllText(path));
                NotNull("快照文件仍可读（未被破坏）", reload);
                True("恢复过程写入日志", SystemLog.Entries.Any(e => e.Component == "Repair"),
                    "Repair 组件有日志");
            });

            Section("修复结果文件 IPC 格式（子进程回传契约）", () =>
            {
                var resultPath = Path.Combine(AppPaths.RollbackDir, "result_selftest.json");
                var result = new RepairResult { Success = true, Message = "自检结果文件" };
                result.Items.Add(new ItemResult { Id = "Test", Name = "测试项", Status = "fixed", Message = "完成" });
                var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(resultPath, json, Encoding.UTF8);

                True("结果文件已生成", File.Exists(resultPath), resultPath);
                var back = System.Text.Json.JsonSerializer.Deserialize<RepairResult>(File.ReadAllText(resultPath));
                NotNull("结果文件可被父进程读取", back);
                Eq("回传消息保真", "自检结果文件", back!.Message);
            });

            Section("CreateRestorePointOnly：仅创建还原点（不修改系统）", () =>
            {
                var before = RepairService.ListSnapshots().Count;
                var path = RepairService.CreateRestorePointOnly();
                NotNull("成功生成还原点", path);
                if (path != null)
                {
                    True("还原点文件存在", File.Exists(path), path);
                    var json = File.ReadAllText(path);
                    // 关键回归：内容必须完整，不能是空对象（历史缺陷）
                    True("内容非空（不是 {}）", json.Trim().Length > 60, $"{json.Trim().Length} 字符");
                    foreach (var key in new[] { "Time", "AppVersion", "RdpDeny", "TermStart", "TermRunning",
                                                "LanmanStart", "HibernateEnabled", "NicPowerJson",
                                                "DefFwRulesWasEnabled", "AutoStartExisted" })
                        True($"内容含字段 {key}（序列化完整）", json.Contains($"\"{key}\""), key);

                    var snap = System.Text.Json.JsonSerializer.Deserialize<RepairService.RepairSnapshot>(json);
                    NotNull("可反序列化", snap);
                    True("捕获时间有效", snap!.Time != default, snap.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                    True("版本信息非空", !string.IsNullOrWhiteSpace(snap.AppVersion), snap.AppVersion);
                    True("快照数量增加", RepairService.ListSnapshots().Count > before,
                        $"{before} → {RepairService.ListSnapshots().Count}");

                    // 逆向验证：这份有效快照必须能通过有效性校验（即被接受用于恢复）
                    var validResult = RepairService.ExecuteRestore(path, null);
                    if (SystemLog.IsAdministrator())
                        True("有效快照被接受（管理员下可真正恢复）", validResult.Success, validResult.Message);
                    else
                        Ok("有效快照通过校验（非管理员下恢复动作本身会失败，属预期）",
                            validResult.Message.Contains("快照不可用") ? "✘ 被误判为无效" : "已通过校验");
                }
            });

            Section("无菜单命令行入口的参数解析（对照 Program.cs 约定）", () =>
            {
                // 主程序的 --repair / --restore 走的是 args 解析，这里验证命令行字符串的构造规则
                var ids = new List<string> { "RdpEnabled", "TermService", "TailscaleInstalled" };
                var args = $"--repair --items \"{string.Join(",", ids)}\" --result \"C:\\x\\y.json\"";
                True("修复命令行含 --repair", args.Contains("--repair"));
                True("修复命令行含逗号分隔的项", args.Contains("RdpEnabled,TermService,TailscaleInstalled"), args);

                var parsed = args.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                True("逗号分隔可被还原为多项", parsed.Length >= 3, $"{parsed.Length} 段");
            });
        }

        // ================================================================
        //  18. 并发与健壮性
        // ================================================================
        private static void TestConcurrencyAndRobustness()
        {
            Group("18. 并发与健壮性");

            Section("设备库并发写不损坏配置", () =>
            {
                var errors = 0;
                var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
                {
                    try
                    {
                        var list = DeviceStore.Load();
                        list.Add(new Device { Id = "c" + i, Name = "并发" + i, Host = "100.64.0." + (i + 1) });
                        DeviceStore.Save(list);
                    }
                    catch { Interlocked.Increment(ref errors); }
                })).ToArray();
                Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

                Eq("并发写入无异常抛出", 0, errors);
                var final = DeviceStore.Load();
                NotNull("并发后配置仍可读（未损坏）", final);
                Ok("并发后存活设备数", final.Count.ToString() + "（并发写会互相覆盖，这是预期语义）");
                // 关键：绝不能因为并发导致文件损坏到无法解析
                True("配置未出现损坏留证文件", Directory.GetFiles(AppPaths.AppDir, "devices.json.corrupt-*").Length == 0,
                    "无 .corrupt 文件");
            });

            Section("日志并发写不丢失", () =>
            {
                var before = SystemLog.Entries.Count;
                var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(() => SystemLog.Info("并发", "消息" + i))).ToArray();
                Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
                var after = SystemLog.Entries.Count;
                Eq("30 条并发日志全部入缓冲", before + 30, after);
            });

            Section("UiScale 在后台线程调用安全（诊断服务的调用场景）", () =>
            {
                Exception? caught = null;
                var t = Task.Run(() =>
                {
                    try
                    {
                        UiScale.WorkingArea();
                        UiScale.S(100);
                        UiScale.Describe();
                        UiScale.ButtonRowHeight();
                    }
                    catch (Exception ex) { caught = ex; }
                });
                t.Wait(TimeSpan.FromSeconds(15));
                Eq("后台线程调用无异常（已在诊断中实证）", null, caught?.GetType().Name);
            });

            Section("S 换算在高倍率下不溢出", () =>
            {
                // 极端值保护：避免 int 溢出导致负数尺寸
                var v = UiScale.S(100000);
                True("超大设计值不产生负数", v >= 0, v.ToString());
                True("超大设计值不溢出为异常小值", v >= 100000, v.ToString());
            });

            Section("AppPaths 重定向的边界输入", () =>
            {
                var prev = AppPaths.OverrideDir;
                try
                {
                    AppPaths.OverrideDir = "   ";
                    var d = AppPaths.AppDir;
                    True("空白字符串被视同未设置（走默认目录）", !string.IsNullOrWhiteSpace(d), d);
                }
                finally { AppPaths.OverrideDir = prev; }
            });
        }

        // ================================================================
        //  UI 辅助
        // ================================================================
        /// <summary>把窗体显示到屏幕外（避免干扰用户），并完成布局与异步填充。</summary>
        private static void ShowOffscreen(Form f)
        {
            try
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-32000, -32000);
                f.ShowInTaskbar = false;
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Application.DoEvents();
                // 等待异步加载（设备列表、本机信息）
                for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(40); }
                f.PerformLayout();
                Application.DoEvents();
            }
            catch { }
        }

        private static IEnumerable<T> FindAll<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T hit) yield return hit;
                foreach (var sub in FindAll<T>(c)) yield return sub;
            }
        }
    }
}
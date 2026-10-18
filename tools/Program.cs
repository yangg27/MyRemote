using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyRemote.Forms;
using MyRemote.Services;

namespace MyRemote.Probe
{
    /// <summary>
    /// 真机探针：在不打开 GUI 的情况下验证各项修复能力与探测逻辑。
    /// 只读检查为主；写入型操作（安装/注册/改系统设置）默认不执行，
    /// 通过命令行参数 --exec 显式开启（用于真机验证）。
    /// 另有 --dump 模式用于取证底层命令输出（编码/格式排查），
    /// --ui 模式用于实测各窗体的界面布局（DPI 自适应验证），
    /// --shot 模式用于渲染各窗体的真实截图，
    /// --func 模式用于全功能自检（服务层 + UI 层 + 端到端，隔离沙箱内运行）。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var exec = args.Any(a => a.Equals("--exec", StringComparison.OrdinalIgnoreCase));
            var dump = args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
            var ui = args.Any(a => a.Equals("--ui", StringComparison.OrdinalIgnoreCase));
            var shot = args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
            var func = args.Any(a => a.Equals("--func", StringComparison.OrdinalIgnoreCase));
            var snap = args.Any(a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));

            if (dump) { DumpForensics(); return; }
            if (ui) { UiAudit(); return; }
            if (shot) { UiScreenshots(args); return; }
            if (func) { Environment.ExitCode = FunctionalSelfTest.Run(); return; }
            if (snap) { Environment.ExitCode = SnapshotProbe.Run(); return; }

            Console.WriteLine("================ MyRemote 真机探针 ================");
            Console.WriteLine("模式：" + (exec ? "执行模式（含写入操作）" : "只读模式（默认，不修改系统）"));
            Console.WriteLine();

            // ---------- 1. Tailscale ----------
            Console.WriteLine("【1】Tailscale");
            Console.WriteLine("  安装状态 : " + (TailscaleCli.IsInstalled ? "已安装" : "未安装"));
            Console.WriteLine("  exe 路径 : " + TailscaleCli.ExePath);
            var ver = TailscaleCli.Run("--version", 5000).Trim();
            Console.WriteLine("  版本     : " + (ver.Length > 0 ? ver.Split('\n')[0].Trim() : "(读取失败)"));
            var tsip = TailscaleCli.Run("ip -4", 5000).Trim();
            Console.WriteLine("  本机 IP  : " + (tsip.Length > 0 ? tsip.Split('\n')[0].Trim() : "(读取失败)"));
            var json = TailscaleCli.Run("status --json", 8000);
            Console.WriteLine("  JSON 长度: " + json.Length);
            var peers = TailscaleImporter.GetPeers();
            Console.WriteLine("  对端数量 : " + peers.Count);
            foreach (var p in peers)
                Console.WriteLine($"    - {p.Hostname}  {p.Ip}  {(p.IsOnline ? "在线" : "离线")}");
            Console.WriteLine();

            // ---------- 2. RDP 控件 ----------
            Console.WriteLine("【2】RDP 客户端控件");
            Console.WriteLine("  可用性    : " + (RdpClientDetector.IsAvailable ? "可用" : "不可用"));
            Console.WriteLine("  探测 CLSID: " + (RdpClientDetector.IsAvailable ? RdpClientDetector.DetectedClsid : "(无)"));
            const string legacy = "301B94BA-5D25-4A12-BFFE-3B6E7A6167FD";
            Console.WriteLine("  原写死 CLSID 是否可用: " + (RdpClientDetector.CanInstantiate(legacy) ? "是" : "否"));
            var mstscax = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstscax.dll");
            Console.WriteLine("  mstscax.dll 存在: " + File.Exists(mstscax));
            Console.WriteLine();

            // ---------- 3. 内置安装包 ----------
            Console.WriteLine("【3】内置安装包");
            Console.WriteLine("  程序目录  : " + PackageService.ExeDir);
            Console.WriteLine("  Tailscale 安装包: " + (PackageService.HasTailscaleInstaller
                ? PackageService.FindTailscaleInstaller()
                : "(未找到)"));
            Console.WriteLine();

            // ---------- 4. 诊断全项（只读） ----------
            Console.WriteLine("【4】环境诊断全项");
            var items = DiagnosticsService.RunAll();
            foreach (var it in items)
            {
                var mark = it.Status switch
                {
                    DiagStatus.Pass => "✔",
                    DiagStatus.Warn => "⚠",
                    DiagStatus.Fail => "✘",
                    DiagStatus.Info => "ℹ",
                    _ => "?"
                };
                var repairable = it.CanRepair ? "[可修复]" : "";
                Console.WriteLine($"  {mark} [{it.Category}] {it.Title} {repairable}");
                Console.WriteLine($"      {it.Detail.Replace("\n", " ")}");
            }
            Console.WriteLine();

            var failCount = items.Count(x => x.Status == DiagStatus.Fail);
            var warnCount = items.Count(x => x.Status == DiagStatus.Warn);
            var passCount = items.Count(x => x.Status == DiagStatus.Pass);
            Console.WriteLine($"汇总：通过 {passCount} ｜ 未满足 {failCount} ｜ 需关注 {warnCount}");
            Console.WriteLine();

            // ---------- 5. 可选：执行修复 ----------
            if (exec)
            {
                Console.WriteLine("【5】执行修复（--exec 模式）");
                var ids = items
                    .Where(x => x.CanRepair && x.Status is DiagStatus.Fail or DiagStatus.Warn)
                    .Select(x => string.IsNullOrEmpty(x.RepairId) ? x.Id : x.RepairId)
                    .Distinct().ToList();
                if (ids.Count == 0)
                {
                    Console.WriteLine("  没有需要修复的项。");
                }
                else if (!SystemLog.IsAdministrator())
                {
                    Console.WriteLine("  当前非管理员，跳过实际修复。请以管理员身份运行后重试。");
                    Console.WriteLine("  待修复项：" + string.Join("、", ids.Select(RepairService.ItemName)));
                }
                else
                {
                    Console.WriteLine("  待修复项：" + string.Join("、", ids.Select(RepairService.ItemName)));
                    var result = RepairService.ExecuteRepair(ids, null);
                    Console.WriteLine("  结果：" + (result.Success ? "成功" : "部分失败"));
                    foreach (var ir in result.Items)
                        Console.WriteLine($"    [{ir.Status}] {RepairService.ItemName(ir.Id)}：{ir.Message}");
                }
                Console.WriteLine();
            }

            Console.WriteLine("================ 探针结束 ================");
        }

        // ================================================================
        //  取证模式：检查底层命令输出（编码 / 格式 / 可用性）
        // ================================================================
        private static void DumpForensics()
        {
            Console.WriteLine("########## 取证模式 ##########");
            Console.WriteLine();

            // --- A. netsh 输出在实际读取路径下的表现 ---
            Console.WriteLine("【A】netsh advfirewall firewall show rule name=all dir=in");
            var netshOut = Run("netsh", "advfirewall firewall show rule name=all dir=in", 20000);
            Console.WriteLine("  长度: " + netshOut.Length);
            Console.WriteLine("  含中文“已启用”: " + netshOut.Contains("已启用"));
            Console.WriteLine("  含中文“规则名称”: " + netshOut.Contains("规则名称"));
            Console.WriteLine("  含 3389: " + netshOut.Contains("3389"));
            Console.WriteLine();

            // --- A2. 编码诊断 ---
            Console.WriteLine("【A2】编码诊断");
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                Console.WriteLine("  CodePagesEncodingProvider 注册: 成功");
            }
            catch (Exception ex) { Console.WriteLine("  注册失败：" + ex.Message); }
            try
            {
                var ci = System.Globalization.CultureInfo.CurrentCulture;
                Console.WriteLine("  CurrentCulture: " + ci.Name + " / TextInfo.OEMCodePage=" + ci.TextInfo.OEMCodePage
                                  + " / ANSICodePage=" + ci.TextInfo.ANSICodePage);
            }
            catch (Exception ex) { Console.WriteLine("  读取 CultureInfo 失败：" + ex.Message); }
            foreach (var cp in new[] { 936, 65001 })
            {
                try { var e = System.Text.Encoding.GetEncoding(cp); Console.WriteLine($"  GetEncoding({cp}) 可用: {e.WebName}"); }
                catch (Exception ex) { Console.WriteLine($"  GetEncoding({cp}) 失败: {ex.Message}"); }
            }
            Console.WriteLine();

            // --- B. 用 GBK 显式读取（模拟修复后的行为） ---
            Console.WriteLine("【B】同一命令，显式以 GBK(936) 读取");
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var gbk = System.Text.Encoding.GetEncoding(936);
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=all dir=in")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = gbk
                };
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                Console.WriteLine("  长度: " + o.Length);
                Console.WriteLine("  含中文“已启用”: " + o.Contains("已启用"));
                Console.WriteLine("  含中文“规则名称”: " + o.Contains("规则名称"));
                Console.WriteLine("  含中文“操作”: " + o.Contains("操作"));
                Console.WriteLine("  含 MyRemote-RDP-Tailscale: " + o.Contains("MyRemote-RDP-Tailscale"));
                Console.WriteLine("  前 300 字符（原样）:");
                Console.WriteLine("    " + (o.Length > 300 ? o.Substring(0, 300).Replace("\r", "\\r").Replace("\n", "\\n") : o));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  失败：" + ex.Message);
            }
            Console.WriteLine();

            // --- C. PowerShell 结构化查询（Get-NetFirewallRule） ---
            Console.WriteLine("【C】PowerShell Get-NetFirewallRule（结构化）");
            var psScript = @"
$out = @()
Get-NetFirewallRule -Direction Inbound -ErrorAction SilentlyContinue | ForEach-Object {
  try {
    $pf = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
    $ports = @($pf.LocalPort) | ForEach-Object { ""$_"" }
    if ($ports -contains '3389') {
      $af = $_ | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
      $remote = (@($af.RemoteAddress) -join ',')
      $out += ('RULEDATA|' + $_.Enabled.ToString() + '|' + $_.Action.ToString() + '|' + $remote)
    }
  } catch {}
}
if ($out.Count -eq 0) { 'RULEDATA|NONE' } else { $out }
";
            var psOut = RunPsEncoded(psScript, 60000);
            Console.WriteLine("  输出长度: " + psOut.Length);
            foreach (var line in psOut.Split('\n').Where(l => l.Trim().Length > 0).Take(20))
                Console.WriteLine("    " + line.Trim());
            Console.WriteLine();

            // --- D. 网卡节能查询（非管理员） ---
            Console.WriteLine("【D】网卡节能查询 Get-NetAdapterPowerManagement（非管理员上下文）");
            var nicScript = @"
$r = @()
Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue | ForEach-Object {
  $r += ('NICDATA|' + $_.Name + '|' + ($_.AllowComputerToTurnOffDevice -eq $true))
}
if ($r.Count -eq 0) { 'NICDATA|NONE' } else { $r }
";
            var nicOut = RunPsEncoded(nicScript, 60000);
            Console.WriteLine("  输出长度: " + nicOut.Length);
            foreach (var line in nicOut.Split('\n').Where(l => l.Trim().Length > 0).Take(20))
                Console.WriteLine("    " + line.Trim());
            Console.WriteLine();

            // --- E. 注册表路径下的网卡节能值 ---
            Console.WriteLine("【E】注册表网卡电源管理值");
            try
            {
                using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
                var subNames = cls?.GetSubKeyNames() ?? Array.Empty<string>();
                int checkedAdapters = 0;
                foreach (var sub in subNames)
                {
                    if (!sub.All(char.IsDigit)) continue;
                    using var dev = cls!.OpenSubKey(sub + @"\Device Parameters\PowerManagement");
                    if (dev == null) continue;
                    var v = dev.GetValue("AllowComputerToTurnOffDevice");
                    if (v == null) continue;
                    checkedAdapters++;
                    Console.WriteLine($"    适配器#{sub}: AllowComputerToTurnOffDevice={v}");
                }
                Console.WriteLine("  有该值的适配器数: " + checkedAdapters);
            }
            catch (Exception ex) { Console.WriteLine("  失败：" + ex.Message); }
            Console.WriteLine();

            Console.WriteLine("########## 取证结束 ##########");
        }

        // ================================================================
        //  界面审计模式：逐一实例化真实窗体，测量布局是否"显示不全"
        // ================================================================
        /// <summary>
        /// 真机实测每个窗体的界面布局。判定两类问题：
        ///   ① 窗体大于屏幕可用区域（小屏/高 DPI 下窗口超出屏幕 → 底部内容看不到、点不着）；
        ///   ② 子控件横向或纵向溢出容器（右侧/底部控件被切掉 → 文字截断、按钮消失）。
        /// 通过 MYREMOTE_UI_SCALE / MYREMOTE_UI_SCREEN 可模拟其它 DPI 与分辨率做压力测试。
        /// </summary>
        private static void UiAudit()
        {
            Console.WriteLine("########## 界面审计（DPI 自适应验证）##########");
            Console.WriteLine("系统 DPI     : " + UiScale.SystemDpi);
            Console.WriteLine("缩放系数     : " + UiScale.Factor.ToString("0.###") + "x  (界面缩放：" + UiScale.Describe() + ")");
            // 用 UiScale 的口径（含 MYREMOTE_UI_SCREEN 模拟），与窗体实际采用的可用区域保持一致
            var wa = UiScale.WorkingArea();
            Console.WriteLine("屏幕可用区域 : " + wa.Width + " x " + wa.Height + "（UiScale 实际采用值）");
            Console.WriteLine("模拟覆盖     : MYREMOTE_UI_SCALE=" + (Environment.GetEnvironmentVariable("MYREMOTE_UI_SCALE") ?? "(未设置)")
                              + "  MYREMOTE_UI_SCREEN=" + (Environment.GetEnvironmentVariable("MYREMOTE_UI_SCREEN") ?? "(未设置)"));
            // 只有当 MYREMOTE_UI_SCALE 生效（≠1）时才需要"同时放大字体"的严格模拟
            var simulateFonts = Math.Abs(UiScale.Factor - 1f) > 0.01f;
            if (simulateFonts) Console.WriteLine("严格模拟     : 已开启（字体同步放大，等价真实高 DPI）");
            Console.WriteLine();

            int totalProblems = 0;

            // 每个窗体：名称 + 构造方式（构造即完成布局）
            var cases = new (string name, Func<Form> make)[]
            {
                ("主界面 MainForm",        () => new MainForm()),
                ("环境诊断 DiagnosticsForm",() => new DiagnosticsForm()),
                ("添加设备 DeviceEditForm",  () => new DeviceEditForm(new MyRemote.Models.Device(), true)),
                ("Tailscale导入",           () => new TailscaleImportForm()),
                ("系统日志 SystemLogForm",   () => new SystemLogForm()),
                ("密码输入 PasswordPrompt",  () => new PasswordPrompt("请输入密码：")),
                ("文本输入 NameInputBox",    () => new NameInputBox("新建文件夹", "请输入名称：")),
                ("文件管理 FileManagerForm", () => new FileManagerForm(new MyRemote.Models.Device { Host = "100.64.0.1", Name = "测试机" }, "x")),
            };

            foreach (var (name, make) in cases)
            {
                Form? f = null;
                try
                {
                    f = make();
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-32000, -32000);   // 屏幕外显示，避免干扰用户桌面
                    f.Show();
                    Application.DoEvents();
                    f.PerformLayout();
                    Application.DoEvents();

                    // 严格模拟：真实高 DPI 下 GDI+ 会把 pt 字体按 DPI 放大，
                    // 而纯 MYREMOTE_UI_SCALE 模拟只放大了控件尺寸、字体没变，
                    // 会漏掉"字变大→按钮文字被切掉"这一类问题。
                    // 因此模拟模式下把字体按同比例放大，使验证等价于真实高 DPI 环境。
                    if (simulateFonts)
                    {
                        ScaleFonts(f, UiScale.Factor);
                        // 字体变化会改变 AutoSize 控件的尺寸，必须重新布局，
                        // 否则测到的是"旧尺寸 + 新字体"的中间态，结论不可信
                        f.PerformLayout();
                        Application.DoEvents();
                        f.PerformLayout();
                        Application.DoEvents();
                    }

                    var probs = new List<string>();

                    // 检查一：窗体是否超出屏幕可用区域
                    if (f.Width > wa.Width || f.Height > wa.Height)
                        probs.Add($"窗体 {f.Width}x{f.Height} 超出屏幕 {wa.Width}x{wa.Height}");

                    // 检查二：递归检查子控件是否溢出其直接容器的客户区
                    CheckOverflow(f, f, probs);

                    // 检查三：工具条是否把按钮藏进了溢出下拉（用户点不到）
                    foreach (var ts in FindAll<ToolStrip>(f))
                    {
                        if (ts.CanOverflow) probs.Add($"{NameOf(ts)} 允许溢出收纳（部分按钮会被藏进下拉菜单）");
                        int need = 0;
                        foreach (ToolStripItem it in ts.Items)
                            if (it.Visible) need += it.Width + it.Margin.Horizontal;

                        if (ts.LayoutStyle == ToolStripLayoutStyle.Flow)
                        {
                            // Flow 布局会自动换行：统计按可视宽度换行后需要的行数，
                            // 再与实际高度比较，确认换行后的按钮没有被裁掉。
                            int widest = Math.Max(1, ts.ClientSize.Width);
                            int lines = 1, cur = 0, itemH = 0;
                            foreach (ToolStripItem it in ts.Items)
                            {
                                if (!it.Visible) continue;
                                if (itemH == 0) itemH = it.Height;
                                int w = it.Width + it.Margin.Horizontal;
                                if (cur > 0 && cur + w > widest) { lines++; cur = w; }
                                else cur += w;
                            }
                            if (itemH == 0) itemH = 22;
                            int needH = lines * itemH + ts.Padding.Vertical + 2;
                            if (ts.ClientSize.Height + 1 < needH)
                                probs.Add($"{NameOf(ts)} 换行后需 {needH}px 高，实际仅 {ts.ClientSize.Height}px（末行按钮会被裁掉）");
                        }
                        else if (need > ts.ClientSize.Width + 2)
                            probs.Add($"{NameOf(ts)} 内容宽 {need}px 超过可视宽度 {ts.ClientSize.Width}px");
                    }

                    // 检查四：按钮文本是否被边框切掉（高 DPI 下最典型的"字显示不全"）
                    CheckTextClipping(f, probs);

                    totalProblems += probs.Count;
                    Console.WriteLine((probs.Count == 0 ? "  OK   " : "  问题 ") + name
                                      + $"   窗体 {f.Width}x{f.Height}"
                                      + (f.MinimumSize.Width > 0 ? $"   最小 {f.MinimumSize.Width}x{f.MinimumSize.Height}" : ""));
                    foreach (var p in probs) Console.WriteLine("         • " + p);
                }
                catch (Exception ex)
                {
                    totalProblems++;
                    Console.WriteLine("  异常 " + name + "：" + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    try { f?.Close(); f?.Dispose(); } catch { }
                    Application.DoEvents();
                }
            }

            Console.WriteLine();
            Console.WriteLine(totalProblems == 0
                ? "结论：所有窗体布局均未发现溢出或超出屏幕。"
                : $"结论：共发现 {totalProblems} 处布局问题（见上）。");
            Console.WriteLine("########## 审计结束 ##########");
        }

        /// <summary>
        /// 把窗体及其所有子控件的字体按比例放大，用于模拟真实高 DPI 环境。
        /// 真实高 DPI 下 GDI+ 会依据 DPI 放大 pt 字体；探针里的 MYREMOTE_UI_SCALE
        /// 只影响控件尺寸，故需显式放大字体才能复现"文字被截断"的问题。
        /// </summary>
        private static void ScaleFonts(Control root, float factor, int depth = 0)
        {
            if (depth > 8) return;

            // 先放大根控件自身：子控件默认继承它的字体，由此一次性整体放大
            if (depth == 0) ScaleOne(root, factor, null);

            foreach (Control c in root.Controls)
            {
                // 关键：只放大"显式设置过"的字体。
                // 未显式设置时 Control.Font 返回的是父级字体对象（同一引用），
                // 若也去放大，就会在每一层递归中重复放大（2.5×2.5×2.5…），
                // 得到严重失真的尺寸，使审计结论不可信。
                ScaleOne(c, factor, root.Font);
                ScaleFonts(c, factor, depth + 1);
            }

            // 工具条项：字体默认继承自工具条，同样只处理显式设置过的
            if (root is ToolStrip ts)
            {
                foreach (ToolStripItem it in ts.Items)
                    ScaleOne(it, factor, ts.Font);
            }
        }

        /// <summary>放大单个控件的字体；若其字体继承自父级（引用相同）则跳过。</summary>
        private static void ScaleOne(Control c, float factor, Font? inheritedFrom)
        {
            try
            {
                var f = c.Font;
                if (inheritedFrom != null && ReferenceEquals(f, inheritedFrom)) return;
                if (f.Unit != GraphicsUnit.Point) return;
                c.Font = new Font(f.FontFamily, f.SizeInPoints * factor, f.Style, GraphicsUnit.Point);
            }
            catch { }
        }

        private static void ScaleOne(ToolStripItem it, float factor, Font? inheritedFrom)
        {
            try
            {
                var f = it.Font;
                if (inheritedFrom != null && ReferenceEquals(f, inheritedFrom)) return;
                if (f.Unit != GraphicsUnit.Point) return;
                it.Font = new Font(f.FontFamily, f.SizeInPoints * factor, f.Style, GraphicsUnit.Point);
            }
            catch { }
        }

        /// <summary>
        /// 界面截图模式：把每个窗体真实渲染并保存为 PNG，用于人工核对视觉效果
        /// （字体是否完整、按钮是否被切、布局是否协调）。
        /// 用法：--shot [输出目录]，默认输出到工程下的 ui-shots 目录。
        /// </summary>
        private static void UiScreenshots(string[] args)
        {
            var outDir = args.SkipWhile(a => !a.Equals("--shot", StringComparison.OrdinalIgnoreCase))
                             .Skip(1).FirstOrDefault(x => !x.StartsWith("--"))
                          ?? Path.Combine(AppContext.BaseDirectory, "ui-shots");
            Directory.CreateDirectory(outDir);

            Console.WriteLine("########## 界面截图 ##########");
            Console.WriteLine("界面缩放 : " + UiScale.Describe());
            var wa = UiScale.WorkingArea();
            Console.WriteLine("屏幕可用 : " + wa.Width + " x " + wa.Height);
            Console.WriteLine("输出目录 : " + outDir);
            Console.WriteLine();

            var cases = new (string file, string title, Func<Form> make)[]
            {
                ("01-main",        "主界面",       () => new MainForm()),
                ("02-diagnostics", "环境诊断",     () => new DiagnosticsForm()),
                ("03-device-edit", "添加设备",     () => new DeviceEditForm(new MyRemote.Models.Device(), true)),
                ("04-ts-import",   "Tailscale导入", () => new TailscaleImportForm()),
                ("05-syslog",      "系统日志",     () => new SystemLogForm()),
                ("06-password",    "密码输入",     () => new PasswordPrompt("请输入「测试机」的登录密码：")),
                ("07-filemgr",     "文件管理",     () => new FileManagerForm(new MyRemote.Models.Device { Host = "100.64.0.1", Name = "测试机" }, "x")),
            };

            foreach (var (file, title, make) in cases)
            {
                Form? f = null;
                try
                {
                    f = make();
                    // 显示在屏幕内（不遮挡任务栏），确保能真实渲染
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(20, 20);
                    f.Show();
                    Application.DoEvents();
                    f.PerformLayout();
                    // 让异步加载的内容（本机信息、日志列表）有机会填充
                    for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    f.PerformLayout();
                    Application.DoEvents();

                    var path = Path.Combine(outDir, file + ".png");
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.WriteLine($"  已保存 {title}：{f.Width}x{f.Height} → {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  失败 " + title + "：" + ex.Message);
                }
                finally
                {
                    try { f?.Close(); f?.Dispose(); } catch { }
                    Application.DoEvents();
                }
            }
            Console.WriteLine();
            Console.WriteLine("########## 截图结束 ##########");
        }

        /// <summary>递归检查控件是否超出其直接容器的可视区域。</summary>
        private static void CheckOverflow(Control container, Control root, List<string> probs, int depth = 0)
        {
            if (depth > 6) return;
            foreach (Control c in container.Controls)
            {
                if (c is ToolStrip) continue;   // 工具条单独检查
                try
                {
                    if (c.Visible && c.Width > 0 && c.Height > 0)
                    {
                        // 有 AutoScroll 的容器靠滚动条兜底，不算溢出
                        var scrollable = container is ScrollableControl sc && sc.AutoScroll;
                        if (!scrollable)
                        {
                            if (c.Right > container.ClientSize.Width + 2)
                                probs.Add($"{NameOf(c)} 右侧超出容器 {NameOf(container)} {c.Right - container.ClientSize.Width}px");
                            if (c.Bottom > container.ClientSize.Height + 2)
                                probs.Add($"{NameOf(c)} 底部超出容器 {NameOf(container)} {c.Bottom - container.ClientSize.Height}px");
                        }
                    }
                    // 只对"容器类"控件递归，避免把 Label 里的文本也算成布局问题
                    if (c is Panel || c is GroupBox || c is TableLayoutPanel || c is FlowLayoutPanel
                        || c is TabControl || c is SplitContainer || c is Form || c is ListView)
                        CheckOverflow(c, root, probs, depth + 1);
                }
                catch { }
            }
        }

        private static IEnumerable<T> FindAll<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T t) yield return t;
                foreach (var sub in FindAll<T>(c)) yield return sub;
            }
        }

        /// <summary>
        /// 检查按钮/标签的文本是否超出其可用宽度（高 DPI 下最典型的"字显示不全"）。
        /// 用实际字体度量文本像素宽度，与客户区宽度比较；超出即判定为会被截断。
        /// 已设置 AutoEllipsis/AutoSize 的控件会自行处理，跳过。
        /// </summary>
        private static void CheckTextClipping(Control root, List<string> probs, int depth = 0)
        {
            if (depth > 6) return;
            foreach (Control c in root.Controls)
            {
                try
                {
                    if (c.Visible && c.Width > 0)
                    {
                        if (c is Button b && !b.AutoSize && !string.IsNullOrEmpty(b.Text))
                        {
                            var need = TextRenderer.MeasureText(b.Text, b.Font).Width;
                            var inner = b.ClientSize.Width - b.Padding.Horizontal - 6;  // 6 ≈ 左右边框余量
                            if (need > inner)
                                probs.Add($"按钮「{b.Text}」文字需 {need}px，可用仅 {inner}px（会被截断）");
                        }
                        // 工具条项的宽度已由上方工具条整体检查覆盖
                    }
                    CheckTextClipping(c, probs, depth + 1);
                }
                catch { }
            }
        }

        private static string NameOf(Control c)
        {
            var t = c.GetType().Name;
            if (c is Button btn && !string.IsNullOrEmpty(btn.Text)) return $"按钮「{btn.Text}」";
            if (c is Label l && !string.IsNullOrEmpty(l.Text))
                return "标签「" + (l.Text.Length > 12 ? l.Text.Substring(0, 12) + "…" : l.Text) + "」";
            if (!string.IsNullOrEmpty(c.Name)) return t + "(" + c.Name + ")";
            return t;
        }

        private static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                return outTask.Result ?? "";
            }
            catch { return ""; }
        }

        /// <summary>用 -EncodedCommand 执行 PowerShell（避免文件编码与转义问题）。</summary>
        private static string RunPsEncoded(string script, int timeoutMs)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                return outTask.Result ?? "";
            }
            catch { return ""; }
        }
    }
}
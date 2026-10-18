using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MyRemote.Services
{
    /// <summary>诊断判定结果。</summary>
    public enum DiagStatus { Pass, Warn, Fail, Info, Unknown }

    /// <summary>单条诊断项。</summary>
    public class DiagnosticItem
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public DiagStatus Status { get; set; } = DiagStatus.Unknown;
        public string Detail { get; set; } = "";
        public string FixGuide { get; set; } = "";
        /// <summary>是否可一键自动修复。</summary>
        public bool CanRepair { get; set; }
        /// <summary>自动修复时对应的修复动作键（多个检测项可映射到同一动作）。</summary>
        public string RepairId { get; set; } = "";
    }

    /// <summary>
    /// 环境自检：一次性把“部署前该满足的所有条件”全部测出来，
    /// 让用户在动手前就知道哪里达标、哪里没达标，并给出逐项解决指引。
    /// 全部为只读检测 + try/catch 兜底，任何一项取不到都不影响其他项与程序运行。
    /// </summary>
    public static class DiagnosticsService
    {
        public static List<DiagnosticItem> RunAll()
        {
            // 防火墙规则转储在本次检测内复用（netsh 输出较大、执行较慢，避免重复调用）
            _firewallDump = null;
            try
            {
                var items = new List<DiagnosticItem>();
                items.AddRange(CheckSystem());
                items.AddRange(CheckNetwork());
                items.AddRange(CheckHostHardening());
                items.AddRange(CheckAccount());
                return items;
            }
            finally
            {
                _firewallDump = null;
            }
        }

        [ThreadStatic] private static string? _firewallDump;

        private static string GetFirewallDump()
        {
            if (_firewallDump != null) return _firewallDump;
            _firewallDump = Run("netsh", "advfirewall firewall show rule name=all dir=in", 10000);
            return _firewallDump;
        }

        // ---------- 通用：执行外部命令并取标准输出 ----------
        // 同时异步读取 stdout 与 stderr，避免子进程写满 stderr 管道时造成死锁卡死。
        //
        // 【关键】编码：Windows 自带命令行工具（netsh / sc / net 等）在中文系统上
        // 以系统 ANSI 代码页（GBK/CP936）输出，而不是 UTF-8。若不显式指定编码，
        // .NET 默认按 UTF-8 解码，中文会被解成乱码（如 “Î´ÆôÓÃ”），
        // 导致所有依赖中文关键字（“已启用”“规则名称”“操作”）的判断全部失效，
        // 表现为明明放行了防火墙却报“未检测到”。
        private static Encoding ConsoleEncoding
        {
            get
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(GetSystemAnsiCodePage());
                }
                catch
                {
                    try { return Encoding.Default; }
                    catch { return Encoding.UTF8; }
                }
            }
        }

        /// <summary>
        /// 取本机真实的 ANSI 代码页（中文系统 936，英文 1252 等）。
        ///
        /// 【重要】不能用 CultureInfo.CurrentCulture.TextInfo.OEMCodePage 来推断：
        /// 本项目启用了 InvariantGlobalization=true（单文件自包含需要），
        /// 此时 CurrentCulture 为空、OEMCodePage 会返回 437（美式 DOS 页），
        /// 在中文系统上导致 netsh 输出被按错误代码页解码、中文关键字全部匹配失败。
        /// 直接向 Windows 询问系统 ANSI 代码页（GetACP）才可靠。
        /// </summary>
        private static int GetSystemAnsiCodePage()
        {
            try
            {
                var cp = GetACP();
                if (cp > 0) return (int)cp;
            }
            catch { }
            return 936;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetACP();

        /// <summary>取本机真实的 OEM 代码页（命令提示符输出用，Ctrl+C 控制台程序多为 OEM 页）。</summary>
        private static int GetSystemOemCodePage()
        {
            try
            {
                var cp = GetOEMCP();
                if (cp > 0) return (int)cp;
            }
            catch { }
            return GetSystemAnsiCodePage();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        /// <summary>
        /// 执行本机命令并取输出。会先按 OEM 代码页解码，若中文关键字仍匹配不上，
        /// 自动改为按 ANSI 代码页再解一次（不同 Windows 工具输出所用代码页不完全一致）。
        /// </summary>
        private static string Run(string exe, string args, int timeoutMs = 8000)
        {
            // 先用 OEM 页（多数控制台工具的默认输出页）
            var first = RunEncoded(exe, args, timeoutMs, CodePageKind.Oem);
            if (LooksLikeGarbled(first))
            {
                var second = RunEncoded(exe, args, timeoutMs, CodePageKind.Ansi);
                // 第二次解出的内容含可读中文则采用
                if (!LooksLikeGarbled(second) || second.Length > first.Length) return second;
            }
            return first;
        }

        private enum CodePageKind { Oem, Ansi }

        /// <summary>
        /// 判断输出是否为乱码：中文 Windows 工具输出里应出现常见中文关键字，
        /// 若一个都匹配不到且存在典型的乱码字符（Ã、Î、Ð 等），判定为乱码。
        /// </summary>
        private static bool LooksLikeGarbled(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            // 出现常见中文字符 → 解码正常
            if (s.Contains("已启用") || s.Contains("规则名称") || s.Contains("操作")
                || s.Contains("服务状态") || s.Contains("状态"))
                return false;
            // 出现 GBK 被误当 Latin-1 解码的典型字符 → 乱码
            foreach (var ch in s)
            {
                if (ch >= '\u00C0' && ch <= '\u00FF') return true;
            }
            return false;
        }

        private static string RunEncoded(string exe, string args, int timeoutMs, CodePageKind kind)
        {
            try
            {
                Encoding enc;
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    var cp = kind == CodePageKind.Oem ? GetSystemOemCodePage() : GetSystemAnsiCodePage();
                    enc = Encoding.GetEncoding(cp);
                }
                catch { enc = Encoding.UTF8; }

                var psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = enc,
                    StandardErrorEncoding = enc
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                return outTask.Result;
            }
            catch { return ""; }
        }

        // ---------- 通用：把 netsh 多行规则输出按“规则名称”切分成规则块 ----------
        private static List<string> SplitRuleBlocks(string outp)
        {
            var blocks = new List<string>();
            var sb = new StringBuilder();
            foreach (var line in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.TrimStart().StartsWith("规则名称", StringComparison.OrdinalIgnoreCase)
                    || line.TrimStart().StartsWith("Rule Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) { blocks.Add(sb.ToString()); sb.Clear(); }
                }
                sb.AppendLine(line);
            }
            if (sb.Length > 0) blocks.Add(sb.ToString());
            return blocks;
        }

        // ---------- 系统基础 ----------
        private static List<DiagnosticItem> CheckSystem()
        {
            var list = new List<DiagnosticItem>();

            // 1) 操作系统版本
            var (ed, isPro) = GetEditionInfo();
            list.Add(new DiagnosticItem
            {
                Id = "OsVersion",
                CanRepair = false,
                Category = "系统基础",
                Title = "操作系统版本（能否作为被控端）",
                Status = isPro ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = ed,
                FixGuide = isPro ? "" :
                    "当前为 Windows 家庭版，自带的 Windows 远程桌面不能接受入站连接（不能当被控端）。\n" +
                    "解决：设置 → 系统 → 激活 → 升级到 Windows 专业版（付费，但会一并解锁 BitLocker、本地组策略、Hyper-V 等）。\n" +
                    "注意：家庭版升级专业版是单向不可逆的，升级前务必创建系统还原点、备份数据。"
            });

            // 2) 远程桌面被控端是否开启
            var rdp = LocalMachineInfo.IsRdpEnabled;
            list.Add(new DiagnosticItem
            {
                Id = "RdpEnabled",
                CanRepair = true,
                RepairId = "RdpEnabled",
                Category = "系统基础",
                Title = "远程桌面被控端已开启",
                Status = rdp ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = rdp ? "已允许入站远程桌面连接" : "fDenyTSConnections=1（被禁用）",
                FixGuide = rdp ? "" :
                    "右键“以管理员身份”运行「一键部署.ps1」即可自动开启；\n" +
                    "或手动：设置 → 系统 → 远程桌面 → 开启。"
            });

            // 3) TermService 服务状态
            var ts = GetServiceState("TermService");
            list.Add(new DiagnosticItem
            {
                Id = "TermService",
                CanRepair = true,
                RepairId = "TermService",
                Category = "系统基础",
                Title = "远程桌面服务(TermService)运行中",
                Status = ts == "RUNNING" ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = "服务状态：" + ts,
                FixGuide = ts == "RUNNING" ? "" :
                    "以管理员运行命令：\nsc config TermService start= auto\nnet start TermService\n（一键部署.ps1 已包含此步骤）"
            });

            // 4) RDP 客户端控件可用性（本机作为主控端连接他人所必需）
            var rdpOk = RdpClientDetector.IsAvailable;
            list.Add(new DiagnosticItem
            {
                Id = "RdpClientCtrl",
                CanRepair = true,
                RepairId = "RdpClientCtrl",
                Category = "系统基础",
                Title = "RDP 客户端控件可用（能否作为主控端）",
                Status = rdpOk ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = rdpOk
                    ? $"已探测到可用控件：{RdpClientDetector.DetectedClsid}"
                    : "本机未注册任何 MsRdpClient 控件（mstscax.dll）",
                FixGuide = rdpOk ? "" :
                    "本机缺少「远程桌面客户端控件」，无法用内置窗口连接其它电脑。\n" +
                    "点「一键修复」会自动执行：以管理员身份重新注册 mstscax.dll，并复检探测结果。\n" +
                    "若仍失败，可手动执行系统修复：sfc /scannow 与 DISM /Online /Cleanup-Image /RestoreHealth，\n" +
                    "或改用系统自带 mstsc.exe 手动连接。"
            });

            // 5) 界面自适应：报告本机 DPI 与屏幕，确认缩放策略正确生效。
            //    这项不参与修复（属提示性信息），但用户反馈"窗口显示不全"时，
            //    看这一项即可知道其 DPI/分辨率，便于远程定位。
            try
            {
                var dpi = UiScale.SystemDpi;
                // 用 UiScale 的线程安全实现：本方法运行在线程池，不能依赖 Screen 组件
                var wa = UiScale.WorkingArea();
                var waText = $"{wa.Width}x{wa.Height}";
                var mainW = UiScale.S(760);
                var mainH = UiScale.S(520);
                var fits = mainW <= wa.Width && mainH <= wa.Height;
                list.Add(new DiagnosticItem
                {
                    Id = "UiScale",
                    CanRepair = false,
                    Category = "系统基础",
                    Title = "界面自适应（不同分辨率下显示完整）",
                    Status = fits ? DiagStatus.Pass : DiagStatus.Warn,
                    Detail = $"界面缩放：{UiScale.Describe()}；屏幕可用区域：{waText}；" +
                             $"主界面实际尺寸：{mainW}x{mainH}" +
                             (fits ? "（在屏幕范围内）" : "（超出屏幕，已自动裁剪到屏幕内）"),
                    FixGuide = fits ? "" :
                        "本机屏幕较小或缩放比例较高，窗口会自动裁剪到屏幕内，并启用横向滚动/自动补满列宽以保证内容可读。\n" +
                        "若仍觉字小或拥挤：\n" +
                        "• 可临时用环境变量 MYREMOTE_UI_SCALE=1.0（或 1.25/1.5）强制指定界面缩放比例后重启程序；\n" +
                        "• 或在「显示设置」中调整缩放比例后重启程序。"
                });
            }
            catch { }

            return list;
        }

        /// <summary>
        /// 取系统版本与“能否作为被控端”的判定。
        /// 关键：中文版 Windows 的 ProductName 是“Windows 10 专业版”，不含 "Pro"，
        /// 单看 ProductName 会把中文专业版误判为家庭版。因此优先用与语言无关的 EditionID
        /// （Professional / Enterprise / Education / Server*），再回退到名称关键字（含中文）。
        /// </summary>
        private static (string display, bool pro) GetEditionInfo()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var name = key?.GetValue("ProductName") as string ?? "未知";
                var editionId = key?.GetValue("EditionID") as string ?? "";
                var display = string.IsNullOrWhiteSpace(editionId) ? name : $"{name}（{editionId}）";

                var pro = editionId.Equals("Professional", StringComparison.OrdinalIgnoreCase)
                       || editionId.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
                       || editionId.Equals("EnterpriseS", StringComparison.OrdinalIgnoreCase)
                       || editionId.Equals("Education", StringComparison.OrdinalIgnoreCase)
                       || editionId.StartsWith("Server", StringComparison.OrdinalIgnoreCase)
                       || name.IndexOf("Pro", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("Education", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.Contains("专业版") || name.Contains("企业版") || name.Contains("教育版");
                return (display, pro);
            }
            catch
            {
                return ("未知", false);
            }
        }

        private static string GetServiceState(string name)
        {
            var outp = Run("sc", $"query \"{name}\"", 8000);
            if (string.IsNullOrWhiteSpace(outp)) return "无法查询";
            var idx = outp.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "未安装";
            var rest = outp.Substring(idx);
            if (rest.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) return "RUNNING";
            if (rest.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0) return "STOPPED";
            if (rest.IndexOf("START_PENDING", StringComparison.OrdinalIgnoreCase) >= 0) return "START_PENDING";
            return "其它";
        }

        // ---------- 网络层 Tailscale ----------
        private static List<DiagnosticItem> CheckNetwork()
        {
            var list = new List<DiagnosticItem>();

            // 4) Tailscale 是否安装
            var installed = IsTailscaleInstalled();
            var hasInstaller = PackageService.HasTailscaleInstaller;
            list.Add(new DiagnosticItem
            {
                Id = "TailscaleInstalled",
                CanRepair = !installed && hasInstaller,   // 内置安装包存在时可直接一键安装
                RepairId = "TailscaleInstalled",
                Category = "网络层(Tailscale)",
                Title = "Tailscale 已安装",
                Status = installed ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = installed
                    ? "已安装：" + TailscaleCli.ExePath
                    : (hasInstaller ? "未检测到 tailscale.exe（已随程序内置安装包，可一键安装）"
                                    : "未检测到 tailscale.exe，且未找到内置安装包"),
                FixGuide = installed ? "" :
                    (hasInstaller
                        ? "点「一键修复」将使用随程序分发的内置安装包自动安装（静默安装，无需手动操作）。\n" +
                          "安装完成后请点击任务栏 Tailscale 图标，用与其它设备相同的账号登录。"
                        : "未找到内置安装包。请把 tailscale-setup-*.exe 放到程序目录（或其 packages 子目录）后重新检测；\n" +
                          "或手动下载安装：https://tailscale.com/download/windows")
            });

            if (!installed)
            {
                list.Add(new DiagnosticItem { Id = "TailscaleOnline", CanRepair = false, Category = "网络层(Tailscale)", Title = "Tailscale 已登录且本机在线", Status = DiagStatus.Fail, Detail = "未安装，无法检测", FixGuide = "先安装并登录 Tailscale 后重新检测。" });
                list.Add(new DiagnosticItem { Id = "Peers", CanRepair = false, Category = "网络层(Tailscale)", Title = "检测到对端设备", Status = DiagStatus.Unknown, Detail = "未安装", FixGuide = "" });
                list.Add(new DiagnosticItem { Id = "P2P", CanRepair = false, Category = "网络层(Tailscale)", Title = "P2P 直连（不限速前提）", Status = DiagStatus.Unknown, Detail = "未安装", FixGuide = "" });
                return list;
            }

            // 5-7) 登录 / 在线 / 对端 / 直连
            var (backend, selfOnline, peers) = ParseTailscaleStatus();
            var loggedIn = backend.Equals("Running", StringComparison.OrdinalIgnoreCase);

            list.Add(new DiagnosticItem
            {
                Id = "TailscaleOnline",
                CanRepair = false,
                Category = "网络层(Tailscale)",
                Title = "Tailscale 已登录且本机在线",
                Status = loggedIn && selfOnline ? DiagStatus.Pass : (loggedIn ? DiagStatus.Warn : DiagStatus.Fail),
                Detail = $"控制面状态：{backend}；本机在线：{selfOnline}",
                FixGuide = loggedIn
                    ? (selfOnline ? "" : "本机未标记为在线，请确认 Tailscale 是否正在运行（任务栏图标是否为绿色）。")
                    : "Tailscale 尚未登录。点击任务栏 Tailscale 图标 → Log in，用与本项目相同的账号登录。\n登录后本机即可获得 100.x.x.x 虚拟 IP。"
            });

            var onlinePeers = peers.Count(p => p.online);
            list.Add(new DiagnosticItem
            {
                Id = "Peers",
                CanRepair = false,
                Category = "网络层(Tailscale)",
                Title = "检测到对端设备",
                Status = onlinePeers > 0 ? DiagStatus.Pass : DiagStatus.Warn,
                Detail = $"在线对端：{onlinePeers} 台（共 {peers.Count} 台）",
                FixGuide = onlinePeers > 0 ? "" :
                    "当前没有其它设备在线。请确认：\n" +
                    "1) 其它电脑也装了 Tailscale 并登录同一账号；\n" +
                    "2) 其它电脑当前已开机并联网。\n" +
                    "注：你只需“想远程连的那台”开机即可，无需所有设备同时在线。"
            });

            var direct = peers.Count(p => p.online && p.direct);
            var p2p = peers.Count == 0 ? DiagStatus.Unknown : (direct > 0 ? DiagStatus.Pass : DiagStatus.Warn);
            list.Add(new DiagnosticItem
            {
                Id = "P2P",
                CanRepair = false,
                Category = "网络层(Tailscale)",
                Title = "P2P 直连（不限速前提）",
                Status = p2p,
                Detail = $"直连对端：{direct} 台 / 在线 {onlinePeers} 台",
                FixGuide = p2p == DiagStatus.Pass ? "" :
                    "直连未建立，数据可能走官方中转（DERP），会变慢。\n" +
                    "排查：\n" +
                    "1) 公司网络可能限制 UDP 出网，可在公司机器用「tailscale ping 对方IP」看是否 relay；\n" +
                    "2) 优先使用公司官方 VPN；\n" +
                    "3) 或改用 ZeroTier（可配国内 Moon 节点）。\n" +
                    "直连成功时带宽为两家宽带上行，真正不限速。"
            });

            return list;
        }

        private static bool IsTailscaleInstalled()
        {
            // 统一入口：自动探测新旧安装路径（旧版 "Tailscale IPN"、1.104+ "Tailscale"），
            // 不依赖 PATH——升级后残留的旧 PATH 会导致裸名调用静默失败。
            return TailscaleCli.IsInstalled;
        }

        private static (string backend, bool selfOnline, List<(bool online, bool direct)> peers) ParseTailscaleStatus()
        {
            var json = TailscaleCli.Run("status --json", 8000);
            string backend = "";
            var selfOnline = false;
            var peers = new List<(bool, bool)>();
            if (string.IsNullOrWhiteSpace(json)) return (backend, selfOnline, peers);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("BackendState", out var bs)) backend = bs.GetString() ?? "";
                if (root.TryGetProperty("Self", out var self)
                    && self.TryGetProperty("Online", out var so) && so.ValueKind == JsonValueKind.True)
                    selfOnline = true;
                if (root.TryGetProperty("Peer", out var pl) && pl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pl.EnumerateObject())
                    {
                        var peer = prop.Value;
                        var online = peer.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
                        var direct = false;
                        if (peer.TryGetProperty("Direct", out var d))
                        {
                            if (d.ValueKind == JsonValueKind.True) direct = true;
                            else if (d.ValueKind == JsonValueKind.False) direct = false;
                        }
                        else if (peer.TryGetProperty("Relay", out var r))
                        {
                            var rv = r.GetString() ?? "";
                            direct = online && string.IsNullOrEmpty(rv);
                        }
                        peers.Add((online, direct));
                    }
                }
            }
            catch { }
            return (backend, selfOnline, peers);
        }

        // ---------- 被控端固化 ----------
        private static List<DiagnosticItem> CheckHostHardening()
        {
            var list = new List<DiagnosticItem>();

            // 8) 休眠（读注册表，不依赖 powercfg 的本地化输出）
            var hibOn = HibernateEnabledByRegistry();
            list.Add(new DiagnosticItem
            {
                Id = "Hibernate",
                CanRepair = true,
                RepairId = "Hibernate",
                Category = "被控端固化",
                Title = "休眠已关闭（避免失联）",
                Status = hibOn ? DiagStatus.Warn : DiagStatus.Pass,
                Detail = hibOn ? "系统启用了休眠（断电/休眠后会失联）" : "休眠已关闭",
                FixGuide = hibOn ?
                    "以管理员运行命令：powercfg /hibernate off\n（一键部署.ps1 已包含此步骤）" : ""
            });

            // 9) 网卡节能
            var (nicOk, nicDetail) = CheckNicPowerSaving();
            list.Add(new DiagnosticItem
            {
                Id = "NicPower",
                CanRepair = true,
                RepairId = "NicPower",
                Category = "被控端固化",
                Title = "网卡节能已关闭（避免断连）",
                Status = nicOk ? DiagStatus.Pass : DiagStatus.Warn,
                Detail = nicDetail,
                FixGuide = nicOk ? "" :
                    "设备管理器 → 网络适配器 → 右键网卡 → 属性 → 电源管理 → 取消勾选“允许计算机关闭此设备以节约电源”。\n" +
                    "（部分系统命令无法读取此项，请按上述手动确认。）"
            });

            // 10) SMB / LanmanServer
            var smb = GetServiceState("LanmanServer");
            list.Add(new DiagnosticItem
            {
                Id = "LanmanServer",
                CanRepair = true,
                RepairId = "LanmanServer",
                Category = "被控端固化",
                Title = "文件共享服务(LanmanServer)运行中",
                Status = smb == "RUNNING" ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = "服务状态：" + smb,
                FixGuide = smb == "RUNNING" ? "" :
                    "以管理员运行命令：\nsc config LanmanServer start= auto\nnet start LanmanServer\n" +
                    "（一键部署.ps1 已包含此步骤；文件管理功能依赖此服务）"
            });

            // 11) 防火墙放行 3389
            var fw = CheckRdpFirewall();
            list.Add(new DiagnosticItem
            {
                Id = "FirewallRdp",
                CanRepair = true,
                RepairId = "FirewallRdp",
                Category = "被控端固化",
                Title = "防火墙放行远程桌面(3389)",
                Status = fw ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = fw ? "存在允许 3389 入站的规则" : "未检测到允许 3389 入站的防火墙规则",
                FixGuide = fw ? "" :
                    "以管理员运行命令：\nnetsh advfirewall firewall add rule name=\"MyRemote-RDP\" dir=in action=allow protocol=TCP localport=3389\n" +
                    "建议：将该规则的作用域限制为 Tailscale 虚拟网段（100.64.0.0/10），避免公网暴露。"
            });

            // 11b) 防火墙作用域安全强度（是否仅对 Tailscale 开放）
            list.AddRange(CheckFirewallScope());

            return list;
        }

        /// <summary>休眠是否启用。读注册表以避免 powercfg 输出随系统语言变化导致误判。</summary>
        private static bool HibernateEnabledByRegistry()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
                var v = k?.GetValue("HibernateEnabled");
                return v != null && Convert.ToInt32(v) != 0;
            }
            catch { return false; }
        }

        private static (bool ok, string detail) CheckNicPowerSaving()
        {
            // 策略：先读注册表（快、无副作用）；注册表取不到时用 PowerShell 兜底查询。
            // 实机经验：部分网卡驱动（尤其虚拟网卡、USB 网卡）不把电源管理写进
            // Device Parameters\PowerManagement，此时注册表路径会返回 0 个适配器，
            // 不能据此判定“未通过”，需要换一条通道再确认。
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
                if (cls != null)
                {
                    var anyAllowed = false;
                    var checkedAdapters = 0;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length == 0 || !sub.All(char.IsDigit)) continue;
                        using var dev = cls.OpenSubKey(sub + @"\Device Parameters\PowerManagement");
                        if (dev == null) continue;
                        var v = dev.GetValue("AllowComputerToTurnOffDevice");
                        if (v == null) continue;
                        checkedAdapters++;
                        if (Convert.ToInt32(v) == 1) anyAllowed = true;
                    }
                    if (checkedAdapters > 0)
                    {
                        return (!anyAllowed, anyAllowed
                            ? "存在允许关闭网卡的适配器"
                            : $"已检查 {checkedAdapters} 个适配器，均未允许关闭");
                    }
                }
            }
            catch (Exception ex) { SystemLog.Exception("Diag", ex); }

            // 注册表没有数据时，用 PowerShell 查询实际生效值
            return CheckNicPowerViaPowerShell();
        }

        /// <summary>
        /// 用 PowerShell 查询网卡电源管理（注册表无数据时的兜底通道）。
        /// 输出格式：每行 "适配器名|True/False"，无数据时输出 "NONE"。
        ///
        /// 【实机约束】本机环境下 Get-NetAdapterPowerManagement 返回空（cmdlet 不可用），
        /// 因此这只是一个"尽力而为"的补充通道；取不到数据时如实报告，
        /// 不据此误判为"未通过"（网卡驱动是否暴露该项本就因驱动而异）。
        /// </summary>
        private static (bool ok, string detail) CheckNicPowerViaPowerShell()
        {
            try
            {
                var script = "$r=@(); Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue | " +
                             "ForEach-Object { $r += ($_.Name + '|' + ($_.AllowComputerToTurnOffDevice -eq $true)) }; " +
                             "if ($r.Count -eq 0) { 'NONE' } else { $r }";
                var outp = RunPsEncoded(script, 60000);

                if (string.IsNullOrWhiteSpace(outp) || outp.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    return (false, "系统未报告网卡电源管理信息（部分网卡驱动不支持该项，非必现问题）。若远程连接偶发断连，请手动到设备管理器确认。");

                var anyAllowed = false;
                var count = 0;
                foreach (var line in outp.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || !t.Contains('|')) continue;
                    count++;
                    var parts = t.Split('|');
                    if (parts.Length >= 2 && parts[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
                        anyAllowed = true;
                }
                if (count == 0)
                    return (false, "系统未报告网卡电源管理信息（部分网卡驱动不支持该项）。");

                return (!anyAllowed, anyAllowed
                    ? "存在允许关闭网卡的适配器（远程连接可能中断）"
                    : $"已检查 {count} 个适配器，均未允许关闭");
            }
            catch (Exception ex)
            {
                return (false, "检测异常：" + ex.Message);
            }
        }

        /// <summary>用 -EncodedCommand 执行 PowerShell 脚本并返回输出（避免文件编码问题）。</summary>
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

        // ---------- 防火墙规则块解析辅助 ----------
        // netsh 输出的一“块”包含：规则名称/已启用/方向/配置文件/分组/本地端口/远程 IP 地址/操作 等行。
        // 必须区分“已启用=是/否”和“操作=允许/阻止”，否则已禁用的默认规则会被误判为已放行。

        private static string? FieldLine(string block, params string[] prefixes)
        {
            return block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => prefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>规则是否已启用（解析不到时按启用处理，宁可多提示也不要漏报）。</summary>
        private static bool BlockEnabled(string block)
        {
            var line = FieldLine(block, "已启用", "Enabled");
            if (line == null) return true;
            return line.Contains("是") || line.Contains("Yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>规则动作是否为“允许”。</summary>
        private static bool BlockAllows(string block)
        {
            var line = FieldLine(block, "操作", "Action");
            if (line == null) return false;
            return line.Contains("允许") || line.Contains("Allow", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 规则块里的「远程 IP」行。注意 netsh 中文输出的实际字段名是
        /// 「远程 IP」（带空格，无「地址」二字），英文为 "RemoteIPs" 或 "Remote IP Addresses"，
        /// 因此这里同时兼容多种写法；用「远程 IP」前缀匹配可同时覆盖「远程 IP」与「远程 IP 地址」。
        /// </summary>
        private static string GetRemoteIpLine(string block) =>
            FieldLine(block, "远程 IP", "RemoteIP", "Remote IP") ?? "";

        /// <summary>规则是否与远程桌面(3389)相关。</summary>
        private static bool BlockIsRdp(string block)
        {
            return block.IndexOf("3389", StringComparison.OrdinalIgnoreCase) >= 0
                || block.IndexOf("远程桌面", StringComparison.OrdinalIgnoreCase) >= 0
                || block.IndexOf("Remote Desktop", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CheckRdpFirewall()
        {
            var outp = GetFirewallDump();
            if (string.IsNullOrWhiteSpace(outp)) return false;
            foreach (var block in SplitRuleBlocks(outp))
            {
                // 只有“已启用 且 操作为允许 且 与 3389/远程桌面相关”才算真正放行
                if (BlockIsRdp(block) && BlockEnabled(block) && BlockAllows(block)) return true;
            }
            return false;
        }

        // ---------- 安全强度：防火墙作用域是否仅限 Tailscale（防暴露） ----------
        private static List<DiagnosticItem> CheckFirewallScope()
        {
            var list = new List<DiagnosticItem>();
            var outp = GetFirewallDump();
            if (string.IsNullOrWhiteSpace(outp))
            {
                list.Add(new DiagnosticItem
                {
                    Category = "安全强度",
                    Id = "FirewallScope",
                    CanRepair = true,
                    RepairId = "FirewallRdp",
                    Title = "RDP(3389) 仅对 Tailscale 开放（防暴露）",
                    Status = DiagStatus.Unknown,
                    Detail = "无法读取防火墙规则",
                    FixGuide = ""
                });
                return list;
            }

            bool anyTailscaleScoped = false;
            bool anyAnyExposed = false;
            int enabledRdpRules = 0;

            foreach (var block in SplitRuleBlocks(outp))
            {
                if (!BlockIsRdp(block) || !BlockEnabled(block) || !BlockAllows(block)) continue;
                enabledRdpRules++;

                // 取该规则块内的「远程 IP」行，判断作用域
                var remoteLine = GetRemoteIpLine(block);
                var isAny = remoteLine.Contains("任何") || remoteLine.Contains("Any", StringComparison.OrdinalIgnoreCase);
                var isTailscale = remoteLine.Contains("100.64");

                if (isAny) anyAnyExposed = true;
                else if (isTailscale) anyTailscaleScoped = true;
            }

            if (enabledRdpRules == 0)
            {
                list.Add(new DiagnosticItem
                {
                    Category = "安全强度",
                    Id = "FirewallScope",
                    CanRepair = true,
                    RepairId = "FirewallRdp",
                    Title = "RDP(3389) 仅对 Tailscale 开放（防暴露）",
                    Status = DiagStatus.Fail,
                    Detail = "未检测到已启用且允许入站的 3389 规则，远程桌面将无法连入。",
                    FixGuide = "点「一键修复」或「立即修复此项」，仅以 Tailscale 虚拟网段(100.64.0.0/10)放行 3389。"
                });
            }
            else if (anyAnyExposed)
            {
                // 只要存在一条“任何地址”的 3389 放行规则，端口就已经对同网段/公网可见 —— 其它限定规则不能抵消这个暴露
                list.Add(new DiagnosticItem
                {
                    Category = "安全强度",
                    Id = "FirewallScope",
                    CanRepair = true,
                    RepairId = "FirewallRdp",
                    Title = "RDP(3389) 仅对 Tailscale 开放（防暴露）",
                    Status = DiagStatus.Warn,
                    Detail = "存在作用域为“任何地址”的 3389 放行规则，端口对所有网络可见，存在被同网段或公网扫描到的风险。",
                    FixGuide = "点「一键修复」或「立即修复此项」：会禁用系统默认“远程桌面”规则组，仅保留 Tailscale 网段(100.64.0.0/10)放行。"
                });
            }
            else if (anyTailscaleScoped)
            {
                list.Add(new DiagnosticItem
                {
                    Category = "安全强度",
                    Id = "FirewallScope",
                    CanRepair = true,
                    RepairId = "FirewallRdp",
                    Title = "RDP(3389) 仅对 Tailscale 开放（防暴露）",
                    Status = DiagStatus.Pass,
                    Detail = "3389 仅对 Tailscale 虚拟网段(100.64.0.0/10)开放，公网/物理网卡不可见，安全。",
                    FixGuide = ""
                });
            }
            else
            {
                list.Add(new DiagnosticItem
                {
                    Category = "安全强度",
                    Id = "FirewallScope",
                    CanRepair = true,
                    RepairId = "FirewallRdp",
                    Title = "RDP(3389) 仅对 Tailscale 开放（防暴露）",
                    Status = DiagStatus.Info,
                    Detail = "3389 已放行，但作用域不是 Tailscale 虚拟网段（例如限定为某个局域网段）。若你只通过 Tailscale 连接，建议改为仅 Tailscale 网段。",
                    FixGuide = "点「一键修复」可把 3389/445 收紧为仅 Tailscale 网段(100.64.0.0/10)可见。"
                });
            }
            return list;
        }

        // ---------- 账号安全 ----------
        private static List<DiagnosticItem> CheckAccount()
        {
            var list = new List<DiagnosticItem>();

            // 12) PIN / Windows Hello
            var hasPin = HasWindowsHelloPin();
            list.Add(new DiagnosticItem
            {
                Id = "PinDependency",
                CanRepair = false,
                Category = "账号安全",
                Title = "远程登录未依赖 PIN / 指纹",
                Status = hasPin ? DiagStatus.Warn : DiagStatus.Pass,
                Detail = hasPin ? "检测到 Windows Hello / PIN 凭证" : "未发现 PIN 凭证",
                FixGuide = hasPin ?
                    "重要：远程桌面无法使用 Windows Hello PIN / 指纹 / 人脸登录，只能使用账号密码。\n" +
                    "请确保用于远程登录的账户设置了密码（非 PIN），连接时如需输入密码请用“密码”而非 PIN。\n" +
                    "如账户此前只用 PIN 登录，请到 设置 → 账户 → 登录选项 为该账户添加密码。" : ""
            });

            // 13) 开机自启（可选）
            var autostart = IsAutoStart();
            list.Add(new DiagnosticItem
            {
                Id = "AutoStart",
                CanRepair = true,
                RepairId = "AutoStart",
                Category = "账号安全",
                Title = "已配置开机自启（推荐）",
                Status = autostart ? DiagStatus.Pass : DiagStatus.Info,
                Detail = autostart ? "已启用开机自启" : "未启用开机自启（关闭电脑后仍可被连的前提是该机已开机）",
                FixGuide = autostart ? "" :
                    "在主窗口托盘右键 → 勾选“开机自启”，即可让 MyRemote 随系统启动并常驻托盘，保证随时可被连接。"
            });

            return list;
        }

        private static bool HasWindowsHelloPin()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\Ngc");
                if (key == null) return false;
                return key.SubKeyCount > 0;
            }
            catch { return false; }
        }

        private static bool IsAutoStart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("MyRemote") != null;
            }
            catch { return false; }
        }
    }
}

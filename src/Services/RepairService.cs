using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MyRemote.Services
{
    /// <summary>单条修复结果。</summary>
    public class ItemResult
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>fixed / failed / skipped</summary>
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>一次修复 / 恢复的整体结果（也是子进程回传给主进程的 IPC 内容）。</summary>
    public class RepairResult
    {
        public bool Success { get; set; }
        public bool UserCancelled { get; set; }
        public string Message { get; set; } = "";
        public List<ItemResult> Items { get; set; } = new();
    }

    /// <summary>快照文件信息（供 UI 选择恢复到哪一次修复前）。</summary>
    public class SnapshotInfo
    {
        public string Path { get; set; } = "";
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// 修复引擎：把「环境诊断」发现的不达标项一键修好，并在修复前自动创建快照，
    /// 支持「一键恢复到修复前的状态」。修复需要管理员权限：若当前非管理员，则自动
    /// 以 runas 拉起一个本程序的高权限子进程（无界面）完成实际修改，结果回传主进程。
    /// </summary>
    public static class RepairService
    {
        // 可修复项 -> 展示名（也是实际修复动作的分支键）
        private static readonly Dictionary<string, string> ItemNames = new()
        {
            ["RdpEnabled"] = "开启远程桌面被控端",
            ["TermService"] = "启动远程桌面服务(TermService)",
            ["Hibernate"] = "关闭休眠（防失联）",
            ["NicPower"] = "关闭网卡节能（防断连）",
            ["LanmanServer"] = "启动文件共享服务(LanmanServer)",
            ["FirewallRdp"] = "防火墙仅对 Tailscale 放行 3389/445",
            ["AutoStart"] = "配置开机自启",
            ["RdpClientCtrl"] = "注册 RDP 客户端控件（主控端必需）",
            ["TailscaleInstalled"] = "安装 Tailscale（内置安装包）",
        };

        private const string RepairMutexName = @"Global\MyRemote-RepairInstance-5D1C-9A3E";

        public static IReadOnlyList<string> RepairableIds => ItemNames.Keys.ToList();
        public static string ItemName(string id) => ItemNames.TryGetValue(id, out var n) ? n : id;

        // ================================================================
        //  对外入口：自动判断是否提权
        // ================================================================
        public static async Task<RepairResult> RequestRepairAsync(IEnumerable<string> repairIds)
        {
            var ids = repairIds.Distinct().Where(x => ItemNames.ContainsKey(x)).ToList();
            if (ids.Count == 0)
                return new RepairResult { Success = true, Message = "没有需要修复的项。" };

            if (SystemLog.IsAdministrator())
            {
                return await Task.Run(() => ExecuteRepair(ids, null));
            }

            var resultPath = Path.Combine(AppPaths.RollbackDir,
                "repair_result_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json");
            var args = $"--repair --items \"{string.Join(",", ids)}\" --result \"{resultPath}\"";
            var spawned = await SpawnElevatedAsync(args);
            if (spawned.UserCancelled) return spawned;
            var r = ReadResultFile(resultPath);
            r.UserCancelled = spawned.UserCancelled;
            return r;
        }

        public static async Task<RepairResult> RequestRestoreAsync(string snapshotPath)
        {
            if (string.IsNullOrEmpty(snapshotPath) || !File.Exists(snapshotPath))
                return new RepairResult { Success = false, Message = "未找到可恢复的快照文件。" };

            if (SystemLog.IsAdministrator())
            {
                return await Task.Run(() => ExecuteRestore(snapshotPath, null));
            }

            var resultPath = Path.Combine(AppPaths.RollbackDir,
                "restore_result_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json");
            var args = $"--restore --snapshot \"{snapshotPath}\" --result \"{resultPath}\"";
            var spawned = await SpawnElevatedAsync(args);
            if (spawned.UserCancelled) return spawned;
            var r = ReadResultFile(resultPath);
            r.UserCancelled = spawned.UserCancelled;
            return r;
        }

        // ================================================================
        //  实际执行（被高权限子进程或被提权后的主进程调用）
        // ================================================================
        public static RepairResult ExecuteRepair(List<string> ids, string? resultPath)
        {
            var result = new RepairResult();
            try
            {
                SystemLog.Info("Repair", "开始一键修复，目标项：" + string.Join(", ", ids));
                var snap = CaptureSnapshot();
                SaveSnapshot(snap);
                SystemLog.Info("Repair", "已创建修复前快照：" + AppPaths.LastSnapshotPath);

                foreach (var id in ids)
                {
                    var ir = FixItem(id, snap);
                    result.Items.Add(ir);
                    SystemLog.Info("Repair", $"[{id}] => {ir.Status}：{ir.Message}");
                }
                result.Success = result.Items.All(x => x.Status == "fixed" || x.Status == "skipped");
                result.Message = result.Success
                    ? "修复完成。建议重新运行「环境诊断」确认全部通过。"
                    : "部分项修复失败，请查看系统日志或手动处理。";
            }
            catch (Exception ex)
            {
                SystemLog.Exception("Repair", ex);
                result.Success = false;
                result.Message = "修复过程中发生异常：" + ex.Message;
            }
            if (resultPath != null) WriteResultFile(resultPath, result);
            return result;
        }

        public static RepairResult ExecuteRestore(string snapshotPath, string? resultPath)
        {
            var result = new RepairResult();
            try
            {
                var snap = LoadSnapshot(snapshotPath);
                var invalid = ExplainInvalidSnapshot(snap);
                if (invalid != null)
                {
                    // 【重要防护】绝不拿"残缺/空的快照"去改系统。
                    // 历史上快照曾因序列化缺陷落盘为空对象 {}，
                    // 若照此恢复会把 RdpDeny=1、服务改为手动/停止、防火墙重新放开，
                    // 在远程场景下会直接切断连接（自己把自己踢下线）。
                    result.Success = false;
                    result.Message = "快照不可用：" + invalid + "。已中止恢复，未改动任何系统设置。";
                    SystemLog.Warn("Repair", $"拒绝恢复（快照无效）：{snapshotPath} —— {invalid}");
                }
                else
                {
                    SystemLog.Info("Repair", "开始一键恢复，回退到快照：" + snapshotPath);
                    RestoreAll(snap!);
                    result.Success = true;
                    result.Message = "已恢复到修复前的状态。建议重新运行「环境诊断」确认。";
                }
            }
            catch (Exception ex)
            {
                SystemLog.Exception("Repair", ex);
                result.Success = false;
                result.Message = "恢复过程中发生异常：" + ex.Message;
            }
            if (resultPath != null) WriteResultFile(resultPath, result);
            return result;
        }

        /// <summary>
        /// 校验快照是否可信；不可信时返回原因（null 表示可用）。
        ///
        /// 判定依据：一份由 CaptureSnapshot() 正常产生的快照必然带有捕获时间与数据内容
        /// （Time 非默认值、且至少有一项真实读数）。而空对象 {} 反序列化后
        /// Time 为 DateTime.MinValue、AppVersion 为空 —— 用它恢复等于把系统
        /// 按"默认值"重写（关远程桌面、停服务），风险极高，必须拒绝。
        /// </summary>
        private static string? ExplainInvalidSnapshot(RepairSnapshot? snap)
        {
            if (snap == null) return "文件无法解析为本程序的快照格式";
            if (snap.Time == default) return "缺少捕获时间（文件可能是空对象或残缺的旧格式）";
            if (string.IsNullOrWhiteSpace(snap.AppVersion)) return "缺少版本信息";
            // NicPowerJson 正常时至少是 "{}"，若为 null 说明字段整体缺失
            if (snap.NicPowerJson == null) return "缺少网卡电源设置字段";
            return null;
        }

        // ================================================================
        //  快照列表（供 UI 选择恢复到哪一次）
        // ================================================================
        public static List<SnapshotInfo> ListSnapshots()
        {
            var list = new List<SnapshotInfo>();
            try
            {
                foreach (var f in Directory.GetFiles(AppPaths.RollbackDir, "snapshot_*.json")
                             .OrderByDescending(x => x))
                {
                    list.Add(new SnapshotInfo { Path = f, Time = File.GetCreationTime(f) });
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 仅创建还原点：捕获当前系统状态并落盘为快照，不做任何修改。
        /// 用途：① 用户想在手动改设置前先存一个可回退点；
        ///       ② 自检/探针在不触碰系统的前提下验证快照链路（落盘格式与可读性）。
        /// 失败时由 SystemLog 记录原因（如无权限读取某些注册表项）。
        /// </summary>
        /// <returns>生成的快照文件路径；失败返回 null。</returns>
        public static string? CreateRestorePointOnly()
        {
            try
            {
                var snap = CaptureSnapshot();
                SaveSnapshot(snap);
                var path = AppPaths.LastSnapshotPath;
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                SystemLog.Exception("Repair", ex);
                return null;
            }
        }

        // ================================================================
        //  内部：逐项修复
        // ================================================================
        private static ItemResult FixItem(string id, RepairSnapshot snap)
        {
            var ir = new ItemResult { Id = id, Name = ItemName(id) };
            try
            {
                switch (id)
                {
                    case "RdpEnabled":
                        SetRegDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 0);
                        // 仅改注册表不够：服务若被停用，端口不会监听，因此同时确保 TermService 运行
                        SetService("TermService", "auto", true);
                        ir.Status = "fixed"; ir.Message = "已允许入站远程桌面连接，并确保 TermService 运行。";
                        break;
                    case "TermService":
                        SetService("TermService", "auto", true);
                        ir.Status = "fixed"; ir.Message = "已将 TermService 设为自动并启动。";
                        break;
                    case "Hibernate":
                        RunCmd("powercfg", "/hibernate off", out _);
                        ir.Status = "fixed"; ir.Message = "已关闭休眠。";
                        break;
                    case "NicPower":
                    {
                        // 优先 PowerShell cmdlet（部分系统可用）；不可用时回退到直接改注册表。
                        var s = RunPsFile("Get-NetAdapterPowerManagement | Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice $false");
                        var psOk = s.exit == 0;
                        var regChanged = SetNicPowerViaRegistry(false);

                        ir.Status = (psOk || regChanged > 0) ? "fixed" : "failed";
                        ir.Message = psOk
                            ? "已对所有网卡关闭“允许计算机关闭此设备”（PowerShell 通道）。"
                            : (regChanged > 0
                                ? $"已通过注册表关闭 {regChanged} 个网卡的节能设置（部分系统 cmdlet 不可用，已自动切换通道）。"
                                : "未找到可修改的网卡电源设置（网卡驱动可能不支持该项，可在设备管理器手动确认）。");
                        break;
                    }
                    case "LanmanServer":
                        SetService("LanmanServer", "auto", true);
                        ir.Status = "fixed"; ir.Message = "已将 LanmanServer 设为自动并启动。";
                        break;
                    case "FirewallRdp":
                        FixFirewall();
                        ir.Status = "fixed";
                        ir.Message = "已禁用系统默认“远程桌面/文件共享”规则组，并仅以 Tailscale 网段(100.64.0.0/10)放行 3389/445。";
                        break;
                    case "AutoStart":
                        SetRunKey("MyRemote", Application.ExecutablePath);
                        ir.Status = "fixed"; ir.Message = "已配置开机自启（注册表 Run 项）。";
                        break;
                    case "TailscaleInstalled":
                    {
                        var (ok, msg) = PackageService.InstallTailscale();
                        ir.Status = ok ? "fixed" : "failed";
                        ir.Message = msg;
                        break;
                    }
                    case "RdpClientCtrl":
                    {
                        var dll = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System), "mstscax.dll");
                        if (!File.Exists(dll))
                        {
                            ir.Status = "failed";
                            ir.Message = "系统中不存在 mstscax.dll，无法注册。本机可能为被裁剪的系统，请改用系统自带 mstsc.exe 连接。";
                            break;
                        }

                        // 执行注册（regsvr32 /s 静默注册），随后清缓存复检
                        RunPsFile($"regsvr32.exe /s \"{dll}\"");
                        SystemLog.Info("Repair", "已执行 regsvr32 注册 mstscax.dll");
                        RdpClientDetector.Reset();
                        Thread.Sleep(800);

                        var okNow = RdpClientDetector.IsAvailable;
                        ir.Status = okNow ? "fixed" : "failed";
                        ir.Message = okNow
                            ? $"已注册 RDP 客户端控件（可用 CLSID：{RdpClientDetector.DetectedClsid}）。"
                            : "已尝试重新注册 mstscax.dll，但仍未探测到可用控件。请运行系统修复：sfc /scannow 与 DISM /Online /Cleanup-Image /RestoreHealth，或改用系统自带 mstsc.exe 连接。";
                        break;
                    }
                    default:
                        ir.Status = "skipped"; ir.Message = "该检测项不支持自动修复，请按指引手动处理。";
                        break;
                }
            }
            catch (Exception ex)
            {
                ir.Status = "failed"; ir.Message = ex.Message;
            }
            return ir;
        }

        private static void FixFirewall()
        {
            // 【实机约束】本机（Win10 IoT LTSC + Windows PowerShell 5.1）下
            // Get-NetFirewallRule 返回 0 条规则（cmdlet 在该环境不可用），
            // 因此修复必须走 netsh —— 它对所有系统都可用，且已在诊断侧验证过可靠性。
            //
            // 做法：按"规则名称"逐条禁用（而不是按组名）：
            // 组名在不同系统语言下名称不同，且部分内置规则的组名与显示名不一致；
            // 而"规则名称"是跨语言稳定的标识（netsh 输出的"规则名称"行）。
            DisableRdpSmbRulesByName();

            // 删除可能重复存在的自定义规则后重建（保证作用域正确）
            RunCmd("netsh", "advfirewall firewall delete rule name=\"MyRemote-RDP-Tailscale\"", out _);
            RunCmd("netsh", "advfirewall firewall delete rule name=\"MyRemote-SMB-Tailscale\"", out _);
            RunCmd("netsh", "advfirewall firewall add rule name=\"MyRemote-RDP-Tailscale\" dir=in action=allow protocol=TCP localport=3389 remoteip=100.64.0.0/10", out _);
            RunCmd("netsh", "advfirewall firewall add rule name=\"MyRemote-SMB-Tailscale\" dir=in action=allow protocol=TCP localport=445 remoteip=100.64.0.0/10", out _);
        }

        /// <summary>
        /// 逐条禁用「入站 + 允许 + 本地端口 3389/445」的系统规则（排除本工具自己的规则）。
        /// 通过解析 netsh 转储找出规则名称，再按名称精确禁用 ——
        /// 名称是跨语言稳定的，避免依赖本地化的组名。
        /// </summary>
        private static void DisableRdpSmbRulesByName()
        {
            var dump = RunCmd("netsh", "advfirewall firewall show rule name=all dir=in", out _);
            if (string.IsNullOrWhiteSpace(dump))
            {
                SystemLog.Warn("Repair", "无法读取防火墙规则转储，改用组名方式兜底。");
                // 兜底：按组名（中文/英文各试一次）
                foreach (var g in new[] { "远程桌面", "remote desktop", "文件和打印机共享", "file and printer sharing" })
                    RunCmd("netsh", $"advfirewall firewall set rule group=\"{g}\" new enable=no", out _);
                return;
            }

            var disabled = 0;
            foreach (var block in SplitBlocks(dump))
            {
                // 只处理：已启用 + 允许 + 涉及 3389/445 + 非本工具规则
                if (!BlockEnabledText(block)) continue;
                if (!BlockAllowsText(block)) continue;
                if (!BlockHasPort(block)) continue;
                var name = FieldOf(block, "规则名称", "Rule Name");
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("MyRemote-", StringComparison.OrdinalIgnoreCase)) continue;

                var (_, _) = (0, "");
                RunCmd("netsh", $"advfirewall firewall set rule name=\"{name}\" new enable=no", out _);
                disabled++;
                SystemLog.Info("Repair", $"已禁用规则：{name}");
            }
            SystemLog.Info("Repair", $"共禁用 {disabled} 条 3389/445 系统规则。");
        }

        private static List<string> SplitBlocks(string dump)
        {
            var blocks = new List<string>();
            var sb = new StringBuilder();
            foreach (var raw in dump.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("规则名称", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Rule Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) { blocks.Add(sb.ToString()); sb.Clear(); }
                }
                sb.AppendLine(line);
            }
            if (sb.Length > 0) blocks.Add(sb.ToString());
            return blocks;
        }

        private static string? FieldOf(string block, params string[] prefixes)
        {
            foreach (var raw in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                foreach (var p in prefixes)
                {
                    if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = line.IndexOf(':', StringComparison.Ordinal);
                        if (idx < 0) continue;
                        return line.Substring(idx + 1).Trim().Replace("\u3000", "").Trim();
                    }
                }
            }
            return null;
        }

        private static bool BlockHasPort(string block)
        {
            // 本地端口行含 3389 或 445
            var port = FieldOf(block, "本地端口", "LocalPort") ?? "";
            return port.Contains("3389") || port.Contains("445")
                || block.Contains("3389") || block.Contains("445");
        }

        // ================================================================
        //  内部：恢复（把快照里的每一项还原回去）
        // ================================================================
        private static void RestoreAll(RepairSnapshot s)
        {
            // 防火墙恢复：与 FixFirewall 对称，走 netsh 按规则名逐条还原。
            // 1) 删除本工具创建的规则
            RunCmd("netsh", "advfirewall firewall delete rule name=\"MyRemote-RDP-Tailscale\"", out _);
            RunCmd("netsh", "advfirewall firewall delete rule name=\"MyRemote-SMB-Tailscale\"", out _);
            // 2) 按快照恢复系统规则（按规则名逐条还原启用状态）
            RestoreRdpSmbRules(s.DefFwRulesWasEnabled);
            SystemLog.Info("Repair", $"恢复防火墙：目标启用状态={s.DefFwRulesWasEnabled}");

            // RDP 被控端
            SetRegDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", s.RdpDeny);
            SystemLog.Info("Repair", $"恢复 fDenyTSConnections={s.RdpDeny}");

            // 服务（TermService / LanmanServer）
            RestoreService("TermService", s.TermStart, s.TermRunning);
            RestoreService("LanmanServer", s.LanmanStart, s.LanmanRunning);

            // 休眠
            if (s.HibernateEnabled) RunCmd("powercfg", "/hibernate on", out _);
            SystemLog.Info("Repair", "恢复休眠启用=" + s.HibernateEnabled);

            // 网卡节能
            RestoreNicPower(s.NicPowerJson);

            // 开机自启
            if (!s.AutoStartExisted)
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); k?.DeleteValue("MyRemote", false); }
                catch (Exception ex) { SystemLog.Exception("Repair", ex); }
            }
            else
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); k?.SetValue("MyRemote", s.AutoStartValue); }
                catch (Exception ex) { SystemLog.Exception("Repair", ex); }
            }
            SystemLog.Info("Repair", "恢复开机自启 existed=" + s.AutoStartExisted);
        }

        /// <summary>
        /// 按快照恢复「3389/445 入站系统规则」的启用状态（按规则名逐条操作，跨语言稳定）。
        /// </summary>
        private static void RestoreRdpSmbRules(bool enabled)
        {
            var dump = RunCmd("netsh", "advfirewall firewall show rule name=all dir=in", out _);
            if (string.IsNullOrWhiteSpace(dump))
            {
                SystemLog.Warn("Repair", "恢复时无法读取防火墙规则转储，改用组名兜底。");
                var val = enabled ? "yes" : "no";
                foreach (var g in new[] { "远程桌面", "remote desktop", "文件和打印机共享", "file and printer sharing" })
                    RunCmd("netsh", $"advfirewall firewall set rule group=\"{g}\" new enable={val}", out _);
                return;
            }

            var restored = 0;
            foreach (var block in SplitBlocks(dump))
            {
                if (!BlockHasPort(block)) continue;
                var name = FieldOf(block, "规则名称", "Rule Name");
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("MyRemote-", StringComparison.OrdinalIgnoreCase)) continue;

                RunCmd("netsh", $"advfirewall firewall set rule name=\"{name}\" new enable={(enabled ? "yes" : "no")}", out _);
                restored++;
            }
            SystemLog.Info("Repair", $"已恢复 {restored} 条 3389/445 规则，启用状态={enabled}");
        }

        private static void RestoreService(string name, int startModeInt, bool wasRunning)
        {
            var scStart = startModeInt switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => "demand" };
            RunCmd("sc", $"config \"{name}\" start= {scStart}", out _);
            if (wasRunning) RunCmd("net", $"start \"{name}\"", out _);
            else RunCmd("net", $"stop \"{name}\"", out _);
            SystemLog.Info("Repair", $"恢复服务 {name}：启动类型={scStart}，原运行状态={wasRunning}");
        }

        /// <summary>
        /// 通过注册表关闭网卡节能（AllowComputerToTurnOffDevice=0）。
        /// 用于 PowerShell cmdlet 不可用的系统（如部分 LTSC / 精简系统）。
        /// 返回实际修改的适配器数量；0 表示未找到可修改项。
        /// </summary>
        private static int SetNicPowerViaRegistry(bool allow)
        {
            var changed = 0;
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}", true);
                if (cls == null) return 0;

                foreach (var sub in cls.GetSubKeyNames())
                {
                    if (sub.Length == 0 || !sub.All(char.IsDigit)) continue;
                    try
                    {
                        using var dev = cls.OpenSubKey(sub + @"\Device Parameters\PowerManagement", true);
                        if (dev == null) continue;
                        var v = dev.GetValue("AllowComputerToTurnOffDevice");
                        if (v == null) continue;
                        dev.SetValue("AllowComputerToTurnOffDevice", allow ? 1 : 0, RegistryValueKind.DWord);
                        changed++;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
            return changed;
        }

        private static void RestoreNicPower(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (map == null || map.Count == 0) return;
                var sb = new StringBuilder();
                sb.AppendLine("$map = @{");
                foreach (var kv in map)
                    sb.AppendLine($"  '{kv.Key.Replace("'", "''")}' = {kv.Value}");
                sb.AppendLine("}");
                sb.AppendLine("foreach ($k in $map.Keys) { try { Set-NetAdapterPowerManagement -Name $k -AllowComputerToTurnOffDevice ($map[$k] -eq 1) } catch { Write-Warning \"$k 恢复失败\" } }");
                RunPsFile(sb.ToString());
            }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
        }

        // ================================================================
        //  快照
        // ================================================================
        private static RepairSnapshot CaptureSnapshot()
        {
            var snap = new RepairSnapshot { Time = DateTime.Now, AppVersion = Application.ProductVersion ?? "1.5" };
            try
            {
                snap.RdpDeny = GetRegDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1);
                snap.TermStart = (int)GetServiceStartMode("TermService");
                snap.TermRunning = IsServiceRunning("TermService");
                snap.LanmanStart = (int)GetServiceStartMode("LanmanServer");
                snap.LanmanRunning = IsServiceRunning("LanmanServer");
                snap.HibernateEnabled = HibernateEnabled();
                snap.NicPowerJson = CaptureNicPower();
                snap.DefRdpGroupEnabled = DefaultGroupEnabled("远程桌面");
                snap.DefSmbGroupEnabled = DefaultGroupEnabled("文件和打印机共享");
                snap.DefFwRulesWasEnabled = AnyPortRuleEnabled(3389) || AnyPortRuleEnabled(445);
                snap.AutoStartExisted = RunKeyExists("MyRemote");
                snap.AutoStartValue = GetRunKeyValue("MyRemote") ?? "";
            }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
            return snap;
        }

        private static void SaveSnapshot(RepairSnapshot snap)
        {
            try
            {
                var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
                var stamp = Path.Combine(AppPaths.RollbackDir,
                    "snapshot_" + snap.Time.ToString("yyyyMMddHHmmss") + ".json");
                File.WriteAllText(stamp, json);
                File.WriteAllText(AppPaths.LastSnapshotPath, json);
                SystemLog.Info("Repair", "快照已保存：" + stamp);
            }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
        }

        private static RepairSnapshot? LoadSnapshot(string path)
        {
            try { return JsonSerializer.Deserialize<RepairSnapshot>(File.ReadAllText(path)); }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); return null; }
        }

        // ================================================================
        //  提权子进程
        // ================================================================
        private static async Task<RepairResult> SpawnElevatedAsync(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                    return new RepairResult { Success = false, Message = "无法启动提权进程。" };
                // UAC 拒绝时进程会很快退出，ExitCode 通常为 1223(ERROR_CANCELLED)
                await p.WaitForExitAsync();
                if (p.ExitCode == 1223)
                    return new RepairResult { UserCancelled = true, Message = "已取消（未授予管理员权限）。" };
                return new RepairResult { Success = true };
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // 用户在 UAC 弹窗上点了“否”
                return new RepairResult { UserCancelled = true, Message = "已取消（未授予管理员权限）。" };
            }
            catch (Exception ex)
            {
                // 用户取消时部分系统也会以“操作已被用户取消”形式抛出，统一按取消处理
                if (ex.Message != null && ex.Message.Contains("取消"))
                    return new RepairResult { UserCancelled = true, Message = "已取消（未授予管理员权限）。" };
                SystemLog.Exception("Repair", ex);
                return new RepairResult { Success = false, Message = "启动提权进程失败：" + ex.Message };
            }
        }

        private static RepairResult ReadResultFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new RepairResult { Success = false, Message = "未生成结果文件（修复进程可能异常退出，请查看系统日志）。" };
                var r = JsonSerializer.Deserialize<RepairResult>(File.ReadAllText(path));
                return r ?? new RepairResult { Success = false, Message = "结果文件解析失败。" };
            }
            catch (Exception ex)
            {
                return new RepairResult { Success = false, Message = "读取结果失败：" + ex.Message };
            }
        }

        private static void WriteResultFile(string path, RepairResult r)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
        }

        // ================================================================
        //  Windows 操作原语
        // ================================================================
        private static string RunCmd(string exe, string args, out int exit)
        {
            // 编码同 DiagnosticsService：Windows 自带命令在中文系统上以 GBK 输出，
            // 不显式指定会把中文解成乱码，导致依赖中文关键字的解析（防火墙规则组等）全部失效。
            // 采用"双通道解码"：先用 OEM 页，出现乱码特征则改用 ANSI 页重取。
            var first = RunCmdEncoded(exe, args, out exit, useOem: true);
            if (LooksGarbled(first))
            {
                var second = RunCmdEncoded(exe, args, out exit, useOem: false);
                if (!LooksGarbled(second) || second.Length > first.Length) return second;
            }
            return first;
        }

        private static string RunCmdEncoded(string exe, string args, out int exit, bool useOem)
        {
            try
            {
                Encoding enc;
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    var cp = useOem ? GetOEMCP() : GetACP();
                    enc = Encoding.GetEncoding(cp > 0 ? (int)cp : 936);
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
                if (p == null) { exit = -1; return ""; }
                // 同时异步读取两条流，避免任一管道写满造成死锁
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                exit = p.HasExited ? p.ExitCode : -1;
                SystemLog.Info("Cmd", $"{exe} {args} => exit {exit}");
                return (outTask.Result ?? "") + (errTask.Result ?? "");
            }
            catch (Exception ex) { exit = -1; SystemLog.Exception("Cmd", ex); return ""; }
        }

        private static bool LooksGarbled(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Contains("已启用") || s.Contains("规则名称") || s.Contains("操作")
                || s.Contains("状态"))
                return false;
            foreach (var ch in s)
                if (ch >= '\u00C0' && ch <= '\u00FF') return true;
            return false;
        }

        /// <summary>
        /// 系统 ANSI 代码页编码（中文系统 GBK/936），用于正确解码 netsh/sc 等命令输出。
        ///
        /// 【重要】不能靠 CultureInfo 推断：本项目启用 InvariantGlobalization=true，
        /// 此时 CurrentCulture 为空、OEMCodePage 会返回 437，在中文系统上导致中文乱码。
        /// 必须直接调用 Windows API（GetACP/GetOEMCP）询问系统实际代码页。
        /// </summary>
        private static Encoding AnsiEncoding
        {
            get
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(GetCodePage());
                }
                catch
                {
                    try { return Encoding.Default; }
                    catch { return Encoding.UTF8; }
                }
            }
        }

        private static int GetCodePage()
        {
            try
            {
                var oem = GetOEMCP();
                if (oem > 0) return (int)oem;
            }
            catch { }
            try
            {
                var acp = GetACP();
                if (acp > 0) return (int)acp;
            }
            catch { }
            return 936;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetACP();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static (int exit, string output) RunPsFile(string script)
        {
            // 改用 -EncodedCommand（Base64/UTF-16LE）执行：
            // 原先写临时 .ps1 再 -File 执行的方式，在中文系统上受代码页/执行策略影响较大
            // （UTF-8 无 BOM 会被按 ANSI 解析、中文网卡名乱码导致命令失败），
            // EncodedCommand 完全绕过文件编码与转义问题，是 PowerShell 最稳的传参方式。
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
                if (p == null) return (-1, "");
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                var exit = p.HasExited ? p.ExitCode : -1;
                SystemLog.Info("PS", "脚本执行 => exit " + exit);
                return (exit, (outTask.Result ?? "") + (errTask.Result ?? ""));
            }
            catch (Exception ex) { SystemLog.Exception("PS", ex); return (-1, ex.Message); }
        }

        private static int GetRegDword(string subkey, string name, int def)
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(subkey); var v = k?.GetValue(name); return v == null ? def : Convert.ToInt32(v); }
            catch { return def; }
        }

        private static void SetRegDword(string subkey, string name, int value)
        {
            using var k = Registry.LocalMachine.CreateSubKey(subkey);
            k?.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static void SetService(string name, string scStart, bool start)
        {
            RunCmd("sc", $"config \"{name}\" start= {scStart}", out _);
            if (start) RunCmd("net", $"start \"{name}\"", out _);
        }

        private static int GetServiceStartMode(string name)
        {
            var o = RunCmd("sc", $"qc \"{name}\"", out _);
            var idx = o.IndexOf("START_TYPE", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 3;
            var rest = o.Substring(idx);
            if (rest.IndexOf("AUTO_START", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (rest.IndexOf("DEMAND_START", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (rest.IndexOf("DISABLED", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (rest.IndexOf("BOOT_START", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (rest.IndexOf("SYSTEM_START", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 3;
        }

        private static bool IsServiceRunning(string name)
        {
            var o = RunCmd("sc", $"query \"{name}\"", out _);
            var idx = o.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var rest = o.Substring(idx);
            return rest.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HibernateEnabled()
        {
            // 直接读注册表，避免 powercfg 输出随系统语言变化而误判（中文系统输出为“休眠已启用”）
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power");
                var v = k?.GetValue("HibernateEnabled");
                return v != null && Convert.ToInt32(v) != 0;
            }
            catch { return false; }
        }

        private static string CaptureNicPower()
        {
            // 用哈希表汇总为 JSON 对象（键=适配器名）。这样无论 1 个还是多个适配器，
            // 输出都稳定的对象结构，不会被管道展开成单个对象而解析失败。
            var script = "$h=@{}; Get-NetAdapterPowerManagement | ForEach-Object { " +
                         "$h[$_.Name] = [int]($_.AllowComputerToTurnOffDevice -eq $true) }; " +
                         "$h | ConvertTo-Json -Compress";
            var (exit, output) = RunPsFile(script);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return "{}";
            var trimmed = output.Trim();
            try { JsonSerializer.Deserialize<Dictionary<string, int>>(trimmed); return trimmed; }
            catch { return "{}"; }
        }

        private static bool DefaultGroupEnabled(string group)
        {
            var o = RunCmd("netsh", $"advfirewall firewall show rule group=\"{group}\" dir=in", out _);
            return BlockEnabledTextIn(o);
        }

        /// <summary>
        /// 快照用：是否存在「已启用 + 允许 + 涉及 3389/445」的入站规则。
        /// 走 netsh 解析：本机环境下 Get-NetFirewallRule 不可用（返回 0 条），
        /// netsh 是唯一可靠的通道。
        /// </summary>
        private static bool AnyPortRuleEnabled(int port)
        {
            var dump = RunCmd("netsh", "advfirewall firewall show rule name=all dir=in", out _);
            if (string.IsNullOrWhiteSpace(dump)) return false;
            foreach (var block in SplitBlocks(dump))
            {
                if (!BlockEnabledText(block)) continue;
                if (!BlockAllowsText(block)) continue;
                if (!BlockHasPort(block)) continue;
                var name = FieldOf(block, "规则名称", "Rule Name");
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("MyRemote-", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        /// <summary>在 netsh 规则转储中判断是否存在任一「已启用」的规则块。</summary>
        private static bool BlockEnabledTextIn(string o)
        {
            if (string.IsNullOrWhiteSpace(o)) return false;
            var sb = new StringBuilder();
            foreach (var raw in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("规则名称", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Rule Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0 && BlockEnabledText(sb.ToString())) return true;
                    sb.Clear();
                }
                sb.AppendLine(line);
            }
            return sb.Length > 0 && BlockEnabledText(sb.ToString());
        }

        private static bool BlockEnabledText(string block)
        {
            foreach (var raw in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (!line.StartsWith("已启用", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Contains("是") || line.Contains("Yes", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>规则块的动作是否为「允许」。</summary>
        private static bool BlockAllowsText(string block)
        {
            foreach (var raw in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (!line.StartsWith("操作", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("Action", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Contains("允许") || line.Contains("Allow", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool RunKeyExists(string name)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue(name) != null; }
            catch { return false; }
        }

        private static string? GetRunKeyValue(string name)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue(name) as string; }
            catch { return null; }
        }

        private static void SetRunKey(string name, string value)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); k?.SetValue(name, value); }
            catch (Exception ex) { SystemLog.Exception("Repair", ex); }
        }

        private static string Trim(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) + "…" : s);

        // ================================================================
        //  快照数据模型
        // ================================================================
        /// <summary>
        /// 修复前快照（「一键恢复」的数据源）。
        ///
        /// 【重要】必须使用属性（get;set;）而不是公开字段 ——
        /// System.Text.Json 默认只序列化属性，用字段会导致落盘文件变成空对象 {}，
        /// 使「一键恢复」读回的全是默认值、形同失效（该缺陷已由全功能自检实测捕获）。
        /// </summary>
        public class RepairSnapshot
        {
            public DateTime Time { get; set; }
            public string AppVersion { get; set; } = "";
            /// <summary>fDenyTSConnections 捕获值</summary>
            public int RdpDeny { get; set; } = 1;
            /// <summary>0 Boot/1 System/2 Auto/3 Manual/4 Disabled</summary>
            public int TermStart { get; set; } = 3;
            public bool TermRunning { get; set; }
            public int LanmanStart { get; set; } = 3;
            public bool LanmanRunning { get; set; }
            public bool HibernateEnabled { get; set; }
            /// <summary>适配器名 -&gt; 是否允许关闭(1/0)</summary>
            public string NicPowerJson { get; set; } = "{}";
            public bool DefRdpGroupEnabled { get; set; }
            public bool DefSmbGroupEnabled { get; set; }
            /// <summary>修复前是否存在已启用的 3389/445 入站放行规则（用于一键恢复）。</summary>
            public bool DefFwRulesWasEnabled { get; set; }
            public bool AutoStartExisted { get; set; }
            public string AutoStartValue { get; set; } = "";
        }
    }
}

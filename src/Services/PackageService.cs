using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyRemote.Services
{
    /// <summary>
    /// 随软件分发的第三方安装包管理：定位内置安装包并静默安装。
    ///
    /// 目录约定（两种都支持，按优先级）：
    /// 1. 程序目录下的 packages 子目录：MyRemote.exe 同级的 packages\tailscale-setup-*.exe
    /// 2. 程序目录同级：MyRemote.exe 同级的 tailscale-setup-*.exe
    ///
    /// 说明：安装动作需要管理员权限（由上层 RepairService 统一提权），
    /// 安装是否成功以实际探测（<see cref="TailscaleCli.IsInstalled"/>）为准，而不是安装程序退出码。
    /// </summary>
    public static class PackageService
    {
        /// <summary>程序所在目录（跟随实际 exe 路径，提权子进程同样适用）。</summary>
        public static string ExeDir
        {
            get
            {
                try
                {
                    var p = Process.GetCurrentProcess().MainModule?.FileName;
                    return string.IsNullOrEmpty(p) ? "." : (Path.GetDirectoryName(p) ?? ".");
                }
                catch { return "."; }
            }
        }

        /// <summary>是否存在内置的 Tailscale 安装包。</summary>
        public static bool HasTailscaleInstaller => FindTailscaleInstaller() != null;

        /// <summary>查找内置的 Tailscale 安装包；未找到返回 null。</summary>
        public static string? FindTailscaleInstaller() => FindByPrefix("tailscale-setup");

        private static string? FindByPrefix(string prefix)
        {
            try
            {
                foreach (var dir in new[] { Path.Combine(ExeDir, "packages"), ExeDir })
                {
                    if (!Directory.Exists(dir)) continue;
                    var hit = Directory.GetFiles(dir, prefix + "*.exe")
                        .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit)) return hit;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 安装 Tailscale（使用内置安装包，静默安装）。
        /// 先按官方推荐参数 /install /quiet /norestart，无效再试 /S（部分版本使用 NSIS 风格参数）；
        /// 最终以实际探测结果为准。
        /// </summary>
        public static (bool ok, string message) InstallTailscale()
        {
            var installer = FindTailscaleInstaller();
            if (installer == null)
                return (false, "未找到内置的 Tailscale 安装包。请把 tailscale-setup-*.exe 放到程序目录（或其 packages 子目录）后重试。");

            if (TailscaleCli.IsInstalled)
                return (true, "Tailscale 已安装，无需重复安装。");

            SystemLog.Info("Package", "开始安装 Tailscale：" + installer);

            var attempts = new[] { "/install /quiet /norestart", "/S" };
            var lastOutput = "";
            foreach (var args in attempts)
            {
                var (exit, output) = RunInstaller(installer, args, 600000);
                lastOutput = $"exit={exit} {Trim(output, 300)}";
                SystemLog.Info("Package", $"安装尝试 [{args}] => {lastOutput}");

                // 安装程序返回后，文件/服务可能还需片刻才就绪，轮询确认
                for (int i = 0; i < 20; i++)
                {
                    if (TailscaleCli.IsInstalled) break;
                    Thread.Sleep(1500);
                }
                if (TailscaleCli.IsInstalled)
                    return (true, "Tailscale 已安装完成。请点击任务栏 Tailscale 图标，用与其它设备相同的账号登录。");
            }

            return (false, "安装未成功（" + lastOutput + "）。可手动双击安装包：" + installer);
        }

        private static (int exit, string output) RunInstaller(string exePath, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "无法启动安装程序");
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(outTask, errTask); } catch { }
                var exit = p.HasExited ? p.ExitCode : -1;
                return (exit, (outTask.Result ?? "") + (errTask.Result ?? ""));
            }
            catch (Exception ex)
            {
                SystemLog.Exception("Package", ex);
                return (-1, ex.Message);
            }
        }

        private static string Trim(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) + "…" : s);

        /// <summary>
        /// 打开 Tailscale 登录界面（拉起托盘 GUI，由用户完成账号登录）。
        /// 登录动作涉及用户在浏览器中的账号验证，无法全自动完成，因此这里只负责把界面带到用户面前。
        /// </summary>
        public static (bool ok, string message) OpenTailscaleLogin()
        {
            if (!TailscaleCli.IsInstalled)
                return (false, "尚未安装 Tailscale。请先点「一键修复」安装，或手动安装内置安装包。");

            try
            {
                // tailscale-ipn.exe 是托盘 GUI：启动后用户可在其中点击 Log in 完成登录
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var ipn = Path.Combine(pf, "Tailscale", "tailscale-ipn.exe");
                var target = File.Exists(ipn) ? ipn : TailscaleCli.ExePath;

                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true
                });
                SystemLog.Info("Package", "已拉起 Tailscale 登录界面：" + target);
                return (true, "已打开 Tailscale 界面。请点击 Log in 并用与其它设备相同的账号登录。");
            }
            catch (Exception ex)
            {
                SystemLog.Exception("Package", ex);
                return (false, "打开 Tailscale 界面失败：" + ex.Message);
            }
        }
    }
}
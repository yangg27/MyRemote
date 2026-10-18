using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MyRemote.Services
{
    /// <summary>
    /// Tailscale CLI 统一入口：负责定位 tailscale.exe 并可靠地执行命令。
    ///
    /// 背景（实机踩坑）：
    /// 1) 安装路径会随版本变化——旧版在 "Tailscale IPN" 目录，1.104+ 改成了 "Tailscale"；
    /// 2) 升级后 PATH 里可能残留旧路径，直接调用 "tailscale.exe" 会静默失败（无输出、无报错），
    ///    表现为「已登录但读不到设备列表」。
    /// 因此这里统一按候选路径探测可执行文件，命中即用，避免依赖 PATH。
    /// </summary>
    public static class TailscaleCli
    {
        private static string? _cachedPath;

        /// <summary>tailscale.exe 的可用路径；找不到时退回裸名（若 PATH 有效仍可用）。</summary>
        public static string ExePath
        {
            get
            {
                if (_cachedPath != null && SafeExists(_cachedPath)) return _cachedPath;
                _cachedPath = FindExe();
                return _cachedPath;
            }
        }

        /// <summary>是否已安装（文件存在，或能正常执行 --version）。</summary>
        public static bool IsInstalled
        {
            get
            {
                try
                {
                    if (SafeExists(ExePath)) return true;
                    return !string.IsNullOrWhiteSpace(Run("--version", 5000));
                }
                catch { return false; }
            }
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }

        private static string FindExe()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var candidates = new[]
            {
                Path.Combine(pf, "Tailscale", "tailscale.exe"),      // 1.104+ 新路径
                Path.Combine(pf, "Tailscale IPN", "tailscale.exe"),  // 旧版路径
                Path.Combine(pf86, "Tailscale", "tailscale.exe"),
                Path.Combine(pf86, "Tailscale IPN", "tailscale.exe"),
                Path.Combine(lad, "Tailscale", "tailscale.exe"),
            };
            foreach (var c in candidates)
            {
                if (SafeExists(c)) return c;
            }
            return "tailscale.exe";
        }

        /// <summary>执行 tailscale 命令并返回标准输出；失败或超时返回空串（不抛异常）。</summary>
        public static string Run(string args, int timeoutMs = 8000)
        {
            try
            {
                var psi = new ProcessStartInfo(ExePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                // 同时异步读取 stdout/stderr，避免子进程写满 stderr 管道造成死锁
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
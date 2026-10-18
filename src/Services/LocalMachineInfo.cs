using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MyRemote.Services
{
    /// <summary>
    /// 读取本机与远程控制相关的环境信息，用于主界面的“本机信息”面板，
    /// 让首次使用的人不必自己跑去查 IP / 用户名 / 是否被控端是否已就绪。
    /// 所有读取都做 try/catch 兜底——任何一项取不到都不影响其它项与程序运行。
    /// </summary>
    public static class LocalMachineInfo
    {
        /// <summary>可用于远程登录的“域\用户”或“机器名\用户”形式。</summary>
        public static string UserName
        {
            get
            {
                try
                {
                    var user = Environment.UserName;
                    var domain = Environment.UserDomainName;
                    if (string.IsNullOrWhiteSpace(domain) ||
                        domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        return user;
                    return $"{domain}\\{user}";
                }
                catch { return Environment.UserName; }
            }
        }

        public static string MachineName => Environment.MachineName;

        /// <summary>本机是否已开启 Windows 远程桌面（TermService 允许入站）。</summary>
        public static bool IsRdpEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"System\CurrentControlSet\Control\Terminal Server");
                    if (key == null) return false;
                    var v = key.GetValue("fDenyTSConnections");
                    return v != null && Convert.ToInt32(v) == 0;
                }
                catch { return false; }
            }
        }

        /// <summary>本机在 Tailscale 虚拟网络中的 IPv4 地址；未安装/未登录则返回空串。</summary>
        public static string GetTailscaleIp()
        {
            try
            {
                var outp = TailscaleCli.Run("ip -4", 5000).Trim();
                var lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return lines.Length > 0 ? lines[0].Trim() : "";
            }
            catch { return ""; }
        }
    }
}

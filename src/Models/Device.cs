using System;

namespace MyRemote.Models
{
    /// <summary>
    /// 一台被管设备的配置。密码不在此对象里，单独由 CredentialVault 以 DPAPI 加密存储。
    /// </summary>
    public class Device
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>显示名，如“家里主力机”</summary>
        public string Name { get; set; } = "未命名设备";

        /// <summary>Tailscale 虚拟 IP（100.x.x.x）或机器名。也支持普通 IP。</summary>
        public string Host { get; set; } = "";

        /// <summary>用于远程登录的 Windows 用户名（不是 PIN）。</summary>
        public string UserName { get; set; } = "";

        /// <summary>RDP 端口，默认 3389。</summary>
        public int RdpPort { get; set; } = 3389;

        /// <summary>文件管理使用的 SMB 共享名，默认用管理员共享 C$。也可改为自定义共享名。</summary>
        public string ShareName { get; set; } = "C$";

        /// <summary>备注，可选。</summary>
        public string Notes { get; set; } = "";
    }
}

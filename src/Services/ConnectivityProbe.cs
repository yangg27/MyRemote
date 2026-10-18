using System.Net.Sockets;
using System.Threading.Tasks;

namespace MyRemote.Services
{
    /// <summary>
    /// 在线探测：直接尝试与目标的 RDP 端口建立 TCP 连接。
    /// 比 ping 更可靠——Tailscale 虚拟网卡下 TCP 3389 通即代表可被控端就绪。
    /// </summary>
    public static class ConnectivityProbe
    {
        public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 2000)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            using var client = new TcpClient();
            client.SendTimeout = timeoutMs;
            client.ReceiveTimeout = timeoutMs;
            try
            {
                var connectTask = client.ConnectAsync(host, port);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask)
                {
                    await connectTask;
                    return client.Connected;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}

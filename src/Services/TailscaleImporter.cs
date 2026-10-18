using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyRemote.Services
{
    /// <summary>
    /// 一台 Tailscale 网络中的对端机器（用于“从 Tailscale 导入”功能）。
    /// </summary>
    public class TailscalePeer
    {
        public string Hostname { get; set; } = "";
        public string Ip { get; set; } = "";
        public bool IsOnline { get; set; }
    }

    /// <summary>
    /// 读取同账号的其他机器，让用户在主界面一键勾选导入为设备，
    /// 免去手抄 100.x.x.x 的痛苦。
    ///
    /// 获取顺序（按可靠性）：
    /// 1. CLI JSON：tailscale.exe status --json（路径经 <see cref="TailscaleCli"/> 自动探测）
    /// 2. LocalAPI：http://127.0.0.1:41112/localapi/v0/status（CLI 异常时的替代通道）
    /// 3. CLI 文本：tailscale status（兜底，正则解析）
    ///
    /// Tailscale 未安装时返回空列表（不影响其它功能）。
    /// </summary>
    public static class TailscaleImporter
    {
        public static List<TailscalePeer> GetPeers()
        {
            try
            {
                // 1. CLI JSON（首选）
                var json = TailscaleCli.Run("status --json");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var cliPeers = ParseJson(json);
                    // JSON 合法即采纳（哪怕对端为空——说明确实没有其它设备）
                    if (LooksLikeValidStatus(json)) return FilterSelf(cliPeers);
                }

                // 2. LocalAPI 替代通道
                var apiPeers = TryGetPeersFromLocalApi();
                if (apiPeers != null) return FilterSelf(apiPeers);

                // 3. 文本兜底
                var text = TailscaleCli.Run("status");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var textPeers = ParseText(text);
                    if (textPeers.Count > 0) return FilterSelf(textPeers);
                }
            }
            catch { }
            return new List<TailscalePeer>();
        }

        /// <summary>
        /// JSON 是否能被正确解析成“有效的 status 响应”。
        /// 用于区分「确实没有对端」和「命令失败返回了垃圾内容」，
        /// 前者直接返回空列表即可，后者应继续尝试其它通道。
        /// </summary>
        private static bool LooksLikeValidStatus(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch { return false; }
        }

        /// <summary>
        /// 过滤掉本机节点（Self）。导入是为了连其他机器，不需要把自己加进设备列表。
        /// </summary>
        private static List<TailscalePeer> FilterSelf(List<TailscalePeer> peers)
        {
            try
            {
                var selfIps = TailscaleCli.Run("ip -4");
                if (!string.IsNullOrWhiteSpace(selfIps))
                {
                    var ownIps = selfIps.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(x => x.Trim())
                                        .Where(x => !string.IsNullOrEmpty(x))
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    peers = peers.Where(p => !ownIps.Contains(p.Ip.Trim())).ToList();
                }
            }
            catch { }
            return peers;
        }

        #region LocalAPI

        private static List<TailscalePeer>? TryGetPeersFromLocalApi()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var json = client.GetStringAsync("http://127.0.0.1:41112/localapi/v0/status").Result;
                if (string.IsNullOrWhiteSpace(json)) return null;
                return ParseJson(json);
            }
            catch { return null; }
        }

        #endregion

        #region JSON 解析

        private static List<TailscalePeer> ParseJson(string json)
        {
            var list = new List<TailscalePeer>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Peer", out var peers) &&
                    peers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in peers.EnumerateObject())
                    {
                        var peer = TryParsePeer(prop.Value);
                        if (peer != null) list.Add(peer);
                    }
                }
            }
            catch { }
            return list;
        }

        private static TailscalePeer? TryParsePeer(JsonElement peer)
        {
            try
            {
                var host = "";
                if (peer.TryGetProperty("HostName", out var h)) host = h.GetString() ?? "";
                if (string.IsNullOrEmpty(host) && peer.TryGetProperty("DNSName", out var dn))
                {
                    var dns = dn.GetString() ?? "";
                    if (!string.IsNullOrEmpty(dns))
                        host = dns.Split('.')[0];
                }

                var online = peer.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;

                var ip = "";
                if (peer.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ips.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s) && s.Contains('.')) { ip = s; break; }
                    }
                }

                if (string.IsNullOrEmpty(ip)) return null;
                if (string.IsNullOrEmpty(host)) host = ip;
                return new TailscalePeer { Hostname = host, Ip = ip, IsOnline = online };
            }
            catch { return null; }
        }

        #endregion

        #region 文本解析兜底

        /// <summary>
        /// tailscale status 文本输出示例：
        /// 100.86.57.99  my-pc          user@example.com  windows   -
        /// 100.64.12.34  other-pc       user@example.com  linux     offline
        /// 第一列是 Tailscale IP，第二列是 hostname，最后一列 offline 表示离线，否则在线。
        /// </summary>
        private static readonly Regex TextLineRegex = new(
            @"^\s*(?<ip>100\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+(?<host>\S+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static List<TailscalePeer> ParseText(string text)
        {
            var list = new List<TailscalePeer>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            try
            {
                foreach (Match m in TextLineRegex.Matches(text))
                {
                    var ip = m.Groups["ip"].Value.Trim();
                    var host = m.Groups["host"].Value.Trim();
                    if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(host)) continue;

                    var lineEnd = text.IndexOf('\n', m.Index);
                    var line = lineEnd < 0 ? text[m.Index..] : text[m.Index..lineEnd];
                    var isOnline = !line.Contains("offline", StringComparison.OrdinalIgnoreCase);

                    list.Add(new TailscalePeer { Hostname = host, Ip = ip, IsOnline = isOnline });
                }
            }
            catch { }
            return list;
        }

        #endregion
    }
}
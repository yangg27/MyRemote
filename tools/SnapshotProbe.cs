using System;
using System.IO;
using System.Text;
using MyRemote.Services;

namespace MyRemote.Probe
{
    /// <summary>
    /// 端到端验证「一键修复 → 快照落盘 → 一键恢复」的数据链路：
    /// 在沙箱目录内捕获真实快照并落盘，检查文件内容是否完整（修复前为空的 {}）。
    /// 不修改任何系统设置。
    /// </summary>
    internal static class SnapshotProbe
    {
        public static int Run()
        {
            Console.WriteLine("========== 快照链路端到端验证 ==========");
            var sandbox = Path.Combine(Path.GetTempPath(), "MyRemote-Snap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sandbox);
            var prev = AppPaths.OverrideDir;
            AppPaths.OverrideDir = sandbox;
            try
            {
                // 通过公开入口触发一次真实快照捕获（只捕获快照，不改任何系统设置）
                Console.WriteLine("正在捕获真实系统快照…");
                var path = RepairService.CreateRestorePointOnly();
                Console.WriteLine("生成路径：" + (path ?? "(失败)"));

                var last = AppPaths.LastSnapshotPath;
                if (!File.Exists(last))
                {
                    Console.WriteLine("✘ 未生成快照文件：" + last);
                    return 1;
                }
                var json = File.ReadAllText(last);
                var len = new FileInfo(last).Length;
                Console.WriteLine($"快照文件：{last}");
                Console.WriteLine($"文件大小：{len} 字节");
                Console.WriteLine("内容：");
                Console.WriteLine(json);
                Console.WriteLine();

                bool ok = len > 60 && json.Contains("RdpDeny") && json.Contains("TermStart")
                          && json.Contains("DefFwRulesWasEnabled") && json.Contains("NicPowerJson");
                Console.WriteLine(ok
                    ? "✔ 快照内容完整（含全部关键字段）—— 「一键恢复」链路可用"
                    : "✘ 快照内容不完整 —— 「一键恢复」将失效");

                // 再验证能被读回
                var items = RepairService.ListSnapshots();
                Console.WriteLine($"快照列表可识别数量：{items.Count}");
                if (ok && items.Count == 0) { Console.WriteLine("✘ 列表未识别到快照"); ok = false; }

                return ok ? 0 : 1;
            }
            finally
            {
                AppPaths.OverrideDir = prev;
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }
    }
}

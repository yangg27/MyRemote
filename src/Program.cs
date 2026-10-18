using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MyRemote.Forms;
using MyRemote.Services;

namespace MyRemote
{
    internal static class Program
    {
        // 单实例互斥：防止多开导致配置文件被并发写损坏、重复建立 RDP 连接
        private static Mutex? _mutex;
        private const string MutexName = "MyRemote-SingleInstance-7B3F9A2E-C1D4-4A1F";

        [STAThread]
        static void Main(string[] args)
        {
            // ---- 无界面修复模式（高权限子进程）：在单实例检查之前分流，避免与主实例互斥冲突 ----
            if (args.Length > 0 && (HasArg(args, "--repair") || HasArg(args, "--restore")))
            {
                RunHeadlessRepair(args);
                return;
            }

            // ---- 单实例保护 ----
            // 注意：Global\ 命名空间在标准用户下可能因缺少 SeCreateGlobalPrivilege 而抛异常，
            // 因此优先 Local\（同一登录会话内唯一，对本工具已足够），失败再逐级降级，绝不阻断启动。
            bool createdNew;
            _mutex = TryCreateMutex(out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "MyRemote 已经在运行（通常在系统托盘中）。\n请勿重复打开，以免配置文件被并发写损坏。",
                    "MyRemote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { _mutex?.Close(); } catch { }
                return;
            }

            // ---- 全局异常捕获：避免静默崩溃，落地日志便于排查 ----
            SetupGlobalHandlers();

            // SystemAware：应用自行处理 DPI 缩放（界面尺寸由 UiScale 显式换算，
            // 因此不会被系统再放大一次，也不会因 SystemAware 而不缩放）。
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 记录界面缩放信息：用户反馈"显示不全"时，从日志即可确认其 DPI 与屏幕尺寸
            try
            {
                var wa = Screen.PrimaryScreen?.WorkingArea;
                SystemLog.Info("启动", $"界面自适应：{UiScale.Describe()}，屏幕可用区域 {(wa?.Width ?? 0)}x{(wa?.Height ?? 0)}");
            }
            catch { }

            Application.Run(new MainForm());

            // 正常退出时释放互斥锁
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Close(); } catch { }
        }

        /// <summary>
        /// 尽最大努力创建单实例互斥体。任一命名空间失败都不影响程序启动：
        /// 极端情况下返回 null 并视作"首次启动"（宁可多开一次，也不能打不开）。
        /// </summary>
        private static Mutex? TryCreateMutex(out bool createdNew)
        {
            foreach (var prefix in new[] { @"Local\", @"Global\" })
            {
                try
                {
                    var m = new Mutex(true, prefix + MutexName, out createdNew);
                    return m;
                }
                catch { /* 换下一个命名空间 */ }
            }
            try
            {
                var m = new Mutex(true, MutexName, out createdNew);
                return m;
            }
            catch
            {
                createdNew = true; // 无法判定，放行
                return null;
            }
        }

        private static void SetupGlobalHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        private static bool _headless;

        // ---- 无界面修复/恢复：被以 runas 提权拉起的高权限子进程调用 ----
        private static void RunHeadlessRepair(string[] args)
        {
            _headless = true;
            SetupGlobalHandlers();
            // 单独的互斥，避免两次修复并发写坏快照。
            // 用 Local\ 并整体兜底：即使互斥创建失败也继续执行，不能因为加锁失败就不修了。
            Mutex? repairMutex = null;
            bool repairCreated = true;
            try { repairMutex = new Mutex(true, @"Local\MyRemote-RepairInstance-5D1C-9A3E", out repairCreated); }
            catch { repairCreated = true; }

            if (!repairCreated)
            {
                SystemLog.Warn("RepairHeadless", "已有修复进程在运行，退出。");
                repairMutex?.Dispose();
                return;
            }

            try
            {
                var itemsArg = GetArg(args, "--items");
                var snapshotArg = GetArg(args, "--snapshot");
                var resultArg = GetArg(args, "--result");

                RepairResult result;
                if (HasArg(args, "--restore"))
                {
                    var snap = string.IsNullOrEmpty(snapshotArg) ? AppPaths.LastSnapshotPath : snapshotArg;
                    result = RepairService.ExecuteRestore(snap, resultArg);
                }
                else
                {
                    var ids = (itemsArg ?? "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    result = RepairService.ExecuteRepair(ids, resultArg);
                }
                if (resultArg == null) SystemLog.Info("RepairHeadless", "结果：" + (result.Success ? "成功" : "失败") + " - " + result.Message);
            }
            catch (Exception ex)
            {
                SystemLog.Exception("RepairHeadless", ex);
            }
            finally
            {
                try { repairMutex?.ReleaseMutex(); } catch { }
                try { repairMutex?.Dispose(); } catch { }
            }
        }

        private static bool HasArg(string[] args, string name) =>
            args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static string? GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].Trim('"');
            return null;
        }

        private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
        {
            LogCrash("UI 线程未处理异常", e.Exception);
        }

        private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogCrash("非 UI 线程未处理异常" + (e.IsTerminating ? "（进程即将退出）" : ""), ex);
        }

        private static void LogCrash(string kind, Exception? ex)
        {
            string? logPath = null;
            try
            {
                var dir = AppPaths.AppDir;
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "crash.log");
                var sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
                sb.AppendLine("类型：" + kind);
                sb.AppendLine("异常详情：");
                sb.AppendLine(ex?.ToString() ?? "（无异常对象）");
                sb.AppendLine();
                File.AppendAllText(logPath, sb.ToString());

                SystemLog.Error("Crash", kind + "：" + (ex?.Message ?? ""));

                // 无界面修复子进程里绝不弹窗：父进程正在等它退出，弹窗会导致父进程一直卡住
                if (_headless) return;

                MessageBox.Show(
                    "MyRemote 遇到了一个错误，已被自动记录到日志文件：\n" + logPath +
                    "\n\n如问题反复出现，请把该日志内容发给我协助排查。",
                    "程序错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 连日志都写不出时，至少弹一次窗告知
                try { MessageBox.Show("MyRemote 遇到错误，且无法写入日志文件。\n" + (ex?.Message ?? ""), "程序错误", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            }
        }
    }
}

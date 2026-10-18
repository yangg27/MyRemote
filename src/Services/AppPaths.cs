using System;
using System.IO;

namespace MyRemote.Services
{
    /// <summary>
    /// 配置存放位置的解析。
    /// 默认在 %APPDATA%\MyRemote（跟随当前 Windows 用户，适合正常安装）。
    /// 当 exe 同目录存在 .portable 标记文件（或已存在 devices.json）时，切换为
    /// <b>便携模式</b>：配置与 exe 同目录，可整体拷贝到 U 盘 / 其它机器带走。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>
        /// 数据目录覆盖（进程内生效）。设置为非空值时，设备/凭据/日志/快照全部改用该目录。
        /// 用途：自检与回归测试使用独立目录，避免污染真实用户数据。
        /// </summary>
        public static string? OverrideDir { get; set; }

        public static bool IsPortable
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(OverrideDir)) return false; // 覆盖目录不参与便携判定
                try
                {
                    var dir = ExeDir;
                    if (File.Exists(Path.Combine(dir, ".portable"))) return true;
                    if (File.Exists(Path.Combine(dir, "devices.json"))) return true;
                }
                catch { }
                return false;
            }
        }

        public static string AppDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(OverrideDir)) return OverrideDir!;
                if (IsPortable) return ExeDir;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyRemote");
            }
        }

        public static string DevicesFile => Path.Combine(AppDir, "devices.json");
        public static string SecretsFile => Path.Combine(AppDir, "secrets.json");

        /// <summary>运行日志文件（便于故障排查与升级）。</summary>
        public static string LogFilePath => Path.Combine(AppDir, "MyRemote.log");

        /// <summary>修复前快照与结果的存放目录（也用于一键恢复）。</summary>
        public static string RollbackDir
        {
            get
            {
                var d = Path.Combine(AppDir, "Rollback");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        /// <summary>最近一次“一键修复”生成的快照（一键恢复默认回退到此）。</summary>
        public static string LastSnapshotPath => Path.Combine(RollbackDir, "last.json");

        private static string ExeDir
        {
            get
            {
                try
                {
                    var p = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    return string.IsNullOrEmpty(p) ? "." : Path.GetDirectoryName(p) ?? ".";
                }
                catch { return "."; }
            }
        }
    }
}

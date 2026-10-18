using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace MyRemote.Services
{
    /// <summary>日志级别。</summary>
    public enum LogLevel { Info, Warn, Error }

    /// <summary>一条日志记录。</summary>
    public class LogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Component = "";
        public string Message = "";
        public override string ToString() =>
            $"{Time:yyyy-MM-dd HH:mm:ss} [{SystemLog.LevelText(Level)}] [{Component}] {Message}";
    }

    /// <summary>
    /// 系统日志：所有模块统一写这里。
    /// - 落地到 AppPaths.LogFilePath（按天/体积自动滚动），便于故障排查与系统升级；
    /// - 同时保留内存环形缓冲（最近 2000 条）供日志查看器实时刷新；
    /// - 写日志永不抛异常，任何失败都被吞掉，避免影响主流程。
    /// </summary>
    public static class SystemLog
    {
        private static readonly object _lock = new();
        private static readonly List<LogEntry> _buffer = new();
        private const int MaxBuffer = 2000;
        private static readonly object _fileLock = new();
        private const long MaxFileBytes = 5 * 1024 * 1024;

        public static string LogFilePath => AppPaths.LogFilePath;

        public static IReadOnlyList<LogEntry> Entries
        {
            get { lock (_lock) return new List<LogEntry>(_buffer); }
        }

        public static void Info(string component, string message) => Write(LogLevel.Info, component, message);
        public static void Warn(string component, string message) => Write(LogLevel.Warn, component, message);
        public static void Error(string component, string message) => Write(LogLevel.Error, component, message);
        public static void Exception(string component, Exception? ex) =>
            Write(LogLevel.Error, component, ex?.ToString() ?? "（无异常对象）");

        /// <summary>记录错误并弹窗提示（运行错误弹窗报错提示）。不阻塞日志落盘。</summary>
        public static void ShowError(string context, Exception? ex)
        {
            Exception(context, ex);
            try
            {
                MessageBox.Show(
                    $"操作失败（{context}）：\n{ex?.Message ?? "未知错误"}\n\n详细已记录到日志：" + LogFilePath,
                    "运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* 弹窗失败也不影响 */ }
        }

        public static void ShowError(string context, string message)
        {
            Error(context, message);
            try
            {
                MessageBox.Show(
                    $"操作失败（{context}）：\n{message}\n\n详细已记录到日志：" + LogFilePath,
                    "运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static void Write(LogLevel level, string component, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Component = component ?? "",
                Message = message ?? ""
            };
            try
            {
                lock (_lock)
                {
                    _buffer.Add(entry);
                    if (_buffer.Count > MaxBuffer) _buffer.RemoveAt(0);
                }
                var line = entry + "\n";
                var path = LogFilePath;
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null) Directory.CreateDirectory(dir);
                    lock (_fileLock)
                    {
                        try
                        {
                            if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                                Rotate(path);
                        }
                        catch { }
                        File.AppendAllText(path, line, Encoding.UTF8);
                    }
                }
                catch { /* 落盘失败不影响主流程 */ }
            }
            catch { }
        }

        private static void Rotate(string path)
        {
            try
            {
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch { }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string LevelText(LogLevel level) => level switch
        {
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "?"
        };
    }
}

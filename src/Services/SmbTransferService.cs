using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MyRemote.Services
{
    /// <summary>
    /// 文件传输引擎。底层就是 Windows SMB：本地路径与远程 UNC 路径（\\host\share\...）
    /// 在文件系统层面无差别，所以传输就是普通的文件流拷贝，只是远程端走的是 SMB 协议。
    ///
    /// 稳定性设计：
    /// - 分块拷贝 + 进度回调 + 可取消；
    /// - **先写 .mrtmp 临时文件，校验大小一致后再原子替换目标**。这样传输中断/失败/取消时，
    ///   目标位置原来的文件不会被半截内容覆盖，也不会残留损坏文件；
    /// - 传输后比对源与目标大小，防止静默损坏。
    /// </summary>
    public static class SmbTransferService
    {
        private const int BufferSize = 1024 * 1024; // 1MB 块，兼顾速度与进度粒度
        private const string TempSuffix = ".mrtmp";

        public class ProgressInfo
        {
            public long BytesDone { get; set; }
            public long BytesTotal { get; set; }
            public string CurrentFile { get; set; } = "";
        }

        /// <summary>拷贝单个文件，带进度、取消与完整性校验。</summary>
        public static async Task CopyFileAsync(string source, string dest, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            EnsureParentDirectory(dest);
            await CopyCoreAsync(source, dest, Path.GetFileName(source), progress, ct, 0, 0);
        }

        /// <summary>递归拷贝目录。会先统计总字节数用于进度展示，并在每个文件拷贝后做完整性校验。</summary>
        public static async Task CopyDirectoryAsync(string sourceDir, string destDir, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }

            Directory.CreateDirectory(destDir);
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }

            long done = 0;
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, rel);
                done = await CopyCoreAsync(file, target, rel, progress, ct, done, total);
            }
        }

        /// <summary>
        /// 单文件拷贝核心：临时文件 + 校验 + 原子替换。
        /// doneBefore/total 用于目录传输时的整体进度。
        /// 返回本次结束后的累计字节数。
        /// </summary>
        private static async Task<long> CopyCoreAsync(string source, string dest, string displayName,
            IProgress<ProgressInfo>? progress, CancellationToken ct, long doneBefore, long total)
        {
            EnsureParentDirectory(dest);
            var tmp = dest + TempSuffix;

            try
            {
                long srcLen;
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    srcLen = src.Length;
                    var fileTotal = total > 0 ? total : srcLen;
                    var buffer = new byte[BufferSize];
                    long done = doneBefore;
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read, ct);
                        done += read;
                        progress?.Report(new ProgressInfo
                        {
                            BytesDone = done,
                            BytesTotal = Math.Max(fileTotal, done),
                            CurrentFile = displayName
                        });
                    }
                    await dst.FlushAsync(ct);
                }

                // 完整性校验：临时文件大小必须与源一致，否则视为失败，不覆盖目标
                var tmpLen = new FileInfo(tmp).Length;
                if (tmpLen != srcLen)
                    throw new IOException($"传输校验失败：{displayName} 大小不一致（源 {srcLen} 字节 / 目标 {tmpLen} 字节）。建议重试。");

                // 校验通过后再原子替换（overwrite=true），确保目标要么是旧文件、要么是完整新文件
                File.Move(tmp, dest, true);
                return doneBefore + srcLen;
            }
            catch
            {
                // 失败/取消：清掉半截临时文件，保持目标原状
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        public static void EnsureParentDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public static string CombineUnc(string host, string share, string relativePath)
        {
            var baseUnc = $"\\\\{host}\\{share}";
            if (string.IsNullOrWhiteSpace(relativePath)) return baseUnc;
            return baseUnc.TrimEnd('\\') + "\\" + relativePath.TrimStart('\\', '/').Replace('/', '\\');
        }
    }
}

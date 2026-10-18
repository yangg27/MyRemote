using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MyRemote.Services
{
    /// <summary>
    /// 把远控账号写进 Windows 凭据管理器（CredWrite），使 SMB 访问 \\host\share 时
    /// 免输密码、不弹窗。
    ///
    /// 为什么不用 cmdkey：cmdkey 的 /pass: 参数会把密码明文写进进程命令行，
    /// 本地任何用户都能通过任务管理器/进程枚举看到，存在信息泄露风险。
    /// CredWrite 直接调用 advapi32 在凭据管理器中保存，密码只以加密形式存于系统，
    /// 且只作用于当前用户的登录会话，更安全也更可靠（不受系统语言影响）。
    /// </summary>
    public static class NetworkShareHelper
    {
        // CRED_TYPE_DOMAIN_PASSWORD：SMB 网络登录凭据（Windows 访问 \\host\ 时会自动匹配）
        private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
        // CRED_PERSIST_ENTERPRISE：持久化到当前用户凭据集，重启后仍有效（等同 cmdkey 默认行为）
        private const uint CRED_PERSIST_ENTERPRISE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public ulong LastWritten; // FILETIME 此处仅占位，写入时填 0 让系统填当前时间
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        public static void AddCredential(string host, string user, string pass)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(pass)) return;
            try
            {
                WriteCred(host, user, pass);
            }
            catch (Exception ex)
            {
                // 不再回退到 cmdkey：它会把密码明文放进进程命令行，任何本地用户都能看到。
                // 失败只记日志，文件管理器仍可正常工作（首次访问时系统会弹窗要求输入）。
                SystemLog.Exception("凭据", ex);
            }
        }

        public static void RemoveCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            try
            {
                CredDelete(host, CRED_TYPE_DOMAIN_PASSWORD, 0);
            }
            catch (Exception ex)
            {
                SystemLog.Exception("凭据", ex);
            }
        }

        private static void WriteCred(string host, string user, string pass)
        {
            // 关键：CRED_TYPE_DOMAIN_PASSWORD 要求 CredentialBlob 是 **UTF-16(Unicode)** 编码的密码。
            // 若用 UTF-8，系统会把字节按 UTF-16 解释，导致存入的密码是乱码，SMB 认证必然失败。
            var blob = Encoding.Unicode.GetBytes(pass);
            // Windows 对域密码凭据的 blob 上限为 512 字节（即 256 个 UTF-16 字符）
            if (blob.Length > 512)
                throw new ArgumentException("密码过长（超过 256 个字符），Windows 凭据管理器无法保存。");
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = host,
                UserName = user,
                Comment = "MyRemote",
                Persist = CRED_PERSIST_ENTERPRISE,
                CredentialBlobSize = (uint)blob.Length,
                LastWritten = 0
            };

            IntPtr blobPtr = IntPtr.Zero;
            try
            {
                blobPtr = Marshal.AllocCoTaskMem(blob.Length);
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                cred.CredentialBlob = blobPtr;

                if (!CredWrite(ref cred, 0))
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(err, "CredWrite 失败");
                }
            }
            finally
            {
                if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            }
        }
    }
}

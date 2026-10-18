using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MyRemote.Services
{
    /// <summary>
    /// 凭据保险箱：把设备登录密码用 Windows DPAPI（CurrentUser 作用域）加密后落盘。
    /// 相比自己写加密算法，DPAPI 直接利用 Windows 登录会话做密钥保护，没有密钥管理负担，
    /// 且只有当前 Windows 用户能解密。仅在本机运行，无法在其它机器上读出。
    /// 落盘后还会收紧 NTFS 权限，仅当前用户可读写，进一步降低泄露面。
    /// </summary>
    public static class CredentialVault
    {
        // 注意：不能缓存为 static readonly —— AppPaths 的数据目录可能被重定向（自检隔离），
        // 缓存会让重定向后仍写入旧路径。
        private static string SecretsFile => AppPaths.SecretsFile;

        /// <summary>
        /// 进程内读写串行化：多个线程同时写凭据时，"临时文件 + 替换"会互相冲突破坏文件。
        /// </summary>
        private static readonly object IoLock = new();

        private static Dictionary<string, string> LoadMap()
        {
            lock (IoLock)
            {
                return LoadMapCore();
            }
        }

        private static Dictionary<string, string> LoadMapCore()
        {
            try
            {
                if (!File.Exists(SecretsFile)) return new Dictionary<string, string>();
                var json = File.ReadAllText(SecretsFile);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static void SaveMap(Dictionary<string, string> map)
        {
            var dir = Path.GetDirectoryName(SecretsFile)!;
            try
            {
                Directory.CreateDirectory(dir);
                // 原子写入：先写临时文件再替换，避免写到一半崩溃导致所有已存密码损坏
                var tmp = SecretsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
                try
                {
                    if (File.Exists(SecretsFile))
                    {
                        // 隐藏属性可能让 Replace 失败，先清掉再替换
                        try { File.SetAttributes(SecretsFile, FileAttributes.Normal); } catch { }
                        File.Replace(tmp, SecretsFile, null);
                    }
                    else File.Move(tmp, SecretsFile);
                }
                catch
                {
                    // 替换失败时退回覆盖写，至少保证本次修改生效
                    File.Copy(tmp, SecretsFile, true);
                    try { File.Delete(tmp); } catch { }
                }
                try { File.SetAttributes(SecretsFile, FileAttributes.Hidden); } catch { }
                HardenAcl();
            }
            catch (Exception ex)
            {
                // 凭据落盘失败不应让“添加/编辑设备”整体崩掉，但要留下可排查的记录
                SystemLog.Exception("凭据", ex);
            }
        }

        /// <summary>
        /// 收紧凭据文件权限：仅当前 Windows 用户可读写，移除其它本地用户/组的访问，降低凭据泄露面。
        /// 仅在 NTFS 且非便携模式（U 盘/FAT 无法设置 ACL）下生效，失败则静默跳过。
        /// </summary>
        private static void HardenAcl()
        {
            if (AppPaths.IsPortable) return; // 便携介质（U 盘/FAT）无法设置 ACL，跳过
            try
            {
                var fi = new FileInfo(SecretsFile);
                if (!fi.Exists) return;
                var sec = fi.GetAccessControl();
                sec.SetAccessRuleProtection(true, false); // 移除继承，避免 Administrators/Users 等继承到访问
                var sid = WindowsIdentity.GetCurrent().User!;
                sec.AddAccessRule(new FileSystemAccessRule(
                    sid, FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(sec);
            }
            catch { /* 权限调整失败不应影响保存功能，静默跳过 */ }
        }

        public static void SavePassword(string deviceId, string password)
        {
            lock (IoLock)
            {
                SavePasswordCore(deviceId, password);
            }
        }

        private static void SavePasswordCore(string deviceId, string password)
        {
            var map = LoadMap();
            if (string.IsNullOrEmpty(password))
            {
                map.Remove(deviceId);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(password);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                map[deviceId] = System.Convert.ToBase64String(protectedBytes);
            }
            SaveMap(map);
        }

        public static string? GetPassword(string deviceId)
        {
            var map = LoadMap();
            if (!map.TryGetValue(deviceId, out var blob) || string.IsNullOrEmpty(blob))
                return null;
            try
            {
                var protectedBytes = System.Convert.FromBase64String(blob);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        public static void DeletePassword(string deviceId)
        {
            lock (IoLock)
            {
                var map = LoadMapCore();
                if (map.Remove(deviceId)) SaveMap(map);
            }
        }
    }
}

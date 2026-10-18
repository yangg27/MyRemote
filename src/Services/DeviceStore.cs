using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MyRemote.Models;

namespace MyRemote.Services
{
    /// <summary>
    /// 设备配置的本地存储。配置文件为 JSON，位于 %APPDATA%\MyRemote\devices.json。
    /// 设备数量不写死，可任意增减；新电脑加入只需“添加设备”。
    /// 写入采用“临时文件 + 原子替换”，并额外保留一份 .bak 备份，防止写到一半崩溃/断电导致配置损坏。
    /// </summary>
    public static class DeviceStore
    {
        /// <summary>
        /// 进程内读写串行化。Save 使用"临时文件 + 原子替换"，
        /// 若两个线程同时替换同一目标文件，会触发 File.Replace/Move 的占用冲突而直接抛异常
        /// （全功能自检实测：12 路并发写有 10 路失败）。加锁后写入互不干扰，避免丢配置。
        /// </summary>
        private static readonly object IoLock = new();

        public static string ConfigFilePath => AppPaths.DevicesFile;

        public static List<Device> Load()
        {
            lock (IoLock)
            {
                return LoadCore();
            }
        }

        private static List<Device> LoadCore()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                    return new List<Device>();
                return Deserialize(File.ReadAllText(ConfigFilePath));
            }
            catch
            {
                // 主文件损坏：无论后续能否从备份恢复，都先把损坏文件另存留证，
                // 避免下一次保存直接把它覆盖、连现场都没了（排障时最需要的就是这份原始内容）。
                try
                {
                    if (File.Exists(ConfigFilePath))
                        File.Copy(ConfigFilePath,
                            ConfigFilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                            true);
                }
                catch { }

                // 尝试从 .bak 备份恢复，避免丢失全部设备
                try
                {
                    var bak = ConfigFilePath + ".bak";
                    if (File.Exists(bak))
                    {
                        var recovered = Deserialize(File.ReadAllText(bak));
                        if (recovered.Count > 0) return recovered;
                    }
                }
                catch { }

                // 回退空列表，不让程序崩
                return new List<Device>();
            }
        }

        private static List<Device> Deserialize(string json)
        {
            var list = JsonSerializer.Deserialize<List<Device>>(json);
            return list ?? new List<Device>();
        }

        public static void Save(IEnumerable<Device> devices)
        {
            lock (IoLock)
            {
                SaveCore(devices);
            }
        }

        private static void SaveCore(IEnumerable<Device> devices)
        {
            Directory.CreateDirectory(AppPaths.AppDir);
            var json = JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ConfigFilePath + ".tmp";
            // 先写临时文件，再原子替换，避免写到一半崩溃/断电导致配置损坏丢失全部设备
            File.WriteAllText(tmp, json);
            var bak = ConfigFilePath + ".bak";
            if (File.Exists(ConfigFilePath))
            {
                // 已有配置：先备份再原子替换
                try { File.Copy(ConfigFilePath, bak, true); } catch { }
                File.Replace(tmp, ConfigFilePath, null);
            }
            else
            {
                // 首次保存：目标文件尚不存在，File.Replace 要求目标必须存在，故直接移动
                File.Move(tmp, ConfigFilePath);
            }
        }

        public static void EnsureDir() => Directory.CreateDirectory(AppPaths.AppDir);
    }
}

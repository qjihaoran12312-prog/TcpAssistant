using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    [DataContract]
    public sealed class ProfilesDocument
    {
        [DataMember(Order = 1)]
        public List<ConnectionProfile> Profiles { get; set; } = new List<ConnectionProfile>();
    }

    /// <summary>
    /// 配置读写。用 DataContractJsonSerializer：.NET Framework 自带，不引第三方包。
    /// 写入走「临时文件 + 原子替换」，断电不会把配置写坏。
    /// </summary>
    public static class ProfileStore
    {
        public static string ConfigDirectory
        {
            get
            {
                // 放在 exe 同级的 config 目录，拷走整个文件夹就能带走配置
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(baseDir, "config");
            }
        }

        public static string DefaultPath
        {
            get { return Path.Combine(ConfigDirectory, "profiles.json"); }
        }

        public static List<ConnectionProfile> Load(string path = null)
        {
            path = path ?? DefaultPath;
            if (!File.Exists(path))
            {
                var builtIn = ConnectionProfile.BuiltIns();
                TrySave(builtIn, path);
                return builtIn;
            }

            try
            {
                // 手工用记事本/PowerShell 改过的配置常带 UTF-8 BOM，
                // 直接喂给序列化器会抛异常并静默回退成默认值，这里先剥掉。
                var bytes = File.ReadAllBytes(path);
                int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

                using (var stream = new MemoryStream(bytes, offset, bytes.Length - offset))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ProfilesDocument));
                    var doc = (ProfilesDocument)serializer.ReadObject(stream);
                    if (doc != null && doc.Profiles != null && doc.Profiles.Count > 0)
                    {
                        foreach (var p in doc.Profiles) p.Normalize();
                        return doc.Profiles;
                    }
                }
            }
            catch (Exception)
            {
                // 配置文件被手改坏了：留个备份，退回内置预设，保证工具还能开
                try
                {
                    File.Copy(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { }
            }

            return ConnectionProfile.BuiltIns();
        }

        public static void Save(IEnumerable<ConnectionProfile> profiles, string path = null)
        {
            path = path ?? DefaultPath;
            var doc = new ProfilesDocument { Profiles = new List<ConnectionProfile>(profiles) };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                       stream, new UTF8Encoding(false), false, true, "  "))
            {
                new DataContractJsonSerializer(typeof(ProfilesDocument)).WriteObject(writer, doc);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void TrySave(List<ConnectionProfile> profiles, string path)
        {
            try { Save(profiles, path); } catch { }
        }
    }
}

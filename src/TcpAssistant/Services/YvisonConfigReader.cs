using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TcpAssistant.Services
{
    /// <summary>
    /// 读取 Yvison 自己的配置文件，把端口“问”出来，省得每次手填。
    /// 只读，不写回，不会影响正在运行的视觉程序。
    /// </summary>
    public static class YvisonConfigReader
    {
        /// <summary>从程序目录往上找 Yvison.exe，找到就是安装根目录。</summary>
        public static string FindRoot(string startDirectory = null)
        {
            var dir = new DirectoryInfo(startDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Yvison.exe"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>外部监控端口：Config\Parameter\监控.bin 里的 "端口号"。</summary>
        public static int? ReadMonitorPort(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            var path = Path.Combine(root, "Config", "Parameter", "监控.bin");
            var text = SafeRead(path);
            if (text == null) return null;

            var m = Regex.Match(text, "\"端口号\"\\s*:\\s*(\\d{1,5})");
            if (m.Success) return ToPort(m.Groups[1].Value);

            m = Regex.Match(text, "\"(?:monitor_port|port)\"\\s*:\\s*(\\d{1,5})");
            return m.Success ? ToPort(m.Groups[1].Value) : null;
        }

        /// <summary>
        /// Skill TCP 端口：各工程目录下 Hardware\TCP.bin。
        /// 真实文件形如 {"连接方式":"服务器","IP":"127.0.0.1","Port":"2002",...}，Port 是字符串。
        /// </summary>
        public static int? ReadSkillTcpPort(string root)
        {
            foreach (var file in EnumerateTcpConfigs(root))
            {
                var text = SafeRead(file);
                if (text == null) continue;
                var m = Regex.Match(text, "\"(?:[Pp]ort|skill_tcp_port)\"\\s*:\\s*\"?(\\d{1,5})");
                if (m.Success)
                {
                    var port = ToPort(m.Groups[1].Value);
                    if (port.HasValue) return port;
                }
            }
            return null;
        }

        /// <summary>Skill TCP 的地址，同样从 TCP.bin 的 "IP" 字段读。</summary>
        public static string ReadSkillTcpHost(string root)
        {
            foreach (var file in EnumerateTcpConfigs(root))
            {
                var text = SafeRead(file);
                if (text == null) continue;
                var m = Regex.Match(text, "\"IP\"\\s*:\\s*\"([^\"]{3,64})\"");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        private static IEnumerable<string> EnumerateTcpConfigs(string root)
        {
            if (string.IsNullOrEmpty(root)) yield break;

            var paramDir = Path.Combine(root, "Config", "Parameter");
            if (!Directory.Exists(paramDir)) yield break;

            string[] files;
            try
            {
                files = Directory.GetFiles(paramDir, "TCP.bin", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var file in files) yield return file;
        }

        /// <summary>给界面用的一行说明。</summary>
        public static string Describe(string root)
        {
            if (string.IsNullOrEmpty(root)) return "未找到 Yvison 安装目录";

            var monitor = ReadMonitorPort(root);
            var skill = ReadSkillTcpPort(root);
            var host = ReadSkillTcpHost(root);
            var parts = new List<string>();
            parts.Add("目录 " + root);
            parts.Add("监控端口 " + (monitor.HasValue ? monitor.Value.ToString(CultureInfo.InvariantCulture) : "未读到"));
            parts.Add("SkillTCP " + (skill.HasValue
                ? (string.IsNullOrEmpty(host) ? "" : host + ":") + skill.Value.ToString(CultureInfo.InvariantCulture)
                : "未读到（默认 2002）"));
            return string.Join("　·　", parts);
        }

        private static string SafeRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int? ToPort(string raw)
        {
            int port;
            if (!int.TryParse(raw, out port)) return null;
            return port > 0 && port <= 65535 ? port : (int?)null;
        }
    }
}

using System.Collections.Generic;

namespace TcpAssistant.Models
{
    /// <summary>本工具扮演的角色。</summary>
    public enum ConnectionRole
    {
        /// <summary>客户端：主动连 Yvison 的 Skill TCP / 监控端口。</summary>
        Client = 0,

        /// <summary>服务端：本机监听，让上位机或 Yvison 连进来。</summary>
        Server = 1
    }

    /// <summary>切帧方式，解决 TCP 粘包/半包。</summary>
    public enum FrameMode
    {
        /// <summary>按分隔符切（默认 CRLF）。</summary>
        Delimiter = 0,

        /// <summary>固定字节长度。</summary>
        FixedLength = 1,

        /// <summary>前 2 字节大端表示载荷长度。</summary>
        LengthPrefix = 2,

        /// <summary>无分隔符，按空闲时间切。</summary>
        Idle = 3
    }

    public enum FrameDirection
    {
        Rx = 0,
        Tx = 1,
        Info = 2
    }

    public enum FrameKind
    {
        Data = 0,
        Heartbeat = 1,
        Timeout = 2,
        Error = 3,
        System = 4
    }

    public enum EngineState
    {
        Disconnected = 0,
        Connecting = 1,
        Listening = 2,
        Connected = 3
    }

    /// <summary>下拉框用：显示中文，值用枚举。</summary>
    public sealed class NamedValue<T>
    {
        public string Name { get; }
        public T Value { get; }

        public NamedValue(string name, T value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => Name;
    }

    /// <summary>常见分隔符表。名字直接存进配置文件，换现场只改一个字符串。</summary>
    public static class Delimiters
    {
        public const string CrLf = "CRLF  \\r\\n";
        public const string Lf = "LF    \\n";
        public const string Cr = "CR    \\r";
        public const string Nul = "NUL   \\x00";
        public const string Etx = "ETX   \\x03";

        private static readonly Dictionary<string, byte[]> Map = new Dictionary<string, byte[]>
        {
            { CrLf, new byte[] { 0x0D, 0x0A } },
            { Lf, new byte[] { 0x0A } },
            { Cr, new byte[] { 0x0D } },
            { Nul, new byte[] { 0x00 } },
            { Etx, new byte[] { 0x03 } }
        };

        public static IReadOnlyList<string> Names { get; } =
            new List<string> { CrLf, Lf, Cr, Nul, Etx };

        public static byte[] Resolve(string name)
        {
            byte[] bytes;
            return Map.TryGetValue(name ?? string.Empty, out bytes) ? bytes : new byte[] { 0x0D, 0x0A };
        }
    }
}

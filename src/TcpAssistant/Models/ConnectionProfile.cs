using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using TcpAssistant.Common;
using TcpAssistant.Services;

namespace TcpAssistant.Models
{
    /// <summary>
    /// 一套完整的链路配置：怎么连、怎么切帧、多久算超时、心跳怎么发。
    /// 现场换设备 = 换一份 Profile，不改程序。
    /// </summary>
    [DataContract]
    public sealed class ConnectionProfile : ObservableObject
    {
        private string _name = "新建配置";
        private ConnectionRole _role = ConnectionRole.Client;
        private string _host = "127.0.0.1";
        private int _port = 2002;
        private string _encoding = "UTF-8";
        private FrameMode _frameMode = FrameMode.Delimiter;
        private string _delimiter = Delimiters.CrLf;
        private int _fixedLength = 8;
        private int _idleMs = 60;
        private int _connectTimeoutMs = 3000;
        private int _replyTimeoutMs = 2000;
        private bool _autoReconnect = true;
        private int _reconnectDelayMs = 1000;
        private bool _heartbeatEnabled;
        private string _heartbeatText = "PING";
        private int _heartbeatIntervalMs = 5000;
        private int _heartbeatMiss = 3;

        [DataMember(Order = 1)]
        public string Name
        {
            get { return _name; }
            set { Set(ref _name, value); }
        }

        [DataMember(Order = 2)]
        public ConnectionRole Role
        {
            get { return _role; }
            set { Set(ref _role, value); Raise(nameof(IsServer)); }
        }

        [DataMember(Order = 3)]
        public string Host
        {
            get { return _host; }
            set { Set(ref _host, value); }
        }

        [DataMember(Order = 4)]
        public int Port
        {
            get { return _port; }
            set { Set(ref _port, value); }
        }

        [DataMember(Order = 5)]
        public string Encoding
        {
            get { return _encoding; }
            set { Set(ref _encoding, value); }
        }

        [DataMember(Order = 6)]
        public FrameMode FrameMode
        {
            get { return _frameMode; }
            set { Set(ref _frameMode, value); }
        }

        [DataMember(Order = 7)]
        public string Delimiter
        {
            get { return _delimiter; }
            set { Set(ref _delimiter, value); }
        }

        [DataMember(Order = 8)]
        public int FixedLength
        {
            get { return _fixedLength; }
            set { Set(ref _fixedLength, value); }
        }

        [DataMember(Order = 9)]
        public int IdleMs
        {
            get { return _idleMs; }
            set { Set(ref _idleMs, value); }
        }

        [DataMember(Order = 10)]
        public int ConnectTimeoutMs
        {
            get { return _connectTimeoutMs; }
            set { Set(ref _connectTimeoutMs, value); }
        }

        [DataMember(Order = 11)]
        public int ReplyTimeoutMs
        {
            get { return _replyTimeoutMs; }
            set { Set(ref _replyTimeoutMs, value); }
        }

        [DataMember(Order = 12)]
        public bool AutoReconnect
        {
            get { return _autoReconnect; }
            set { Set(ref _autoReconnect, value); }
        }

        [DataMember(Order = 13)]
        public int ReconnectDelayMs
        {
            get { return _reconnectDelayMs; }
            set { Set(ref _reconnectDelayMs, value); }
        }

        [DataMember(Order = 14)]
        public bool HeartbeatEnabled
        {
            get { return _heartbeatEnabled; }
            set { Set(ref _heartbeatEnabled, value); }
        }

        [DataMember(Order = 15)]
        public string HeartbeatText
        {
            get { return _heartbeatText; }
            set { Set(ref _heartbeatText, value); }
        }

        [DataMember(Order = 16)]
        public int HeartbeatIntervalMs
        {
            get { return _heartbeatIntervalMs; }
            set { Set(ref _heartbeatIntervalMs, value); }
        }

        [DataMember(Order = 17)]
        public int HeartbeatMiss
        {
            get { return _heartbeatMiss; }
            set { Set(ref _heartbeatMiss, value); }
        }

        /// <summary>界面绑定用；序列化时走下面的 CommandsData（List 更稳妥）。</summary>
        [IgnoreDataMember]
        public ObservableCollection<CommandItem> Commands { get; set; } = new ObservableCollection<CommandItem>();

        [DataMember(Order = 18, Name = "Commands")]
        private List<CommandItem> CommandsData
        {
            get { return new List<CommandItem>(Commands); }
            set { Commands = new ObservableCollection<CommandItem>(value ?? new List<CommandItem>()); }
        }

        public bool IsServer => _role == ConnectionRole.Server;

        /// <summary>下拉框、日志里直接显示名字，避免出现类名。</summary>
        public override string ToString()
        {
            return Name;
        }

        public FrameSplitter MakeSplitter()
        {
            return new FrameSplitter(_frameMode, Delimiters.Resolve(_delimiter), _fixedLength);
        }

        public string Endpoint => Host + ":" + Port;

        /// <summary>深拷贝，用于“新建配置”与取消编辑。</summary>
        public ConnectionProfile Clone()
        {
            var copy = new ConnectionProfile
            {
                Name = Name,
                Role = Role,
                Host = Host,
                Port = Port,
                Encoding = Encoding,
                FrameMode = FrameMode,
                Delimiter = Delimiter,
                FixedLength = FixedLength,
                IdleMs = IdleMs,
                ConnectTimeoutMs = ConnectTimeoutMs,
                ReplyTimeoutMs = ReplyTimeoutMs,
                AutoReconnect = AutoReconnect,
                ReconnectDelayMs = ReconnectDelayMs,
                HeartbeatEnabled = HeartbeatEnabled,
                HeartbeatText = HeartbeatText,
                HeartbeatIntervalMs = HeartbeatIntervalMs,
                HeartbeatMiss = HeartbeatMiss
            };
            foreach (var cmd in Commands) copy.Commands.Add(cmd.Clone());
            return copy;
        }

        /// <summary>反序列化出来的旧配置可能少了字段，这里补默认值，避免启动就崩。</summary>
        public void Normalize()
        {
            if (Commands == null) Commands = new ObservableCollection<CommandItem>();
            if (string.IsNullOrWhiteSpace(Name)) Name = "未命名配置";
            if (Port <= 0 || Port > 65535) Port = 2002;
            if (string.IsNullOrEmpty(Host)) Host = "127.0.0.1";
            if (!Delimiters.Names.Contains(_delimiter)) _delimiter = Delimiters.CrLf;
            if (!TextCodec.IsSupportedEncoding(_encoding)) _encoding = "UTF-8";
            if (FixedLength <= 0) FixedLength = 8;
            if (IdleMs <= 0) IdleMs = 60;
            if (ConnectTimeoutMs < 200) ConnectTimeoutMs = 3000;
            if (ReplyTimeoutMs < 50) ReplyTimeoutMs = 2000;
            if (ReconnectDelayMs < 100) ReconnectDelayMs = 1000;
            if (HeartbeatIntervalMs < 200) HeartbeatIntervalMs = 5000;
            if (HeartbeatMiss < 1) HeartbeatMiss = 3;
        }

        // ---------------------------------------------------------- 预设

        private static CommandItem Cmd(string name, string text, bool hex = false,
                                       bool crlf = false, bool reply = true, string note = "")
        {
            return new CommandItem
            {
                Name = name, Text = text, IsHex = hex, AppendCrLf = crlf,
                ExpectReply = reply, Note = note
            };
        }

        /// <summary>Yvison 主程序 Skill TCP，默认 2002。</summary>
        public static ConnectionProfile SkillTcpPreset()
        {
            var p = new ConnectionProfile
            {
                Name = "Yvison · Skill TCP (2002)",
                Role = ConnectionRole.Client,
                Host = "127.0.0.1",
                Port = 2002,
                Encoding = "UTF-8",
                FrameMode = FrameMode.Delimiter,
                Delimiter = Delimiters.CrLf,
                ReplyTimeoutMs = 2000,
                HeartbeatEnabled = true,
                HeartbeatText = "PING",
                HeartbeatIntervalMs = 5000,
                HeartbeatMiss = 3
            };
            p.Commands.Add(Cmd("启动执行", "START", crlf: true, note: "对应主程序『启动执行』"));
            p.Commands.Add(Cmd("停止执行", "STOP", crlf: true, note: "对应主程序『停止执行』"));
            p.Commands.Add(Cmd("查询状态", "STATUS", crlf: true, note: "查运行状态 / 当前项目"));
            p.Commands.Add(Cmd("触发一次", "TRIG 1", crlf: true, note: "单次触发，1 为触发号"));
            p.Commands.Add(Cmd("心跳", "PING", crlf: true, reply: false, note: "保活，不等待回包"));
            return p;
        }

        /// <summary>Yvison 外部监控 Monitoring.exe，默认 2001。</summary>
        public static ConnectionProfile MonitorPreset()
        {
            var p = new ConnectionProfile
            {
                Name = "Yvison · 外部监控 (2001)",
                Role = ConnectionRole.Client,
                Host = "127.0.0.1",
                Port = 2001,
                Encoding = "UTF-8",
                FrameMode = FrameMode.Delimiter,
                Delimiter = Delimiters.CrLf,
                ReplyTimeoutMs = 3000,
                HeartbeatEnabled = false
            };
            p.Commands.Add(Cmd("查询运行状态", "{\"cmd\":\"status\"}", crlf: true, note: "回 { ok = .., vision = .. }"));
            p.Commands.Add(Cmd("项目列表", "{\"cmd\":\"projects\"}", crlf: true, note: "项目 + 运行状态 + 软件标识"));
            p.Commands.Add(Cmd("最近日志", "{\"cmd\":\"logs\",\"count\":20}", crlf: true, note: "取监控日志"));
            return p;
        }

        /// <summary>反向：本工具当服务端，让人连进来验证对端。</summary>
        public static ConnectionProfile ListenerPreset()
        {
            var p = SkillTcpPreset();
            p.Name = "模拟服务端 · 监听 2002";
            p.Role = ConnectionRole.Server;
            p.Host = "0.0.0.0";
            p.Commands.Clear();
            p.Commands.Add(Cmd("回 OK", "OK", note: "收到触发后回一个 OK"));
            return p;
        }

        public static List<ConnectionProfile> BuiltIns()
        {
            return new List<ConnectionProfile> { SkillTcpPreset(), MonitorPreset(), ListenerPreset() };
        }
    }
}

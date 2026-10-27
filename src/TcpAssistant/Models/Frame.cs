using System;

namespace TcpAssistant.Models
{
    /// <summary>一条通信记录：一次收、一次发，或一条系统提示。</summary>
    public sealed class Frame
    {
        public int Seq { get; set; }
        public DateTime Time { get; set; }
        public FrameDirection Direction { get; set; }
        public FrameKind Kind { get; set; }
        public byte[] Data { get; set; }
        public double? RttMs { get; set; }
        public string Note { get; set; }

        /// <summary>日志表里的摘要，由 ViewModel 按当前编码算好后填入。</summary>
        public string Preview { get; set; }

        public string TimeText => Time.ToString("HH:mm:ss.fff");

        public string DirText
        {
            get
            {
                switch (Direction)
                {
                    case FrameDirection.Rx: return "← 收";
                    case FrameDirection.Tx: return "→ 发";
                    default: return "· 提示";
                }
            }
        }

        public int Length => Data == null ? 0 : Data.Length;

        public string RttText => RttMs.HasValue ? RttMs.Value.ToString("0") + " ms" : "";

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case FrameKind.Heartbeat: return "心跳";
                    case FrameKind.Timeout: return "超时";
                    case FrameKind.Error: return "异常";
                    case FrameKind.System: return "系统";
                    default: return "数据";
                }
            }
        }
    }
}

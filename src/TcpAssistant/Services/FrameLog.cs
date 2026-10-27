using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    /// <summary>
    /// 帧日志。必须只在 UI 线程操作（绑定到 DataGrid），
    /// 后台线程的帧由 ViewModel 用 Dispatcher 搬过来再写。
    /// </summary>
    public sealed class FrameLog
    {
        private int _seq;

        public ObservableCollection<Frame> Items { get; } = new ObservableCollection<Frame>();

        /// <summary>上限：超过就丢最早的，长时间跑内存不会涨。</summary>
        public int Capacity { get; set; } = 5000;

        public Frame Add(FrameDirection direction, FrameKind kind, byte[] data,
                         string encoding, string note = "", double? rtt = null)
        {
            _seq++;
            var frame = new Frame
            {
                Seq = _seq,
                Time = DateTime.Now,
                Direction = direction,
                Kind = kind,
                Data = data ?? new byte[0],
                Note = note ?? string.Empty,
                RttMs = rtt
            };
            frame.Preview = BuildPreview(frame, encoding);

            Items.Add(frame);
            while (Items.Count > Capacity) Items.RemoveAt(0);
            return frame;
        }

        private static string BuildPreview(Frame frame, string encoding)
        {
            if (frame.Data != null && frame.Data.Length > 0)
            {
                string body = TextCodec.BuildPreview(frame.Data, encoding, 400);
                return string.IsNullOrEmpty(frame.Note) ? body : body + "   · " + frame.Note;
            }
            return frame.Note ?? string.Empty;
        }

        public void Clear()
        {
            Items.Clear();
        }

        public int Count(FrameDirection direction)
        {
            int n = 0;
            foreach (var f in Items) if (f.Direction == direction) n++;
            return n;
        }

        public long Bytes(FrameDirection direction)
        {
            long n = 0;
            foreach (var f in Items) if (f.Direction == direction) n += f.Length;
            return n;
        }

        public double? AverageRtt()
        {
            double sum = 0;
            int n = 0;
            foreach (var f in Items)
            {
                if (f.RttMs.HasValue) { sum += f.RttMs.Value; n++; }
            }
            return n == 0 ? (double?)null : sum / n;
        }

        // ---------------------------------------------------------------- 导出

        public int ExportCsv(string path, string encoding)
        {
            var sb = new StringBuilder();
            sb.AppendLine("序号,时间,方向,类型,字节数,耗时(ms),文本,HEX,备注");
            foreach (var f in Items)
            {
                sb.Append(f.Seq).Append(',')
                  .Append(f.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',')
                  .Append(f.DirText).Append(',')
                  .Append(f.KindText).Append(',')
                  .Append(f.Length).Append(',')
                  .Append(f.RttMs.HasValue ? f.RttMs.Value.ToString("0.0") : "").Append(',')
                  .Append(Csv(TextCodec.Decode(f.Data, encoding))).Append(',')
                  .Append(Csv(TextCodec.ToHex(f.Data))).Append(',')
                  .Append(Csv(f.Note)).AppendLine();
            }
            // 带 BOM 的 UTF-8，Excel 双击打开中文不乱码
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return Items.Count;
        }

        public int ExportText(string path, string encoding)
        {
            var sb = new StringBuilder();
            foreach (var f in Items)
            {
                sb.Append('[').Append(f.Time.ToString("HH:mm:ss.fff")).Append("] ")
                  .Append(f.DirText).Append(" (").Append(f.Length).Append("B)");
                if (f.RttMs.HasValue) sb.Append(" <").Append(f.RttMs.Value.ToString("0")).Append("ms>");
                sb.Append(' ');

                string body = f.Data.Length > 0
                    ? TextCodec.Decode(f.Data, encoding).Replace("\r", "").TrimEnd('\n')
                    : f.Note;
                sb.Append(body).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return Items.Count;
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", "\\r").Replace("\n", "\\n");
            if (value.IndexOfAny(new[] { ',', '"' }) >= 0)
            {
                value = "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }
    }
}

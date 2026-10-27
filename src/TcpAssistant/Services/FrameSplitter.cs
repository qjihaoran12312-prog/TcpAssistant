using System.Collections.Generic;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    /// <summary>
    /// TCP 是字节流，一次 Receive 可能拿到半条报文，也可能拿到三条。
    /// 这里负责把字节流切成完整报文，四种方式覆盖现场绝大多数设备。
    /// </summary>
    public sealed class FrameSplitter
    {
        private const int MaxBuffer = 1 << 20; // 1MB，防止对端不发分隔符把内存吃光

        private readonly FrameMode _mode;
        private readonly byte[] _delimiter;
        private readonly int _fixedLength;
        private readonly List<byte> _buffer = new List<byte>(8192);

        public FrameSplitter(FrameMode mode, byte[] delimiter, int fixedLength)
        {
            _mode = mode;
            _delimiter = delimiter != null && delimiter.Length > 0 ? delimiter : new byte[] { 0x0D, 0x0A };
            _fixedLength = fixedLength > 0 ? fixedLength : 8;
        }

        public FrameMode Mode => _mode;

        /// <summary>还没凑成整帧的残留字节数，状态栏会显示“半包等待中”。</summary>
        public int PendingLength => _buffer.Count;

        /// <summary>取出残留字节的副本（用于“收到但未成帧”的提示）。</summary>
        public byte[] Pending()
        {
            return _buffer.ToArray();
        }

        public void Reset() => _buffer.Clear();

        /// <summary>喂入原始字节，返回本次凑齐的完整帧（可能 0 条或多条）。</summary>
        public List<byte[]> Feed(byte[] data)
        {
            if (data != null && data.Length > 0) _buffer.AddRange(data);

            List<byte[]> frames;
            switch (_mode)
            {
                case FrameMode.FixedLength:
                    frames = TakeFixed();
                    break;
                case FrameMode.LengthPrefix:
                    frames = TakeLengthPrefixed();
                    break;
                case FrameMode.Idle:
                    frames = new List<byte[]>(); // 交给 FlushIdle
                    break;
                default:
                    frames = TakeDelimited();
                    break;
            }

            GuardOverflow();
            return frames;
        }

        /// <summary>空闲切帧专用：把缓冲区剩余字节整体作为一帧吐出来。</summary>
        public List<byte[]> FlushIdle()
        {
            var frames = new List<byte[]>();
            if (_mode == FrameMode.Idle && _buffer.Count > 0)
            {
                frames.Add(_buffer.ToArray());
                _buffer.Clear();
            }
            return frames;
        }

        private List<byte[]> TakeDelimited()
        {
            var frames = new List<byte[]>();
            while (true)
            {
                int idx = IndexOfDelimiter(0);
                if (idx < 0) break;

                var frame = new byte[idx];
                _buffer.CopyTo(0, frame, 0, idx);
                frames.Add(frame);
                _buffer.RemoveRange(0, idx + _delimiter.Length);
            }
            return frames;
        }

        private List<byte[]> TakeFixed()
        {
            var frames = new List<byte[]>();
            while (_buffer.Count >= _fixedLength)
            {
                var frame = new byte[_fixedLength];
                _buffer.CopyTo(0, frame, 0, _fixedLength);
                frames.Add(frame);
                _buffer.RemoveRange(0, _fixedLength);
            }
            return frames;
        }

        private List<byte[]> TakeLengthPrefixed()
        {
            var frames = new List<byte[]>();
            while (_buffer.Count >= 2)
            {
                int length = (_buffer[0] << 8) | _buffer[1];
                if (length <= 0 || length > MaxBuffer)
                {
                    // 长度字段明显不对，丢掉一个字节重新找边界，避免死等
                    _buffer.RemoveRange(0, 1);
                    continue;
                }
                if (_buffer.Count < 2 + length) break;

                var frame = new byte[length];
                _buffer.CopyTo(2, frame, 0, length);
                frames.Add(frame);
                _buffer.RemoveRange(0, 2 + length);
            }
            return frames;
        }

        private int IndexOfDelimiter(int start)
        {
            int last = _buffer.Count - _delimiter.Length;
            for (int i = start; i <= last; i++)
            {
                bool hit = true;
                for (int j = 0; j < _delimiter.Length; j++)
                {
                    if (_buffer[i + j] != _delimiter[j]) { hit = false; break; }
                }
                if (hit) return i;
            }
            return -1;
        }

        private void GuardOverflow()
        {
            if (_buffer.Count <= MaxBuffer) return;
            int drop = _buffer.Count - MaxBuffer;
            _buffer.RemoveRange(0, drop);
        }
    }
}

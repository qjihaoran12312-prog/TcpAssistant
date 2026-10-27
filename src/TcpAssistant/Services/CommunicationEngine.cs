using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    /// <summary>
    /// 通信编排层：连接生命周期、切帧、心跳、回包超时判定，全部在这里。
    /// 只抛事件不碰界面，界面层负责把事件搬到 UI 线程渲染。
    /// </summary>
    public sealed class CommunicationEngine : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Stopwatch _pendingWatch = new Stopwatch();

        private ConnectionProfile _profile;
        private FrameSplitter _splitter;
        private CancellationTokenSource _cts;
        private TcpSession _session;
        private Timer _timer;
        private PendingReply _pending;
        private DateTime _lastRx;
        private DateTime _lastTx;
        private bool _heartbeatLostNotified;
        private DateTime _lastPartialNotice = DateTime.MinValue;
        private int _lastPartialLength = -1;

        public event Action<Frame> FrameReady;
        public event Action<EngineState, string> StateChanged;
        public event Action<string, bool> Notice;   // 文本, 是否错误

        public EngineState State { get; private set; } = EngineState.Disconnected;
        public string StateDetail { get; private set; } = "未连接";
        public bool IsActive => State != EngineState.Disconnected;
        public string Peer => _session == null ? null : _session.Peer;

        public int PendingBufferBytes
        {
            get { lock (_gate) { return _splitter == null ? 0 : _splitter.PendingLength; } }
        }

        // ---------------------------------------------------------------- 生命周期

        public void Start(ConnectionProfile profile)
        {
            Stop();

            _profile = profile;
            lock (_gate)
            {
                _splitter = profile.MakeSplitter();
                _pending = null;
                _heartbeatLostNotified = false;
            }

            _lastRx = DateTime.Now;
            _lastTx = DateTime.Now;
            _cts = new CancellationTokenSource();
            _timer = new Timer(OnTick, null, 50, 50);

            if (profile.IsServer)
            {
                var server = new TcpServerService(profile);
                server.Listening += ep => SetState(EngineState.Listening, "监听 " + ep);
                server.Failed += msg =>
                {
                    EmitNotice(msg, true);
                    EmitInfo(msg);
                    SetState(EngineState.Disconnected, "监听失败");
                };
                server.Accepted += OnSessionOpened;
                server.Closed += OnSessionClosed;
                RunSafe(server.RunAsync(_cts.Token));
            }
            else
            {
                var client = new TcpClientService(profile);
                client.Connecting += ep => SetState(EngineState.Connecting, "连接 " + ep);
                client.Opened += OnSessionOpened;
                client.ConnectFailed += msg =>
                {
                    EmitNotice("连接失败：" + msg, true);
                    EmitInfo("连接失败：" + msg);
                };
                client.Closed += OnSessionClosed;
                client.RetryWaiting += msg =>
                {
                    if (!IsActive) return;
                    SetState(EngineState.Connecting, msg);
                };
                RunSafe(client.RunAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            var timer = _timer;
            _timer = null;
            if (timer != null) { try { timer.Dispose(); } catch { } }

            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            TcpSession session;
            lock (_gate)
            {
                session = _session;
                _session = null;
                _pending = null;
                if (_splitter != null) _splitter.Reset();
            }

            if (session != null) { try { session.Dispose(); } catch { } }

            SetState(EngineState.Disconnected, "未连接");
        }

        // ---------------------------------------------------------------- 发送

        /// <summary>发一帧。expectReply=true 时登记“等回包”，超时会在日志里留一条超时记录。</summary>
        public async Task<bool> SendAsync(byte[] payload, bool expectReply, int replyTimeoutMs,
                                          FrameKind kind, string label)
        {
            TcpSession session;
            CancellationToken token = CancellationToken.None;
            lock (_gate)
            {
                session = _session;
                if (_cts != null) token = _cts.Token;
            }

            if (session == null)
            {
                EmitNotice("未连接，无法发送", true);
                return false;
            }

            // 先登记“等回包”再发送：回环/局域网里回包可能比 await 返回还快，
            // 顺序反了就会丢掉 RTT，日志里看起来像“回包对不上”。
            if (expectReply && replyTimeoutMs > 0)
            {
                lock (_gate)
                {
                    _pending = new PendingReply { TimeoutMs = replyTimeoutMs, Label = label };
                    _pendingWatch.Restart();
                }
            }

            bool ok = await session.SendAsync(payload, token).ConfigureAwait(false);
            if (!ok)
            {
                lock (_gate) { _pending = null; }
                EmitNotice("发送失败，连接可能已断开", true);
                EmitInfo("发送失败：" + label);
                return false;
            }

            _lastTx = DateTime.Now;
            FrameReady?.Invoke(new Frame
            {
                Time = DateTime.Now,
                Direction = FrameDirection.Tx,
                Kind = kind,
                Data = payload,
                Note = label
            });

            return true;
        }

        // ---------------------------------------------------------------- 收包

        private void OnSessionOpened(TcpSession session)
        {
            lock (_gate) { _session = session; }
            session.Received += OnRawReceived;
            _lastRx = DateTime.Now;
            _heartbeatLostNotified = false;

            SetState(EngineState.Connected, "已连接 " + session.Peer);
            EmitInfo("已连接 " + session.Peer);
        }

        private void OnSessionClosed(string reason)
        {
            bool wasActive;
            lock (_gate)
            {
                _session = null;
                _pending = null;
                wasActive = _cts != null && !_cts.IsCancellationRequested;
            }

            EmitInfo("连接断开：" + reason);
            if (!wasActive) return;   // 主动 Stop() 会自己更新状态

            var profile = _profile;
            if (profile == null) return;

            if (profile.IsServer)
            {
                SetState(EngineState.Listening, "监听中（对端已断开）");
            }
            else if (profile.AutoReconnect)
            {
                SetState(EngineState.Connecting, "已断开，等待重连");
            }
            else
            {
                SetState(EngineState.Disconnected, "已断开：" + reason);
            }
        }

        private void OnRawReceived(byte[] raw)
        {
            _lastRx = DateTime.Now;
            _heartbeatLostNotified = false;

            List<byte[]> frames;
            lock (_gate)
            {
                if (_splitter == null) return;
                frames = _splitter.Feed(raw);
            }

            foreach (var payload in frames) EmitRx(payload);

            // 有字节进来却凑不成帧：多半是对方没发分隔符。不提示的话，界面看起来就是“没收到”。
            if (frames.Count == 0) ReportPartial();
        }

        /// <summary>“收到但未成帧”提示，1 秒内同长度只提示一次，避免刷屏。</summary>
        private void ReportPartial()
        {
            int pending;
            FrameMode mode;
            byte[] buffer;
            lock (_gate)
            {
                if (_splitter == null) return;
                pending = _splitter.PendingLength;
                mode = _splitter.Mode;
                buffer = pending > 0 ? _splitter.Pending() : null;
            }

            if (pending <= 0 || buffer == null || mode == FrameMode.Idle) return;

            var now = DateTime.Now;
            if (pending == _lastPartialLength && (now - _lastPartialNotice).TotalMilliseconds < 1000) return;
            _lastPartialLength = pending;
            _lastPartialNotice = now;

            int show = Math.Min(buffer.Length, 32);
            var head = new byte[show];
            Buffer.BlockCopy(buffer, 0, head, 0, show);

            FrameReady?.Invoke(new Frame
            {
                Time = now,
                Direction = FrameDirection.Info,
                Kind = FrameKind.System,
                Data = new byte[0],
                Note = "已收到 " + pending + " 字节，但没有找到分隔符，暂未成帧：" +
                       TextCodec.ToHex(head) + (buffer.Length > show ? " …" : "") +
                       "（可把切帧方式改成「空闲时间切帧」）"
            });
        }

        private void EmitRx(byte[] payload)
        {
            double? rtt = null;
            lock (_gate)
            {
                if (_pending != null)
                {
                    rtt = _pendingWatch.Elapsed.TotalMilliseconds;
                    _pending = null;
                }
            }

            FrameReady?.Invoke(new Frame
            {
                Time = DateTime.Now,
                Direction = FrameDirection.Rx,
                Kind = FrameKind.Data,
                Data = payload,
                RttMs = rtt
            });
        }

        // ---------------------------------------------------------------- 定时检查

        private void OnTick(object state)
        {
            try
            {
                var cts = _cts;
                var profile = _profile;
                if (cts == null || cts.IsCancellationRequested || profile == null) return;

                // 1) 回包超时
                PendingReply pending;
                double elapsed = 0;
                lock (_gate)
                {
                    pending = _pending;
                    if (pending != null) elapsed = _pendingWatch.Elapsed.TotalMilliseconds;
                }

                if (pending != null && elapsed > pending.TimeoutMs)
                {
                    lock (_gate) { _pending = null; }
                    string note = "等待「" + pending.Label + "」回包超时（" + pending.TimeoutMs + " ms）";
                    FrameReady?.Invoke(new Frame
                    {
                        Time = DateTime.Now,
                        Direction = FrameDirection.Info,
                        Kind = FrameKind.Timeout,
                        Data = new byte[0],
                        Note = note
                    });
                    EmitNotice(note, true);
                }

                bool connected;
                lock (_gate) { connected = _session != null; }
                if (!connected) return;

                // 2) 心跳发送与丢失判定
                if (profile.HeartbeatEnabled)
                {
                    double sinceTx = (DateTime.Now - _lastTx).TotalMilliseconds;
                    bool hasPending;
                    lock (_gate) { hasPending = _pending != null; }

                    if (sinceTx >= profile.HeartbeatIntervalMs && !hasPending)
                    {
                        var bytes = TextCodec.Encode(profile.HeartbeatText, profile.Encoding);

                        // 按分隔符切帧的协议，心跳必须自己带上分隔符，否则对端永远凑不出一帧
                        if (bytes.Length > 0 && profile.FrameMode == FrameMode.Delimiter)
                        {
                            var sep = Delimiters.Resolve(profile.Delimiter);
                            var merged = new byte[bytes.Length + sep.Length];
                            Buffer.BlockCopy(bytes, 0, merged, 0, bytes.Length);
                            Buffer.BlockCopy(sep, 0, merged, bytes.Length, sep.Length);
                            bytes = merged;
                        }

                        if (bytes.Length > 0)
                        {
                            var ignored = SendAsync(bytes, false, 0, FrameKind.Heartbeat, "心跳");
                        }
                    }

                    double sinceRx = (DateTime.Now - _lastRx).TotalMilliseconds;
                    if (sinceRx >= (long)profile.HeartbeatIntervalMs * profile.HeartbeatMiss
                        && !_heartbeatLostNotified)
                    {
                        _heartbeatLostNotified = true;
                        string note = "心跳丢失：已 " + (int)(sinceRx / 1000) + " 秒未收到任何数据";
                        FrameReady?.Invoke(new Frame
                        {
                            Time = DateTime.Now,
                            Direction = FrameDirection.Info,
                            Kind = FrameKind.Error,
                            Data = new byte[0],
                            Note = note
                        });
                        EmitNotice(note, true);
                    }
                }

                // 3) 空闲切帧：无分隔符协议靠“静默多久”判断一帧结束
                if (profile.FrameMode == FrameMode.Idle)
                {
                    List<byte[]> frames = null;
                    lock (_gate)
                    {
                        if (_splitter != null && _splitter.PendingLength > 0)
                        {
                            double idle = (DateTime.Now - _lastRx).TotalMilliseconds;
                            if (idle >= profile.IdleMs) frames = _splitter.FlushIdle();
                        }
                    }
                    if (frames != null)
                    {
                        foreach (var payload in frames) EmitRx(payload);
                    }
                }
            }
            catch (Exception)
            {
                // 定时检查绝不能把进程带崩
            }
        }

        // ---------------------------------------------------------------- 工具

        private void SetState(EngineState state, string detail)
        {
            bool changed = State != state || StateDetail != detail;
            State = state;
            StateDetail = detail ?? string.Empty;
            if (changed) StateChanged?.Invoke(state, StateDetail);
        }

        private void EmitNotice(string text, bool isError)
        {
            var handler = Notice;
            if (handler != null) handler(text, isError);
        }

        private void EmitInfo(string text)
        {
            FrameReady?.Invoke(new Frame
            {
                Time = DateTime.Now,
                Direction = FrameDirection.Info,
                Kind = FrameKind.System,
                Data = new byte[0],
                Note = text
            });
        }

        private void RunSafe(Task task)
        {
            task.ContinueWith(t =>
            {
                var ex = t.Exception;
                if (ex == null) return;
                EmitNotice("后台任务异常：" + ex.GetBaseException().Message, true);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class PendingReply
        {
            public int TimeoutMs { get; set; }
            public string Label { get; set; }
        }
    }
}

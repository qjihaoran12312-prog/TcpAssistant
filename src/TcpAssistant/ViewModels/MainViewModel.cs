using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TcpAssistant.Common;
using TcpAssistant.Models;
using TcpAssistant.Services;

namespace TcpAssistant.ViewModels
{
    /// <summary>
    /// 界面状态的唯一来源：把 CommunicationEngine 的事件搬成可绑定的属性，
    /// 同时把界面操作翻译成引擎调用。所有引擎回调都在 UI 线程落地。
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly CommunicationEngine _engine = new CommunicationEngine();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _statsTimer;
        private readonly DispatcherTimer _loopTimer;

        private ConnectionProfile _selectedProfile;
        private CommandItem _selectedCommand;
        private string _sendText = "START";
        private bool _isHexSend;
        private bool _appendCrlf;
        private bool _expectReply = true;
        private string _replyTimeoutText = "2000";
        private string _loopIntervalText = "1000";
        private bool _isLooping;
        private string _keyword = "";
        private bool _showHex;
        private bool _pauseScroll;
        private bool _isConnected;
        private string _stateKey = "disconnected";
        private string _statusText = "未连接";
        private string _statsText = "收 0 帧 · 发 0 帧";
        private string _noticeText = "";
        private bool _noticeIsError;
        private string _sendPreview = "将发送 0 字节";
        private string _newCommandName = "新指令";
        private bool _showAdvanced;
        private bool _disposed;

        public MainViewModel()
        {
            _dispatcher = Application.Current != null
                ? Application.Current.Dispatcher
                : Dispatcher.CurrentDispatcher;

            Profiles = new ObservableCollection<ConnectionProfile>(ProfileStore.Load());
            _selectedProfile = Profiles.Count > 0 ? Profiles[0] : ConnectionProfile.SkillTcpPreset();
            if (Profiles.Count == 0) Profiles.Add(_selectedProfile);

            Roles = new List<NamedValue<ConnectionRole>>
            {
                new NamedValue<ConnectionRole>("客户端（主动连接）", ConnectionRole.Client),
                new NamedValue<ConnectionRole>("服务端（本机监听）", ConnectionRole.Server)
            };

            FrameModes = new List<NamedValue<FrameMode>>
            {
                new NamedValue<FrameMode>("按分隔符切帧", FrameMode.Delimiter),
                new NamedValue<FrameMode>("固定长度切帧", FrameMode.FixedLength),
                new NamedValue<FrameMode>("长度前缀 2 字节", FrameMode.LengthPrefix),
                new NamedValue<FrameMode>("空闲时间切帧", FrameMode.Idle)
            };

            Encodings = TextCodec.Encodings;
            Delimiters = Models.Delimiters.Names;

            _engine.FrameReady += OnEngineFrame;
            _engine.StateChanged += OnEngineState;
            _engine.Notice += OnEngineNotice;

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _statsTimer.Tick += (s, e) => RefreshStats();
            _statsTimer.Start();

            _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _loopTimer.Tick += (s, e) => SendCurrent();

            ConnectCommand = new RelayCommand(() => Connect(false), () => !IsConnected);
            DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
            SendCommand = new RelayCommand(() => SendCurrent(), () => IsConnected);
            LoadCommandCommand = new RelayCommand(LoadSelectedCommand, () => SelectedCommand != null);
            SendSelectedCommand = new RelayCommand(() => SendCommandItem(SelectedCommand), () => IsConnected && SelectedCommand != null);
            AddCommandCommand = new RelayCommand(AddCommand);
            DeleteCommandCommand = new RelayCommand(DeleteCommand, () => SelectedCommand != null);
            ClearLogCommand = new RelayCommand(ClearLog);
            ExportLogCommand = new RelayCommand(ExportLog);
            DetectYvisonCommand = new RelayCommand(DetectYvison);
            SaveProfileCommand = new RelayCommand(SaveProfiles);
            AddProfileCommand = new RelayCommand(AddProfile);
            DeleteProfileCommand = new RelayCommand(DeleteProfile, () => Profiles.Count > 1);
            ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);

            Raise(nameof(SelectedProfile));
            SelectedCommand = _selectedProfile.Commands.Count > 0 ? _selectedProfile.Commands[0] : null;
            Raise(nameof(SelectedCommand));
            UpdateSendPreview();
        }

        // ---------------------------------------------------------------- 数据

        public ObservableCollection<ConnectionProfile> Profiles { get; }
        public List<NamedValue<ConnectionRole>> Roles { get; }
        public List<NamedValue<FrameMode>> FrameModes { get; }
        public IReadOnlyList<string> Encodings { get; }
        public IReadOnlyList<string> Delimiters { get; }
        public FrameLog Log { get; } = new FrameLog();

        public ConnectionProfile SelectedProfile
        {
            get { return _selectedProfile; }
            set
            {
                if (value == null || value == _selectedProfile) return;
                _selectedProfile = value;
                Raise();
                Raise(nameof(SelectedCommand));
                SelectedCommand = value.Commands.Count > 0 ? value.Commands[0] : null;
                _replyTimeoutText = value.ReplyTimeoutMs.ToString(CultureInfo.InvariantCulture);
                Raise(nameof(ReplyTimeoutText));
                UpdateSendPreview();
            }
        }

        public CommandItem SelectedCommand
        {
            get { return _selectedCommand; }
            set { Set(ref _selectedCommand, value); }
        }

        // ---------------------------------------------------------------- 发送区

        public string SendText
        {
            get { return _sendText; }
            set { if (Set(ref _sendText, value)) UpdateSendPreview(); }
        }

        public bool IsHexSend
        {
            get { return _isHexSend; }
            set { if (Set(ref _isHexSend, value)) UpdateSendPreview(); }
        }

        public bool AppendCrlf
        {
            get { return _appendCrlf; }
            set { if (Set(ref _appendCrlf, value)) UpdateSendPreview(); }
        }

        /// <summary>发送框下方那行提示：实际会发多少字节、内容长什么样，避免 HEX 模式误发。</summary>
        public string SendPreview
        {
            get { return _sendPreview; }
            private set { Set(ref _sendPreview, value); }
        }

        public bool ExpectReply
        {
            get { return _expectReply; }
            set { Set(ref _expectReply, value); }
        }

        public string ReplyTimeoutText
        {
            get { return _replyTimeoutText; }
            set { Set(ref _replyTimeoutText, value); }
        }

        public string LoopIntervalText
        {
            get { return _loopIntervalText; }
            set { Set(ref _loopIntervalText, value); }
        }

        public bool IsLooping
        {
            get { return _isLooping; }
            set
            {
                if (!Set(ref _isLooping, value)) return;
                if (value)
                {
                    int ms = ParseInt(LoopIntervalText, 1000);
                    _loopTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, ms));
                    _loopTimer.Start();
                }
                else
                {
                    _loopTimer.Stop();
                }
            }
        }

        // ---------------------------------------------------------------- 显示选项

        public string Keyword
        {
            get { return _keyword; }
            set { Set(ref _keyword, value); }
        }

        public bool ShowHex
        {
            get { return _showHex; }
            set { Set(ref _showHex, value); }
        }

        public bool PauseScroll
        {
            get { return _pauseScroll; }
            set { Set(ref _pauseScroll, value); }
        }

        public string NewCommandName
        {
            get { return _newCommandName; }
            set { Set(ref _newCommandName, value); }
        }

        /// <summary>展开/收起「重连、心跳、超时」这类低频设置，小屏下默认收起。</summary>
        public bool ShowAdvanced
        {
            get { return _showAdvanced; }
            set { if (Set(ref _showAdvanced, value)) Raise(nameof(AdvancedToggleText)); }
        }

        public string AdvancedToggleText
        {
            get { return _showAdvanced ? "高级设置 ▴" : "高级设置 ▾"; }
        }

        // ---------------------------------------------------------------- 状态

        public bool IsConnected
        {
            get { return _isConnected; }
            private set
            {
                if (Set(ref _isConnected, value))
                {
                    // 按钮的可用状态交给 CommandManager 统一刷新
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                    if (!value) IsLooping = false;
                }
            }
        }

        public string StateKey
        {
            get { return _stateKey; }
            private set { Set(ref _stateKey, value); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { Set(ref _statusText, value); }
        }

        public string StatsText
        {
            get { return _statsText; }
            private set { Set(ref _statsText, value); }
        }

        public string NoticeText
        {
            get { return _noticeText; }
            private set { Set(ref _noticeText, value); }
        }

        public bool NoticeIsError
        {
            get { return _noticeIsError; }
            private set { Set(ref _noticeIsError, value); }
        }

        // ---------------------------------------------------------------- 命令

        public RelayCommand ConnectCommand { get; private set; }
        public RelayCommand DisconnectCommand { get; private set; }
        public RelayCommand SendCommand { get; private set; }
        public RelayCommand LoadCommandCommand { get; private set; }
        public RelayCommand SendSelectedCommand { get; private set; }
        public RelayCommand AddCommandCommand { get; private set; }
        public RelayCommand DeleteCommandCommand { get; private set; }
        public RelayCommand ClearLogCommand { get; private set; }
        public RelayCommand ExportLogCommand { get; private set; }
        public RelayCommand DetectYvisonCommand { get; private set; }
        public RelayCommand SaveProfileCommand { get; private set; }
        public RelayCommand AddProfileCommand { get; private set; }
        public RelayCommand DeleteProfileCommand { get; private set; }
        public RelayCommand ToggleAdvancedCommand { get; private set; }

        /// <summary>有新帧时通知视图追加到接收区。</summary>
        public event Action<Frame> FrameAppended;

        // ---------------------------------------------------------------- 连接

        private void Connect(bool _)
        {
            var profile = SelectedProfile;
            if (profile == null) return;

            if (profile.Port <= 0 || profile.Port > 65535)
            {
                SetNotice("端口号必须在 1~65535 之间", true);
                return;
            }
            if (!TextCodec.IsSupportedEncoding(profile.Encoding))
            {
                SetNotice("编码不受支持，已回退 UTF-8", true);
                profile.Encoding = "UTF-8";
            }

            Log.Clear();
            FrameAppended?.Invoke(null);      // 视图收到 null = 清空接收区
            _engine.Start(profile);
            SetNotice((profile.IsServer ? "开始监听 " : "开始连接 ") + profile.Endpoint, false);
        }

        private void Disconnect()
        {
            _engine.Stop();
            IsLooping = false;
            SetNotice("已断开", false);
        }

        // ---------------------------------------------------------------- 发送

        private async void SendCurrent()
        {
            try
            {
                var profile = SelectedProfile;
                if (profile == null) return;

                string error;
                var payload = BuildPayload(SendText, IsHexSend, AppendCrlf, out error);
                if (error != null)
                {
                    SetNotice(error, true);
                    return;
                }
                if (payload.Length == 0)
                {
                    SetNotice("发送内容为空", true);
                    return;
                }

                int timeout = ParseInt(ReplyTimeoutText, profile.ReplyTimeoutMs);
                bool ok = await _engine.SendAsync(payload, ExpectReply, ExpectReply ? timeout : 0,
                                                  FrameKind.Data, IsLooping ? "循环发送" : "手动发送");
                if (!ok && !IsConnected) SetNotice("未连接，发送已取消", true);
            }
            catch (Exception ex)
            {
                SetNotice("发送异常：" + ex.Message, true);
            }
        }

        private async void SendCommandItem(CommandItem cmd)
        {
            if (cmd == null) return;
            try
            {
                LoadSelectedCommand();
                string error;
                var payload = BuildPayload(cmd.Text, cmd.IsHex, cmd.AppendCrLf, out error);
                if (error != null)
                {
                    SetNotice(error, true);
                    return;
                }
                int timeout = ParseInt(ReplyTimeoutText, SelectedProfile.ReplyTimeoutMs);
                await _engine.SendAsync(payload, cmd.ExpectReply, cmd.ExpectReply ? timeout : 0,
                                        FrameKind.Data, cmd.Name);
            }
            catch (Exception ex)
            {
                SetNotice("发送异常：" + ex.Message, true);
            }
        }

        private byte[] BuildPayload(string text, bool isHex, bool appendCrlf, out string error)
        {
            error = null;
            byte[] body;
            string encoding = SelectedProfile != null ? SelectedProfile.Encoding : "UTF-8";

            if (isHex)
            {
                if (!TextCodec.TryParseHex(text, out body, out error)) return new byte[0];
            }
            else
            {
                body = TextCodec.Encode(TextCodec.Unescape(text ?? string.Empty), encoding);
            }

            return appendCrlf ? WithCrLf(body) : body;
        }

        private static byte[] WithCrLf(byte[] body)
        {
            var merged = new byte[body.Length + 2];
            Buffer.BlockCopy(body, 0, merged, 0, body.Length);
            merged[body.Length] = 0x0D;
            merged[body.Length + 1] = 0x0A;
            return merged;
        }

        private void LoadSelectedCommand()
        {
            var cmd = SelectedCommand;
            if (cmd == null) return;
            SendText = cmd.Text;
            IsHexSend = cmd.IsHex;
            AppendCrlf = cmd.AppendCrLf;
            ExpectReply = cmd.ExpectReply;
        }

        /// <summary>重算「即将发送」的字节数与内容；HEX 模式下能一眼看出实际发的是什么。</summary>
        private void UpdateSendPreview()
        {
            string error;
            var bytes = BuildPayload(SendText, IsHexSend, AppendCrlf, out error);
            if (error != null)
            {
                SendPreview = "⚠ " + error;
                return;
            }
            if (bytes.Length == 0)
            {
                SendPreview = "将发送 0 字节";
                return;
            }

            var encoding = SelectedProfile != null ? SelectedProfile.Encoding : "UTF-8";
            string body = IsHexSend
                ? TextCodec.ToHex(bytes)
                : TextCodec.BuildPreview(bytes, encoding, 48);
            SendPreview = "将发送 " + bytes.Length + " 字节 · " + body;
        }

        /// <summary>供命令行 / 脚本在连接建立后立刻发一条指令（--send "STATUS"）。</summary>
        public void SendImmediately(string text, bool appendCrlf = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            SendText = text;
            IsHexSend = false;
            AppendCrlf = appendCrlf;
            SendCurrent();
        }

        // ---------------------------------------------------------------- 指令管理

        private void AddCommand()
        {
            if (SelectedProfile == null) return;
            if (string.IsNullOrWhiteSpace(SendText))
            {
                SetNotice("发送区为空，先写点内容再保存为指令", true);
                return;
            }

            var cmd = new CommandItem
            {
                Name = string.IsNullOrWhiteSpace(NewCommandName) ? "新指令" : NewCommandName.Trim(),
                Text = SendText,
                IsHex = IsHexSend,
                AppendCrLf = AppendCrlf,
                ExpectReply = ExpectReply
            };
            SelectedProfile.Commands.Add(cmd);
            SelectedCommand = cmd;
            SetNotice("已添加指令：" + cmd.Name, false);
        }

        private void DeleteCommand()
        {
            var cmd = SelectedCommand;
            if (cmd == null || SelectedProfile == null) return;
            SelectedProfile.Commands.Remove(cmd);
            SelectedCommand = SelectedProfile.Commands.Count > 0 ? SelectedProfile.Commands[0] : null;
            SetNotice("已删除指令", false);
        }

        // ---------------------------------------------------------------- 配置

        private void SaveProfiles()
        {
            try
            {
                ProfileStore.Save(Profiles);
                SetNotice("配置已保存：" + ProfileStore.DefaultPath, false);
            }
            catch (Exception ex)
            {
                SetNotice("保存失败：" + ex.Message, true);
            }
        }

        private void AddProfile()
        {
            var copy = SelectedProfile != null ? SelectedProfile.Clone() : ConnectionProfile.SkillTcpPreset();
            copy.Name = copy.Name + " 副本";
            Profiles.Add(copy);
            SelectedProfile = copy;
            SetNotice("已新建配置：" + copy.Name, false);
        }

        private void DeleteProfile()
        {
            if (Profiles.Count <= 1 || SelectedProfile == null) return;
            int index = Profiles.IndexOf(SelectedProfile);
            Profiles.Remove(SelectedProfile);
            SelectedProfile = Profiles[Math.Max(0, Math.Min(index, Profiles.Count - 1))];
        }

        private void DetectYvison()
        {
            var root = YvisonConfigReader.FindRoot();
            if (string.IsNullOrEmpty(root))
            {
                SetNotice("未找到 Yvison.exe：把本工具放在 Yvison 目录下即可自动识别端口", true);
                return;
            }

            var monitor = YvisonConfigReader.ReadMonitorPort(root);
            var skill = YvisonConfigReader.ReadSkillTcpPort(root);
            var skillHost = YvisonConfigReader.ReadSkillTcpHost(root);
            var profile = SelectedProfile;

            if (profile != null)
            {
                if (!string.IsNullOrEmpty(skillHost)) profile.Host = skillHost;
                if (profile.Port == 2001 && monitor.HasValue) profile.Port = monitor.Value;
                else if (skill.HasValue) profile.Port = skill.Value;
                else if (monitor.HasValue) profile.Port = monitor.Value;
            }

            SetNotice(YvisonConfigReader.Describe(root), false);
        }

        // ---------------------------------------------------------------- 日志

        private void ClearLog()
        {
            Log.Clear();
            FrameAppended?.Invoke(null);
            RefreshStats();
        }

        /// <summary>供界面/自检立即刷新统计（正常由 400ms 定时器驱动）。</summary>
        public void RefreshStatsNow()
        {
            RefreshStats();
        }

        private void ExportLog()
        {
            if (Log.Items.Count == 0)
            {
                SetNotice("日志为空，没有可导出的内容", true);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出通信日志",
                FileName = "tcp-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Filter = "CSV（Excel 可打开）|*.csv|纯文本|*.txt",
                DefaultExt = "csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var encoding = SelectedProfile != null ? SelectedProfile.Encoding : "UTF-8";
                int count = dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? Log.ExportText(dialog.FileName, encoding)
                    : Log.ExportCsv(dialog.FileName, encoding);
                SetNotice("已导出 " + count + " 条记录：" + dialog.FileName, false);
            }
            catch (Exception ex)
            {
                SetNotice("导出失败：" + ex.Message, true);
            }
        }

        // ---------------------------------------------------------------- 引擎事件（后台线程）

        private void OnEngineFrame(Frame frame)
        {
            Post(() =>
            {
                var encoding = SelectedProfile != null ? SelectedProfile.Encoding : "UTF-8";
                var added = Log.Add(frame.Direction, frame.Kind, frame.Data, encoding, frame.Note, frame.RttMs);
                FrameAppended?.Invoke(added);
                RefreshStats();
            });
        }

        private void OnEngineState(EngineState state, string detail)
        {
            Post(() =>
            {
                switch (state)
                {
                    case EngineState.Connected:
                        StateKey = "connected";
                        IsConnected = true;
                        break;
                    case EngineState.Listening:
                        StateKey = "listening";
                        IsConnected = true;
                        break;
                    case EngineState.Connecting:
                        StateKey = "connecting";
                        IsConnected = false;
                        break;
                    default:
                        StateKey = "disconnected";
                        IsConnected = false;
                        break;
                }
                StatusText = detail;
            });
        }

        private void OnEngineNotice(string text, bool isError)
        {
            Post(() => SetNotice(text, isError));
        }

        private void Post(Action action)
        {
            if (_disposed) return;
            try
            {
                if (_dispatcher.CheckAccess()) action();
                else _dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // 窗口关闭过程中的排队回调，忽略即可
            }
        }

        private void RefreshStats()
        {
            long rx = Log.Bytes(FrameDirection.Rx);
            long tx = Log.Bytes(FrameDirection.Tx);
            int rxC = Log.Count(FrameDirection.Rx);
            int txC = Log.Count(FrameDirection.Tx);
            var avg = Log.AverageRtt();
            int pending = _engine.PendingBufferBytes;

            StatsText = string.Format(CultureInfo.InvariantCulture,
                "收 {0} 帧 / {1} B　·　发 {2} 帧 / {3} B　·　平均 RTT {4}　·　半包 {5} B",
                rxC, rx, txC, tx,
                avg.HasValue ? avg.Value.ToString("0") + " ms" : "—",
                pending);
        }

        private void SetNotice(string text, bool isError)
        {
            NoticeText = text ?? string.Empty;
            NoticeIsError = isError;
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse((text ?? string.Empty).Trim(), out value) && value > 0 ? value : fallback;
        }

        public void Dispose()
        {
            _disposed = true;
            _statsTimer.Stop();
            _loopTimer.Stop();
            _engine.Dispose();
        }
    }
}

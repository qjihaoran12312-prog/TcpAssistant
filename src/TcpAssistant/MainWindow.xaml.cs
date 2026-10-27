using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TcpAssistant.Models;
using TcpAssistant.Services;
using TcpAssistant.ViewModels;

// Models.Frame 与 System.Windows.Controls.Frame 重名，这里明确指向业务模型
using Frame = TcpAssistant.Models.Frame;

namespace TcpAssistant
{
    /// <summary>
    /// 只做“视图相关”的事：把帧渲染成带颜色的行、自动滚动、双击回填。
    /// 业务判断全部在 MainViewModel / CommunicationEngine 里。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxRenderedLines = 1500;

        private static readonly Brush RxBrush = Frozen("#1B2430");
        private static readonly Brush TxBrush = Frozen("#2E7DD1");
        private static readonly Brush MetaBrush = Frozen("#8894A3");
        private static readonly Brush WarnBrush = Frozen("#C97A16");
        private static readonly Brush ErrBrush = Frozen("#D0382E");
        private static readonly Brush HeartBrush = Frozen("#9AA6B4");

        private readonly MainViewModel _vm;

        /// <summary>供自检/快照工具读取当前界面状态。</summary>
        public MainViewModel ViewModel
        {
            get { return _vm; }
        }

        /// <summary>按当前日志重绘通信记录（快照、切换 HEX 显示都会用到）。</summary>
        public void RefreshDisplay()
        {
            RerenderAll();
        }

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainViewModel();
            DataContext = _vm;

            _vm.FrameAppended += OnFrameAppended;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Log.Items.CollectionChanged += OnLogChanged;

            Closing += (s, e) => _vm.Dispose();

            if (!string.IsNullOrEmpty(App.SnapshotPath) || App.AutoConnect) Loaded += OnStartupLoaded;
        }

        /// <summary>启动后的动作：可选自动连接；快照模式下多等一会儿再截图。</summary>
        private void OnStartupLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnStartupLoaded;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (App.ListenPort > 0 && _vm.SelectedProfile != null)
                {
                    // 命令行指定监听端口：切到服务端，让对端主动连进来
                    _vm.SelectedProfile.Role = ConnectionRole.Server;
                    _vm.SelectedProfile.Host = "0.0.0.0";
                    _vm.SelectedProfile.Port = App.ListenPort;
                }

                if (App.AutoConnect && _vm.ConnectCommand.CanExecute(null))
                {
                    _vm.ConnectCommand.Execute(null);
                }

                if (!string.IsNullOrEmpty(App.SendOnConnect))
                {
                    // 等连接真正建立再发，最多等 8 秒
                    int waited = 0;
                    var waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    waitTimer.Tick += (s, args) =>
                    {
                        waited += 200;
                        if (!_vm.IsConnected && waited < 8000) return;
                        waitTimer.Stop();
                        if (_vm.IsConnected) _vm.SendImmediately(App.SendOnConnect);
                    };
                    waitTimer.Start();
                }

                if (string.IsNullOrEmpty(App.SnapshotPath)) return;

                if (App.AutoConnect)
                {
                    // 真连一次再截图，等心跳跑出一轮真实收发
                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(Math.Max(300, App.SnapshotDelayMs))
                    };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        Diagnostics.SnapshotTool.Capture(this, App.SnapshotPath, App.SnapshotWidth, App.SnapshotHeight);
                        Application.Current.Shutdown(0);
                    };
                    timer.Start();
                    return;
                }

                Diagnostics.SnapshotTool.Seed(this);
                Diagnostics.SnapshotTool.ScrollToTop(this);
                UpdateLayout();
                Diagnostics.SnapshotTool.Capture(this, App.SnapshotPath, App.SnapshotWidth, App.SnapshotHeight);
                Application.Current.Shutdown(0);
            }), DispatcherPriority.Loaded);
        }

        private static Brush Frozen(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        // ---------------------------------------------------------------- 帧渲染

        private void OnFrameAppended(Frame frame)
        {
            if (frame == null)
            {
                ReceiveBox.Document.Blocks.Clear();
                return;
            }
            if (!MatchKeyword(frame)) return;
            AppendLine(frame);
        }

        private void AppendLine(Frame frame)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("[" + frame.TimeText + "] ") { Foreground = MetaBrush });
            paragraph.Inlines.Add(new Run(frame.DirText + " ") { Foreground = DirectionBrush(frame), FontWeight = FontWeights.SemiBold });

            string body;
            if (frame.Data != null && frame.Data.Length > 0)
            {
                var encoding = _vm.SelectedProfile != null ? _vm.SelectedProfile.Encoding : "UTF-8";
                body = _vm.ShowHex ? TextCodec.ToHex(frame.Data) : TextCodec.Decode(frame.Data, encoding);
            }
            else
            {
                body = frame.Note ?? string.Empty;
            }

            body = body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            paragraph.Inlines.Add(new Run(body) { Foreground = BodyBrush(frame) });

            if (frame.RttMs.HasValue)
            {
                paragraph.Inlines.Add(new Run("  <" + frame.RttMs.Value.ToString("0") + " ms>") { Foreground = MetaBrush });
            }

            ReceiveBox.Document.Blocks.Add(paragraph);

            while (ReceiveBox.Document.Blocks.Count > MaxRenderedLines)
            {
                var first = ReceiveBox.Document.Blocks.FirstBlock;
                if (first == null) break;
                ReceiveBox.Document.Blocks.Remove(first);
            }

            if (!_vm.PauseScroll) ReceiveBox.ScrollToEnd();
        }

        private static Brush DirectionBrush(Frame frame)
        {
            if (frame.Kind == FrameKind.Timeout) return WarnBrush;
            if (frame.Kind == FrameKind.Error) return ErrBrush;
            if (frame.Kind == FrameKind.System) return MetaBrush;
            if (frame.Kind == FrameKind.Heartbeat) return HeartBrush;
            return frame.Direction == FrameDirection.Tx ? TxBrush : RxBrush;
        }

        private static Brush BodyBrush(Frame frame)
        {
            switch (frame.Kind)
            {
                case FrameKind.Timeout: return WarnBrush;
                case FrameKind.Error: return ErrBrush;
                case FrameKind.System: return MetaBrush;
                case FrameKind.Heartbeat: return HeartBrush;
                default: return frame.Direction == FrameDirection.Tx ? TxBrush : RxBrush;
            }
        }

        private bool MatchKeyword(Frame frame)
        {
            string keyword = (_vm.Keyword ?? string.Empty).Trim();
            if (keyword.Length == 0) return true;

            string haystack = ((frame.Preview ?? string.Empty) + " " + (frame.Note ?? string.Empty)).ToLowerInvariant();
            return haystack.Contains(keyword.ToLowerInvariant());
        }

        private void RerenderAll()
        {
            ReceiveBox.Document.Blocks.Clear();

            // 只重绘最近若干条：过滤框每敲一个字都会触发重绘，全量重排会卡手
            int total = _vm.Log.Items.Count;
            int start = Math.Max(0, total - 400);
            for (int i = start; i < total; i++)
            {
                var frame = _vm.Log.Items[i];
                if (!MatchKeyword(frame)) continue;
                AppendLine(frame);
            }
            if (!_vm.PauseScroll) ReceiveBox.ScrollToEnd();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ShowHex))
            {
                RerenderAll();
            }
            else if (e.PropertyName == nameof(MainViewModel.Keyword))
            {
                RerenderAll();
                ApplyLogFilter();
            }
        }

        private void OnLogChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_vm.PauseScroll) return;
            if (_vm.Log.Items.Count == 0) return;
            LogGrid.ScrollIntoView(_vm.Log.Items[_vm.Log.Items.Count - 1]);
        }

        private void ApplyLogFilter()
        {
            var view = CollectionViewSource.GetDefaultView(LogGrid.ItemsSource);
            if (view == null) return;

            string keyword = (_vm.Keyword ?? string.Empty).Trim().ToLowerInvariant();
            if (keyword.Length == 0)
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = item =>
                {
                    var frame = item as Frame;
                    if (frame == null) return false;
                    string haystack = ((frame.Preview ?? string.Empty) + " " + (frame.Note ?? string.Empty)
                                       + " " + frame.DirText + " " + frame.KindText).ToLowerInvariant();
                    return haystack.Contains(keyword);
                };
            }
            view.Refresh();
        }

        // ---------------------------------------------------------------- 交互

        private void SendBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
            }
        }

        private void CommandList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_vm.SelectedCommand == null) return;
            if (_vm.SendSelectedCommand.CanExecute(null)) _vm.SendSelectedCommand.Execute(null);
        }

        private void LogGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var frame = LogGrid.SelectedItem as Frame;
            if (frame == null || frame.Data == null || frame.Data.Length == 0) return;

            var encoding = _vm.SelectedProfile != null ? _vm.SelectedProfile.Encoding : "UTF-8";
            _vm.SendText = TextCodec.Decode(frame.Data, encoding);
            _vm.IsHexSend = false;
            SendBox.Focus();
            SendBox.CaretIndex = SendBox.Text.Length;
        }
    }
}

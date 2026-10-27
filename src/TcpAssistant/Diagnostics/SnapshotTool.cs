using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TcpAssistant.Models;
using TcpAssistant.Services;

namespace TcpAssistant.Diagnostics
{
    /// <summary>
    /// 界面快照：按指定尺寸排列主窗口的可视化树并渲染成 PNG。
    /// 不依赖屏幕大小，可以在任意分辨率下检查布局（宽屏 / 窄屏都能过一遍）。
    /// 命令行：TcpAssistant.exe --snapshot out.png [宽 高]
    /// </summary>
    internal static class SnapshotTool
    {
        public static void Capture(MainWindow window, string outputPath, int width, int height)
        {
            try
            {
                var root = window.Content as FrameworkElement;
                if (root == null) throw new InvalidOperationException("窗口没有内容");

                if (width <= 0) width = (int)Math.Round(window.ActualWidth);
                if (height <= 0) height = (int)Math.Round(window.ActualHeight);

                // 先让窗口按目标尺寸重新排一次版，再按内容实际大小截图
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

                width = (int)Math.Round(root.ActualWidth);
                height = (int)Math.Round(root.ActualHeight);

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(outputPath))
                {
                    encoder.Save(stream);
                }

                WriteReport(window, outputPath, width, height);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(outputPath + ".error.txt", ex.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void WriteReport(MainWindow window, string outputPath, int width, int height)
        {
            try
            {
                var vm = window.ViewModel;
                var sb = new StringBuilder();
                sb.AppendLine("size=" + width + "x" + height);
                sb.AppendLine("logFrames=" + vm.Log.Items.Count);
                sb.AppendLine("rendered=" + window.ReceiveBox.Document.Blocks.Count);
                sb.AppendLine("receiveBox=" + window.ReceiveBox.ActualWidth.ToString("0") + "x" +
                              window.ReceiveBox.ActualHeight.ToString("0"));
                sb.AppendLine("sendBox=" + window.SendBox.ActualWidth.ToString("0") + "x" +
                              window.SendBox.ActualHeight.ToString("0"));
                sb.AppendLine("heartbeatMiss=" + (vm.SelectedProfile == null ? "null" : vm.SelectedProfile.HeartbeatMiss.ToString()));
                File.WriteAllText(outputPath + ".info.txt", sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>塞几帧样例，快照里能顺带检查收发配色与日志列宽。</summary>
        public static void Seed(MainWindow window)
        {
            var vm = window.ViewModel;
            vm.Log.Clear();

            var encoding = vm.SelectedProfile != null ? vm.SelectedProfile.Encoding : "UTF-8";

            vm.Log.Add(FrameDirection.Info, FrameKind.System, new byte[0], encoding, "已连接 127.0.0.1:2002");
            vm.Log.Add(FrameDirection.Tx, FrameKind.Data, TextCodec.Encode("START", encoding), encoding, "启动执行");
            vm.Log.Add(FrameDirection.Rx, FrameKind.Data, TextCodec.Encode("OK", encoding), encoding, null, 12);
            vm.Log.Add(FrameDirection.Tx, FrameKind.Data, TextCodec.Encode("TRIG 1", encoding), encoding, "触发一次");
            vm.Log.Add(FrameDirection.Rx, FrameKind.Data,
                       TextCodec.Encode("{ result = \"OK\", score = 0.987, id = 1 }", encoding), encoding, null, 18);
            vm.Log.Add(FrameDirection.Tx, FrameKind.Heartbeat, TextCodec.Encode("PING", encoding), encoding, "心跳");
            vm.Log.Add(FrameDirection.Info, FrameKind.Timeout, new byte[0], encoding, "等待「查询状态」回包超时（2000 ms）");
            vm.Log.Add(FrameDirection.Rx, FrameKind.Data,
                       TextCodec.Encode("{ ok = true, vision = \"5\", logs = [\"服务器已启动，端口 2002\"], count = 1 }", encoding),
                       encoding, null, 9);

            window.RefreshDisplay();
            vm.RefreshStatsNow();
        }

        public static void ScrollToTop(MainWindow window)
        {
            window.ReceiveBox.ScrollToHome();
        }
    }
}

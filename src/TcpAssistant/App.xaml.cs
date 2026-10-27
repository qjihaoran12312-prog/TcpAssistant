using System.Windows;
using System.Windows.Threading;

namespace TcpAssistant
{
    public partial class App : Application
    {
        /// <summary>非空表示以「快照模式」运行：界面加载完成后渲染一张 PNG 再退出。</summary>
        public static string SnapshotPath { get; private set; }

        public static int SnapshotWidth { get; private set; }
        public static int SnapshotHeight { get; private set; }

        /// <summary>--connect：启动后自动按当前配置发起连接（快捷方式里很实用）。</summary>
        public static bool AutoConnect { get; private set; }

        /// <summary>--delay：截图前等待多少毫秒，留给连接、心跳跑出真实数据。</summary>
        public static int SnapshotDelayMs { get; private set; } = 1200;

        /// <summary>--send "STATUS"：连上之后立刻发一条指令，用于命令行联调。</summary>
        public static string SendOnConnect { get; private set; }

        /// <summary>--listen 2003：强制以服务端身份监听指定端口（对方当客户端连进来）。</summary>
        public static int ListenPort { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 未处理异常不让程序直接消失，现场调试时最怕这个
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--connect") AutoConnect = true;

                if (e.Args[i] == "--delay" && i + 1 < e.Args.Length)
                {
                    int delay;
                    if (int.TryParse(e.Args[i + 1], out delay)) SnapshotDelayMs = delay;
                }

                if (e.Args[i] == "--send" && i + 1 < e.Args.Length) SendOnConnect = e.Args[i + 1];

                if (e.Args[i] == "--listen" && i + 1 < e.Args.Length)
                {
                    int port;
                    if (int.TryParse(e.Args[i + 1], out port) && port > 0 && port <= 65535) ListenPort = port;
                }
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
            {
                SnapshotPath = e.Args[1];

                // 可选：--snapshot out.png 1100 720，用于检查窄窗口下的布局
                int width, height;
                if (e.Args.Length >= 4 &&
                    int.TryParse(e.Args[2], out width) && int.TryParse(e.Args[3], out height))
                {
                    SnapshotWidth = width;
                    SnapshotHeight = height;
                }
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                "程序遇到未处理的异常：\n\n" + e.Exception.Message + "\n\n" + e.Exception.StackTrace,
                "TCP 通信助手",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}

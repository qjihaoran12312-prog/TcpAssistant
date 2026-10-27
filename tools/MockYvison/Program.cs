using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MockYvison
{
    /// <summary>
    /// 假 Yvison：同时监听 Skill TCP(2002) 与 外部监控(2001)。
    /// 没有现场设备时用它验证通信助手本身，也能反过来验证上位机。
    ///
    /// 用法：MockYvison.exe [--skill 2002] [--monitor 2001]
    /// </summary>
    internal static class Program
    {
        private static int _trigCount;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            int skill = ReadArg(args, "--skill", 2002);
            int monitor = ReadArg(args, "--monitor", 2001);

            Console.WriteLine("========================================");
            Console.WriteLine(" Yvison 模拟服务端");
            Console.WriteLine("   Skill TCP : " + skill);
            Console.WriteLine("   外部监控  : " + monitor);
            Console.WriteLine("   Ctrl+C 退出");
            Console.WriteLine("========================================");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            var a = ListenAsync(skill, "Skill", HandleSkill, cts.Token);
            var b = ListenAsync(monitor, "监控", HandleMonitor, cts.Token);

            Task.WaitAll(a, b);
            Console.WriteLine("已退出。");
            return 0;
        }

        private static int ReadArg(string[] args, string name, int fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value)) return value;
                }
            }
            return fallback;
        }

        private static async Task ListenAsync(int port, string tag, Func<string, string> handler, CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[" + tag + "] 监听 " + port + " 失败：" + ex.Message);
                return;
            }

            Console.WriteLine("[" + tag + "] 已监听 0.0.0.0:" + port);

            using (token.Register(() => { try { listener.Stop(); } catch { } }))
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    Console.WriteLine("[" + tag + "] 客户端接入 " + client.Client.RemoteEndPoint);
                    var ignored = ServeAsync(client, tag, handler, token);
                }
            }
        }

        private static async Task ServeAsync(TcpClient client, string tag, Func<string, string> handler, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                var pending = new StringBuilder();

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                        if (n <= 0) break;

                        pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
                        string text = pending.ToString();

                        int idx;
                        while ((idx = text.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            string line = text.Substring(0, idx);
                            text = text.Substring(idx + 2);

                            Console.WriteLine("[" + tag + "] <- " + line);
                            string reply = handler(line);
                            if (reply == null)
                            {
                                Console.WriteLine("[" + tag + "] （本帧故意不回，用于验证超时）");
                                continue;
                            }

                            var bytes = Encoding.UTF8.GetBytes(reply + "\r\n");
                            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                            Console.WriteLine("[" + tag + "] -> " + reply);
                        }

                        pending.Clear();
                        pending.Append(text);
                    }
                }
                catch (Exception)
                {
                    // 对端断开或取消，直接收尾
                }

                Console.WriteLine("[" + tag + "] 连接断开");
            }
        }

        // ---------------------------------------------------------------- 协议

        private static string HandleSkill(string line)
        {
            string cmd = (line ?? string.Empty).Trim();
            if (cmd.Length == 0) return "ERR EMPTY";

            if (Equals(cmd, "START")) return "OK";
            if (Equals(cmd, "STOP")) return "OK";
            if (Equals(cmd, "PING")) return "PONG";
            if (Equals(cmd, "STATUS")) return "{ ok = true, vision = \"5\", running = true }";
            if (Equals(cmd, "SLOW")) return null;                 // 延迟不回复：验证“回包超时”
            if (cmd.StartsWith("TRIG", StringComparison.OrdinalIgnoreCase))
            {
                int id = 1;
                var parts = cmd.Split(' ');
                if (parts.Length > 1) int.TryParse(parts[1], out id);
                _trigCount++;
                return "{ result = \"OK\", score = 0.987, id = " + id + ", seq = " + _trigCount + " }";
            }

            return "ERR UNKNOWN";
        }

        private static bool Equals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string HandleMonitor(string line)
        {
            string cmd = (line ?? string.Empty).Trim();
            if (cmd.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "{ ok = true, vision = \"5\", running = true, count = 1 }";
            }
            if (cmd.IndexOf("projects", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "{ ok = true, vision = \"5\", logs = [\"Test\"], count = 1 }";
            }
            if (cmd.IndexOf("logs", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "{ ok = true, vision = \"5\", logs = [\"Skill TCP 服务器已启动，端口 2002\"], count = 1 }";
            }
            return "{ ok = false, vision = \"5\", logs = [], count = 0 }";
        }
    }
}

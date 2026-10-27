using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpAssistant.Models;
using TcpAssistant.Services;
using Frame = TcpAssistant.Models.Frame;

namespace TcpAssistant.SelfTest
{
    /// <summary>
    /// 无界面自测：编解码、切帧、配置序列化、以及引擎对真实 socket 的收发与超时。
    /// 用法：SelfTest.exe  （返回码 0 表示全部通过）
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("TCP 通信助手 · 自测");
            Console.WriteLine("----------------------------------------");

            TestCodec();
            TestSplitter();
            TestProfileRoundTrip();
            TestEngineLoopback();
            TestTimeout();

            Console.WriteLine("----------------------------------------");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failed + " 项");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 断言

        private static void Check(string name, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FAIL] " + name + (detail == null ? "" : "  ->  " + detail));
            }
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        // ---------------------------------------------------------------- 编解码

        private static void TestCodec()
        {
            Console.WriteLine("[编码 / HEX / 转义]");

            byte[] bytes;
            string error;
            Check("HEX 解析（带空格与 0x）",
                  TextCodec.TryParseHex("0x01 0A ff", out bytes, out error) &&
                  bytes.SequenceEqual(new byte[] { 0x01, 0x0A, 0xFF }));

            Check("HEX 奇数长度被拒绝",
                  !TextCodec.TryParseHex("ABC", out bytes, out error) && error != null);

            Check("HEX 格式化",
                  TextCodec.ToHex(new byte[] { 0x0D, 0x0A }) == "0D 0A");

            Check("转义还原 \\r\\n",
                  TextCodec.Unescape("A\\r\\nB") == "A\r\nB");

            Check("转义还原 \\x41",
                  TextCodec.Unescape("\\x41\\x42") == "AB");

            string gbk = TextCodec.Decode(TextCodec.Encode("启动执行", "GBK"), "GBK");
            Check("GBK 往返", gbk == "启动执行", gbk);

            Check("可读性差的报文退化成 HEX",
                  TextCodec.BuildPreview(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "UTF-8").Contains("00"));
        }

        // ---------------------------------------------------------------- 切帧

        private static void TestSplitter()
        {
            Console.WriteLine("[切帧]");

            var splitter = new FrameSplitter(FrameMode.Delimiter, new byte[] { 0x0D, 0x0A }, 0);
            var first = splitter.Feed(Encoding.UTF8.GetBytes("OK"));
            Check("半包不吐帧", first.Count == 0 && splitter.PendingLength == 2);

            var second = splitter.Feed(Encoding.UTF8.GetBytes("\r\nPONG\r\n"));
            Check("补齐后一次吐两帧",
                  second.Count == 2 &&
                  Encoding.UTF8.GetString(second[0]) == "OK" &&
                  Encoding.UTF8.GetString(second[1]) == "PONG");

            var fixedSplitter = new FrameSplitter(FrameMode.FixedLength, null, 3);
            var frames = fixedSplitter.Feed(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            Check("固定长度切帧", frames.Count == 2 && frames[1][0] == 4 && fixedSplitter.PendingLength == 1);

            var lengthSplitter = new FrameSplitter(FrameMode.LengthPrefix, null, 0);
            var payload = new byte[] { 0x00, 0x03, 0x41, 0x42, 0x43 };
            var lengthFrames = lengthSplitter.Feed(payload);
            Check("长度前缀切帧",
                  lengthFrames.Count == 1 && Encoding.ASCII.GetString(lengthFrames[0]) == "ABC");

            var idle = new FrameSplitter(FrameMode.Idle, null, 0);
            idle.Feed(Encoding.UTF8.GetBytes("RAW"));
            Check("空闲切帧 flush", idle.FlushIdle().Count == 1);
        }

        // ---------------------------------------------------------------- 配置

        private static void TestProfileRoundTrip()
        {
            Console.WriteLine("[配置序列化]");

            var path = Path.Combine(Path.GetTempPath(), "tcp-assistant-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var profile = ConnectionProfile.SkillTcpPreset();
                profile.Port = 2345;
                profile.HeartbeatEnabled = true;
                try
                {
                    ProfileStore.Save(new[] { profile }, path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    Save 抛出：" + ex.GetType().FullName + " / " + ex.Message);
                    _failed++;
                    return;
                }

                var loaded = ProfileStore.Load(path);
                Check("读回一条配置", loaded.Count == 1);
                Check("端口保持一致", loaded[0].Port == 2345);
                Check("心跳开关保持", loaded[0].HeartbeatEnabled);
                Check("指令条数保持",
                      loaded[0].Commands.Count == profile.Commands.Count,
                      "期望 " + profile.Commands.Count + "，实际 " + loaded[0].Commands.Count);
                Check("指令内容保持", loaded[0].Commands[0].Text == "START");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        // ---------------------------------------------------------------- 引擎回环

        private static readonly List<Frame> Received = new List<Frame>();
        private static readonly object Gate = new object();

        private static void Collect(Frame frame)
        {
            lock (Gate) Received.Add(frame);
        }

        private static bool HasFrame(Func<Frame, bool> predicate)
        {
            lock (Gate) return Received.Any(predicate);
        }

        private static Frame FirstFrame(Func<Frame, bool> predicate)
        {
            lock (Gate) return Received.FirstOrDefault(predicate);
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static void TestEngineLoopback()
        {
            Console.WriteLine("[引擎 · 真实 socket 收发]");

            int port = FreePort();
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var serverTask = Task.Run(async () =>
            {
                using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    var pending = new StringBuilder();
                    while (true)
                    {
                        int n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (n <= 0) break;

                        pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
                        string text = pending.ToString();
                        int idx;
                        while ((idx = text.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            string line = text.Substring(0, idx);
                            text = text.Substring(idx + 2);

                            if (line == "START")
                            {
                                var reply = Encoding.UTF8.GetBytes("OK\r\n");
                                await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
                            }
                            else if (line == "SPLIT")
                            {
                                // 故意拆成两段发，验证粘包/半包处理
                                var a = Encoding.UTF8.GetBytes("O");
                                var b = Encoding.UTF8.GetBytes("K\r\n");
                                await stream.WriteAsync(a, 0, a.Length).ConfigureAwait(false);
                                Thread.Sleep(80);
                                await stream.WriteAsync(b, 0, b.Length).ConfigureAwait(false);
                            }
                            // SLOW：不回，用于验证超时
                        }
                        pending.Clear();
                        pending.Append(text);
                    }
                }
            });

            var profile = ConnectionProfile.SkillTcpPreset();
            profile.Host = "127.0.0.1";
            profile.Port = port;
            profile.AutoReconnect = false;
            profile.HeartbeatEnabled = false;
            profile.ReplyTimeoutMs = 700;

            using (var engine = new CommunicationEngine())
            {
                engine.FrameReady += Collect;
                engine.Start(profile);

                bool connected = WaitFor(() => engine.State == EngineState.Connected, 3000);
                Check("连接建立", connected, engine.State.ToString());

                if (connected)
                {
                    engine.SendAsync(Encoding.UTF8.GetBytes("START\r\n"), true, 700, FrameKind.Data, "START")
                          .GetAwaiter().GetResult();

                    bool gotOk = WaitFor(() => HasFrame(f => f.Direction == FrameDirection.Rx &&
                                                            Encoding.UTF8.GetString(f.Data) == "OK"), 2000);
                    Check("收到回包 OK", gotOk);

                    var rx = FirstFrame(f => f.Direction == FrameDirection.Rx);
                    Check("回包带 RTT", rx != null && rx.RttMs.HasValue);

                    engine.SendAsync(Encoding.UTF8.GetBytes("SPLIT\r\n"), true, 700, FrameKind.Data, "SPLIT")
                          .GetAwaiter().GetResult();
                    bool halfPacketOk = WaitFor(() => HasFrame(f => f.Direction == FrameDirection.Rx &&
                                                                    Encoding.UTF8.GetString(f.Data) == "OK"), 2000);
                    Check("半包 + 粘包被正确拼成一帧", halfPacketOk);
                }
            }

            listener.Stop();
            try { serverTask.Wait(500); } catch { }
        }

        private static void TestTimeout()
        {
            Console.WriteLine("[引擎 · 回包超时]");

            Received.Clear();

            int port = FreePort();
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var serverTask = Task.Run(async () =>
            {
                using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    while (await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
                    {
                        // 收但永远不回
                    }
                }
            });

            var profile = ConnectionProfile.SkillTcpPreset();
            profile.Host = "127.0.0.1";
            profile.Port = port;
            profile.AutoReconnect = false;
            profile.HeartbeatEnabled = false;
            profile.ReplyTimeoutMs = 300;

            using (var engine = new CommunicationEngine())
            {
                engine.FrameReady += Collect;
                engine.Start(profile);

                if (WaitFor(() => engine.State == EngineState.Connected, 3000))
                {
                    engine.SendAsync(Encoding.UTF8.GetBytes("SLOW\r\n"), true, 300, FrameKind.Data, "SLOW")
                          .GetAwaiter().GetResult();

                    bool timeout = WaitFor(() => HasFrame(f => f.Kind == FrameKind.Timeout), 2000);
                    Check("无回包时产生超时记录", timeout);

                    var frame = FirstFrame(f => f.Kind == FrameKind.Timeout);
                    Check("超时记录带说明", frame != null && !string.IsNullOrEmpty(frame.Note),
                          frame == null ? "无" : frame.Note);
                }
                else
                {
                    Check("连接建立（超时用例）", false, engine.State.ToString());
                }
            }

            listener.Stop();
            try { serverTask.Wait(500); } catch { }
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    /// <summary>
    /// 服务端：本机监听一个端口，接受一个客户端（新连接顶掉旧连接）。
    /// 用来替代 Yvison 或旁路验证上位机，配合“模拟数据”排查对端问题。
    /// </summary>
    public sealed class TcpServerService
    {
        private readonly string _host;
        private readonly int _port;

        public event Action<string> Listening;
        public event Action<TcpSession> Accepted;
        public event Action<string> Closed;
        public event Action<string> Failed;

        public TcpServerService(ConnectionProfile profile)
        {
            _host = profile.Host;
            _port = profile.Port;
        }

        public async Task RunAsync(CancellationToken token)
        {
            IPAddress address;
            if (!IPAddress.TryParse(_host, out address)) address = IPAddress.Any;   // 0.0.0.0 / 主机名都退回全网卡

            var listener = new TcpListener(address, _port);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Failed?.Invoke("监听失败：" + SocketErrors.Describe(ex));
                return;
            }

            Listening?.Invoke(_host + ":" + _port);

            TcpSession current = null;
            using (token.Register(() => { try { listener.Stop(); } catch { } }))
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }
                    catch (SocketException) { break; }   // Stop() 会让 Accept 抛这个

                    if (current != null)
                    {
                        current.Dispose();               // 单客户端模型：新连接顶掉旧连接
                        current = null;
                    }

                    var session = new TcpSession(client);
                    session.Closed += reason => Closed?.Invoke(reason);
                    current = session;
                    Accepted?.Invoke(session);

                    var ignored = session.RunAsync(token);   // 后台收包，继续 accept
                }

                if (current != null) current.Dispose();
            }
        }
    }
}

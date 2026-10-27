using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TcpAssistant.Services
{
    /// <summary>把 SocketException 翻成人话，现场排查时比错误码有用。</summary>
    public static class SocketErrors
    {
        public static string Describe(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerExceptions.Count > 0) ex = agg.InnerExceptions[0];

            var timeout = ex as TimeoutException;
            if (timeout != null) return timeout.Message;

            var socket = ex as SocketException;
            if (socket != null)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused:
                        return "目标拒绝连接（端口未监听，或被防火墙拦截）";
                    case SocketError.TimedOut:
                        return "连接超时（检查 IP / 网线 / 对端是否开机）";
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                        return "主机名无法解析";
                    case SocketError.NetworkUnreachable:
                    case SocketError.HostUnreachable:
                        return "网络不可达（检查网段与路由）";
                    case SocketError.ConnectionReset:
                        return "连接被对端重置（对端进程可能已退出）";
                    case SocketError.ConnectionAborted:
                        return "连接被中止";
                    case SocketError.AddressAlreadyInUse:
                        return "端口已被占用（已有实例在监听，或别的程序占着）";
                    case SocketError.AccessDenied:
                        return "端口被拒绝访问（权限不足或端口被系统保留）";
                }
                return socket.Message + "（" + socket.SocketErrorCode + "）";
            }

            return ex.Message;
        }
    }

    /// <summary>
    /// 一条已建立的连接：一个收线程（await ReadAsync）+ 发送锁。
    /// 只负责搬字节，不理解协议；切帧在 FrameSplitter，编排在 CommunicationEngine。
    /// </summary>
    public sealed class TcpSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private int _closeRaised;
        private int _disposed;

        public string Peer { get; }
        public DateTime OpenedAt { get; }

        public event Action<byte[]> Received;
        public event Action<string> Closed;

        public TcpSession(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;                 // 视觉触发报文都短，禁掉 Nagle 减延迟
            _stream = client.GetStream();
            OpenedAt = DateTime.Now;
            try
            {
                Peer = client.Client.RemoteEndPoint != null
                    ? client.Client.RemoteEndPoint.ToString()
                    : "未知对端";
            }
            catch
            {
                Peer = "未知对端";
            }
        }

        /// <summary>阻塞式收包循环，直到对端关闭、出错或被取消。</summary>
        public async Task RunAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            string reason = "连接已断开";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = await _stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        reason = "对端关闭连接";
                        break;
                    }

                    var data = new byte[n];
                    Buffer.BlockCopy(buffer, 0, data, 0, n);
                    Received?.Invoke(data);
                }
            }
            catch (OperationCanceledException)
            {
                reason = "已主动断开";
            }
            catch (ObjectDisposedException)
            {
                reason = "连接已释放";
            }
            catch (IOException ex)
            {
                reason = "连接中断：" + ex.Message;
            }
            catch (SocketException ex)
            {
                reason = "网络异常：" + SocketErrors.Describe(ex);
            }
            catch (Exception ex)
            {
                reason = "接收异常：" + ex.Message;
            }
            finally
            {
                Dispose();
                RaiseClosed(token.IsCancellationRequested ? "已主动断开" : reason);
            }
        }

        /// <summary>发送；失败返回 false 并触发 Closed。</summary>
        public async Task<bool> SendAsync(byte[] data, CancellationToken token)
        {
            if (data == null || data.Length == 0) return true;
            if (_disposed == 1) return false;

            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                RaiseClosed("发送失败：" + SocketErrors.Describe(ex));
                return false;
            }
            finally
            {
                // 断开瞬间 Dispose 可能已经跑过，Release 抛异常会盖掉真正的错误原因
                try { _writeLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private void RaiseClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closeRaised, 1) == 0) Closed?.Invoke(reason);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            try { _writeLock.Dispose(); } catch { }
        }
    }
}

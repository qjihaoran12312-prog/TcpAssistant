using System;
using System.Threading;
using System.Threading.Tasks;
using TcpAssistant.Models;

namespace TcpAssistant.Services
{
    /// <summary>
    /// 客户端：循环「连接 → 收发 → 断开 → 等一会儿重连」。
    /// 断线自动重连放在这里，而不是界面层，界面只管显示状态。
    /// </summary>
    public sealed class TcpClientService
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private readonly bool _autoReconnect;
        private readonly int _reconnectDelayMs;

        public event Action<string> Connecting;
        public event Action<TcpSession> Opened;
        public event Action<string> ConnectFailed;
        public event Action<string> Closed;
        public event Action<string> RetryWaiting;

        public TcpClientService(ConnectionProfile profile)
        {
            _host = profile.Host;
            _port = profile.Port;
            _connectTimeoutMs = Math.Max(200, profile.ConnectTimeoutMs);
            _autoReconnect = profile.AutoReconnect;
            _reconnectDelayMs = Math.Max(100, profile.ReconnectDelayMs);
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Connecting?.Invoke(_host + ":" + _port);

                TcpClientHolder holder = null;
                try
                {
                    holder = await ConnectAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (holder != null) holder.Dispose();
                    ConnectFailed?.Invoke(SocketErrors.Describe(ex));
                    if (!_autoReconnect) return;
                    RetryWaiting?.Invoke(_reconnectDelayMs + " ms 后重试");
                    if (!await DelayAsync(_reconnectDelayMs, token).ConfigureAwait(false)) return;
                    continue;
                }

                using (var session = new TcpSession(holder.Client))
                {
                    session.Closed += reason => Closed?.Invoke(reason);
                    Opened?.Invoke(session);
                    await session.RunAsync(token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested || !_autoReconnect) return;

                RetryWaiting?.Invoke(_reconnectDelayMs + " ms 后重连");
                if (!await DelayAsync(_reconnectDelayMs, token).ConfigureAwait(false)) return;
            }
        }

        private async Task<TcpClientHolder> ConnectAsync(CancellationToken token)
        {
            var holder = new TcpClientHolder();
            var connectTask = holder.Client.ConnectAsync(_host, _port);
            var delayTask = Task.Delay(_connectTimeoutMs, token);

            var done = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
            if (done != connectTask)
            {
                Observe(connectTask);              // 避免超时后 connect 的异常变成未观察异常
                holder.Dispose();
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("连接超时（" + _connectTimeoutMs + " ms 内未建立连接）");
            }

            Observe(delayTask);
            await connectTask.ConfigureAwait(false);   // 连接被拒绝时在这里抛出
            return holder;
        }

        private static async Task<bool> DelayAsync(int ms, CancellationToken token)
        {
            try
            {
                await Task.Delay(ms, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; },
                              TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>包一层，保证连接失败时能干净地关掉 socket。</summary>
        private sealed class TcpClientHolder : IDisposable
        {
            public System.Net.Sockets.TcpClient Client { get; } = new System.Net.Sockets.TcpClient();

            public void Dispose()
            {
                try { Client.Close(); } catch { }
            }
        }
    }
}

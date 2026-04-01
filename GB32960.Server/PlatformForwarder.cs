using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GB32960.Protocol;
using Microsoft.Extensions.Logging;

namespace GB32960.Server;

/// <summary>
/// 上级平台转发 — 将终端数据转发到政府/上级监管平台
/// GB/T 32960 要求新能源车数据需转发至国家平台
/// 支持断线重连、队列缓存
/// </summary>
public class PlatformForwarder : IDisposable
{
    private readonly ILogger<PlatformForwarder> _logger;
    private readonly ForwarderConfig _config;
    private Socket? _socket;
    private bool _isConnected;
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _forwardTask;
    private long _totalForwarded;
    private long _totalDropped;
    private DateTime _lastReconnect = DateTime.MinValue;

    public long TotalForwarded => Interlocked.Read(ref _totalForwarded);
    public long TotalDropped => Interlocked.Read(ref _totalDropped);
    public bool IsConnected => _isConnected;
    public int QueueSize => _sendQueue.Count;

    public PlatformForwarder(ILogger<PlatformForwarder> logger, ForwarderConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public void Start()
    {
        if (!_config.Enabled) return;
        _forwardTask = Task.Run(ForwardLoop);
        _logger.LogInformation("平台转发已启动: {host}:{port}", _config.Host, _config.Port);
    }

    /// <summary>将原始报文加入转发队列</summary>
    public void Forward(byte[] rawPacket)
    {
        if (!_config.Enabled) return;

        // 队列满则丢弃最旧的
        while (_sendQueue.Count >= _config.MaxQueueSize)
        {
            _sendQueue.TryDequeue(out _);
            Interlocked.Increment(ref _totalDropped);
        }

        _sendQueue.Enqueue(rawPacket);
    }

    /// <summary>转发实时/补发数据（重新打包，VIN保持不变）</summary>
    public void ForwardRealtimeData(byte[] rawPacket)
    {
        // 直接转发原始报文，上级平台自行解析
        Forward(rawPacket);
    }

    private async Task ForwardLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 确保连接
                if (!_isConnected)
                {
                    await ConnectAsync();
                    if (!_isConnected)
                    {
                        await Task.Delay(_config.ReconnectIntervalMs, _cts.Token).ContinueWith(_ => { });
                        continue;
                    }
                }

                // 发送队列中的数据
                int batchCount = 0;
                while (_sendQueue.TryDequeue(out var data) && batchCount < 100)
                {
                    try
                    {
                        _socket!.Send(data, 0, data.Length, SocketFlags.None);
                        Interlocked.Increment(ref _totalForwarded);
                        batchCount++;
                    }
                    catch (SocketException)
                    {
                        _isConnected = false;
                        // 发送失败的数据重新入队
                        _sendQueue.Enqueue(data);
                        _logger.LogWarning("平台转发连接断开，将重连");
                        break;
                    }
                }

                if (batchCount == 0)
                    await Task.Delay(10, _cts.Token).ContinueWith(_ => { });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平台转发异常");
                _isConnected = false;
                await Task.Delay(1000);
            }
        }
    }

    private async Task ConnectAsync()
    {
        // 限制重连频率
        if ((DateTime.Now - _lastReconnect).TotalMilliseconds < _config.ReconnectIntervalMs)
            return;

        _lastReconnect = DateTime.Now;

        try
        {
            _socket?.Close();
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.NoDelay = true;
            _socket.SendTimeout = 5000;

            await _socket.ConnectAsync(new IPEndPoint(
                IPAddress.Parse(_config.Host), _config.Port));

            _isConnected = true;
            _logger.LogInformation("已连接上级平台: {host}:{port}", _config.Host, _config.Port);

            // 发送平台登入
            if (_config.SendPlatformLogin)
            {
                var loginPacket = GB32960Encoder.Encode(
                    CommandType.PlatformLogin, ResponseFlag.Command,
                    _config.PlatformVIN, EncryptionType.None, Array.Empty<byte>());
                _socket.Send(loginPacket);
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogDebug("连接上级平台失败: {msg}", ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        // 发送平台登出
        if (_isConnected && _config.SendPlatformLogin)
        {
            try
            {
                var logoutPacket = GB32960Encoder.Encode(
                    CommandType.PlatformLogout, ResponseFlag.Command,
                    _config.PlatformVIN, EncryptionType.None, Array.Empty<byte>());
                _socket?.Send(logoutPacket);
            }
            catch { }
        }

        _forwardTask?.Wait(3000);
        _socket?.Close();
    }
}

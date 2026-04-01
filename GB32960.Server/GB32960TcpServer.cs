using System.Net;
using System.Net.Sockets;
using GB32960.Protocol;
using GB32960.Protocol.DataTypes;
using Microsoft.Extensions.Logging;

namespace GB32960.Server;

public class GB32960TcpServer
{
    private readonly ILogger<GB32960TcpServer> _logger;
    private readonly SessionManager _sessionManager;
    private readonly ServerConfig _config;
    private Socket? _serverSocket;
    private bool _isRunning;

    private long _totalMessagesReceived;
    private long _totalMessagesSent;
    private long _totalConnections;
    private long _totalDisconnections;

    public SessionManager Sessions => _sessionManager;
    public long TotalMessagesReceived => Interlocked.Read(ref _totalMessagesReceived);
    public long TotalMessagesSent => Interlocked.Read(ref _totalMessagesSent);
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long TotalDisconnections => Interlocked.Read(ref _totalDisconnections);

    public GB32960TcpServer(ILogger<GB32960TcpServer> logger, ServerConfig config)
    {
        _logger = logger;
        _config = config;
        _sessionManager = new SessionManager();
    }

    public void Start()
    {
        _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _serverSocket.Bind(new IPEndPoint(IPAddress.Parse(_config.IpAddress), _config.Port));
        _serverSocket.Listen(_config.MaxConnections);

        _isRunning = true;
        _logger.LogInformation("GB/T 32960 服务器已启动 - {ip}:{port}, 最大连接: {max}",
            _config.IpAddress, _config.Port, _config.MaxConnections);

        // 开始接受连接
        BeginAccept();

        // 启动会话清理定时器
        Task.Run(CleanupTask);
    }

    public void Stop()
    {
        _isRunning = false;
        try { _serverSocket?.Close(); } catch { }
        _logger.LogInformation("服务器已停止");
    }

    // ─── Accept ──────────────────────────────────

    private void BeginAccept()
    {
        if (!_isRunning) return;
        var args = new SocketAsyncEventArgs();
        args.Completed += OnAcceptCompleted;
        try
        {
            if (!_serverSocket!.AcceptAsync(args))
                ProcessAccept(args);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs args) => ProcessAccept(args);

    private void ProcessAccept(SocketAsyncEventArgs args)
    {
        if (args.SocketError == SocketError.Success && args.AcceptSocket != null)
        {
            var clientSocket = args.AcceptSocket;
            var session = _sessionManager.AddSession(clientSocket);
            Interlocked.Increment(ref _totalConnections);

            _logger.LogDebug("新连接: {ep}, SessionId: {sid}",
                clientSocket.RemoteEndPoint, session.SessionId);

            BeginReceive(session);
        }

        args.Dispose();
        BeginAccept();
    }

    // ─── Receive ─────────────────────────────────

    private void BeginReceive(SessionInfo session)
    {
        if (!_isRunning || !session.Socket.Connected) return;

        var args = new SocketAsyncEventArgs();
        args.SetBuffer(new byte[_config.ReceiveBufferSize], 0, _config.ReceiveBufferSize);
        args.UserToken = session;
        args.Completed += OnReceiveCompleted;

        try
        {
            if (!session.Socket.ReceiveAsync(args))
                ProcessReceive(args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("接收异常: {msg}", ex.Message);
            HandleDisconnect(session);
            args.Dispose();
        }
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs args) => ProcessReceive(args);

    private void ProcessReceive(SocketAsyncEventArgs args)
    {
        var session = (SessionInfo)args.UserToken!;

        if (args.SocketError != SocketError.Success || args.BytesTransferred <= 0)
        {
            HandleDisconnect(session);
            args.Dispose();
            return;
        }

        // 追加到消息缓冲区
        session.MessageBuffer.Append(args.Buffer!, args.Offset, args.BytesTransferred);
        _sessionManager.UpdateActiveTime(session.SessionId);

        // 提取完整消息
        var messages = session.MessageBuffer.ExtractMessages();
        foreach (var msgData in messages)
        {
            Interlocked.Increment(ref _totalMessagesReceived);
            session.ReceivedMessages++;
            Task.Run(() => ProcessMessage(session, msgData));
        }

        args.Dispose();
        BeginReceive(session);
    }

    private void HandleDisconnect(SessionInfo session)
    {
        _logger.LogDebug("断开连接: VIN={vin}, SessionId={sid}", session.VIN, session.SessionId);
        Interlocked.Increment(ref _totalDisconnections);
        _sessionManager.RemoveSession(session.SessionId);
    }

    // ─── 消息处理 ─────────────────────────────────

    private void ProcessMessage(SessionInfo session, byte[] data)
    {
        try
        {
            var message = GB32960Decoder.Decode(data);
            if (message == null)
            {
                _logger.LogWarning("消息解码失败, SessionId={sid}, Len={len}", session.SessionId, data.Length);
                return;
            }

            // 绑定VIN
            if (string.IsNullOrEmpty(session.VIN) && !string.IsNullOrEmpty(message.VIN))
                _sessionManager.BindVin(session.SessionId, message.VIN);

            byte[]? response = message.Command switch
            {
                CommandType.VehicleLogin      => HandleVehicleLogin(session, message),
                CommandType.RealtimeData      => HandleRealtimeData(session, message),
                CommandType.SupplementaryData => HandleSupplementaryData(session, message),
                CommandType.VehicleLogout     => HandleVehicleLogout(session, message),
                CommandType.Heartbeat         => HandleHeartbeat(session, message),
                CommandType.TimeSync          => HandleTimeSync(session, message),
                CommandType.PlatformLogin     => HandlePlatformLogin(session, message),
                CommandType.PlatformLogout    => HandlePlatformLogout(session, message),
                _ => HandleUnknown(session, message),
            };

            if (response != null)
                SendData(session, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理消息异常, VIN={vin}", session.VIN);
        }
    }

    // ─── 命令处理器 ──────────────────────────────

    private byte[]? HandleVehicleLogin(SessionInfo session, GB32960Message msg)
    {
        var loginData = GB32960Decoder.DecodeVehicleLogin(msg.Data);
        if (loginData == null)
        {
            _logger.LogWarning("车辆登入解析失败: VIN={vin}", msg.VIN);
            return GB32960Encoder.EncodeVehicleLoginResponse(msg.VIN, ResponseFlag.Error);
        }

        session.IsLoggedIn = true;
        session.ICCID = loginData.ICCID;
        session.LoginSequence = loginData.LoginSequence;

        _logger.LogInformation("车辆登入: VIN={vin}, ICCID={iccid}, 流水号={seq}, 子系统数={sub}",
            msg.VIN, loginData.ICCID, loginData.LoginSequence, loginData.SubsystemCount);

        return GB32960Encoder.EncodeVehicleLoginResponse(msg.VIN, ResponseFlag.Success);
    }

    private byte[]? HandleRealtimeData(SessionInfo session, GB32960Message msg)
    {
        var (time, items) = GB32960Decoder.DecodeRealtimeData(msg.Data);

        _logger.LogDebug("实时数据: VIN={vin}, 时间={time}, 信息体数={count}",
            msg.VIN, time.ToString("yyyy-MM-dd HH:mm:ss"), items.Count);

        foreach (var item in items)
        {
            switch (item)
            {
                case VehicleData vd:
                    _logger.LogDebug("  整车: 状态={s}, SOC={soc}%, 速度={spd}km/h, 里程={mil}km, 电压={v}V, 电流={a}A",
                        vd.Status, vd.SOC, vd.GetSpeedKmh(), vd.GetMileageKm(), vd.GetVoltageV(), vd.GetCurrentA());
                    break;
                case VehiclePositionData pos:
                    _logger.LogDebug("  位置: 经度={lon}, 纬度={lat}, 有效={v}",
                        pos.GetLongitude().ToString("F6"), pos.GetLatitude().ToString("F6"), pos.IsValid);
                    break;
                case AlarmData alarm when alarm.MaxAlarmLevel > 0:
                    _logger.LogWarning("  报警: VIN={vin}, 等级={lv}, 标志=0x{flags:X8}",
                        msg.VIN, alarm.MaxAlarmLevel, alarm.GeneralAlarmFlags);
                    break;
            }
        }

        return GB32960Encoder.EncodeRealtimeDataResponse(msg.VIN, CommandType.RealtimeData);
    }

    private byte[]? HandleSupplementaryData(SessionInfo session, GB32960Message msg)
    {
        var (time, items) = GB32960Decoder.DecodeRealtimeData(msg.Data);
        _logger.LogDebug("补发数据: VIN={vin}, 时间={time}, 信息体数={count}",
            msg.VIN, time.ToString("yyyy-MM-dd HH:mm:ss"), items.Count);
        return GB32960Encoder.EncodeRealtimeDataResponse(msg.VIN, CommandType.SupplementaryData);
    }

    private byte[]? HandleVehicleLogout(SessionInfo session, GB32960Message msg)
    {
        var logoutData = GB32960Decoder.DecodeVehicleLogout(msg.Data);
        session.IsLoggedIn = false;
        _logger.LogInformation("车辆登出: VIN={vin}, 流水号={seq}",
            msg.VIN, logoutData?.LogoutSequence);
        return GB32960Encoder.EncodeResponse(CommandType.VehicleLogout, ResponseFlag.Success, msg.VIN);
    }

    private byte[]? HandleHeartbeat(SessionInfo session, GB32960Message msg)
    {
        _logger.LogDebug("心跳: VIN={vin}", msg.VIN);
        return GB32960Encoder.EncodeHeartbeatResponse(msg.VIN);
    }

    private byte[]? HandleTimeSync(SessionInfo session, GB32960Message msg)
    {
        _logger.LogDebug("终端校时: VIN={vin}", msg.VIN);
        return GB32960Encoder.EncodeTimeSyncResponse(msg.VIN);
    }

    private byte[]? HandlePlatformLogin(SessionInfo session, GB32960Message msg)
    {
        _logger.LogInformation("平台登入: VIN={vin}", msg.VIN);
        return GB32960Encoder.EncodeResponse(CommandType.PlatformLogin, ResponseFlag.Success, msg.VIN);
    }

    private byte[]? HandlePlatformLogout(SessionInfo session, GB32960Message msg)
    {
        _logger.LogInformation("平台登出: VIN={vin}", msg.VIN);
        return GB32960Encoder.EncodeResponse(CommandType.PlatformLogout, ResponseFlag.Success, msg.VIN);
    }

    private byte[]? HandleUnknown(SessionInfo session, GB32960Message msg)
    {
        _logger.LogWarning("未知命令: VIN={vin}, CMD=0x{cmd:X2}", msg.VIN, (byte)msg.Command);
        return GB32960Encoder.EncodeResponse(msg.Command, ResponseFlag.Invalid, msg.VIN);
    }

    // ─── 发送 ────────────────────────────────────

    private void SendData(SessionInfo session, byte[] data)
    {
        try
        {
            session.Socket.Send(data, 0, data.Length, SocketFlags.None);
            Interlocked.Increment(ref _totalMessagesSent);
            session.SentMessages++;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("发送失败: VIN={vin}, {msg}", session.VIN, ex.Message);
            HandleDisconnect(session);
        }
    }

    // ─── 定时清理 ─────────────────────────────────

    private async Task CleanupTask()
    {
        while (_isRunning)
        {
            await Task.Delay(60_000);
            var cleaned = _sessionManager.CleanupTimeoutSessions(_config.SessionTimeoutMinutes);
            if (cleaned > 0)
                _logger.LogInformation("清理超时会话: {count} 个", cleaned);
        }
    }

    // ─── 下行命令接口 ─────────────────────────────

    public bool SendQuery(string vin, byte[] queryData)
    {
        var session = _sessionManager.GetSessionByVin(vin);
        if (session == null) return false;
        SendData(session, GB32960Encoder.EncodeQuery(vin, queryData));
        return true;
    }

    public bool SendSetup(string vin, byte[] setupData)
    {
        var session = _sessionManager.GetSessionByVin(vin);
        if (session == null) return false;
        SendData(session, GB32960Encoder.EncodeSetup(vin, setupData));
        return true;
    }

    public bool SendControl(string vin, byte[] controlData)
    {
        var session = _sessionManager.GetSessionByVin(vin);
        if (session == null) return false;
        SendData(session, GB32960Encoder.EncodeControl(vin, controlData));
        return true;
    }
}

using System.Buffers;
using System.Collections.Concurrent;
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

    // SocketAsyncEventArgs 对象池
    private readonly ConcurrentStack<SocketAsyncEventArgs> _receiveArgsPool = new();
    private int _poolCreated;

    // 统计
    private long _totalMessagesReceived;
    private long _totalMessagesSent;
    private long _totalConnections;
    private long _totalDisconnections;
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private readonly ConcurrentDictionary<CommandType, long> _commandStats = new();
    private DateTime _startTime;

    public SessionManager Sessions => _sessionManager;
    public long TotalMessagesReceived => Interlocked.Read(ref _totalMessagesReceived);
    public long TotalMessagesSent => Interlocked.Read(ref _totalMessagesSent);
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long TotalDisconnections => Interlocked.Read(ref _totalDisconnections);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public IReadOnlyDictionary<CommandType, long> CommandStats => _commandStats;
    public TimeSpan Uptime => DateTime.Now - _startTime;

    public GB32960TcpServer(ILogger<GB32960TcpServer> logger, ServerConfig config)
    {
        _logger = logger;
        _config = config;
        _sessionManager = new SessionManager();
    }

    public void Start()
    {
        _startTime = DateTime.Now;

        // 预分配 SocketAsyncEventArgs 池
        int preAlloc = Math.Min(_config.MaxConnections, 10000);
        for (int i = 0; i < preAlloc; i++)
            _receiveArgsPool.Push(CreateReceiveArgs());
        _poolCreated = preAlloc;
        _logger.LogInformation("预分配 SocketAsyncEventArgs: {count}", preAlloc);

        _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _serverSocket.Bind(new IPEndPoint(IPAddress.Parse(_config.IpAddress), _config.Port));
        _serverSocket.Listen(_config.MaxConnections);

        _isRunning = true;
        _logger.LogInformation("GB/T 32960 服务器已启动 - {ip}:{port}", _config.IpAddress, _config.Port);

        BeginAccept();
        Task.Run(CleanupTask);
    }

    public void Stop()
    {
        _isRunning = false;
        try { _serverSocket?.Close(); } catch { }
        // 关闭所有会话
        foreach (var s in _sessionManager.GetAllSessions())
            _sessionManager.RemoveSession(s.SessionId);
        _logger.LogInformation("服务器已停止");
    }

    // ─── SocketAsyncEventArgs 池 ─────────────────

    private SocketAsyncEventArgs CreateReceiveArgs()
    {
        var args = new SocketAsyncEventArgs();
        args.Completed += OnReceiveCompleted;
        // 使用 ArrayPool 租借缓冲区
        var buffer = ArrayPool<byte>.Shared.Rent(_config.ReceiveBufferSize);
        args.SetBuffer(buffer, 0, _config.ReceiveBufferSize);
        return args;
    }

    private SocketAsyncEventArgs RentReceiveArgs()
    {
        if (_receiveArgsPool.TryPop(out var args))
            return args;
        // 池耗尽，动态创建
        Interlocked.Increment(ref _poolCreated);
        return CreateReceiveArgs();
    }

    private void ReturnReceiveArgs(SocketAsyncEventArgs args)
    {
        args.UserToken = null;
        args.AcceptSocket = null;
        _receiveArgsPool.Push(args);
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
            clientSocket.NoDelay = true;
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

        var args = RentReceiveArgs();
        args.UserToken = session;

        try
        {
            if (!session.Socket.ReceiveAsync(args))
                ProcessReceive(args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("接收异常: {msg}", ex.Message);
            ReturnReceiveArgs(args);
            HandleDisconnect(session);
        }
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs args) => ProcessReceive(args);

    private void ProcessReceive(SocketAsyncEventArgs args)
    {
        var session = (SessionInfo)args.UserToken!;

        if (args.SocketError != SocketError.Success || args.BytesTransferred <= 0)
        {
            ReturnReceiveArgs(args);
            HandleDisconnect(session);
            return;
        }

        Interlocked.Add(ref _totalBytesReceived, args.BytesTransferred);
        session.MessageBuffer.Append(args.Buffer!, args.Offset, args.BytesTransferred);
        _sessionManager.UpdateActiveTime(session.SessionId);

        var messages = session.MessageBuffer.ExtractMessages();

        // 归还SAEA并立即开始下一次接收（不阻塞在消息处理上）
        ReturnReceiveArgs(args);
        BeginReceive(session);

        // 消息处理：少量消息直接内联处理，避免线程池调度开销
        // 大量消息（粘包）才用Task.Run防止阻塞IO线程
        if (messages.Count <= 2)
        {
            foreach (var msgData in messages)
            {
                Interlocked.Increment(ref _totalMessagesReceived);
                session.ReceivedMessages++;
                ProcessMessage(session, msgData);
            }
        }
        else
        {
            foreach (var msgData in messages)
            {
                Interlocked.Increment(ref _totalMessagesReceived);
                session.ReceivedMessages++;
            }
            Task.Run(() =>
            {
                foreach (var msgData in messages)
                    ProcessMessage(session, msgData);
            });
        }
    }

    private void HandleDisconnect(SessionInfo session)
    {
        if (string.IsNullOrEmpty(session.VIN))
            _logger.LogDebug("断开: SessionId={sid}", session.SessionId);
        else
            _logger.LogInformation("断开: VIN={vin}", session.VIN);
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
                _logger.LogWarning("消息解码失败, Len={len}, Hex={hex}",
                    data.Length, BitConverter.ToString(data, 0, Math.Min(data.Length, 30)));
                return;
            }

            // 统计命令类型
            _commandStats.AddOrUpdate(message.Command, 1, (_, v) => v + 1);

            // 绑定VIN
            if (string.IsNullOrEmpty(session.VIN) && !string.IsNullOrEmpty(message.VIN.Trim()))
                _sessionManager.BindVin(session.SessionId, message.VIN.Trim());

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
                SendDataAsync(session, response);
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

        _logger.LogInformation("车辆登入: VIN={vin}, ICCID={iccid}, 流水号={seq}, 子系统={sub}",
            msg.VIN, loginData.ICCID, loginData.LoginSequence, loginData.SubsystemCount);

        return GB32960Encoder.EncodeVehicleLoginResponse(msg.VIN, ResponseFlag.Success);
    }

    private byte[]? HandleRealtimeData(SessionInfo session, GB32960Message msg)
    {
        var (time, items) = GB32960Decoder.DecodeRealtimeData(msg.Data);

        _logger.LogDebug("实时数据: VIN={vin}, 时间={time}, 信息体={count}",
            msg.VIN, time.ToString("yyyy-MM-dd HH:mm:ss"), items.Count);

        LogRealtimeItems(msg.VIN, items);

        return GB32960Encoder.EncodeRealtimeDataResponse(msg.VIN, CommandType.RealtimeData);
    }

    private byte[]? HandleSupplementaryData(SessionInfo session, GB32960Message msg)
    {
        var (time, items) = GB32960Decoder.DecodeRealtimeData(msg.Data);
        _logger.LogDebug("补发数据: VIN={vin}, 时间={time}, 信息体={count}",
            msg.VIN, time.ToString("yyyy-MM-dd HH:mm:ss"), items.Count);
        LogRealtimeItems(msg.VIN, items);
        return GB32960Encoder.EncodeRealtimeDataResponse(msg.VIN, CommandType.SupplementaryData);
    }

    private byte[]? HandleVehicleLogout(SessionInfo session, GB32960Message msg)
    {
        var logoutData = GB32960Decoder.DecodeVehicleLogout(msg.Data);
        session.IsLoggedIn = false;
        _logger.LogInformation("车辆登出: VIN={vin}, 流水号={seq}", msg.VIN, logoutData?.LogoutSequence);
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

    // ─── 实时数据详细日志 ─────────────────────────

    private void LogRealtimeItems(string vin, List<IRealtimeInfoItem> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case VehicleData vd:
                    _logger.LogDebug("  [{vin}] 整车: 状态={s}, SOC={soc}%, 速度={spd:F1}km/h, 里程={mil:F1}km, {v:F1}V/{a:F1}A",
                        vin, vd.Status, vd.SOC, vd.GetSpeedKmh(), vd.GetMileageKm(), vd.GetVoltageV(), vd.GetCurrentA());
                    break;
                case DriveMotorData dm:
                    foreach (var m in dm.Motors)
                        _logger.LogDebug("  [{vin}] 电机#{seq}: 状态={s}, RPM={rpm}, 温度={t}℃",
                            vin, m.Sequence, m.State, m.GetRPM(), m.GetMotorTempC());
                    break;
                case FuelCellData fc:
                    _logger.LogDebug("  [{vin}] 燃料电池: {v:F1}V, {a:F1}A, 探针数={n}",
                        vin, fc.Voltage / 10.0, fc.Current / 10.0, fc.TempProbeCount);
                    break;
                case EngineData eng:
                    _logger.LogDebug("  [{vin}] 发动机: 状态={s}, RPM={rpm}",
                        vin, eng.State, eng.CrankshaftRPM);
                    break;
                case VehiclePositionData pos:
                    _logger.LogDebug("  [{vin}] 位置: {lon:F6},{lat:F6}, 有效={v}",
                        vin, pos.GetLongitude(), pos.GetLatitude(), pos.IsValid);
                    break;
                case ExtremeValueData ev:
                    _logger.LogDebug("  [{vin}] 极值: 电压{maxV:F3}~{minV:F3}V, 温度{maxT}~{minT}℃",
                        vin, ev.GetMaxVoltageV(), ev.GetMinVoltageV(), ev.GetMaxTempC(), ev.GetMinTempC());
                    break;
                case AlarmData alarm:
                    if (alarm.MaxAlarmLevel > 0)
                        _logger.LogWarning("  [{vin}] 报警! 等级={lv}, 标志=0x{flags:X8}, 电池故障={bf}, 电机故障={mf}",
                            vin, alarm.MaxAlarmLevel, alarm.GeneralAlarmFlags,
                            alarm.BatteryFaultCount, alarm.MotorFaultCount);
                    break;
                case BatteryVoltageData bv:
                    _logger.LogDebug("  [{vin}] 电压: {n}个子系统, 总单体={cells}",
                        vin, bv.SubsystemCount, bv.Subsystems.Sum(s => s.TotalCellCount));
                    break;
                case BatteryTemperatureData bt:
                    _logger.LogDebug("  [{vin}] 温度: {n}个子系统, 总探针={probes}",
                        vin, bt.SubsystemCount, bt.Subsystems.Sum(s => s.ProbeCount));
                    break;
            }
        }
    }

    // ─── 异步发送 ────────────────────────────────

    private void SendDataAsync(SessionInfo session, byte[] data)
    {
        try
        {
            var args = new SocketAsyncEventArgs();
            args.SetBuffer(data, 0, data.Length);
            args.Completed += (_, e) =>
            {
                if (e.SocketError == SocketError.Success)
                {
                    Interlocked.Increment(ref _totalMessagesSent);
                    Interlocked.Add(ref _totalBytesSent, e.BytesTransferred);
                    session.SentMessages++;
                }
                e.Dispose();
            };

            if (!session.Socket.SendAsync(args))
            {
                Interlocked.Increment(ref _totalMessagesSent);
                Interlocked.Add(ref _totalBytesSent, data.Length);
                session.SentMessages++;
                args.Dispose();
            }
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
        SendDataAsync(session, GB32960Encoder.EncodeQuery(vin, queryData));
        return true;
    }

    public bool SendSetup(string vin, byte[] setupData)
    {
        var session = _sessionManager.GetSessionByVin(vin);
        if (session == null) return false;
        SendDataAsync(session, GB32960Encoder.EncodeSetup(vin, setupData));
        return true;
    }

    public bool SendControl(string vin, byte[] controlData)
    {
        var session = _sessionManager.GetSessionByVin(vin);
        if (session == null) return false;
        SendDataAsync(session, GB32960Encoder.EncodeControl(vin, controlData));
        return true;
    }
}

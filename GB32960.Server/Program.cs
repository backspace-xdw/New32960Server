using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GB32960.Protocol;
using GB32960.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new ConfigurationBuilder()
    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var serverConfig = new ServerConfig();
config.GetSection("ServerConfig").Bind(serverConfig);

// 日志配置
var fileLogConfig = new FileLogConfig();
config.GetSection("FileLog").Bind(fileLogConfig);

var logLevel = Enum.TryParse<LogLevel>(serverConfig.LogLevel, true, out var lv) ? lv : LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(logLevel);
    builder.AddSimpleConsole(options => { options.TimestampFormat = "HH:mm:ss "; options.SingleLine = true; });
    if (fileLogConfig.Enabled)
        builder.AddProvider(new FileLoggerProvider(fileLogConfig));
});
var logger = loggerFactory.CreateLogger<GB32960TcpServer>();

// InfluxDB 存储（可选）
var influxConfig = new InfluxDbConfig();
config.GetSection("InfluxDb").Bind(influxConfig);
InfluxDbStore? influxStore = null;
if (influxConfig.Enabled)
{
    influxStore = new InfluxDbStore(loggerFactory.CreateLogger<InfluxDbStore>(), influxConfig);
    influxStore.Start();
}

// 原始报文存档（可选）
var archiveConfig = new RawArchiveConfig();
config.GetSection("RawArchive").Bind(archiveConfig);
RawPacketArchiver? archiver = null;
if (archiveConfig.Enabled)
{
    archiver = new RawPacketArchiver(loggerFactory.CreateLogger<RawPacketArchiver>(), archiveConfig);
    archiver.Start();
}

// 上级平台转发（可选）
var forwarderConfig = new ForwarderConfig();
config.GetSection("PlatformForwarder").Bind(forwarderConfig);
PlatformForwarder? forwarder = null;
if (forwarderConfig.Enabled)
{
    forwarder = new PlatformForwarder(loggerFactory.CreateLogger<PlatformForwarder>(), forwarderConfig);
    forwarder.Start();
}

var server = new GB32960TcpServer(logger, serverConfig, influxStore, archiver, forwarder);

PrintBanner(serverConfig, influxConfig, archiveConfig, forwarderConfig, fileLogConfig);
server.Start();

Console.WriteLine("按键: S=统计  A=自动刷新(5s)  L=终端列表  Q=退出");
Console.WriteLine();

bool autoRefresh = false;
var lastRefresh = DateTime.MinValue;

while (true)
{
    if (Console.KeyAvailable)
    {
        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.Q:
                Console.WriteLine("正在停止...");
                server.Stop();
                goto Exit;
            case ConsoleKey.S:
                ShowStatistics(server);
                break;
            case ConsoleKey.A:
                autoRefresh = !autoRefresh;
                Console.WriteLine(autoRefresh ? ">> 自动刷新已开启 (5秒)" : ">> 自动刷新已关闭");
                break;
            case ConsoleKey.L:
                ShowSessionList(server);
                break;
        }
    }

    if (autoRefresh && (DateTime.Now - lastRefresh).TotalSeconds >= 5)
    {
        lastRefresh = DateTime.Now;
        ShowStatistics(server);
    }

    await Task.Delay(100);
}

Exit:
Console.WriteLine("服务器已停止。");

// ─── 显示函数 ────────────────────────────────────

static void PrintBanner(ServerConfig cfg, InfluxDbConfig influx, RawArchiveConfig archive, ForwarderConfig fwd, FileLogConfig fileLog)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║   GB/T 32960.3-2016 新能源汽车远程监测平台              ║");
    Console.WriteLine("║   New Energy Vehicle Remote Monitoring Server           ║");
    Console.WriteLine("║   支持 100,000+ 并发终端连接                            ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine($"  监听地址:   {cfg.IpAddress}:{cfg.Port}");
    Console.WriteLine($"  最大连接:   {cfg.MaxConnections:N0}");
    Console.WriteLine($"  会话超时:   {cfg.SessionTimeoutMinutes} 分钟");

    PrintModuleStatus("文件日志", fileLog.Enabled, fileLog.Enabled ? $"{fileLog.Directory}/ (保留{fileLog.RetainDays}天)" : null);
    PrintModuleStatus("报文存档", archive.Enabled, archive.Enabled ? $"{archive.BaseDirectory}/" : null);
    PrintModuleStatus("InfluxDB", influx.Enabled, influx.Enabled ? $"{influx.Url} → {influx.Org}/{influx.Bucket}" : null);
    PrintModuleStatus("平台转发", fwd.Enabled, fwd.Enabled ? $"{fwd.Host}:{fwd.Port}" : null);
    Console.WriteLine();
}

static void PrintModuleStatus(string name, bool enabled, string? detail)
{
    Console.Write($"  {name,-10}");
    if (enabled)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("● 已启用");
        Console.ResetColor();
        if (detail != null) Console.Write($"  {detail}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("○ 未启用");
        Console.ResetColor();
    }
    Console.WriteLine();
}

static void ShowStatistics(GB32960TcpServer server)
{
    var uptime = server.Uptime;
    var totalRecv = server.TotalMessagesReceived;
    var throughput = uptime.TotalSeconds > 0 ? totalRecv / uptime.TotalSeconds : 0;

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"──── 统计 [{DateTime.Now:HH:mm:ss}] 运行 {uptime.Days}天{uptime.Hours}时{uptime.Minutes}分 ────");
    Console.ResetColor();

    Console.Write("  在线: "); WriteColored($"{server.Sessions.OnlineCount}", ConsoleColor.Green);
    Console.Write("  登入: "); WriteColored($"{server.Sessions.LoggedInCount}", ConsoleColor.Green);
    Console.Write("  总连接: "); WriteColored($"{server.TotalConnections:N0}", ConsoleColor.Cyan);
    Console.Write("  断开: "); WriteColored($"{server.TotalDisconnections:N0}", ConsoleColor.DarkGray);
    Console.WriteLine();

    Console.Write("  消息收: "); WriteColored($"{totalRecv:N0}", ConsoleColor.Cyan);
    Console.Write("  发: "); WriteColored($"{server.TotalMessagesSent:N0}", ConsoleColor.Cyan);
    Console.Write("  流量: "); WriteColored($"{FormatBytes(server.TotalBytesReceived)}↓ {FormatBytes(server.TotalBytesSent)}↑", ConsoleColor.DarkCyan);
    Console.Write("  吞吐: "); WriteColored($"{throughput:F1} msg/s", ConsoleColor.White);
    Console.WriteLine();

    // 命令类型统计
    if (server.CommandStats.Count > 0)
    {
        Console.Write("  命令: ");
        foreach (var (cmd, count) in server.CommandStats.OrderByDescending(kv => kv.Value))
        {
            var name = cmd switch
            {
                CommandType.VehicleLogin => "登入",
                CommandType.RealtimeData => "实时",
                CommandType.SupplementaryData => "补发",
                CommandType.VehicleLogout => "登出",
                CommandType.Heartbeat => "心跳",
                CommandType.TimeSync => "校时",
                CommandType.PlatformLogin => "平台入",
                CommandType.PlatformLogout => "平台出",
                _ => $"0x{(byte)cmd:X2}",
            };
            Console.Write($"{name}={count:N0} ");
        }
        Console.WriteLine();
    }

    // 存储/转发统计
    if (server.InfluxStore != null)
    {
        var s = server.InfluxStore;
        Console.Write("  InfluxDB: "); WriteColored($"{s.TotalWrites:N0}写入", ConsoleColor.Green);
        WriteColored($"{s.TotalErrors:N0}错误", s.TotalErrors > 0 ? ConsoleColor.Red : ConsoleColor.DarkGray);
        WriteColored($"队列{s.QueueSize:N0}", ConsoleColor.DarkCyan);
        Console.WriteLine();
    }
    if (server.Archiver != null)
        Console.WriteLine($"  报文存档: {server.Archiver.TotalArchived:N0} 条");
    if (server.Forwarder != null)
    {
        var f = server.Forwarder;
        Console.Write("  平台转发: "); WriteColored(f.IsConnected ? "已连接" : "未连接", f.IsConnected ? ConsoleColor.Green : ConsoleColor.Red);
        WriteColored($"{f.TotalForwarded:N0}转发", ConsoleColor.Cyan);
        WriteColored($"{f.TotalDropped:N0}丢弃", f.TotalDropped > 0 ? ConsoleColor.Red : ConsoleColor.DarkGray);
        WriteColored($"队列{f.QueueSize:N0}", ConsoleColor.DarkCyan);
        Console.WriteLine();
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("─────────────────────────────────────────────────────");
    Console.ResetColor();
}

static void ShowSessionList(GB32960TcpServer server)
{
    var sessions = server.Sessions.GetAllSessions()
        .OrderByDescending(s => s.LastActiveTime)
        .Take(30)
        .ToList();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"──── 终端列表 (前30, 共{server.Sessions.OnlineCount}) ────");
    Console.ResetColor();

    Console.WriteLine($"  {"VIN",-20} {"登入",-4} {"收",-8} {"发",-8} {"ICCID",-22} {"最后活跃"}");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  {new string('─', 80)}");
    Console.ResetColor();

    foreach (var s in sessions)
    {
        if (s.IsLoggedIn) Console.ForegroundColor = ConsoleColor.Green;
        else if (s.VIN.Length == 0) Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine($"  {(s.VIN.Length > 0 ? s.VIN : "(未绑定)"),-20} {(s.IsLoggedIn ? "是" : "否"),-4} {s.ReceivedMessages,-8:N0} {s.SentMessages,-8:N0} {s.ICCID,-22} {s.LastActiveTime:MM-dd HH:mm:ss}");
        Console.ResetColor();
    }
    Console.WriteLine();
}

static void WriteColored(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ResetColor();
    Console.Write("  ");
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return $"{bytes}B";
    if (bytes < 1048576) return $"{bytes / 1024.0:F1}KB";
    if (bytes < 1073741824) return $"{bytes / 1048576.0:F1}MB";
    return $"{bytes / 1073741824.0:F2}GB";
}

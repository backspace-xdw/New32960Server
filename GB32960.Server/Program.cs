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

var logLevel = Enum.TryParse<LogLevel>(serverConfig.LogLevel, true, out var lv) ? lv : LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(logLevel);
    builder.AddSimpleConsole(options => { options.TimestampFormat = "HH:mm:ss "; options.SingleLine = true; });
});
var logger = loggerFactory.CreateLogger<GB32960TcpServer>();

var server = new GB32960TcpServer(logger, serverConfig);

PrintBanner(serverConfig);
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

static void PrintBanner(ServerConfig cfg)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║   GB/T 32960.3-2016 新能源汽车远程监测平台              ║");
    Console.WriteLine("║   New Energy Vehicle Remote Monitoring Server           ║");
    Console.WriteLine("║   支持 100,000+ 并发终端连接                            ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine($"  监听地址: {cfg.IpAddress}:{cfg.Port}");
    Console.WriteLine($"  最大连接: {cfg.MaxConnections:N0}");
    Console.WriteLine($"  会话超时: {cfg.SessionTimeoutMinutes} 分钟");
    Console.WriteLine($"  缓冲大小: {cfg.ReceiveBufferSize} 字节");
    Console.WriteLine($"  日志级别: {cfg.LogLevel}");
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

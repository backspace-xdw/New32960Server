using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GB32960.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 加载配置
var config = new ConfigurationBuilder()
    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var serverConfig = new ServerConfig();
config.GetSection("ServerConfig").Bind(serverConfig);

// 创建日志
var logLevel = Enum.TryParse<LogLevel>(serverConfig.LogLevel, true, out var lv) ? lv : LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(logLevel);
    builder.AddSimpleConsole(options => { options.TimestampFormat = "HH:mm:ss "; options.SingleLine = true; });
});
var logger = loggerFactory.CreateLogger<GB32960TcpServer>();

// 启动服务器
var server = new GB32960TcpServer(logger, serverConfig);

Console.WriteLine("============================================================");
Console.WriteLine("  GB/T 32960.3-2016 新能源汽车远程监测平台");
Console.WriteLine("  支持 100,000+ 并发终端连接");
Console.WriteLine("============================================================");
Console.WriteLine();
Console.WriteLine($"  监听地址: {serverConfig.IpAddress}:{serverConfig.Port}");
Console.WriteLine($"  最大连接: {serverConfig.MaxConnections}");
Console.WriteLine($"  会话超时: {serverConfig.SessionTimeoutMinutes} 分钟");
Console.WriteLine($"  缓冲大小: {serverConfig.ReceiveBufferSize} 字节");
Console.WriteLine($"  日志级别: {serverConfig.LogLevel}");
Console.WriteLine();

server.Start();

Console.WriteLine("服务器已启动。按 S 查看统计, Q 退出");
Console.WriteLine();

while (true)
{
    if (Console.KeyAvailable)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Q)
        {
            Console.WriteLine("正在停止服务器...");
            server.Stop();
            break;
        }
        if (key.Key == ConsoleKey.S)
        {
            ShowStatistics(server);
        }
    }
    await Task.Delay(100);
}

Console.WriteLine("服务器已停止。");

static void ShowStatistics(GB32960TcpServer server)
{
    Console.WriteLine();
    Console.WriteLine("────────────────────── 服务器统计 ──────────────────────");
    Console.WriteLine($"  在线终端: {server.Sessions.OnlineCount}");
    Console.WriteLine($"  已登入:   {server.Sessions.LoggedInCount}");
    Console.WriteLine($"  总连接:   {server.TotalConnections}");
    Console.WriteLine($"  总断开:   {server.TotalDisconnections}");
    Console.WriteLine($"  消息收:   {server.TotalMessagesReceived}");
    Console.WriteLine($"  消息发:   {server.TotalMessagesSent}");
    Console.WriteLine("───────────────────────────────────────────────────────");

    var sessions = server.Sessions.GetAllSessions()
        .OrderByDescending(s => s.LastActiveTime)
        .Take(20);

    Console.WriteLine($"  {"VIN",-20} {"登入",-5} {"收/发",-12} {"ICCID",-22} {"最后活跃"}");
    Console.WriteLine($"  {new string('-', 85)}");
    foreach (var s in sessions)
    {
        Console.WriteLine($"  {s.VIN,-20} {(s.IsLoggedIn ? "是" : "否"),-5} {s.ReceivedMessages}/{s.SentMessages,-10} {s.ICCID,-22} {s.LastActiveTime:MM-dd HH:mm:ss}");
    }
    Console.WriteLine();
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GB32960.Protocol;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║  GB/T 32960 测试客户端               ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("  1. 单车交互测试");
Console.WriteLine("  2. 快速吞吐量压测（连→发→断）");
Console.WriteLine("  3. 万台并发持续连接测试");
Console.Write("请选择: ");

var choice = Console.ReadLine()?.Trim();
string host = "127.0.0.1";
int port = 32960;

Console.Write($"服务器地址 [{host}]: ");
var inputHost = Console.ReadLine()?.Trim();
if (!string.IsNullOrEmpty(inputHost)) host = inputHost;

Console.Write($"服务器端口 [{port}]: ");
var inputPort = Console.ReadLine()?.Trim();
if (int.TryParse(inputPort, out int p)) port = p;

switch (choice)
{
    case "2": await RunThroughputTest(host, port); break;
    case "3": await RunConcurrentHoldTest(host, port); break;
    default: await RunSingleTest(host, port); break;
}

// ═══════════════════════════════════════════════════════════
// 模式1: 单车交互测试
// ═══════════════════════════════════════════════════════════

static async Task RunSingleTest(string host, int port)
{
    string vin = "LSGJA52U6AE001234";
    Console.Write($"VIN [{vin}]: ");
    var inputVin = Console.ReadLine()?.Trim();
    if (!string.IsNullOrEmpty(inputVin)) vin = inputVin.PadRight(17)[..17];

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));
    Console.WriteLine($"已连接到 {host}:{port}");

    ushort seq = 1;
    while (true)
    {
        Console.WriteLine("\n命令: 1=登入 2=实时数据 3=补发 4=登出 7=心跳 8=校时 Q=退出");
        Console.Write("> ");
        var cmd = Console.ReadLine()?.Trim().ToUpper();

        byte[]? packet = cmd switch
        {
            "1" => BuildVehicleLogin(vin, seq++),
            "2" => BuildRealtimeData(vin),
            "3" => BuildSupplementaryData(vin),
            "4" => BuildVehicleLogout(vin, seq++),
            "7" => GB32960Encoder.Encode(CommandType.Heartbeat, ResponseFlag.Command, vin, EncryptionType.None, []),
            "8" => GB32960Encoder.Encode(CommandType.TimeSync, ResponseFlag.Command, vin, EncryptionType.None, []),
            "Q" => null,
            _ => null,
        };

        if (cmd == "Q") break;
        if (packet == null) { Console.WriteLine("无效命令"); continue; }

        socket.Send(packet);
        Console.WriteLine($"  发送: {packet.Length} 字节, CMD=0x{packet[2]:X2}");

        var buf = new byte[256];
        int received = socket.Receive(buf);
        if (received > 0)
        {
            var resp = GB32960Decoder.Decode(buf[..received]);
            if (resp != null)
                Console.WriteLine($"  应答: CMD=0x{(byte)resp.Command:X2}, RSP=0x{(byte)resp.Response:X2}, DataLen={resp.DataLength}");
        }
    }

    socket.Shutdown(SocketShutdown.Both);
}

// ═══════════════════════════════════════════════════════════
// 模式2: 快速吞吐量压测（连→发→断）
// ═══════════════════════════════════════════════════════════

static async Task RunThroughputTest(string host, int port)
{
    Console.Write("并发数量 [100]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int count);
    if (count <= 0) count = 100;

    Console.Write("每车消息数 [10]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int msgCount);
    if (msgCount <= 0) msgCount = 10;

    Console.WriteLine($"\n开始吞吐量压测: {count} 车, 每车 {msgCount} 条...\n");

    int success = 0, fail = 0;
    long totalMessages = 0;
    var sw = Stopwatch.StartNew();

    // 分批启动，避免瞬间冲击
    int batchSize = Math.Min(count, 500);
    for (int batch = 0; batch < count; batch += batchSize)
    {
        int end = Math.Min(batch + batchSize, count);
        var tasks = Enumerable.Range(batch, end - batch).Select(async i =>
        {
            string vin = $"LTEST{i:D12}";
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.ReceiveTimeout = 10000;
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));

                var buf = new byte[128];
                socket.Send(BuildVehicleLogin(vin, 1));
                socket.Receive(buf);

                for (int m = 0; m < msgCount; m++)
                {
                    socket.Send(BuildRealtimeData(vin));
                    socket.Receive(buf);
                    Interlocked.Increment(ref totalMessages);
                }

                socket.Send(BuildVehicleLogout(vin, 2));
                socket.Receive(buf);
                socket.Shutdown(SocketShutdown.Both);
                Interlocked.Increment(ref success);
            }
            catch { Interlocked.Increment(ref fail); }
        });
        await Task.WhenAll(tasks);
        Console.Write($"\r  进度: {end}/{count}  成功={success} 失败={fail}");
    }

    sw.Stop();
    Console.WriteLine($"\n\n────────────── 吞吐量压测结果 ──────────────");
    Console.WriteLine($"  总车辆:   {count}");
    Console.WriteLine($"  成功:     {success}");
    Console.WriteLine($"  失败:     {fail}");
    Console.WriteLine($"  总消息:   {totalMessages:N0}");
    Console.WriteLine($"  耗时:     {sw.Elapsed.TotalSeconds:F2} 秒");
    Console.WriteLine($"  吞吐量:   {totalMessages / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} msg/s");
    Console.WriteLine("──────────────────────────────────────────────");
}

// ═══════════════════════════════════════════════════════════
// 模式3: 万台并发持续连接测试
// 测试 N 个终端同时保持连接，周期性发送数据
// ═══════════════════════════════════════════════════════════

static async Task RunConcurrentHoldTest(string host, int port)
{
    Console.Write("目标连接数 [10000]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int targetCount);
    if (targetCount <= 0) targetCount = 10000;

    Console.Write("数据发送间隔秒 [30]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int intervalSec);
    if (intervalSec <= 0) intervalSec = 30;

    Console.Write("每批建立连接数 [500]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int batchSize);
    if (batchSize <= 0) batchSize = 500;

    Console.Write("测试持续分钟 [5]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int durationMin);
    if (durationMin <= 0) durationMin = 5;

    Console.WriteLine();
    Console.WriteLine($"┌─────────────────────────────────────────────┐");
    Console.WriteLine($"│  万台并发持续连接测试                        │");
    Console.WriteLine($"│  目标: {targetCount:N0} 连接                           │");
    Console.WriteLine($"│  间隔: {intervalSec}秒/条  批次: {batchSize}  时长: {durationMin}分钟  │");
    Console.WriteLine($"└─────────────────────────────────────────────┘");
    Console.WriteLine();

    var cts = new CancellationTokenSource();
    var clients = new List<ClientHolder>();
    int connected = 0, connectFailed = 0;
    long totalMsgSent = 0, totalMsgRecv = 0, totalErrors = 0;
    long latencySum = 0, latencyCount = 0;
    var testStart = Stopwatch.StartNew();

    // ──── 阶段1: 分批建立连接 ────
    Console.WriteLine("[阶段1] 分批建立连接...");
    var connSw = Stopwatch.StartNew();

    for (int batch = 0; batch < targetCount; batch += batchSize)
    {
        int end = Math.Min(batch + batchSize, targetCount);
        var tasks = new List<Task>();

        for (int i = batch; i < end; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                string vin = $"LT32960{idx:D10}";
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    socket.ReceiveTimeout = 10000;
                    socket.SendTimeout = 5000;
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));

                    // 发送登入
                    var buf = new byte[128];
                    socket.Send(BuildVehicleLogin(vin, 1));
                    socket.Receive(buf);

                    var client = new ClientHolder { Socket = socket, VIN = vin, Buffer = buf };
                    lock (clients) clients.Add(client);
                    Interlocked.Increment(ref connected);
                }
                catch
                {
                    Interlocked.Increment(ref connectFailed);
                }
            }));
        }

        await Task.WhenAll(tasks);

        double rate = connected * 100.0 / targetCount;
        Console.Write($"\r  连接进度: {connected:N0}/{targetCount:N0} ({rate:F1}%)  失败: {connectFailed}  耗时: {connSw.Elapsed.TotalSeconds:F1}s");
    }

    connSw.Stop();
    Console.WriteLine($"\n\n[阶段1完成] 已连接: {connected:N0}, 失败: {connectFailed}, 耗时: {connSw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  连接建立速率: {connected / Math.Max(connSw.Elapsed.TotalSeconds, 0.001):F0} 连接/秒\n");

    if (connected == 0)
    {
        Console.WriteLine("无成功连接，测试终止。");
        return;
    }

    // ──── 阶段2: 持续发送数据 ────
    Console.WriteLine($"[阶段2] 持续发送数据，间隔 {intervalSec}s，持续 {durationMin} 分钟...");
    Console.WriteLine("  按 Q 提前结束\n");

    var endTime = DateTime.Now.AddMinutes(durationMin);
    int round = 0;

    // 后台统计刷新
    var statsTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(3000, cts.Token).ContinueWith(_ => { });
            if (cts.Token.IsCancellationRequested) break;
            double avgLatency = latencyCount > 0 ? (double)latencySum / latencyCount / 10000.0 : 0; // ticks to ms
            Console.Write($"\r  在线={connected:N0}  轮次={round}  发送={totalMsgSent:N0}  应答={totalMsgRecv:N0}" +
                          $"  错误={totalErrors}  延时={avgLatency:F2}ms  运行={testStart.Elapsed:mm\\:ss}  ");
        }
    });

    // 检测Q键退出
    var keyTask = Task.Run(() =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
            {
                cts.Cancel();
                break;
            }
            Thread.Sleep(200);
        }
    });

    while (DateTime.Now < endTime && !cts.Token.IsCancellationRequested)
    {
        round++;
        var roundSw = Stopwatch.StartNew();

        // 并发发送：分批发送，每批并行
        var snapshot = clients.ToArray();
        int sendBatch = Math.Min(snapshot.Length, 2000);

        for (int b = 0; b < snapshot.Length; b += sendBatch)
        {
            if (cts.Token.IsCancellationRequested) break;
            int bEnd = Math.Min(b + sendBatch, snapshot.Length);
            var sendTasks = new List<Task>();

            for (int ci = b; ci < bEnd; ci++)
            {
                var c = snapshot[ci];
                sendTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var sw2 = Stopwatch.StartNew();
                        var packet = BuildRealtimeData(c.VIN);
                        c.Socket.Send(packet);
                        Interlocked.Increment(ref totalMsgSent);

                        int recv = c.Socket.Receive(c.Buffer);
                        if (recv > 0)
                        {
                            Interlocked.Increment(ref totalMsgRecv);
                            sw2.Stop();
                            Interlocked.Add(ref latencySum, sw2.Elapsed.Ticks);
                            Interlocked.Increment(ref latencyCount);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref totalErrors);
                    }
                }));
            }

            await Task.WhenAll(sendTasks);
        }

        // 等待到下一个发送周期
        var elapsed = roundSw.Elapsed;
        int waitMs = Math.Max(0, intervalSec * 1000 - (int)elapsed.TotalMilliseconds);
        if (waitMs > 0 && !cts.Token.IsCancellationRequested)
            await Task.Delay(waitMs, cts.Token).ContinueWith(_ => { });
    }

    cts.Cancel();
    testStart.Stop();

    // ──── 阶段3: 断开连接 ────
    Console.WriteLine($"\n\n[阶段3] 断开所有连接...");
    foreach (var c in clients)
    {
        try
        {
            c.Socket.Send(BuildVehicleLogout(c.VIN, 99));
            c.Socket.Shutdown(SocketShutdown.Both);
            c.Socket.Close();
        }
        catch { }
    }

    // ──── 结果 ────
    double avgLat = latencyCount > 0 ? (double)latencySum / latencyCount / TimeSpan.TicksPerMillisecond : 0;
    double msgPerSec = totalMsgSent / Math.Max(testStart.Elapsed.TotalSeconds, 0.001);

    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║               万台并发测试结果                       ║");
    Console.WriteLine("╠══════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  目标连接:    {targetCount,10:N0}                          ║");
    Console.WriteLine($"║  实际连接:    {connected,10:N0}  (失败: {connectFailed})             ║");
    Console.WriteLine($"║  连接成功率:  {connected * 100.0 / targetCount,9:F1}%                          ║");
    Console.WriteLine($"║  测试时长:    {testStart.Elapsed.TotalSeconds,9:F1}s                           ║");
    Console.WriteLine($"║  发送轮次:    {round,10}                          ║");
    Console.WriteLine($"║  总发送消息:  {totalMsgSent,10:N0}                          ║");
    Console.WriteLine($"║  总收到应答:  {totalMsgRecv,10:N0}                          ║");
    Console.WriteLine($"║  总错误:      {totalErrors,10:N0}                          ║");
    Console.WriteLine($"║  平均延时:    {avgLat,9:F2}ms                          ║");
    Console.WriteLine($"║  吞吐量:      {msgPerSec,9:F0} msg/s                     ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
}

// ═══════════════════════════════════════════════════════════
// 辅助类和消息构造
// ═══════════════════════════════════════════════════════════

static byte[] BuildVehicleLogin(string vin, ushort seq)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    data.Add((byte)(now.Year - 2000)); data.Add((byte)now.Month); data.Add((byte)now.Day);
    data.Add((byte)now.Hour); data.Add((byte)now.Minute); data.Add((byte)now.Second);
    data.Add((byte)(seq >> 8)); data.Add((byte)(seq & 0xFF));
    for (int i = 0; i < 20; i++) data.Add(0x89);
    data.Add(1); data.Add(0);
    return GB32960Encoder.Encode(CommandType.VehicleLogin, ResponseFlag.Command, vin, EncryptionType.None, data.ToArray());
}

static byte[] BuildVehicleLogout(string vin, ushort seq)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    data.Add((byte)(now.Year - 2000)); data.Add((byte)now.Month); data.Add((byte)now.Day);
    data.Add((byte)now.Hour); data.Add((byte)now.Minute); data.Add((byte)now.Second);
    data.Add((byte)(seq >> 8)); data.Add((byte)(seq & 0xFF));
    return GB32960Encoder.Encode(CommandType.VehicleLogout, ResponseFlag.Command, vin, EncryptionType.None, data.ToArray());
}

static byte[] BuildRealtimeData(string vin)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    var rnd = Random.Shared;

    data.Add((byte)(now.Year - 2000)); data.Add((byte)now.Month); data.Add((byte)now.Day);
    data.Add((byte)now.Hour); data.Add((byte)now.Minute); data.Add((byte)now.Second);

    // 0x01 整车数据 20B
    data.Add(0x01);
    data.Add(0x01); data.Add(0x03); data.Add(0x01);
    ushort speed = (ushort)rnd.Next(0, 1200);
    data.Add((byte)(speed >> 8)); data.Add((byte)(speed & 0xFF));
    uint mileage = (uint)rnd.Next(10000, 500000);
    data.Add((byte)(mileage >> 24)); data.Add((byte)(mileage >> 16));
    data.Add((byte)(mileage >> 8)); data.Add((byte)(mileage & 0xFF));
    ushort voltage = (ushort)(3800 + rnd.Next(0, 400));
    data.Add((byte)(voltage >> 8)); data.Add((byte)(voltage & 0xFF));
    ushort current = (ushort)(10000 + rnd.Next(-500, 500));
    data.Add((byte)(current >> 8)); data.Add((byte)(current & 0xFF));
    data.Add((byte)rnd.Next(20, 100));
    data.Add(0x01); data.Add(0x0E);
    ushort ins = (ushort)rnd.Next(500, 5000);
    data.Add((byte)(ins >> 8)); data.Add((byte)(ins & 0xFF));
    data.Add((byte)rnd.Next(0, 50)); data.Add(0x00);

    // 0x05 位置 9B
    data.Add(0x05); data.Add(0x00);
    uint lon = (uint)(121_473_000 + rnd.Next(-10000, 10000));
    uint lat = (uint)(31_230_000 + rnd.Next(-10000, 10000));
    data.Add((byte)(lon >> 24)); data.Add((byte)(lon >> 16)); data.Add((byte)(lon >> 8)); data.Add((byte)(lon & 0xFF));
    data.Add((byte)(lat >> 24)); data.Add((byte)(lat >> 16)); data.Add((byte)(lat >> 8)); data.Add((byte)(lat & 0xFF));

    // 0x06 极值 14B
    data.Add(0x06);
    data.Add(1); data.Add(1);
    ushort maxV = (ushort)(4150 + rnd.Next(0, 50));
    data.Add((byte)(maxV >> 8)); data.Add((byte)(maxV & 0xFF));
    data.Add(1); data.Add(2);
    ushort minV = (ushort)(3900 + rnd.Next(0, 50));
    data.Add((byte)(minV >> 8)); data.Add((byte)(minV & 0xFF));
    data.Add(1); data.Add(1); data.Add((byte)(40 + rnd.Next(20, 45)));
    data.Add(1); data.Add(2); data.Add((byte)(40 + rnd.Next(15, 30)));

    // 0x07 报警 9B
    data.Add(0x07); data.Add(0x00);
    data.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

    return GB32960Encoder.Encode(CommandType.RealtimeData, ResponseFlag.Command, vin, EncryptionType.None, data.ToArray());
}

static byte[] BuildSupplementaryData(string vin)
{
    var rtData = BuildRealtimeData(vin);
    rtData[2] = (byte)CommandType.SupplementaryData;
    byte checksum = 0;
    for (int i = 2; i < rtData.Length - 1; i++) checksum ^= rtData[i];
    rtData[^1] = checksum;
    return rtData;
}

class ClientHolder
{
    public Socket Socket { get; set; } = null!;
    public string VIN { get; set; } = "";
    public byte[] Buffer { get; set; } = new byte[128];
}

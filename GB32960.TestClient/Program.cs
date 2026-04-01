using System.Net;
using System.Net.Sockets;
using System.Text;
using GB32960.Protocol;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("GB/T 32960 测试客户端");
Console.WriteLine("1. 单车测试（交互模式）");
Console.WriteLine("2. 并发压测");
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

if (choice == "2")
    await RunConcurrencyTest(host, port);
else
    await RunSingleTest(host, port);

// ─── 单车测试 ────────────────────────────────────

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
        Console.WriteLine();
        Console.WriteLine("命令: 1=登入 2=实时数据 3=补发 4=登出 7=心跳 8=校时 Q=退出");
        Console.Write("> ");
        var cmd = Console.ReadLine()?.Trim().ToUpper();

        byte[]? packet = cmd switch
        {
            "1" => BuildVehicleLogin(vin, seq++),
            "2" => BuildRealtimeData(vin),
            "3" => BuildSupplementaryData(vin),
            "4" => BuildVehicleLogout(vin, seq++),
            "7" => GB32960Encoder.EncodeResponse(CommandType.Heartbeat, ResponseFlag.Command, vin),
            "8" => GB32960Encoder.Encode(CommandType.TimeSync, ResponseFlag.Command, vin, EncryptionType.None, Array.Empty<byte>()),
            "Q" => null,
            _ => null,
        };

        if (cmd == "Q") break;
        if (packet == null) { Console.WriteLine("无效命令"); continue; }

        socket.Send(packet);
        Console.WriteLine($"  发送: {packet.Length} 字节, CMD=0x{packet[2]:X2}");

        // 接收应答
        var buf = new byte[256];
        int received = socket.Receive(buf);
        if (received > 0)
        {
            var resp = GB32960Decoder.Decode(buf[..received]);
            if (resp != null)
                Console.WriteLine($"  应答: CMD=0x{(byte)resp.Command:X2}, RSP=0x{(byte)resp.Response:X2}, DataLen={resp.DataLength}");
            else
                Console.WriteLine($"  应答: {received} 字节 (解析失败)");
        }
    }

    socket.Shutdown(SocketShutdown.Both);
    Console.WriteLine("已断开连接");
}

// ─── 并发压测 ────────────────────────────────────

static async Task RunConcurrencyTest(string host, int port)
{
    Console.Write("并发数量 [100]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int count);
    if (count <= 0) count = 100;

    Console.Write("每车消息数 [10]: ");
    int.TryParse(Console.ReadLine()?.Trim(), out int msgCount);
    if (msgCount <= 0) msgCount = 10;

    Console.WriteLine($"开始压测: {count} 车, 每车 {msgCount} 条消息...");

    int success = 0, fail = 0;
    long totalMessages = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();

    var tasks = Enumerable.Range(0, count).Select(async i =>
    {
        string vin = $"LTEST{i:D12}";
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveTimeout = 5000;
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));

            // 登入
            var loginPacket = BuildVehicleLogin(vin, 1);
            socket.Send(loginPacket);
            var buf = new byte[128];
            socket.Receive(buf);

            // 发送实时数据
            for (int m = 0; m < msgCount; m++)
            {
                var dataPacket = BuildRealtimeData(vin);
                socket.Send(dataPacket);
                socket.Receive(buf);
                Interlocked.Increment(ref totalMessages);
            }

            // 登出
            var logoutPacket = BuildVehicleLogout(vin, 2);
            socket.Send(logoutPacket);
            socket.Receive(buf);

            socket.Shutdown(SocketShutdown.Both);
            Interlocked.Increment(ref success);
        }
        catch
        {
            Interlocked.Increment(ref fail);
        }
    });

    await Task.WhenAll(tasks);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("────────────── 压测结果 ──────────────");
    Console.WriteLine($"  总车辆:   {count}");
    Console.WriteLine($"  成功:     {success}");
    Console.WriteLine($"  失败:     {fail}");
    Console.WriteLine($"  总消息:   {totalMessages}");
    Console.WriteLine($"  耗时:     {sw.Elapsed.TotalSeconds:F2} 秒");
    Console.WriteLine($"  吞吐量:   {totalMessages / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} 消息/秒");
    Console.WriteLine("──────────────────────────────────────");
}

// ─── 构造消息 ────────────────────────────────────

static byte[] BuildVehicleLogin(string vin, ushort seq)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    // 采集时间 6B
    data.Add((byte)(now.Year - 2000));
    data.Add((byte)now.Month);
    data.Add((byte)now.Day);
    data.Add((byte)now.Hour);
    data.Add((byte)now.Minute);
    data.Add((byte)now.Second);
    // 登入流水号 2B
    data.Add((byte)(seq >> 8));
    data.Add((byte)(seq & 0xFF));
    // ICCID 20B (BCD)
    for (int i = 0; i < 20; i++) data.Add(0x89); // mock ICCID
    // 子系统数 1B + 编码长度 1B
    data.Add(1);
    data.Add(0);

    return GB32960Encoder.Encode(CommandType.VehicleLogin, ResponseFlag.Command, vin,
        EncryptionType.None, data.ToArray());
}

static byte[] BuildVehicleLogout(string vin, ushort seq)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    data.Add((byte)(now.Year - 2000));
    data.Add((byte)now.Month);
    data.Add((byte)now.Day);
    data.Add((byte)now.Hour);
    data.Add((byte)now.Minute);
    data.Add((byte)now.Second);
    data.Add((byte)(seq >> 8));
    data.Add((byte)(seq & 0xFF));

    return GB32960Encoder.Encode(CommandType.VehicleLogout, ResponseFlag.Command, vin,
        EncryptionType.None, data.ToArray());
}

static byte[] BuildRealtimeData(string vin)
{
    var data = new List<byte>();
    var now = DateTime.Now;
    var rnd = new Random();

    // 采集时间 6B
    data.Add((byte)(now.Year - 2000));
    data.Add((byte)now.Month);
    data.Add((byte)now.Day);
    data.Add((byte)now.Hour);
    data.Add((byte)now.Minute);
    data.Add((byte)now.Second);

    // 信息类型 0x01 整车数据 (20B)
    data.Add(0x01);
    data.Add(0x01); // 启动
    data.Add(0x03); // 未充电
    data.Add(0x01); // 纯电
    ushort speed = (ushort)(rnd.Next(0, 1200)); // 0-120km/h
    data.Add((byte)(speed >> 8)); data.Add((byte)(speed & 0xFF));
    uint mileage = (uint)(rnd.Next(10000, 500000)); // 1000-50000km
    data.Add((byte)(mileage >> 24)); data.Add((byte)(mileage >> 16));
    data.Add((byte)(mileage >> 8)); data.Add((byte)(mileage & 0xFF));
    ushort voltage = (ushort)(3800 + rnd.Next(0, 400)); // 380-420V
    data.Add((byte)(voltage >> 8)); data.Add((byte)(voltage & 0xFF));
    ushort current = (ushort)(10000 + rnd.Next(-500, 500)); // -50~50A
    data.Add((byte)(current >> 8)); data.Add((byte)(current & 0xFF));
    data.Add((byte)rnd.Next(20, 100)); // SOC
    data.Add(0x01); // DC-DC working
    data.Add(0x0E); // D档
    ushort insulation = (ushort)rnd.Next(500, 5000);
    data.Add((byte)(insulation >> 8)); data.Add((byte)(insulation & 0xFF));
    data.Add((byte)rnd.Next(0, 50)); // 加速踏板
    data.Add(0x00); // 制动踏板

    // 信息类型 0x05 位置 (9B)
    data.Add(0x05);
    data.Add(0x00); // 有效,北纬,东经
    uint lon = (uint)(121_473_000 + rnd.Next(-10000, 10000));
    uint lat = (uint)(31_230_000 + rnd.Next(-10000, 10000));
    data.Add((byte)(lon >> 24)); data.Add((byte)(lon >> 16));
    data.Add((byte)(lon >> 8)); data.Add((byte)(lon & 0xFF));
    data.Add((byte)(lat >> 24)); data.Add((byte)(lat >> 16));
    data.Add((byte)(lat >> 8)); data.Add((byte)(lat & 0xFF));

    // 信息类型 0x06 极值 (14B)
    data.Add(0x06);
    data.Add(1); data.Add(1); // 最高电压子系统1,电池1
    ushort maxV = (ushort)(4150 + rnd.Next(0, 50));
    data.Add((byte)(maxV >> 8)); data.Add((byte)(maxV & 0xFF));
    data.Add(1); data.Add(2); // 最低电压子系统1,电池2
    ushort minV = (ushort)(3900 + rnd.Next(0, 50));
    data.Add((byte)(minV >> 8)); data.Add((byte)(minV & 0xFF));
    data.Add(1); data.Add(1); // 最高温度
    data.Add((byte)(40 + rnd.Next(20, 45))); // 20-45℃ (offset 40)
    data.Add(1); data.Add(2); // 最低温度
    data.Add((byte)(40 + rnd.Next(15, 30))); // 15-30℃

    // 信息类型 0x07 报警 (min 9B, 无故障码)
    data.Add(0x07);
    data.Add(0x00); // 最高报警等级 0=无
    data.AddRange(new byte[] { 0, 0, 0, 0 }); // 通用报警标志=0
    data.Add(0); // 电池故障数=0
    data.Add(0); // 电机故障数=0
    data.Add(0); // 发动机故障数=0
    data.Add(0); // 其他故障数=0

    return GB32960Encoder.Encode(CommandType.RealtimeData, ResponseFlag.Command, vin,
        EncryptionType.None, data.ToArray());
}

static byte[] BuildSupplementaryData(string vin)
{
    // 补发数据格式与实时数据相同
    var rtData = BuildRealtimeData(vin);
    // 修改命令标识为0x03
    rtData[2] = (byte)CommandType.SupplementaryData;
    // 重新计算校验码
    byte checksum = 0;
    for (int i = 2; i < rtData.Length - 1; i++)
        checksum ^= rtData[i];
    rtData[^1] = checksum;
    return rtData;
}

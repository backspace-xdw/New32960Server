namespace GB32960.Server;

public class ServerConfig
{
    public string IpAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 32960;
    public int MaxConnections { get; set; } = 100000;
    public int SessionTimeoutMinutes { get; set; } = 30;
    public int ReceiveBufferSize { get; set; } = 4096;
    public string LogLevel { get; set; } = "Information";
}

public class InfluxDbConfig
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://localhost:8086";
    public string Token { get; set; } = "";
    public string Org { get; set; } = "xdw";
    public string Bucket { get; set; } = "gb32960";
    public int BatchSize { get; set; } = 5000;          // 批量写入条数
    public int FlushIntervalMs { get; set; } = 100;      // 队列空时等待间隔
    public bool WriteCellVoltages { get; set; } = false;  // 单体电压（数据量大，默认关）
}

public class RawArchiveConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseDirectory { get; set; } = "RawData";
    public bool ArchiveSent { get; set; } = false;        // 是否也存档发出的应答
}

public class ForwarderConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 32961;
    public int MaxQueueSize { get; set; } = 100000;
    public int ReconnectIntervalMs { get; set; } = 5000;
    public bool SendPlatformLogin { get; set; } = true;   // 连接后自动发送平台登入
    public string PlatformVIN { get; set; } = "PLATFORM00000000";
}

public class FileLogConfig
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "Logs";
    public int RetainDays { get; set; } = 30;             // 日志保留天数
}

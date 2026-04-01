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

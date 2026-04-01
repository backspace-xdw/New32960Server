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

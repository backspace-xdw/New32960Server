using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GB32960.Server;

/// <summary>
/// 原始报文存档 — 按 VIN/日期 分目录保存二进制原始帧
/// 用于合规回溯和故障排查
/// 异步批量写入，不阻塞消息处理
/// </summary>
public class RawPacketArchiver : IDisposable
{
    private readonly ILogger<RawPacketArchiver> _logger;
    private readonly RawArchiveConfig _config;
    private readonly ConcurrentQueue<(string vin, byte[] data, DateTime time, string direction)> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _writeTask;
    private long _totalArchived;

    public long TotalArchived => Interlocked.Read(ref _totalArchived);

    public RawPacketArchiver(ILogger<RawPacketArchiver> logger, RawArchiveConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public void Start()
    {
        if (!_config.Enabled) return;
        Directory.CreateDirectory(_config.BaseDirectory);
        _writeTask = Task.Run(WriteLoop);
        _logger.LogInformation("原始报文存档已启动: {dir}", _config.BaseDirectory);
    }

    /// <summary>存档收到的原始报文</summary>
    public void ArchiveReceived(string vin, byte[] rawData)
    {
        if (!_config.Enabled) return;
        _queue.Enqueue((vin, rawData, DateTime.Now, "RX"));
    }

    /// <summary>存档发出的原始报文</summary>
    public void ArchiveSent(string vin, byte[] rawData)
    {
        if (!_config.Enabled || !_config.ArchiveSent) return;
        _queue.Enqueue((vin, rawData, DateTime.Now, "TX"));
    }

    private async Task WriteLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int count = 0;
                while (_queue.TryDequeue(out var item) && count < 1000)
                {
                    WritePacket(item.vin, item.data, item.time, item.direction);
                    count++;
                }

                if (count > 0)
                    Interlocked.Add(ref _totalArchived, count);
                else
                    await Task.Delay(50, _cts.Token).ContinueWith(_ => { });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "报文存档写入失败");
                await Task.Delay(500);
            }
        }
    }

    private void WritePacket(string vin, byte[] data, DateTime time, string direction)
    {
        try
        {
            // 目录: base/VIN/2026-04-01/
            string vinDir = string.IsNullOrEmpty(vin) ? "_unknown" : vin;
            string dateDir = time.ToString("yyyy-MM-dd");
            string dir = Path.Combine(_config.BaseDirectory, vinDir, dateDir);
            Directory.CreateDirectory(dir);

            // 文件: base/VIN/2026-04-01/data.bin (追加写入)
            string filePath = Path.Combine(dir, "data.bin");

            using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            // 写入格式: [方向1B][时间戳8B][长度4B][原始数据NB]
            fs.WriteByte(direction == "TX" ? (byte)0x01 : (byte)0x00);
            var tickBytes = BitConverter.GetBytes(time.Ticks);
            fs.Write(tickBytes, 0, 8);
            var lenBytes = BitConverter.GetBytes(data.Length);
            fs.Write(lenBytes, 0, 4);
            fs.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("写入报文文件失败: {msg}", ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writeTask?.Wait(3000);
    }
}

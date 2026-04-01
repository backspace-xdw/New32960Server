using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GB32960.Server;

/// <summary>
/// 文件日志 — 按日期滚动，自动清理过期文件
/// 异步批量写入，不阻塞业务线程
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogConfig _config;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeTask;
    private string _currentDate = "";
    private StreamWriter? _writer;

    public FileLoggerProvider(FileLogConfig config)
    {
        _config = config;
        Directory.CreateDirectory(config.Directory);
        _writeTask = Task.Run(WriteLoop);

        // 启动时清理过期日志
        Task.Run(CleanupOldLogs);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string message) => _queue.Enqueue(message);

    private async Task WriteLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int count = 0;
                while (_queue.TryDequeue(out var line) && count < 500)
                {
                    EnsureWriter();
                    _writer!.WriteLine(line);
                    count++;
                }

                if (count > 0)
                    _writer?.Flush();
                else
                    await Task.Delay(50, _cts.Token).ContinueWith(_ => { });
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(100); }
        }

        _writer?.Flush();
        _writer?.Dispose();
    }

    private void EnsureWriter()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today == _currentDate && _writer != null) return;

        _writer?.Flush();
        _writer?.Dispose();
        _currentDate = today;
        string path = Path.Combine(_config.Directory, $"gb32960-{today}.log");
        _writer = new StreamWriter(path, append: true) { AutoFlush = false };
    }

    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_config.RetainDays);
            foreach (var file in Directory.GetFiles(_config.Directory, "gb32960-*.log"))
            {
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writeTask.Wait(3000);
        _writer?.Dispose();
    }
}

internal class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "INF",
        };

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {_category}: {formatter(state, exception)}";
        if (exception != null)
            line += $"\n  {exception.GetType().Name}: {exception.Message}";

        _provider.Enqueue(line);
    }
}

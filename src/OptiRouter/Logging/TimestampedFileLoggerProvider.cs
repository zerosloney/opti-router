using System.Text;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Logging;

/// <summary>
/// 极简文件日志 Provider：以 simple 控制台格式追加写入 <c>logs/service.log</c>，
/// 每行带本地时间戳前缀 <c>[yyyy-MM-dd HH:mm:ss.fff]</c>。
/// <para>
/// 背景：内置 SimpleConsoleFormatter 为 internal 且默认实例不经 DI options，
/// TimestampFormat 的配置与代码路径均无法生效，曾致 service.log 8.5 万行无时间戳。
/// 经由独立文件 Provider 直写，彻底绕开 console formatter 的限制，
/// 并以 ClearProviders 移除 console/EventLog 默认 provider（后者在关停时有 disposed 竞态刷屏）。
/// </para>
/// </summary>
/// <remarks>
/// intentional-simple: 全局单一写入锁串行追加（日志量低于千行/分钟，锁竞争可忽略）；
/// 进程生命周期内不轮转，保留期由 AuditRetentionService 之外的常规运维清理。
/// 需要轮转/压缩时升级为按日期文件或引入第三方 provider。
/// </remarks>
public sealed class TimestampedFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    /// <summary>以追加模式打开日志文件（目录不存在则创建）。</summary>
    /// <param name="filePath">日志文件绝对路径。</param>
    public TimestampedFileLoggerProvider(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    internal void Write(string categoryName, LogLevel logLevel, EventId eventId, string message, Exception? exception)
    {
        string level = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => logLevel.ToString()
        };

        var sb = new StringBuilder(128);
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
          .Append(level).Append(": ").Append(categoryName)
          .Append('[').Append(eventId.Id).AppendLine("]");

        sb.Append("      ").AppendLine(message);
        if (exception is not null)
        {
            sb.Append("      ").AppendLine(exception.ToString().Replace("\n", "\n      "));
        }

        lock (_lock)
        {
            _writer.Write(sb.ToString());
        }
    }

    private sealed class FileLogger(TimestampedFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            provider.Write(categoryName, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}

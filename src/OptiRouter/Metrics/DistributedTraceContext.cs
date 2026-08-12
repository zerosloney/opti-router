using System.Diagnostics;

namespace OptiRouter.Metrics;

/// <summary>
/// W3C Distributed TraceContext (traceparent) 解析与生成工具。
/// 标准格式: 00-{4byte-version}-{16byte-trace-id}-{8byte-parent-id}-{1byte-trace-flags}
/// 映射至 System.Diagnostics.ActivitySource，支持与 OpenTelemetry Collector / Jaeger 无缝集成。
/// </summary>
public static class DistributedTraceContext
{
    public static readonly ActivitySource ActivitySource = new("OptiRouter.Tracing", "1.0.0");

    /// <summary>
    /// 解析 W3C traceparent 请求头。
    /// </summary>
    public static (string TraceId, string ParentSpanId) ParseTraceParent(string? traceparentHeader)
    {
        if (string.IsNullOrWhiteSpace(traceparentHeader))
        {
            return (GenerateTraceId(), string.Empty);
        }

        var parts = traceparentHeader.Trim().Split('-');
        if (parts.Length >= 3 && parts[1].Length == 32 && parts[2].Length == 16)
        {
            return (parts[1], parts[2]);
        }

        return (GenerateTraceId(), string.Empty);
    }

    /// <summary>
    /// 生成符合 W3C 规范的 128-bit (32 hex) TraceId。
    /// </summary>
    public static string GenerateTraceId()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// 生成符合 W3C 规范的 64-bit (16 hex) SpanId。
    /// </summary>
    public static string GenerateSpanId()
    {
        return Random.Shared.NextInt64().ToString("x16");
    }

    /// <summary>
    /// 格式化为 W3C traceparent 头字符串。
    /// </summary>
    public static string BuildTraceParent(string traceId, string spanId, bool sampled = true)
    {
        string validTraceId = string.IsNullOrWhiteSpace(traceId) || traceId.Length != 32 ? GenerateTraceId() : traceId;
        string validSpanId = string.IsNullOrWhiteSpace(spanId) || spanId.Length != 16 ? GenerateSpanId() : spanId;
        string flags = sampled ? "01" : "00";
        return $"00-{validTraceId}-{validSpanId}-{flags}";
    }
}

/// <summary>
/// 请求级 Trace 作用域：中间件在入口 <see cref="Begin"/>，<c>OutcomeRecorder.RecordAudit</c> 读 <see cref="Current"/>，
/// 通过 <see cref="AsyncLocal{T}"/> 沿异步流贯穿整条请求，无需在每个 RecordAudit 调用点显式传 trace 参数。
/// 嵌套 Begin 会覆盖父 scope（本服务单层入口），Dispose 复原。
/// </summary>
public sealed class TraceScope(string traceId, string spanId, string? parentSpanId) : IDisposable
{
    private static readonly AsyncLocal<TraceScope?> _current = new();

    /// <summary>当前异步流的活跃 trace 作用域；未在 trace 中间件覆盖范围内时为 null。</summary>
    public static TraceScope? Current => _current.Value;

    public string TraceId { get; } = traceId;
    public string SpanId { get; } = spanId;
    public string? ParentSpanId { get; } = parentSpanId;

    public static TraceScope Begin(string traceId, string spanId, string? parentSpanId)
    {
        var scope = new TraceScope(traceId, spanId, parentSpanId);
        _current.Value = scope;
        return scope;
    }

    public void Dispose() => _current.Value = null;
}

namespace EvolverCollect.Core.Abstractions;

/// <summary>连接/握手结果。约定：驱动失败时返回 Fail，而不是抛异常（便于界面直接展示原因）。</summary>
public sealed record ConnectResult(bool Success, string? Error = null, TimeSpan Elapsed = default)
{
    public static ConnectResult Ok(TimeSpan elapsed = default) => new(true, null, elapsed);
    public static ConnectResult Fail(string error, TimeSpan elapsed = default) => new(false, error, elapsed);
}

/// <summary>写点位结果。</summary>
public sealed record WriteResult(bool Success, string? Error = null)
{
    public static WriteResult Ok() => new(true);
    public static WriteResult Fail(string error) => new(false, error);
}

[Flags]
public enum DriverCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,
    Browse = 4,   // 支持地址空间浏览（如 OPC UA）
    Subscribe = 8, // 支持订阅推送（如 OPC UA），可替代轮询
}

/// <summary>
/// 驱动元数据。
/// ParameterSchemaJson 是连接参数的 JSON Schema，控制台据此<b>自动渲染配置表单</b>——新增驱动前端零改动。
/// DocsSlug 关联文档中心教程路径，界面“查看帮助”直达。
/// </summary>
public sealed record DriverDescriptor(
    string Id,
    string DisplayName,
    string Vendor,
    string ParameterSchemaJson,
    DriverCapabilities Capabilities,
    string? AddressHelp = null,
    string? DocsSlug = null);

/// <summary>驱动运行统计（线程安全可变对象，供界面与 API 直读）。</summary>
public sealed class DriverStats
{
    private long _totalReads;
    private long _failedReads;
    private long _totalPoints;
    private long _totalWrites;
    private long _failedWrites;

    public DateTime? LastErrorAt { get; private set; }
    public string? LastError { get; private set; }
    public TimeSpan LastLatency { get; private set; }

    public void RecordRead(TimeSpan latency, bool success, int pointCount, string? error = null)
    {
        Interlocked.Increment(ref _totalReads);
        if (success) { Interlocked.Add(ref _totalPoints, pointCount); }
        else { Interlocked.Increment(ref _failedReads); SetError(error); }
        LastLatency = latency;
    }

    public void RecordWrite(bool success, string? error = null)
    {
        Interlocked.Increment(ref _totalWrites);
        if (!success) { Interlocked.Increment(ref _failedWrites); SetError(error); }
    }

    private void SetError(string? error)
    {
        LastError = error;
        LastErrorAt = DateTime.Now;
    }

    public long TotalReads => Interlocked.Read(ref _totalReads);
    public long FailedReads => Interlocked.Read(ref _failedReads);
    public long TotalPoints => Interlocked.Read(ref _totalPoints);
    public long TotalWrites => Interlocked.Read(ref _totalWrites);
    public long FailedWrites => Interlocked.Read(ref _failedWrites);
    public double SuccessRate => TotalReads == 0 ? 1.0 : 1.0 - (double)FailedReads / TotalReads;
}

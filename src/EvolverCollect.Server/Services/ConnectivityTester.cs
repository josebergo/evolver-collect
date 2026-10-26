namespace EvolverCollect.Server.Services;

using System.Diagnostics;
using EvolverCollect.Core.Abstractions;

/// <summary>连通性测试：用给定连接参数创建临时驱动实例探测（API 与控制台共用）。</summary>
public sealed class ConnectivityTester(IEnumerable<IDriverFactory> factories)
{
    public async Task<(bool Success, string? Error, double ElapsedMs)> TestAsync(
        string driverId, string? connectionJson, CancellationToken ct = default)
    {
        var factory = factories.FirstOrDefault(f => f.DriverId.Equals(driverId, StringComparison.OrdinalIgnoreCase));
        if (factory is null) return (false, $"未知驱动类型: {driverId}", 0);

        try
        {
            var driver = factory.Create(connectionJson ?? "{}");
            try
            {
                var sw = Stopwatch.StartNew();
                var result = await driver.ConnectAsync(ct);
                await driver.DisconnectAsync();
                return (result.Success, result.Error, sw.Elapsed.TotalMilliseconds);
            }
            finally { await driver.DisposeAsync(); }
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }
}

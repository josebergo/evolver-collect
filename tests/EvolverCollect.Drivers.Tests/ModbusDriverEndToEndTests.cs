namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Drivers.Modbus;
using EvolverCollect.Simulator;

/// <summary>端到端：真驱动 ↔ 模拟从站（TCP 与 RTU-over-TCP 两种封装全过一遍）。</summary>
public class ModbusDriverEndToEndTests
{
    private static TagDefinition Tag(long id, string address, TagDataType type, bool writable = false) =>
        new(id, 1, $"t{id}", address, type, writable, null, 1, 0, null, null, null);

    private static DeviceBatch Batch(params TagDefinition[] tags) => new(1, 1, "DEV-1", 1, tags);

    [Fact]
    public async Task Tcp_read_write_against_simulator()
    {
        await using var server = new ModbusSimulatorServer(0, SimFraming.ModbusTcp);
        await server.StartAsync();
        server.SetHoldingFloat(0, 66.5f);          // HR0-1
        server.SetHolding(2, 300);                 // HR2（温度 ×10）
        server.SetCoil(0, true);
        server.SetInput(0, 1234);                  // IR0

        await using var driver = new ModbusDriver(new ModbusOptions
        {
            Host = "127.0.0.1", Port = server.EffectivePort, Mode = "tcp", TimeoutMs = 2000,
        });

        var connect = await driver.ConnectAsync(CancellationToken.None);
        Assert.True(connect.Success, connect.Error);

        var points = await driver.ReadAsync(Batch(
            Tag(1, "40001", TagDataType.Float),
            Tag(2, "40003", TagDataType.UInt16),
            Tag(3, "00001", TagDataType.Bool),
            Tag(4, "30001", TagDataType.UInt16)), CancellationToken.None);

        Assert.Equal(4, points.Count);
        Assert.All(points, p => Assert.Equal(Quality.Good, p.Quality));
        Assert.Equal(66.5f, points[0].Value);
        Assert.Equal((ushort)300, points[1].Value);
        Assert.Equal(true, points[2].Value);
        Assert.Equal((ushort)1234, points[3].Value);

        // 写：单寄存器 + 多寄存器（Float）+ 线圈
        Assert.True((await driver.WriteAsync(Tag(10, "40010", TagDataType.UInt16, writable: true), (ushort)777, "DEV-1", 1, CancellationToken.None)).Success);
        Assert.Equal(777, server.GetHolding(9));

        Assert.True((await driver.WriteAsync(Tag(11, "40012", TagDataType.Float, writable: true), 12.5f, "DEV-1", 1, CancellationToken.None)).Success);
        Assert.Equal(12.5f, ReadSimFloat(server, 11), 3);

        Assert.True((await driver.WriteAsync(Tag(12, "00002", TagDataType.Bool, writable: true), true, "DEV-1", 1, CancellationToken.None)).Success);
        Assert.True(server.GetCoil(1));
    }

    [Fact]
    public async Task Rtu_over_tcp_read_write_against_simulator()
    {
        await using var server = new ModbusSimulatorServer(0, SimFraming.RtuOverTcp);
        await server.StartAsync();
        server.SetHolding(0, 4242);

        await using var driver = new ModbusDriver(new ModbusOptions
        {
            Host = "127.0.0.1", Port = server.EffectivePort, Mode = "rtu-over-tcp", TimeoutMs = 2000,
        });

        var connect = await driver.ConnectAsync(CancellationToken.None);
        Assert.True(connect.Success, connect.Error);

        var points = await driver.ReadAsync(Batch(Tag(1, "40001", TagDataType.UInt16)), CancellationToken.None);
        Assert.Equal((ushort)4242, points[0].Value);

        Assert.True((await driver.WriteAsync(Tag(2, "40002", TagDataType.UInt16, writable: true), (ushort)99, "DEV-1", 1, CancellationToken.None)).Success);
        Assert.Equal(99, server.GetHolding(1));
    }

    [Fact]
    public async Task Illegal_address_yields_bad_points_not_connection_failure()
    {
        await using var server = new ModbusSimulatorServer(0, SimFraming.ModbusTcp);
        await server.StartAsync();

        await using var driver = new ModbusDriver(new ModbusOptions
        {
            Host = "127.0.0.1", Port = server.EffectivePort, Mode = "tcp",
        });
        Assert.True((await driver.ConnectAsync(CancellationToken.None)).Success);

        // 模拟器数据区只有 10000：HR 20000 必然非法地址 → 该点 Bad，不抛异常
        var points = await driver.ReadAsync(Batch(
            Tag(1, "HR20000", TagDataType.UInt16),
            Tag(2, "HR0", TagDataType.UInt16)), CancellationToken.None);

        Assert.Equal(2, points.Count);
        var illegal = points.Single(p => p.TagId == 1);
        var normal = points.Single(p => p.TagId == 2);
        Assert.Equal(Quality.Bad, illegal.Quality);
        Assert.Contains("非法数据地址", (string)illegal.Value!);
        Assert.Equal(Quality.Good, normal.Quality);
    }

    [Fact]
    public async Task Connect_failure_returns_fail_not_throw()
    {
        await using var driver = new ModbusDriver(new ModbusOptions { Host = "127.0.0.1", Port = 1, Mode = "tcp" });
        var result = await driver.ConnectAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    private static float ReadSimFloat(ModbusSimulatorServer server, int address)
    {
        var hi = server.GetHolding(address);
        var lo = server.GetHolding(address + 1);
        return BitConverter.Int32BitsToSingle((hi << 16) | lo);
    }
}

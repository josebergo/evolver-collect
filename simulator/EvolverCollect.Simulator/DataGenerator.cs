namespace EvolverCollect.Simulator;

/// <summary>
/// 虚拟数据发生器：向模拟从站写入连续变化的工艺量，让演示/测试界面有“活”数据。
/// HR0-1 主轴负载(Float 正弦) HR2 温度 HR4-7 随机漂移 | C0 运行状态 C1 心跳 | IR0 累计产量
/// </summary>
public sealed class DataGenerator(ModbusSimulatorServer server, int intervalMs = 500) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private double _phase;
    private double _temp = 25.0;
    private uint _counter;

    public void Start()
    {
        _loop = Task.Run(async () =>
        {
            var random = new Random(42);
            while (!_cts.IsCancellationRequested)
            {
                _phase += 0.15;
                var load = (float)(45 + 40 * Math.Sin(_phase));
                server.SetHoldingFloat(0, load);

                _temp = Math.Clamp(_temp + (random.NextDouble() - 0.5) * 0.6, 20, 32);
                server.SetHolding(2, (ushort)(_temp * 10)); // 原始值 250~320，点位上配 aX+b /10 得工程量

                for (var i = 4; i <= 7; i++)
                    server.SetHolding(i, (ushort)(server.GetHolding(i) + random.Next(-20, 21)));

                server.SetCoil(0, Math.Sin(_phase / 8) > -0.2); // 模拟偶发停机
                server.SetCoil(1, true);

                _counter += (uint)random.Next(0, 3);
                // UInt32 大端字序：第一个寄存器是高位字
                server.SetInput(0, (ushort)(_counter >> 16));
                server.SetInput(1, (ushort)(_counter & 0xFFFF));

                try { await Task.Delay(intervalMs, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

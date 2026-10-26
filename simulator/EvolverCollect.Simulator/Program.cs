using EvolverCollect.Simulator;

var port = 1502;
var framing = SimFraming.ModbusTcp;
foreach (var arg in args)
{
    if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)) int.TryParse(arg["--port=".Length..], out port);
    if (arg is "--rtu" or "--framing=rtu") framing = SimFraming.RtuOverTcp;
}

Console.WriteLine($"""
    ╔════════════════════════════════════════════════════════╗
    ║  Evolver Collect 模拟器 (Modbus 从站 + 数据发生器)      ║
    ╠════════════════════════════════════════════════════════╣
    ║  监听: 0.0.0.0:{port}  封装: {framing}                       ║
    ║  默认从站号: 1                                          ║
    ║  演示点位:                                              ║
    ║   HR0-1  主轴负载 Float (0~85)                          ║
    ║   HR2    温度原始值 (×10)                               ║
    ║   HR4-7  随机漂移                                       ║
    ║   C0     运行状态 / C1 心跳                             ║
    ║   IR0-1  累计产量 (UInt32)                              ║
    ║  Ctrl+C 退出                                            ║
    ╚════════════════════════════════════════════════════════╝
    """);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var server = new ModbusSimulatorServer(port, framing) { VerboseLog = args.Contains("--verbose") };
await server.StartAsync(cts.Token);

using var generator = new DataGenerator(server);
generator.Start();
Console.WriteLine("模拟器已启动。配合 evolver-collect 使用：新建通道 → 驱动选 Modbus → IP 127.0.0.1 → 端口 " + port);

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { Console.WriteLine("模拟器已退出。"); }

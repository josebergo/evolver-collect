namespace EvolverCollect.Drivers.Modbus;

using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>Modbus 驱动工厂：声明驱动元数据（含连接参数 JSON Schema，控制台据此自动渲染表单）。</summary>
public sealed class ModbusDriverFactory : IDriverFactory
{
    public const string DriverIdValue = "modbus-tcp";

    public static DriverDescriptor DescriptorValue { get; } = new(
        Id: DriverIdValue,
        DisplayName: "Modbus（TCP / RTU）",
        Vendor: "通用协议",
        ParameterSchemaJson: """
        {
          "type": "object",
          "properties": {
            "host":      { "type": "string",  "title": "IP 地址",       "default": "127.0.0.1" },
            "port":      { "type": "integer", "title": "端口",          "default": 502 },
            "unitId":    { "type": "integer", "title": "默认从站号",     "default": 1 },
            "mode":      { "type": "string",  "title": "模式", "enum": ["tcp", "rtu-over-tcp", "rtu"], "default": "tcp",
                           "description": "tcp=标准 Modbus TCP；rtu-over-tcp=串口服务器透传；rtu=本机串口" },
            "timeoutMs": { "type": "integer", "title": "超时(ms)",       "default": 3000 },
            "littleWordOrder": { "type": "boolean", "title": "字交换序(CDAB)", "default": false,
                           "description": "32 位以上值浮点乱码时勾选" },
            "serialPort":{ "type": "string",  "title": "串口(仅 rtu)",   "default": "COM1" },
            "baudRate":  { "type": "integer", "title": "波特率(仅 rtu)", "default": 9600 },
            "parity":    { "type": "string",  "title": "校验(仅 rtu)",   "enum": ["none", "even", "odd"], "default": "even" },
            "dataBits":  { "type": "integer", "title": "数据位(仅 rtu)", "default": 8 },
            "stopBits":  { "type": "number",  "title": "停止位(仅 rtu)", "enum": [1, 1.5, 2], "default": 1 }
          }
        }
        """,
        Capabilities: DriverCapabilities.Read | DriverCapabilities.Write,
        AddressHelp: "40001=保持寄存器(1基) / 30001=输入寄存器 / 00001=线圈 / 10001=离散输入；前缀式(0基)：HR0 / IR0 / C0 / DI0。Int32/Float 占 2 个寄存器，Bool 配 BitIndex 拆位。",
        DocsSlug: "protocol-tutorials/modbus/01-overview");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        ModbusOptions options;
        try
        {
            options = JsonSerializer.Deserialize<ModbusOptions>(
                string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ModbusOptions();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Modbus 连接参数不合法: {ex.Message}", ex);
        }
        return new ModbusDriver(options);
    }
}

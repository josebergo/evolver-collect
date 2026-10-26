namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Drivers.Modbus;

/// <summary>协议帧向量测试：用标准手册样例帧验证编解码（工业软件最硬的测试手段）。</summary>
public class ModbusCodecTests
{
    [Fact]
    public void Crc16_known_vector()
    {
        // 广泛使用的标准样例：01 03 00 00 00 02 → CRC = 0x0BC4（低字节在前 C4 0B）
        var crc = ModbusCrc16.Compute([0x01, 0x03, 0x00, 0x00, 0x00, 0x02]);
        Assert.Equal(0x0BC4, crc);
    }

    [Fact]
    public void Build_tcp_read_request_matches_known_bytes()
    {
        var frame = ModbusCodec.BuildTcpRead(0x0001, 0x01, ModbusArea.HoldingRegister, 0, 2);
        Assert.Equal(
            "000100000006010300000002",
            Convert.ToHexString(frame));
    }

    [Fact]
    public void Parse_tcp_read_response_extracts_register_data()
    {
        // 事务号 0001 的读响应：2 个寄存器 = 0x000A, 0x0102
        var frame = Convert.FromHexString("000100000007010304000A0102");
        var data = ModbusCodec.ParseTcpReadResponse(frame, 0x0001, 0x01);
        Assert.Equal("000A0102", Convert.ToHexString(data.Span));

        Assert.Equal((ushort)10, ModbusValues.DecodeRegister(data.ToArray(), 0));
        Assert.Equal((ushort)0x0102, ModbusValues.DecodeRegister(data.ToArray(), 1));
    }

    [Fact]
    public void Tcp_exception_response_translated_to_modbus_exception()
    {
        // FC03|0x80 + 02（非法数据地址）
        var frame = Convert.FromHexString("000200000003018302");
        var ex = Assert.Throws<ModbusException>(
            () => ModbusCodec.ParseTcpReadResponse(frame, 0x0002, 0x01));
        Assert.Equal(2, ex.ExceptionCode);
        Assert.Contains("非法数据地址", ex.Message);
    }

    [Fact]
    public void Rtu_read_request_appends_crc_low_byte_first()
    {
        var frame = ModbusCodec.BuildRtuRead(0x01, ModbusArea.HoldingRegister, 0, 2);
        Assert.Equal("010300000002C40B", Convert.ToHexString(frame));
    }

    [Fact]
    public void Parse_rtu_response_validates_crc_and_extracts_data()
    {
        // 从站 1 读 2 个保持寄存器：值 0x0A, 0x0102（CRC 由编码器生成保证正确）
        var response = ModbusCrc16.Append([0x01, 0x03, 0x04, 0x00, 0x0A, 0x01, 0x02]);
        var data = ModbusCodec.ParseRtuReadResponse(response, 0x01);
        Assert.Equal("000A0102", Convert.ToHexString(data));
    }

    [Fact]
    public void Rtu_corrupted_crc_is_rejected()
    {
        var response = ModbusCrc16.Append([0x01, 0x03, 0x04, 0x00, 0x0A, 0x01, 0x02]);
        response[^1] ^= 0xFF; // 破坏 CRC
        Assert.Throws<IOException>(() => ModbusCodec.ParseRtuReadResponse(response, 0x01));
    }

    [Fact]
    public void Write_single_register_roundtrip()
    {
        var request = ModbusCodec.BuildTcpWriteSingleRegister(0x0003, 0x01, 100, 0x1B58);
        Assert.Equal("000300000006010600641B58", Convert.ToHexString(request));

        var echo = Convert.FromHexString("000300000006010600641B58");
        ModbusCodec.ParseTcpWriteResponse(echo, 0x0003, 0x01, ModbusCodec.WriteSingleRegister, 100);
    }
}

namespace EvolverCollect.Drivers.Modbus;

/// <summary>
/// Modbus 帧构造与解析（TCP 的 MBAP 封装 + RTU 的 CRC 封装共用同一套 PDU 逻辑）。
/// 功能码：01 读线圈 02 读离散输入 03 读保持寄存器 04 读输入寄存器
///         05 写单线圈 06 写单寄存器 16(0x10) 写多寄存器
/// </summary>
public static class ModbusCodec
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleRegisters = 0x10;

    public static byte ReadFunctionCode(ModbusArea area) => area switch
    {
        ModbusArea.Coil => ReadCoils,
        ModbusArea.DiscreteInput => ReadDiscreteInputs,
        ModbusArea.HoldingRegister => ReadHoldingRegisters,
        ModbusArea.InputRegister => ReadInputRegisters,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    // ---------------- TCP（MBAP 头：事务号2 + 协议号2 + 长度2 + 单元号1） ----------------

    public static byte[] BuildTcpRead(ushort transactionId, byte unitId, ModbusArea area, ushort start, ushort count)
    {
        var pdu = BuildReadPdu(area, start, count);
        return WrapTcp(transactionId, unitId, pdu);
    }

    public static byte[] BuildTcpWriteSingleCoil(ushort transactionId, byte unitId, ushort address, bool value)
        => WrapTcp(transactionId, unitId, [WriteSingleCoil, Hb(address), Lb(address), (byte)(value ? 0xFF : 0x00), 0x00]);

    public static byte[] BuildTcpWriteSingleRegister(ushort transactionId, byte unitId, ushort address, ushort value)
        => WrapTcp(transactionId, unitId, [WriteSingleRegister, Hb(address), Lb(address), Hb(value), Lb(value)]);

    public static byte[] BuildTcpWriteMultipleRegisters(ushort transactionId, byte unitId, ushort address, ushort[] values)
    {
        var pdu = new byte[6 + values.Length * 2 + 1];
        pdu[0] = WriteMultipleRegisters;
        pdu[1] = Hb(address); pdu[2] = Lb(address);
        pdu[3] = Hb((ushort)values.Length); pdu[4] = Lb((ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
        {
            pdu[6 + i * 2] = Hb(values[i]);
            pdu[7 + i * 2] = Lb(values[i]);
        }
        return WrapTcp(transactionId, unitId, pdu);
    }

    /// <summary>校验并解析 TCP 读响应，返回数据体（字节数组，线圈打包位 / 寄存器大端双字节）。</summary>
    public static Memory<byte> ParseTcpReadResponse(ReadOnlySpan<byte> frame, ushort expectedTransactionId, byte expectedUnitId)
    {
        if (frame.Length < 9) throw new IOException($"Modbus TCP 响应帧不完整（{frame.Length} 字节）");
        var tid = (ushort)((frame[0] << 8) | frame[1]);
        var pid = (ushort)((frame[2] << 8) | frame[3]);
        var len = (ushort)((frame[4] << 8) | frame[5]);
        var unit = frame[6];
        if (pid != 0) throw new IOException($"Modbus TCP 响应协议号异常: {pid}");
        if (tid != expectedTransactionId) throw new IOException($"Modbus TCP 事务号不匹配：期望 {expectedTransactionId} 实际 {tid}");
        if (unit != expectedUnitId) throw new IOException($"Modbus TCP 单元号不匹配：期望 {expectedUnitId} 实际 {unit}");
        if (frame.Length < 6 + len) throw new IOException("Modbus TCP 响应帧长度字段与实际不符");

        var fc = frame[7];
        if ((fc & 0x80) != 0)
        {
            if (frame.Length < 9) throw new IOException("Modbus 异常响应帧不完整");
            throw ModbusException.Create(fc, frame[8]);
        }
        var byteCount = frame[8];
        if (len < byteCount + 3 || frame.Length < 9 + byteCount)
            throw new IOException("Modbus TCP 响应数据长度与字节数字段不符");
        return frame.Slice(9, byteCount).ToArray();
    }

    // ---------------- RTU（地址1 + PDU + CRC2） ----------------

    public static byte[] BuildRtuRead(byte unitId, ModbusArea area, ushort start, ushort count)
        => ModbusCrc16.Append([unitId, .. BuildReadPdu(area, start, count)]);

    public static byte[] BuildRtuWriteSingleCoil(byte unitId, ushort address, bool value)
        => ModbusCrc16.Append([unitId, WriteSingleCoil, Hb(address), Lb(address), (byte)(value ? 0xFF : 0x00), 0x00]);

    public static byte[] BuildRtuWriteSingleRegister(byte unitId, ushort address, ushort value)
        => ModbusCrc16.Append([unitId, WriteSingleRegister, Hb(address), Lb(address), Hb(value), Lb(value)]);

    public static byte[] BuildRtuWriteMultipleRegisters(byte unitId, ushort address, ushort[] values)
    {
        var frame = new List<byte>(8 + values.Length * 2) { unitId, WriteMultipleRegisters, Hb(address), Lb(address),
            Hb((ushort)values.Length), Lb((ushort)values.Length), (byte)(values.Length * 2) };
        foreach (var v in values) { frame.Add(Hb(v)); frame.Add(Lb(v)); }
        return ModbusCrc16.Append([.. frame]);
    }

    /// <summary>校验 CRC 并解析 RTU 读响应。</summary>
    public static byte[] ParseRtuReadResponse(ReadOnlySpan<byte> frame, byte expectedUnitId)
    {
        if (frame.Length < 5) throw new IOException($"Modbus RTU 响应帧不完整（{frame.Length} 字节）");
        if (!ModbusCrc16.Validate(frame)) throw new IOException("Modbus RTU 响应 CRC 校验失败（检查串口参数/干扰）");
        if (frame[0] != expectedUnitId) throw new IOException($"RTU 响应站号不匹配：期望 {expectedUnitId} 实际 {frame[0]}");
        var fc = frame[1];
        if ((fc & 0x80) != 0)
            throw ModbusException.Create(fc, frame[2]);
        var byteCount = frame[2];
        if (frame.Length < 3 + byteCount + 2) throw new IOException("RTU 响应长度与字节数字段不符");
        return frame.Slice(3, byteCount).ToArray();
    }

    // ---------------- 写响应校验 ----------------

    /// <summary>校验 TCP 写回显帧（FC05/06/16）。异常时抛 ModbusException / IOException。</summary>
    public static void ParseTcpWriteResponse(ReadOnlySpan<byte> frame, ushort expectedTransactionId, byte expectedUnitId, byte fc, ushort address)
    {
        if (frame.Length < 12) throw new IOException($"Modbus TCP 写响应帧不完整（{frame.Length} 字节）");
        var tid = (ushort)((frame[0] << 8) | frame[1]);
        if (tid != expectedTransactionId) throw new IOException("Modbus TCP 写响应事务号不匹配");
        if (frame[6] != expectedUnitId) throw new IOException("Modbus TCP 写响应单元号不匹配");
        var rfc = frame[7];
        if ((rfc & 0x80) != 0) throw ModbusException.Create(rfc, frame[8]);
        if (rfc != fc) throw new IOException($"写响应功能码不匹配：期望 {fc:X2} 实际 {rfc:X2}");
        var echoAddr = (ushort)((frame[8] << 8) | frame[9]);
        if (echoAddr != address) throw new IOException("写响应地址回显不匹配");
    }

    /// <summary>校验 RTU 写回显帧（8 字节）。</summary>
    public static void ParseRtuWriteResponse(ReadOnlySpan<byte> frame, byte expectedUnitId, byte fc, ushort address)
    {
        if (frame.Length is < 5 or > 8) throw new IOException($"Modbus RTU 写响应帧长度异常（{frame.Length}）");
        if (!ModbusCrc16.Validate(frame)) throw new IOException("RTU 写响应 CRC 校验失败");
        if (frame[0] != expectedUnitId) throw new IOException("RTU 写响应站号不匹配");
        var rfc = frame[1];
        if ((rfc & 0x80) != 0) throw ModbusException.Create(rfc, frame[2]);
        if (rfc != fc) throw new IOException("写响应功能码不匹配");
        var echoAddr = (ushort)((frame[2] << 8) | frame[3]);
        if (echoAddr != address) throw new IOException("写响应地址回显不匹配");
    }

    // ---------------- 共用 PDU ----------------

    private static byte[] BuildReadPdu(ModbusArea area, ushort start, ushort count)
    {
        var fc = ReadFunctionCode(area);
        return [fc, Hb(start), Lb(start), Hb(count), Lb(count)];
    }

    private static byte[] WrapTcp(ushort transactionId, byte unitId, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        frame[0] = Hb(transactionId); frame[1] = Lb(transactionId);
        frame[2] = 0; frame[3] = 0;                       // 协议号 = 0（Modbus）
        frame[4] = Hb((ushort)(pdu.Length + 1)); frame[5] = Lb((ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static byte Hb(ushort v) => (byte)(v >> 8);
    private static byte Lb(ushort v) => (byte)(v & 0xFF);
}

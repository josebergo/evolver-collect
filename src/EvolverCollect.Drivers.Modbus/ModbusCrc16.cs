namespace EvolverCollect.Drivers.Modbus;

/// <summary>
/// CRC-16/Modbus：多项式 0xA001（反转的 0x8005）、初值 0xFFFF、低字节在前。
/// 报文单元测试中用官方手册样例帧作为向量验证。
/// </summary>
public static class ModbusCrc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>校验帧尾 CRC（RTU 模式）。返回 false 表示校验失败（干扰/断帧）。</summary>
    public static bool Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        var body = frame[..^2];
        var expected = Compute(body);
        return frame[^2] == (byte)(expected & 0xFF) && frame[^1] == (byte)(expected >> 8);
    }

    /// <summary>把 CRC 追加到帧尾（低字节在前）。</summary>
    public static byte[] Append(byte[] frame)
    {
        var crc = Compute(frame);
        Array.Resize(ref frame, frame.Length + 2);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }
}

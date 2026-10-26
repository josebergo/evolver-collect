namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Drivers.Modbus;

public class ModbusValuesTests
{
    [Fact]
    public void Decode_int32_big_word_order()
    {
        // 寄存器 [0x1234, 0x5678] 标准大端 → 0x12345678
        byte[] data = [0x12, 0x34, 0x56, 0x78];
        Assert.Equal(0x12345678u, ModbusValues.Decode(TagDataType.UInt32, data, 0, littleWordOrder: false));
    }

    [Fact]
    public void Decode_int32_little_word_order_cdab()
    {
        // 字交换序（CDAB）：寄存器 [0x5678, 0x1234] → 0x12345678
        byte[] data = [0x56, 0x78, 0x12, 0x34];
        Assert.Equal(0x12345678u, ModbusValues.Decode(TagDataType.UInt32, data, 0, littleWordOrder: true));
    }

    [Fact]
    public void Decode_float_10_25()
    {
        // 10.25f = 0x41240000
        byte[] data = [0x41, 0x24, 0x00, 0x00];
        var value = ModbusValues.Decode(TagDataType.Float, data, 0, littleWordOrder: false);
        Assert.Equal(10.25f, value);
    }

    [Fact]
    public void Decode_double_roundtrip()
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.PI);
        byte[] data =
        [
            (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
            (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits,
        ];
        Assert.Equal(Math.PI, ModbusValues.Decode(TagDataType.Double, data, 0, littleWordOrder: false));
    }

    [Fact]
    public void Decode_bits_packs_lsb_first()
    {
        byte[] data = [0b0101]; // 位 0 和位 2 为 true
        Assert.Equal(true, ModbusValues.DecodeBits(data, 0));
        Assert.Equal(false, ModbusValues.DecodeBits(data, 1));
        Assert.Equal(true, ModbusValues.DecodeBits(data, 2));
    }

    [Theory]
    [InlineData(TagDataType.Bool, 1)]
    [InlineData(TagDataType.Int16, 1)]
    [InlineData(TagDataType.UInt32, 2)]
    [InlineData(TagDataType.Float, 2)]
    [InlineData(TagDataType.Int64, 4)]
    [InlineData(TagDataType.Double, 4)]
    public void Value_count_per_type(TagDataType type, int expected) =>
        Assert.Equal(expected, ModbusValues.ValueCount(type));

    [Fact]
    public void Encode_decode_roundtrip_both_word_orders()
    {
        foreach (var little in new[] { false, true })
        {
            foreach (var value in new object[] { (short)-123, 66u, -4567, 78.5f, 3.14159 })
            {
                var type = value switch
                {
                    short => TagDataType.Int16,
                    uint => TagDataType.UInt32,
                    int => TagDataType.Int32,
                    float => TagDataType.Float,
                    double => TagDataType.Double,
                    _ => throw new InvalidOperationException(),
                };
                var regs = ModbusValues.Encode(value, little);
                var data = new byte[regs.Length * 2];
                for (var i = 0; i < regs.Length; i++)
                {
                    data[i * 2] = (byte)(regs[i] >> 8);
                    data[i * 2 + 1] = (byte)regs[i];
                }
                var decoded = ModbusValues.Decode(type, data, 0, little);
                Assert.Equal(Convert.ToDouble(value), Convert.ToDouble(decoded), 5);
            }
        }
    }
}

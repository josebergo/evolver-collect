namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Drivers.Modbus;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("40001", ModbusArea.HoldingRegister, 0)]   // 经典：保持寄存器 1 基
    [InlineData("40100", ModbusArea.HoldingRegister, 99)]
    [InlineData("30001", ModbusArea.InputRegister, 0)]
    [InlineData("30010", ModbusArea.InputRegister, 9)]
    [InlineData("00001", ModbusArea.Coil, 0)]
    [InlineData("00123", ModbusArea.Coil, 122)]
    [InlineData("10001", ModbusArea.DiscreteInput, 0)]
    [InlineData("HR5", ModbusArea.HoldingRegister, 5)]     // 前缀：0 基
    [InlineData("hr10", ModbusArea.HoldingRegister, 10)]
    [InlineData("IR200", ModbusArea.InputRegister, 200)]
    [InlineData("C7", ModbusArea.Coil, 7)]
    [InlineData("DI9", ModbusArea.DiscreteInput, 9)]
    public void Parses_both_classic_and_prefix_forms(string input, ModbusArea area, ushort offset)
    {
        var addr = ModbusAddress.Parse(input);
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("50001")]   // 首位必须是 0/1/3/4
    [InlineData("40")]      // 位数不足
    [InlineData("4000")]    // 位数不足
    [InlineData("HR")]
    [InlineData("HR-3")]
    public void Invalid_address_throws_format_exception(string input) =>
        Assert.Throws<FormatException>(() => ModbusAddress.Parse(input));
}

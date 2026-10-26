namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Drivers.Fins;
using EvolverCollect.Drivers.Mc;

/// <summary>MC/FINS 自研协议栈帧测试：已知向量 + 往返一致。</summary>
public class McFinsFrameTests
{
    // ---------------- MC ----------------

    [Fact]
    public void Mc_word_read_request_known_bytes()
    {
        // 读 D100 起 2 个字：副头 + 长度 + 定时器 + 0104/0001 + 100(LE 3B) + A8 + 2
        var frame = McFrame.BuildWordRead((byte)McDevice.D, 100, 2, 1);
        Assert.Equal(
            "500000FFFF03000C0010000104000164 0000A80200".Replace(" ", ""),
            Convert.ToHexString(frame));
    }

    [Fact]
    public void Mc_word_read_response_parses_big_endian_words()
    {
        var response = Convert.FromHexString("D00000FFFF0300" + "0600" + "0000" + "006400C8");
        var words = McFrame.ParseWordReadResponse(response, 2);
        Assert.Equal(new ushort[] { 100, 200 }, words);
    }

    [Fact]
    public void Mc_end_code_translated()
    {
        // 结束码 C0006：点数超限
        var response = Convert.FromHexString("D00000FFFF0300" + "0400" + "C006");
        var ex = Assert.Throws<McException>(() => McFrame.ParseWriteResponse(response));
        Assert.Equal(0xC006, ex.EndCode);
        Assert.Contains("点数", ex.Message);
    }

    [Fact]
    public void Mc_bit_read_response_flags()
    {
        // 每位 1 字节：0x10=ON
        var response = Convert.FromHexString("D00000FFFF0300" + "0500" + "0000" + "100001");
        var bits = McFrame.ParseBitReadResponse(response, 3);
        Assert.Equal([true, false, true], bits);
    }

    [Theory]
    [InlineData("D100", McDevice.D, 100, false)]
    [InlineData("d5", McDevice.D, 5, false)]
    [InlineData("W200", McDevice.W, 200, false)]
    [InlineData("M10", McDevice.M, 10, true)]
    [InlineData("X1A", McDevice.X, 0x1A, true)]
    [InlineData("Y20", McDevice.Y, 0x20, true)]
    public void Mc_address_parses(string input, McDevice device, int offset, bool isBit)
    {
        var addr = McAddress.Parse(input);
        Assert.Equal(device, addr.Device);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(isBit, addr.IsBit);
    }

    [Fact]
    public void Mc_values_roundtrip()
    {
        var words = McValues.EncodeWords(TagDataType.Float, 12.5f);
        Assert.Equal(new ushort[] { 0x4148, 0x0000 }, words);
        var decoded = McValues.DecodeWords(TagDataType.Float, words, 0);
        Assert.IsType<float>(decoded);
        Assert.Equal(12.5f, (float)decoded);
    }

    // ---------------- FINS ----------------

    [Fact]
    public void Fins_memory_read_frame_known_bytes()
    {
        // DM0 读 1 字：ICF80 RSV00 GCT02 DNA00 DA1=05 DA2=00 SNA00 SA1=FE SA2=00 SID=01 + 0101 82 0000 0001
        var frame = FinsFrame.BuildMemoryRead(FinsArea.DmWord, 0, 1, serverNode: 5, clientNode: 0xFE, sid: 1);
        Assert.Equal(
            "800002000500 00FE00 01" .Replace(" ", "") + "0101" + "82" + "0000" + "0001",
            Convert.ToHexString(frame));
    }

    [Fact]
    public void Fins_read_response_parses_data()
    {
        var body = Convert.FromHexString("80000200050000FE0001" + "0000" + "0064");
        var data = FinsFrame.ParseMemoryReadResponse(body, 2);
        Assert.Equal("0064", Convert.ToHexString(data));
    }

    [Fact]
    public void Fins_end_code_translated()
    {
        var body = Convert.FromHexString("80000200050000FE0001" + "0104");
        var ex = Assert.Throws<FinsException>(() => FinsFrame.ParseWriteResponse(body));
        Assert.Equal(0x0104, ex.EndCode);
        Assert.Contains("地址范围", ex.Message);
    }

    [Theory]
    [InlineData("D100", FinsArea.DmWord)]
    [InlineData("D100.2", FinsArea.DmBit)]
    [InlineData("CIO50", FinsArea.CioWord)]
    [InlineData("W10.15", FinsArea.WorkBit)]
    [InlineData("H5", FinsArea.HoldingWord)]
    public void Fins_address_parses(string input, FinsArea area)
    {
        var addr = FinsAddress.Parse(input);
        Assert.Equal(area, addr.Area);
    }

    [Fact]
    public void Fins_bit_address_is_word_times_16_plus_bit()
    {
        var addr = FinsAddress.Parse("D100.3");
        Assert.Equal(100, addr.Offset);
        Assert.Equal(100 * 16 + 3, addr.BitAddress);
    }
}

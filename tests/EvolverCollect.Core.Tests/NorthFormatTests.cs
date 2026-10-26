namespace EvolverCollect.Core.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Pipeline;

public class NorthFormatTests
{
    private static readonly DataPoint P1 = new(1, 1, 1, "CNC-01", "temp", 25.3, Quality.Good, 1798300800123);
    private static readonly DataPoint P2 = new(1, 1, 2, "CNC-01", "run", true, Quality.Good, 1798300800150);

    [Fact]
    public void Json_lines_format()
    {
        var lines = NorthFormat.FormatBatch([P1, P2], "json-lines").Split((char)10);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"device\":\"CNC-01\"", lines[0]);
        Assert.Contains("\"tag\":\"temp\"", lines[0]);
        Assert.Contains("\"quality\":\"Good\"", lines[0]);
        Assert.Contains("\"timestamp\":1798300800123", lines[0]);
    }

    [Fact]
    public void Json_array_format()
    {
        var json = NorthFormat.FormatBatch([P1, P2], "json-array");
        Assert.StartsWith("[", json);
        Assert.EndsWith("]", json);
        Assert.Contains("\"device\"", json);   // 对外字段约定 device/tag/value/quality/timestamp
        Assert.Contains("\"tag\"", json);
    }

    [Fact]
    public void Csv_format_quotes_strings()
    {
        var csv = NorthFormat.FormatBatch([P1 with { Value = "a,b" }], "csv");
        // CSV 行以 '\n'(char 10) 结尾
        var expected = "CNC-01,temp,\"a,b\",Good,1798300800123" + new string((char)10, 1);
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void TagFilter_include_and_exclude()
    {
        var onlyCnc1 = new TagFilter("CNC-01.*", null);
        Assert.True(onlyCnc1.Matches(P1));
        Assert.False(onlyCnc1.Matches(P1 with { DeviceName = "CNC-02" }));

        var noDebug = new TagFilter(null, "*.debug*");
        Assert.True(noDebug.Matches(P1));
        Assert.False(noDebug.Matches(P1 with { TagName = "debug.counter" }));

        var combined = new TagFilter("CNC-01.*;PLC-0?.*", "CNC-01.internal.*");
        Assert.True(combined.Matches(P1));
        Assert.True(combined.Matches(P1 with { DeviceName = "PLC-01" }));
        Assert.False(combined.Matches(P1 with { TagName = "internal.x" }));
    }
}

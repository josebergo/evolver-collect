namespace EvolverCollect.Core.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Engine;

public class TagEngineTests
{
    private static TagDefinition Tag(long id, TagDataType type = TagDataType.Float,
        double a = 1.0, double b = 0.0, double? deadband = null, int? bit = null) =>
        new(id, 1, $"tag{id}", "40001", type, false, null, a, b, deadband, bit, null);

    private static DataPoint Point(TagDefinition tag, object? value, Quality q = Quality.Good) =>
        new(1, 1, tag.Id, "dev", tag.Name, value, q, 1000);

    [Fact]
    public void First_value_always_emitted()
    {
        var engine = new TagEngine();
        var tag = Tag(1, TagDataType.Int16);
        var out1 = engine.Process(new DeviceBatch(1, 1, "dev", 1, [tag]), [Point(tag, 5)]);
        var out2 = engine.Process(new DeviceBatch(1, 1, "dev", 1, [tag]), [Point(tag, 5)]);

        Assert.Single(out1);
        Assert.Empty(out2); // 未变化不重复发布
    }

    [Fact]
    public void Scale_converts_raw_to_engineering_value()
    {
        var engine = new TagEngine();
        // 温度原始值 ×0.1（寄存器 253 → 25.3°C）
        var tag = Tag(1, TagDataType.UInt16, a: 0.1);
        var result = engine.Process(new DeviceBatch(1, 1, "dev", 1, [tag]), [Point(tag, 253)]);
        Assert.Equal(25.3, (double)result[0].Value!, 3);
    }

    [Fact]
    public void Bit_index_extracts_switch_from_register()
    {
        var engine = new TagEngine();
        var tag = Tag(1, TagDataType.UInt16, bit: 3); // 0b1000 = 第 3 位
        var result = engine.Process(new DeviceBatch(1, 1, "dev", 1, [tag]), [Point(tag, 8)]);
        Assert.Equal(true, result[0].Value);
    }

    [Fact]
    public void Deadband_filters_small_changes()
    {
        var engine = new TagEngine();
        var tag = Tag(1, TagDataType.Float, deadband: 2.0);
        var batch = new DeviceBatch(1, 1, "dev", 1, [tag]);

        Assert.Single(engine.Process(batch, [Point(tag, 10.0)])); // 首值
        Assert.Empty(engine.Process(batch, [Point(tag, 11.5)]));  // 变化 1.5 < 死区 2
        var emitted = engine.Process(batch, [Point(tag, 12.5)]);  // 变化 2.5 > 死区
        Assert.Single(emitted);
        Assert.Equal(12.5, emitted[0].Value);
    }

    [Fact]
    public void Quality_change_bad_then_good_re_emits()
    {
        var engine = new TagEngine();
        var tag = Tag(1, TagDataType.Int16);
        var batch = new DeviceBatch(1, 1, "dev", 1, [tag]);

        Assert.Single(engine.Process(batch, [Point(tag, 7)]));
        Assert.Single(engine.Process(batch, [Point(tag, "断链", Quality.Bad)])); // 质量变化必发
        Assert.Empty(engine.Process(batch, [Point(tag, "仍断链", Quality.Bad)])); // 持续 Bad 不重复
        var recovered = engine.Process(batch, [Point(tag, 7)]);                    // 恢复后即使值相同也发布
        Assert.Single(recovered);
        Assert.Equal(7, recovered[0].Value);
    }

    [Fact]
    public void Bool_emits_on_change_only()
    {
        var engine = new TagEngine();
        var tag = Tag(1, TagDataType.Bool);
        var batch = new DeviceBatch(1, 1, "dev", 1, [tag]);

        Assert.Single(engine.Process(batch, [Point(tag, false)]));
        Assert.Empty(engine.Process(batch, [Point(tag, false)]));
        Assert.Single(engine.Process(batch, [Point(tag, true)]));
    }
}

namespace EvolverCollect.Drivers.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Drivers.Modbus;

public class ReadPlannerTests
{
    private static TagDefinition Tag(long id, string address, TagDataType type = TagDataType.UInt16) =>
        new(id, 1, $"t{id}", address, type, false, null, 1, 0, null, null, null);

    private static (TagDefinition Tag, ModbusAddress Addr) Item(long id, string address, TagDataType type = TagDataType.UInt16)
        => (Tag(id, address, type), ModbusAddress.Parse(address));

    [Fact]
    public void Contiguous_registers_merge_into_single_read()
    {
        var plans = ReadPlanner.Plan([Item(1, "40001"), Item(2, "40002"), Item(3, "40003")]);
        var plan = Assert.Single(plans);
        Assert.Equal(ModbusArea.HoldingRegister, plan.Area);
        Assert.Equal(0, plan.Start);
        Assert.Equal(3, plan.Count);
        Assert.Equal(3, plan.Items.Count);
    }

    [Fact]
    public void Small_gap_within_tolerance_still_merges()
    {
        // HR0 与 HR5 间隔 5 ≤ 8 → 合并读 0~6
        var plans = ReadPlanner.Plan([Item(1, "HR0"), Item(2, "HR5")]);
        var plan = Assert.Single(plans);
        Assert.Equal(6, plan.Count);
    }

    [Fact]
    public void Large_gap_splits()
    {
        var plans = ReadPlanner.Plan([Item(1, "HR0"), Item(2, "HR50")]);
        Assert.Equal(2, plans.Count);
    }

    [Fact]
    public void Different_areas_never_merge()
    {
        var plans = ReadPlanner.Plan([Item(1, "40001"), Item(2, "30001"), Item(3, "00001")]);
        Assert.Equal(3, plans.Count);
        var expected = new[] { ModbusArea.HoldingRegister, ModbusArea.InputRegister, ModbusArea.Coil }.OrderBy(a => a);
        Assert.Equal(expected, plans.Select(p => p.Area).OrderBy(a => a));
    }

    [Fact]
    public void Wide_values_span_multiple_registers_in_plan()
    {
        // Float 占 2 个寄存器：HR0(Float) + HR1(UInt16) 应合并为一次读 2 个寄存器
        var plans = ReadPlanner.Plan([Item(1, "HR0", TagDataType.Float), Item(2, "HR1")]);
        var plan = Assert.Single(plans);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void Read_count_capped_at_125_registers()
    {
        // 300 个连续 UInt16 点位 → 至少 3 次读取（125/125/50）
        var items = Enumerable.Range(0, 300).Select(i => Item(i + 1, $"HR{i}")).ToList();
        var plans = ReadPlanner.Plan(items);
        Assert.True(plans.Count >= 3, $"期望拆分为至少 3 次读取，实际 {plans.Count}");
        Assert.All(plans, p => Assert.True(p.Count <= 125));
        Assert.Equal(300, plans.Sum(p => p.Items.Count)); // 点位不丢
    }
}

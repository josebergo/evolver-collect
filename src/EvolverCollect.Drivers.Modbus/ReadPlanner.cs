namespace EvolverCollect.Drivers.Modbus;

using EvolverCollect.Core.Abstractions;

/// <summary>读取计划：一次功能码读取覆盖的连续区间与落在其中的点位。</summary>
internal sealed record ReadPlan(ModbusArea Area, ushort Start, ushort Count, List<(TagDefinition Tag, ModbusAddress Addr, int Index)> Items);

/// <summary>
/// 合并读规划：同数据区、地址相近的点位合并成尽量少的读取指令（上限 125 寄存器/2000 位）。
/// 这是千级点位场景读次数数量级下降的关键。
/// </summary>
internal static class ReadPlanner
{
    /// <summary>相邻区间间隔不超过该值仍合并（读多余寄存器的代价远小于多一次往返）。</summary>
    private const int GapTolerance = 8;

    public static List<ReadPlan> Plan(IEnumerable<(TagDefinition Tag, ModbusAddress Addr)> items)
    {
        var plans = new List<ReadPlan>();
        foreach (var group in items.GroupBy(x => x.Addr.Area))
        {
            var ordered = group.OrderBy(x => x.Addr.Offset).ToList();
            var maxPerRead = group.Key is ModbusArea.HoldingRegister or ModbusArea.InputRegister
                ? ModbusValues.MaxReadRegisters : ModbusValues.MaxReadCoils;

            ReadPlan? current = null;
            foreach (var (tag, addr) in ordered)
            {
                var length = addr.IsRegister ? ModbusValues.ValueCount(tag.DataType) : 1;
                var need = (ushort)(addr.Offset + length);

                if (current is not null)
                {
                    // 排序保证 addr.Offset >= current.Start：负间隔 = 点位跨计划重叠（如 Float 跨 2 寄存器），同样可合并
                    var gap = addr.Offset - (current.Start + current.Count);
                    var canMerge = gap <= GapTolerance && need - current.Start <= maxPerRead;
                    if (canMerge)
                    {
                        if (need > current.Start + current.Count) current = current with { Count = (ushort)(need - current.Start) };
                        current.Items.Add((tag, addr, current.Items.Count));
                        continue;
                    }
                    plans.Add(current);
                }
                current = new ReadPlan(group.Key, addr.Offset, (ushort)length, [(tag, addr, 0)]);
            }
            if (current is not null) plans.Add(current);
        }
        return plans;
    }
}

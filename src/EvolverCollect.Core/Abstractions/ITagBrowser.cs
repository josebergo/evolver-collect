namespace EvolverCollect.Core.Abstractions;

/// <summary>地址空间中的一个节点（用于 OPC UA 等可浏览协议的树形展示与导入）。</summary>
public sealed record TagNode(
    string Id,
    string Name,
    string? DataType,
    bool Writable,
    bool HasChildren,
    string? Address = null);

/// <summary>地址空间浏览器：懒加载遍历设备的可采集点位。</summary>
public interface ITagBrowser
{
    Task<IReadOnlyList<TagNode>> BrowseAsync(string? parentId, CancellationToken ct = default);
}

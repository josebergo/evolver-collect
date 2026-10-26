namespace EvolverCollect.Plugins.Sdk;

/// <summary>
/// 外部插件清单：进程外驱动以 evolver-plugin.json 声明自身，宿主启动时发现并加载。
/// 进程内插件（推荐，最简单）：引用 EvolverCollect.Core，实现 IDriverFactory，
/// 在宿主 Program.cs 注册一行即可——参见文档《如何编写一个新驱动》。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>插件名（如 my-protocol-driver）。</summary>
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Vendor { get; set; } = "";
    /// <summary>插件类型：driver | northbound。</summary>
    public string Kind { get; set; } = "driver";
    /// <summary>进程外插件的可执行文件名（相对清单目录）。</summary>
    public string? Executable { get; set; }
    /// <summary>进程外协议：stdio-json（与 FOCAS 适配器同构，见 ADR-0004/0005）。</summary>
    public string Protocol { get; set; } = "stdio-json";
    /// <summary>驱动元数据（与 DriverDescriptor 对齐；进程外插件由宿主渲染表单）。</summary>
    public DriverManifestInfo Driver { get; set; } = new();
}

public sealed class DriverManifestInfo
{
    public string DriverId { get; set; } = "";
    public string ParameterSchemaJson { get; set; } = "{}";
    public string AddressHelp { get; set; } = "";
    public bool SupportsRead { get; set; } = true;
    public bool SupportsWrite { get; set; }
    public bool SupportsBrowse { get; set; }
}

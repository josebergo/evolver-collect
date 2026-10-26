namespace EvolverCollect.Storage;

using System.ComponentModel.DataAnnotations;

/// <summary>南向通道（一个物理/逻辑连接）。</summary>
public class ChannelEntity
{
    [Key]
    public long Id { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    /// <summary>驱动类型标识，如 modbus-tcp / opcua / s7 / mc / fins。</summary>
    [Required, MaxLength(50)]
    public string DriverId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>连接参数（JSON，结构由驱动的 ParameterSchemaJson 定义）。</summary>
    [Required]
    public string ConnectionJson { get; set; } = "{}";
    public int DefaultPollIntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 3000;
    public int RetryCount { get; set; } = 2;
    [MaxLength(500)]
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<DeviceEntity> Devices { get; set; } = [];
}

/// <summary>逻辑设备（从属于通道，对应一台物理设备/一个从站）。</summary>
public class DeviceEntity
{
    [Key]
    public long Id { get; set; }
    public long ChannelId { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    /// <summary>从站号/节点地址（Modbus unit id、FINS 节点号等；驱动自行使用）。</summary>
    public int? StationNo { get; set; }
    public long? TemplateId { get; set; }
    public bool Enabled { get; set; } = true;
    [MaxLength(500)]
    public string? Description { get; set; }

    public ChannelEntity? Channel { get; set; }
    public List<TagEntity> Tags { get; set; } = [];
}

/// <summary>采集点位。</summary>
public class TagEntity
{
    [Key]
    public long Id { get; set; }
    public long DeviceId { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    /// <summary>协议地址（语法由驱动定义，如 40001 / DB1.DBX0.0 / ns=2;s=Channel1.Device1.Tag1）。</summary>
    [Required, MaxLength(300)]
    public string Address { get; set; } = "";
    /// <summary>统一数据类型名（Bool/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String）。</summary>
    [Required, MaxLength(20)]
    public string DataType { get; set; } = "Int16";
    public bool Writable { get; set; }
    /// <summary>采集周期覆盖（null = 用通道默认周期）。</summary>
    public int? PollIntervalMs { get; set; }
    /// <summary>工程量换算 value = ScaleA * 原始值 + ScaleB。</summary>
    public double ScaleA { get; set; } = 1.0;
    public double ScaleB { get; set; } = 0.0;
    /// <summary>死区：变化量超过该值才发布（null = 任意变化都发布）。</summary>
    public double? Deadband { get; set; }
    /// <summary>位拆取：取整数的第 N 位当开关量。</summary>
    public int? BitIndex { get; set; }
    [MaxLength(300)]
    public string? Description { get; set; }
    public int SortOrder { get; set; }

    public DeviceEntity? Device { get; set; }
}

/// <summary>北向应用（数据出口）。</summary>
public class NorthAppEntity
{
    [Key]
    public long Id { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    /// <summary>北向类型：mqtt / http / kafka / database。</summary>
    [Required, MaxLength(50)]
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [Required]
    public string ConnectionJson { get; set; } = "{}";
    /// <summary>点位过滤：device-01.temp-*（支持 * 与 ? 通配，分号分隔多模式）。</summary>
    [MaxLength(2000)]
    public string? IncludeFilter { get; set; }
    [MaxLength(2000)]
    public string? ExcludeFilter { get; set; }
    /// <summary>报文格式：json-lines | json-array | csv。</summary>
    [Required, MaxLength(20)]
    public string Format { get; set; } = "json-lines";
    public int BatchSize { get; set; } = 100;
    public int BatchIntervalMs { get; set; } = 500;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>报警规则。</summary>
public class AlarmRuleEntity
{
    [Key]
    public long Id { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    public long TagId { get; set; }
    /// <summary>条件：Gt | Lt | Eq | Ne | Change。</summary>
    [Required, MaxLength(10)]
    public string Condition { get; set; } = "Gt";
    [MaxLength(100)]
    public string Threshold { get; set; } = "0";
    /// <summary>1 提示 / 2 警告 / 3 严重。</summary>
    public int Level { get; set; } = 2;
    /// <summary>连续 N 次满足条件才触发（防抖）。</summary>
    public int DurationEvaluations { get; set; } = 1;
    /// <summary>连续 N 次不满足条件才恢复。</summary>
    public int RecoverEvaluations { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>报警历史。</summary>
public class AlarmHistoryEntity
{
    [Key]
    public long Id { get; set; }
    public long RuleId { get; set; }
    [MaxLength(100)]
    public string RuleName { get; set; } = "";
    public long TagId { get; set; }
    [MaxLength(100)]
    public string TagName { get; set; } = "";
    [MaxLength(100)]
    public string DeviceName { get; set; } = "";
    public int Level { get; set; }
    public string TriggerValue { get; set; } = "";
    [MaxLength(500)]
    public string Message { get; set; } = "";
    public DateTime TriggeredAt { get; set; } = DateTime.Now;
    public DateTime? RecoveredAt { get; set; }
}

/// <summary>设备模板（厂商+型号 → 点位集合）。</summary>
public class TemplateEntity
{
    [Key]
    public long Id { get; set; }
    [Required, MaxLength(100)]
    public string Vendor { get; set; } = "";
    [Required, MaxLength(100)]
    public string Model { get; set; } = "";
    [MaxLength(300)]
    public string? Description { get; set; }
    /// <summary>TemplateTag[] 的 JSON。</summary>
    [Required]
    public string TagsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>控制台账号。</summary>
public class UserEntity
{
    [Key]
    public long Id { get; set; }
    [Required, MaxLength(50)]
    public string UserName { get; set; } = "";
    /// <summary>PBKDF2 哈希（PasswordHasher 生成，格式见该类注释）。</summary>
    [Required]
    public string PasswordHash { get; set; } = "";
    /// <summary>admin | operator（operator 只读）。</summary>
    [Required, MaxLength(20)]
    public string Role { get; set; } = "operator";
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>键值设置（监听地址、保留期、积压上限等）。</summary>
public class SettingEntity
{
    [Key, MaxLength(100)]
    public string Key { get; set; } = "";
    [Required]
    public string Value { get; set; } = "";
}

/// <summary>离线数据队列（断点续传）。Id 自增即全局顺序。</summary>
public class SpoolEntity
{
    [Key]
    public long Id { get; set; }
    public long NorthAppId { get; set; }
    /// <summary>一个批次序列化后的报文（与北向格式一致，恢复后原样发送）。</summary>
    [Required]
    public string Payload { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

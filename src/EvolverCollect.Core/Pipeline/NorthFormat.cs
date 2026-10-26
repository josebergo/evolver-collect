namespace EvolverCollect.Core.Pipeline;

using System.Text;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>北向报文格式化：json-lines（默认，逐行 JSON 对象）/ json-array / csv。与运行时/离线回放共用同一出口，保证格式一致。</summary>
public static class NorthFormat
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>对外字段名约定（文档中心《MQTT 报文设计》）：device/tag/value/quality/timestamp(ms)/sourceTimestamp?</summary>
    private sealed record NorthPointDto(
        string Device, string Tag, object? Value, string Quality, long Timestamp, long? SourceTimestamp);

    private static string Serialize(DataPoint p) =>
        JsonSerializer.Serialize(new NorthPointDto(p.DeviceName, p.TagName, p.Value, p.Quality.ToString(), p.TimestampMs, p.SourceTimestampMs), JsonOptions);

    /// <summary>数据点的单行 JSON（camelCase）：{"device":"...","tag":"...","value":...,"quality":"Good","timestamp":1798...}</summary>
    public static string ToJsonLine(DataPoint p) => Serialize(p);

    public static string FormatBatch(IReadOnlyList<DataPoint> points, string format) => format switch
    {
        "json-array" => JsonSerializer.Serialize(points.Select(SerializeDto).ToList(), JsonOptions),
        "csv" => ToCsv(points),
        _ => string.Join("\n", points.Select(Serialize)),
    };

    private static NorthPointDto SerializeDto(DataPoint p) =>
        new(p.DeviceName, p.TagName, p.Value, p.Quality.ToString(), p.TimestampMs, p.SourceTimestampMs);

    private static string ToCsv(IReadOnlyList<DataPoint> points)
    {
        var sb = new StringBuilder(points.Count * 48);
        foreach (var p in points)
        {
            sb.Append(p.DeviceName).Append(',').Append(p.TagName).Append(',');
            AppendValue(sb, p.Value).Append(',');
            sb.Append(p.Quality).Append(',').Append(p.TimestampMs).Append('\n');
        }
        return sb.ToString();
    }

    private static StringBuilder AppendValue(StringBuilder sb, object? v)
    {
        return v is string s ? sb.Append('"').Append(s.Replace("\"", "\"\"")).Append('"') : sb.Append(v);
    }
}

/// <summary>点位过滤器：device.tag 通配匹配（* 与 ?，分号分隔多模式）。null IncludeFilter = 全部通过。</summary>
public sealed class TagFilter
{
    private readonly string[] _includes;
    private readonly string[] _excludes;

    public TagFilter(string? includeFilter, string? excludeFilter)
    {
        _includes = Split(includeFilter);
        _excludes = Split(excludeFilter);
    }

    public bool Matches(DataPoint p)
    {
        var key = $"{p.DeviceName}.{p.TagName}";
        if (_excludes.Length > 0 && _excludes.Any(e => MatchesPattern(key, e))) return false;
        return _includes.Length == 0 || _includes.Any(i => MatchesPattern(key, i));
    }

    private static string[] Split(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? []
            : filter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // 通配匹配（* 任意串、? 单字符）；不做正则，避免注入与转义歧义
    private static bool MatchesPattern(string input, string pattern)
    {
        return Match(input.AsSpan(), pattern.AsSpan());

        static bool Match(ReadOnlySpan<char> s, ReadOnlySpan<char> p)
        {
            int star = -1, si = 0, pi = 0, mark = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si])) { si++; pi++; }
                else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
                else if (star >= 0) { pi = star + 1; si = ++mark; }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }
    }
}

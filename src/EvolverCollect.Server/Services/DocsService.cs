namespace EvolverCollect.Server.Services;

using System.Reflection;
using System.Text.RegularExpressions;
using Markdig;

/// <summary>
/// 内置文档中心：docs/ 下的 Markdown 作为嵌入资源打进 exe（离线可用）。
/// 首次访问时建立目录（frontmatter title/order 排序）与全文索引（中文按子串匹配）。
/// 安全：渲染禁用原始 HTML，防存储型 XSS。
/// </summary>
public sealed partial class DocsService
{
    private const string ResourcePrefix = "EvolverCollect.Server.docs.";

    public sealed record DocMeta(string Slug, string Title, string Section, int Order);
    public sealed record DocEntry(DocMeta Meta, string Html);

    private readonly Lazy<Dictionary<string, DocEntry>> _docs;
    private readonly ILogger<DocsService>? _logger;

    public DocsService(ILogger<DocsService>? logger = null)
    {
        _logger = logger;
        _docs = new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()          // 表格/任务列表/自动链接
        .UsePipeTables()
        .DisableHtml()                    // 工控安全基线：不渲染原始 HTML
        .Build();

    private Dictionary<string, DocEntry> BuildIndex()
    {
        var result = new Dictionary<string, DocEntry>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;

            // MSBuild 嵌入资源名会把目录中的连字符规范成下划线，这里统一还原（全部命名为 kebab-case）
            var slug = name[ResourcePrefix.Length..^3].Replace('.', '/').Replace('_', '-');
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd();

            var (title, order) = ParseFrontMatter(markdown, slug);
            var section = slug.Contains('/') ? slug[..slug.LastIndexOf('/')] : "";
            var html = Markdown.ToHtml(markdown, Pipeline);
            result[slug] = new DocEntry(new DocMeta(slug, title, section, order), html);
            _logger?.LogDebug("文档已加载: {Slug}", slug);
        }
        return result;
    }

    private static (string Title, int Order) ParseFrontMatter(string markdown, string fallbackTitle)
    {
        var match = FrontMatterRegex().Match(markdown);
        if (!match.Success) return (TitleFromSlug(fallbackTitle), 99);
        var title = TitleLineRegex().Match(match.Groups[1].Value).Groups[1].Value.Trim();
        var order = int.TryParse(OrderLineRegex().Match(match.Groups[1].Value).Groups[1].Value, out var o) ? o : 99;
        return (string.IsNullOrEmpty(title) ? TitleFromSlug(fallbackTitle) : title, order);
    }

    private static string TitleFromSlug(string slug)
    {
        var last = slug.Contains('/') ? slug[(slug.LastIndexOf('/') + 1)..] : slug;
        last = last.Length > 3 && char.IsDigit(last[0]) && last.Contains('-') ? last[(last.IndexOf('-') + 1)..] : last;
        return last.Replace('-', ' ');
    }

    public IReadOnlyList<DocMeta> GetToc() =>
        [.. _docs.Value.Values.Select(d => d.Meta)
            .OrderBy(d => d.Section, StringComparer.Ordinal)
            .ThenBy(d => d.Order)
            .ThenBy(d => d.Title)];

    public DocEntry? Get(string slug) =>
        _docs.Value.TryGetValue(slug, out var entry) ? entry : null;

    /// <summary>全文搜索（标题权重 + 正文子串；中文子串足够现场使用，分词升级见 ADR-0005）。</summary>
    public IReadOnlyList<DocMeta> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        return [.. _docs.Value.Values
            .Where(d => d.Meta.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Meta)
            .Concat(_docs.Value.Values
                .Where(d => !d.Meta.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && PlainText(d).Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Meta))
            .Distinct()
            .Take(20)];
    }

    private static string PlainText(DocEntry entry) =>
        HtmlTagRegex().Replace(entry.Html, " ");
}

public sealed partial class DocsService
{
    [GeneratedRegex(@"^---\s*\n(?<fm>.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();

    [GeneratedRegex(@"^title:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleLineRegex();

    [GeneratedRegex(@"^order:\s*(\d+)$", RegexOptions.Multiline)]
    private static partial Regex OrderLineRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}

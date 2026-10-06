using System.Reflection;

namespace Sol.Application.Features.Agent;

/// <summary>A Skill that ships inside the Sol assembly. Metadata mirrors the SKILL.md frontmatter.</summary>
public sealed record BuiltInSkill(string Slug, string Name, string Description);

/// <summary>
/// The read-only starter Skill library embedded in this assembly.
///
/// User-installed Skills live in storage and win slug conflicts; built-ins fill the gap before the
/// user installs anything, so `load_skill` is never an empty catalog on a fresh device. They are
/// pure markdown — no scripts — so they never touch the Skill script runner or its approval path.
/// </summary>
public static class BuiltInSkillCatalog
{
    private const string ResourcePrefix = "Sol.BuiltInSkills.";

    private static readonly BuiltInSkill[] Skills =
    [
        new(
            "canvas-recipes",
            "Sol 画布操作手册",
            "把常见任务翻译成工具调用序列的速查手册:图生视频管线、画质增强、扩图改比例、参考图链式创作、节点布局惯例。涉及具体画布操作时先读它。"),
        new(
            "image-prompt-craft",
            "图像提示词工程",
            "结构化图像提示词写法(主体/构图/光影/风格/质量),风格与镜头词汇表,比例与分辨率的取舍,用 count/seed 做受控迭代,配合 edit_image 修图。用户要求写图或改图提示词时读它。"),
        new(
            "video-prompt-craft",
            "视频提示词与运镜",
            "视频生成提示词的结构:主体动作描述、运镜词汇(推拉摇移/环绕/手持)、时长与分辨率设置、图生视频的首帧策略。用户要做视频或从图片生成视频时读它。"),
        new(
            "storyboard-workflow",
            "分镜故事板工作流",
            "从剧本到画布的完整编排:拆分镜头列表、批量建节点、一次 connect_nodes 连线、run_nodes 并行生成、保持镜头一致性的技巧。用户给剧本/故事/分镜时读它。"),
        new(
            "batch-variation",
            "批量变体与风格探索",
            "用 seed 矩阵和风格矩阵做受控变体,多个 imageGen 节点并行探索,结果筛选与淘汰。用户要'多来几个方案'、'换风格试试'时读它。"),
    ];

    public static IReadOnlyList<BuiltInSkill> All => Skills;

    public static BuiltInSkill? Find(string slug) =>
        Skills.FirstOrDefault(skill => string.Equals(skill.Slug, slug, StringComparison.Ordinal));

    /// <summary>Reads an embedded Skill file. Paths are relative to the skill folder and flattened.</summary>
    public static string? ReadText(string slug, string path)
    {
        if (!IsSafeRelativePath(path) || Find(slug) is null) return null;
        // %(RecursiveDir) uses the platform separator in resource names ('/' on Unix, '\' on
        // Windows), so the skill-relative path is normalized for matching instead of rebuilt.
        var suffix = path.Replace('\\', '/');
        return ReadResource(ReadResourceNames()
            .FirstOrDefault(name => NormalizedSuffix(name, slug) == suffix));
    }

    /// <summary>Lists the files one built-in Skill carries, as forward-slash relative paths.</summary>
    public static IReadOnlyList<string> ListFiles(string slug)
    {
        return [.. ReadResourceNames()
            .Select(name => NormalizedSuffix(name, slug))
            .Where(suffix => suffix.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    /// <summary>The skill-relative path of an embedded resource, or "" when it belongs to another skill.</summary>
    private static string NormalizedSuffix(string resourceName, string slug)
    {
        var folderPrefix = $"{ResourcePrefix}{slug}/";
        var altPrefix = $"{ResourcePrefix}{slug}\\";
        var suffix = resourceName.StartsWith(folderPrefix, StringComparison.Ordinal)
            ? resourceName[folderPrefix.Length..]
            : resourceName.StartsWith(altPrefix, StringComparison.Ordinal)
                ? resourceName[altPrefix.Length..]
                : null;
        return suffix?.Replace('\\', '/') ?? string.Empty;
    }

    private static IEnumerable<string> ReadResourceNames() =>
        typeof(BuiltInSkillCatalog).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal));

    private static string? ReadResource(string? resourceName)
    {
        if (resourceName is null) return null;

        var assembly = typeof(BuiltInSkillCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        return path.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or '/');
    }
}


namespace Sol.Application.Features.Agent;

/// <summary>Tuning for the Agent run loop.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Model turns one run may spend on tool calls.
    ///
    /// A plain Q&A turn ends on the first reply, but a canvas orchestration — building a cast of
    /// characters, then a storyboard of text/image/video chains — spends several turns per node
    /// and routinely runs past 20. The default leaves headroom for a 6-8 shot story; raise it for
    /// longer ones. The run ends with a progress summary rather than an error when the budget is
    /// spent, so everything already on the canvas stays usable.
    ///
    /// Independent of the per-run wall-clock cap in <c>AgentRunHost</c>: a budget this high only
    /// completes when the generations it triggers also fit in that cap.
    /// </summary>
    public int MaxToolIterations { get; set; } = 40;
}

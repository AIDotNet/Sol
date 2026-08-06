using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>Signals durable runs to the background host and cancels in-flight model streams.</summary>
public interface IAgentRunControl
{
    void Enqueue(AgentRunId runId);

    bool Cancel(AgentRunId runId);
}

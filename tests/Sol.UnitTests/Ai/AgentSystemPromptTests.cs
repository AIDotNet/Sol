namespace Sol.UnitTests.Ai;

public class AgentSystemPromptTests
{
    [Fact]
    public void PromptContainsTheProductSafetyAndGenerationContract()
    {
        var prompt = Sol.Application.Features.Agent.AgentSystemPrompt.Text;
        var normalized = string.Join(
            ' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("infinite node canvas", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("All canvas reads and mutations must go through", prompt);
        Assert.Contains("Call it exactly once for one intended attempt", normalized);
        Assert.Contains("Never call `run_node` again while", normalized);
        Assert.Contains("If it times out", prompt);
        Assert.Contains("load_skill", prompt);
        Assert.Contains("approval", prompt, StringComparison.OrdinalIgnoreCase);
    }
}

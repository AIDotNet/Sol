using System.Text;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Skills;

namespace Sol.UnitTests.Ai;

public sealed class SkillScriptRunnerTests
{
    [Fact]
    public async Task Missing_socket_reports_unavailable()
    {
        using var runner = Runner(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("../script.py")]
    [InlineData("/script.py")]
    [InlineData("script.exe")]
    public async Task Unsafe_or_unsupported_script_paths_are_rejected(string scriptPath)
    {
        using var runner = Runner(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));
        var request = new SkillScriptRequest(
            [new SkillPackageFile(scriptPath, Encoding.UTF8.GetBytes("print('unsafe')"))],
            scriptPath,
            [],
            null,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Script_must_belong_to_the_submitted_package()
    {
        using var runner = Runner(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));
        var request = new SkillScriptRequest(
            [new SkillPackageFile("SKILL.md", Encoding.UTF8.GetBytes("instructions"))],
            "script.py",
            [],
            null,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_cannot_exceed_configured_limit()
    {
        using var runner = Runner(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sock"));
        var request = new SkillScriptRequest(
            [new SkillPackageFile("script.py", Encoding.UTF8.GetBytes("print('ok')"))],
            "script.py",
            [],
            null,
            TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    private static UnixSocketSkillScriptRunner Runner(string socketPath) => new(
        Microsoft.Extensions.Options.Options.Create(new SkillsOptions
        {
            RunnerSocketPath = socketPath,
            RunnerTimeoutSeconds = 30,
            RunnerMaxOutputBytes = 256 * 1024,
            MaxEntries = 100,
            MaxExtractedBytes = 2 * 1024 * 1024,
        }));
}

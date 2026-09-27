using System.Text;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Skills;

namespace Sol.UnitTests.Ai;

public sealed class SkillScriptRunnerTests
{
    [Fact]
    public async Task Disabled_runner_never_executes_scripts()
    {
        var runner = new DisabledSkillScriptRunner(new SkillsOptions());

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new SkillScriptRequest([], "script.py", [], null, TimeSpan.FromSeconds(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Missing_socket_reports_unavailable()
    {
        var runner = Runner();

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("../script.py")]
    [InlineData("/script.py")]
    [InlineData("script.exe")]
    public async Task Unsafe_or_unsupported_script_paths_are_rejected(string scriptPath)
    {
        var runner = Runner();
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
        var runner = Runner();
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
        var runner = Runner();
        var request = new SkillScriptRequest(
            [new SkillPackageFile("script.py", Encoding.UTF8.GetBytes("print('ok')"))],
            "script.py",
            [],
            null,
            TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "''")]
    [InlineData("plain-arg", "'plain-arg'")]
    [InlineData("a b --flag=1", "'a b --flag=1'")]
    // The embedded quote must use the '\'' idiom; any other replacement lets argument content
    // close the quoted span and append commands (see the injection case below).
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a'b'c", "'a'\\''b'\\''c'")]
    public void Arguments_are_single_quoted_with_the_posix_escape_idiom(string input, string expected)
    {
        Assert.Equal(expected, OpenSandboxSkillScriptRunner.ShellQuote(input));
    }

    [Fact]
    public void An_injection_attempt_stays_a_single_argument()
    {
        // The whole payload — including the would-be command separators — must remain inside
        // the quoted span. Quoted form: ''\''; touch /tmp/pwned; #' — the embedded '\'' closes,
        // escapes, and reopens the span, so sh receives exactly one argument and never executes
        // the trailing text.
        var quoted = OpenSandboxSkillScriptRunner.ShellQuote("'; touch /tmp/pwned; #");

        Assert.Equal("''\\''; touch /tmp/pwned; #'", quoted);
        // After removing escaped quotes (\' — a literal quote, not a delimiter), every
        // remaining single quote is a span delimiter and must therefore pair up.
        var delimiters = quoted.Replace("\\'", string.Empty);
        Assert.Equal(0, delimiters.Count(c => c == '\'') % 2);
    }

    private static OpenSandboxSkillScriptRunner Runner() => new(
        Microsoft.Extensions.Options.Options.Create(new SkillsOptions
        {
            OpenSandboxDomain = "localhost:8090",
            RunnerTimeoutSeconds = 30,
            RunnerMaxOutputBytes = 256 * 1024,
            MaxEntries = 100,
            MaxExtractedBytes = 2 * 1024 * 1024,
        }));
}

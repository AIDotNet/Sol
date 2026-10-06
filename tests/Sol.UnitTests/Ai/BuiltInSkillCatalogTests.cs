namespace Sol.UnitTests.Ai;

public class BuiltInSkillCatalogTests
{
    [Fact]
    public void ShipsAStarterLibraryWithUniqueSlugs()
    {
        var skills = Sol.Application.Features.Agent.BuiltInSkillCatalog.All;

        Assert.NotEmpty(skills);
        Assert.Equal(
            skills.Count,
            skills.Select(skill => skill.Slug).Distinct(StringComparer.Ordinal).Count());
        Assert.All(skills, skill =>
        {
            Assert.False(string.IsNullOrWhiteSpace(skill.Name));
            Assert.False(string.IsNullOrWhiteSpace(skill.Description));
        });
    }

    [Fact]
    public void EverySkillLoadsItsSkillMarkdownAndFrontmatterMatchesTheCatalog()
    {
        foreach (var skill in Sol.Application.Features.Agent.BuiltInSkillCatalog.All)
        {
            var content = Sol.Application.Features.Agent.BuiltInSkillCatalog.ReadText(
                skill.Slug, "SKILL.md");
            Assert.True(content is not null, $"{skill.Slug} must embed SKILL.md");
            Assert.Contains("name: ", content, StringComparison.Ordinal);
            Assert.Contains("slug: " + skill.Slug, content, StringComparison.Ordinal);

            var files = Sol.Application.Features.Agent.BuiltInSkillCatalog.ListFiles(skill.Slug);
            Assert.Contains("SKILL.md", files);
        }
    }

    [Fact]
    public void UnknownSlugsAndUnsafePathsAreRejected()
    {
        const string anySlug = "canvas-recipes";
        var catalog = Sol.Application.Features.Agent.BuiltInSkillCatalog.All;

        Assert.Null(Sol.Application.Features.Agent.BuiltInSkillCatalog.Find("no-such-skill"));
        Assert.Null(Sol.Application.Features.Agent.BuiltInSkillCatalog.ReadText("no-such-skill", "SKILL.md"));
        Assert.Null(Sol.Application.Features.Agent.BuiltInSkillCatalog.ReadText(catalog[0].Slug, "../escape.md"));
        Assert.Null(Sol.Application.Features.Agent.BuiltInSkillCatalog.ReadText(anySlug, "/absolute.md"));
        Assert.Null(Sol.Application.Features.Agent.BuiltInSkillCatalog.ReadText(anySlug, "missing.md"));
    }

}

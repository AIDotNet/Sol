using Sol.Domain.Ai;

namespace Sol.UnitTests.Ai;

/// <summary>
/// Enum ↔ wire-string mapping.
/// </summary>
/// <remarks>
/// Values are persisted as text and constrained by a database CHECK, so the two directions must
/// stay in step. A protocol added to <c>ToWire</c> but not to the parser reads back as an
/// exception at query time; the reverse writes a value the CHECK rejects on insert.
/// </remarks>
public class AiEnumNameTests
{
    public static TheoryData<ProviderType> AllProviderTypes()
    {
        var data = new TheoryData<ProviderType>();
        foreach (var value in Enum.GetValues<ProviderType>()) data.Add(value);
        return data;
    }

    public static TheoryData<ModelCategory> AllCategories()
    {
        var data = new TheoryData<ModelCategory>();
        foreach (var value in Enum.GetValues<ModelCategory>()) data.Add(value);
        return data;
    }

    public static TheoryData<McpTransport> AllTransports()
    {
        var data = new TheoryData<McpTransport>();
        foreach (var value in Enum.GetValues<McpTransport>()) data.Add(value);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProviderTypes))]
    public void EveryProviderTypeRoundTrips(ProviderType value)
    {
        Assert.True(AiEnumNames.TryParseProviderType(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void EveryCategoryRoundTrips(ModelCategory value)
    {
        Assert.True(AiEnumNames.TryParseModelCategory(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [MemberData(nameof(AllTransports))]
    public void EveryTransportRoundTrips(McpTransport value)
    {
        Assert.True(AiEnumNames.TryParseMcpTransport(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void WireNamesAreKebabCaseAndDistinct()
    {
        // The database CHECK constraints list these literals; a stray capital or duplicate
        // would fail on insert rather than at build time.
        var names = Enum.GetValues<ProviderType>().Select(value => value.ToWire()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, name => Assert.Equal(name.ToLowerInvariant(), name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("openai")]
    [InlineData("OpenAI-Chat")]
    [InlineData(null)]
    public void UnknownValuesAreRejectedRatherThanGuessed(string? text)
    {
        Assert.False(AiEnumNames.TryParseProviderType(text, out _));
    }
}

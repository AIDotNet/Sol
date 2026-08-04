namespace Sol.Application.Abstractions.Common;

/// <summary>Time source, so identity-resolution windows are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Identity;

/// <summary>
/// The account scope attached to the current request, when the current device has been linked
/// to a signed-in account. Infrastructure uses this scope when opening a database connection;
/// background jobs naturally run with a null account scope and remain device-scoped.
/// </summary>
public interface IAccountContext
{
    AccountId? AccountId { get; set; }
}

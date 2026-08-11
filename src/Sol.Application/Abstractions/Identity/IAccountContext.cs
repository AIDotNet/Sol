using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Identity;

/// <summary>
/// The account scope attached to the current request, when the current device has been linked
/// to a signed-in account. Infrastructure uses this scope when opening a database connection.
/// Background jobs start with a null scope and must explicitly restore it from durable device
/// ownership before accessing account-wide resources.
/// </summary>
public interface IAccountContext
{
    AccountId? AccountId { get; set; }
}

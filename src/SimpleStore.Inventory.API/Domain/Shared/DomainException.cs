namespace SimpleStore.Inventory.API.Domain.Shared;

// Thrown by the domain layer when an invariant is violated.
// The endpoint layer maps this to HTTP 400 Bad Request.
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

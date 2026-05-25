namespace SimpleStore.Identity.API.Client;

public class UserSummary
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public bool IsLockedOut { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
}

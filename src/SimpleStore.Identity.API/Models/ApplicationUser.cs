using Microsoft.AspNetCore.Identity;

namespace SimpleStore.Identity.API.Models;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
}

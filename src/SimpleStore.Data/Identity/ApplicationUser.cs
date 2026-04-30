using Microsoft.AspNetCore.Identity;
namespace SimpleStore.Data.Identity;
public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
}

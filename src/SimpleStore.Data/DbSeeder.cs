using Microsoft.AspNetCore.Identity;
using SimpleStore.Data.Identity;

namespace SimpleStore.Data;

public static class DbSeeder
{
    public const string DemoUserEmail = "demo@simplestore.local";
    public const string DemoUserPassword = "Demo123!";

    public static async Task SeedIdentityAsync(UserManager<ApplicationUser> userManager)
    {
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            FullName = "Demo User"
        };

        await userManager.CreateAsync(user, DemoUserPassword);
    }
}

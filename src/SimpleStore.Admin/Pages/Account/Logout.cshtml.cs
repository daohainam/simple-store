using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Admin.Services.Auth;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Admin.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly IIdentityApiClient _identity;
    private readonly ITokenStore _tokens;

    public LogoutModel(IIdentityApiClient identity, ITokenStore tokens)
    {
        _identity = identity;
        _tokens = tokens;
    }

    public async Task<IActionResult> OnGet() => await LogoutAsync();
    public async Task<IActionResult> OnPost() => await LogoutAsync();

    private async Task<IActionResult> LogoutAsync()
    {
        var current = await _tokens.GetAsync();
        if (current is { RefreshToken.Length: > 0 })
        {
            await _identity.LogoutAsync(new RefreshRequest { RefreshToken = current.RefreshToken });
        }
        await _tokens.ClearAsync();
        return LocalRedirect("~/Account/Login");
    }
}

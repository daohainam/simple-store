using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Identity.API.Client;
using SimpleStore.Web.Services.Auth;

namespace SimpleStore.Web.Areas.Identity.Pages.Account;

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

    public async Task<IActionResult> OnPost(string? returnUrl = null)
    {
        var current = await _tokens.GetAsync();
        if (current is { RefreshToken.Length: > 0 })
        {
            await _identity.LogoutAsync(new RefreshRequest { RefreshToken = current.RefreshToken });
        }

        await _tokens.ClearAsync();
        return LocalRedirect(returnUrl ?? Url.Content("~/"));
    }
}

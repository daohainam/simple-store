using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Admin.Services.Auth;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Admin.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IIdentityApiClient _identity;
    private readonly ITokenStore _tokens;

    public LoginModel(IIdentityApiClient identity, ITokenStore tokens)
    {
        _identity = identity;
        _tokens = tokens;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public void OnGet()
    {
        ReturnUrl ??= Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var returnUrl = string.IsNullOrEmpty(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl) ? Url.Content("~/") : ReturnUrl;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var response = await _identity.LoginAsync(new LoginRequest
        {
            Email = Input.Email,
            Password = Input.Password
        });

        if (response is null)
        {
            ErrorMessage = "Invalid email or password.";
            return Page();
        }

        // Admin enforces role Admin — reject early so the user gets a clear message instead of a redirect loop.
        if (!response.User.Roles.Contains("Admin"))
        {
            ErrorMessage = "Account does not have admin access.";
            return Page();
        }

        await _tokens.SetAsync(new TokenSet
        {
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            ExpiresAt = response.ExpiresAt
        });

        return LocalRedirect(returnUrl);
    }
}

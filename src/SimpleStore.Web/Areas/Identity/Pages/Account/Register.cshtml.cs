using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Identity.API.Client;
using SimpleStore.Web.Services.Auth;

namespace SimpleStore.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class RegisterModel : PageModel
{
    private readonly IIdentityApiClient _identity;
    private readonly ITokenStore _tokens;

    public RegisterModel(IIdentityApiClient identity, ITokenStore tokens)
    {
        _identity = identity;
        _tokens = tokens;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, Display(Name = "Full name"), StringLength(100)]
        public string FullName { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password), Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");
        ReturnUrl = returnUrl;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var response = await _identity.RegisterAsync(new RegisterRequest
        {
            Email = Input.Email,
            FullName = Input.FullName,
            Password = Input.Password
        });

        if (response is null)
        {
            ModelState.AddModelError(string.Empty, "Could not create the account. Email may already be in use or the password is too weak.");
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

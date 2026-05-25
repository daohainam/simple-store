using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Identity.API.Client;
using SimpleStore.Web.Services.Auth;

namespace SimpleStore.Web.Areas.Identity.Pages.Account;

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

    public string? ReturnUrl { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ModelState.AddModelError(string.Empty, ErrorMessage);
        }
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid)
        {
            ReturnUrl = returnUrl;
            return Page();
        }

        var response = await _identity.LoginAsync(new LoginRequest
        {
            Email = Input.Email,
            Password = Input.Password
        });

        if (response is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            ReturnUrl = returnUrl;
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

    // Returns JSON PublicKeyCredentialRequestOptions for the WebAuthn assertion ceremony.
    public async Task<IActionResult> OnPostPasskeyRequestOptionsAsync()
    {
        var optionsJson = await _identity.GetPasskeyAssertionOptionsAsync();
        return Content(optionsJson, "application/json");
    }

    // Receives the signed assertion (raw WebAuthn credential JSON) and signs the user in.
    public async Task<IActionResult> OnPostPasskeyAssertAsync([FromBody] JsonElement credential, string? returnUrl = null)
    {
        var response = await _identity.PasskeyAssertionAsync(new PasskeyAssertionRequest
        {
            CredentialJson = credential.GetRawText()
        });

        if (response is null)
        {
            return BadRequest(new { error = "Passkey sign-in failed." });
        }

        await _tokens.SetAsync(new TokenSet
        {
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            ExpiresAt = response.ExpiresAt
        });

        return new JsonResult(new { redirectTo = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/") });
    }
}

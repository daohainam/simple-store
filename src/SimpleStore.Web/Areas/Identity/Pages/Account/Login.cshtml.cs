using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Data.Identity;

namespace SimpleStore.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
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

    public async Task OnGetAsync(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ModelState.AddModelError(string.Empty, ErrorMessage);
        }

        // Clear any leftover external cookie so login starts clean
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

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

        var result = await _signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: false);
        if (result.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "This account is locked.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
        }

        ReturnUrl = returnUrl;
        return Page();
    }

    // Returns JSON PublicKeyCredentialRequestOptions for the WebAuthn assertion ceremony.
    public async Task<IActionResult> OnPostPasskeyRequestOptionsAsync()
    {
        var optionsJson = await _signInManager.MakePasskeyRequestOptionsAsync(user: null);
        return Content(optionsJson, "application/json");
    }

    // Receives the signed assertion (raw WebAuthn credential JSON) and signs the user in.
    public async Task<IActionResult> OnPostPasskeyAssertAsync([FromBody] JsonElement credential, string? returnUrl = null)
    {
        var credentialJson = credential.GetRawText();
        var result = await _signInManager.PasskeySignInAsync(credentialJson);
        if (!result.Succeeded)
        {
            return BadRequest(new { error = "Passkey sign-in failed." });
        }

        return new JsonResult(new { redirectTo = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/") });
    }
}

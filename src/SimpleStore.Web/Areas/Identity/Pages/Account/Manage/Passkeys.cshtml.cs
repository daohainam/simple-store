using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Data.Identity;

namespace SimpleStore.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public class PasskeysModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public PasskeysModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = Array.Empty<UserPasskeyInfo>();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return NotFound();

        Passkeys = (await _userManager.GetPasskeysAsync(user)).ToList();
        return Page();
    }

    // Step 2 of registration: server issues PublicKeyCredentialCreationOptions.
    public async Task<IActionResult> OnPostCreationOptionsAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var userId = await _userManager.GetUserIdAsync(user);
        var userName = await _userManager.GetUserNameAsync(user) ?? "User";

        var optionsJson = await _signInManager.MakePasskeyCreationOptionsAsync(new()
        {
            Id = userId,
            Name = userName,
            DisplayName = user.FullName.Length > 0 ? user.FullName : userName
        });

        return Content(optionsJson, "application/json");
    }

    // Step 7 of registration: server verifies attestation and stores the passkey.
    public async Task<IActionResult> OnPostRegisterAsync([FromBody] RegisterRequest body)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        if (body.Credential.ValueKind == JsonValueKind.Undefined)
        {
            return BadRequest(new { error = "Missing credential." });
        }

        var attestation = await _signInManager.PerformPasskeyAttestationAsync(body.Credential.GetRawText());
        if (!attestation.Succeeded)
        {
            return BadRequest(new { error = attestation.Failure?.Message ?? "Attestation failed." });
        }

        var passkey = attestation.Passkey;
        if (!string.IsNullOrWhiteSpace(body.Name))
        {
            passkey.Name = body.Name.Trim();
        }

        var add = await _userManager.AddOrUpdatePasskeyAsync(user, passkey);
        if (!add.Succeeded)
        {
            return BadRequest(new { error = string.Join("; ", add.Errors.Select(e => e.Description)) });
        }

        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string credentialId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return NotFound();

        var bytes = Convert.FromBase64String(NormalizeBase64(credentialId));
        var passkey = await _userManager.GetPasskeyAsync(user, bytes);
        if (passkey is null)
        {
            ErrorMessage = "Passkey not found.";
            return RedirectToPage();
        }

        var result = await _userManager.RemovePasskeyAsync(user, bytes);
        StatusMessage = result.Succeeded ? "Passkey removed." : "Failed to remove passkey.";
        return RedirectToPage();
    }

    private static string NormalizeBase64(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
    }

    public class RegisterRequest
    {
        public JsonElement Credential { get; set; }
        public string? Name { get; set; }
    }
}

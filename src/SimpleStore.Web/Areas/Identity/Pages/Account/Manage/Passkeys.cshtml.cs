using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public class PasskeysModel : PageModel
{
    private readonly IIdentityApiClient _identity;

    public PasskeysModel(IIdentityApiClient identity) => _identity = identity;

    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = Array.Empty<UserPasskeyInfo>();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Passkeys = await _identity.GetPasskeysAsync();
        return Page();
    }

    // Step 2 of registration: server issues PublicKeyCredentialCreationOptions.
    public async Task<IActionResult> OnPostCreationOptionsAsync()
    {
        var optionsJson = await _identity.GetPasskeyCreationOptionsAsync();
        return Content(optionsJson, "application/json");
    }

    // Step 7 of registration: server verifies attestation and stores the passkey.
    public async Task<IActionResult> OnPostRegisterAsync([FromBody] RegisterRequestBody body)
    {
        if (body.Credential.ValueKind == JsonValueKind.Undefined)
        {
            return BadRequest(new { error = "Missing credential." });
        }

        try
        {
            await _identity.PasskeyAttestationAsync(new PasskeyAttestationRequest
            {
                CredentialJson = body.Credential.GetRawText(),
                Name = body.Name
            });
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string credentialId)
    {
        try
        {
            await _identity.DeletePasskeyAsync(credentialId);
            StatusMessage = "Passkey removed.";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Failed to remove passkey.";
        }
        return RedirectToPage();
    }

    public class RegisterRequestBody
    {
        public JsonElement Credential { get; set; }
        public string? Name { get; set; }
    }
}

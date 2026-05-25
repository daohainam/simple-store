using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IIdentityApiClient _identity;

    public IndexModel(IIdentityApiClient identity) => _identity = identity;

    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required, Display(Name = "Full name"), StringLength(100)]
        public string FullName { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var me = await _identity.GetMeAsync();
        if (me is null) return NotFound();

        Email = me.Email;
        Input = new InputModel { FullName = me.FullName };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            var me = await _identity.GetMeAsync();
            Email = me?.Email ?? string.Empty;
            return Page();
        }

        await _identity.UpdateMeAsync(new UpdateProfileRequest { FullName = Input.FullName });

        StatusMessage = "Profile updated.";
        return RedirectToPage();
    }
}

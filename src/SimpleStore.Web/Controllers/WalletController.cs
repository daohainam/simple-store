using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimpleStore.Payment.API.Client;
using SimpleStore.Web.ViewModels;

namespace SimpleStore.Web.Controllers;

// Customer wallet: view the prepaid balance, top up, and review transaction history. The balance
// is what the checkout saga's payment step charges — depositing here is how a shopper makes their
// next checkout succeed (and not depositing is how it fails, releasing the reserved stock).
[Authorize]
public class WalletController : Controller
{
    private readonly IPaymentApiClient _payments;

    public WalletController(IPaymentApiClient payments) => _payments = payments;

    public async Task<IActionResult> Index()
    {
        var account = await _payments.GetMyAccountAsync();
        var transactions = await _payments.GetMyTransactionsAsync();
        return View(new WalletViewModel { Account = account, Transactions = transactions });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deposit(decimal amount)
    {
        if (amount <= 0)
        {
            TempData["Error"] = "Deposit amount must be positive.";
            return RedirectToAction(nameof(Index));
        }

        var account = await _payments.DepositAsync(amount);
        TempData["Success"] = $"Deposited ${amount:N2}. New balance: ${account.Balance:N2}.";
        return RedirectToAction(nameof(Index));
    }
}

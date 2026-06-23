using SimpleStore.Payment.API.Client;

namespace SimpleStore.Web.ViewModels;

public class WalletViewModel
{
    public AccountDto Account { get; set; } = new();
    public IReadOnlyList<TransactionDto> Transactions { get; set; } = Array.Empty<TransactionDto>();
}

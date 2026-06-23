using System.ComponentModel.DataAnnotations;

namespace SimpleStore.Payment.API.Models;

// A customer's prepaid balance. One account per user (UserId is a soft reference to
// AspNetUsers.Id in identitydb — no cross-DB FK). Accounts are auto-provisioned at zero
// balance on first access. Balance is debited by the checkout saga's payment step and
// topped up by deposits.
public class PaymentAccount
{
    public Guid Id { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
}

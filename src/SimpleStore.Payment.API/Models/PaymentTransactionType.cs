namespace SimpleStore.Payment.API.Models;

public enum PaymentTransactionType
{
    // Funds added to the account (top-up).
    Deposit,
    // Funds debited to pay for an order (driven by the checkout saga).
    Payment
}

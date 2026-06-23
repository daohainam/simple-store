using MassTransit;
using SimpleStore.Contracts;
using SimpleStore.Payment.API.Services;

namespace SimpleStore.Payment.API.Consumers;

/// <summary>
/// Consumes the checkout saga's ProcessPaymentRequestedEventV1 and charges the customer's account.
/// PaymentService publishes PaymentSucceededEventV1 / PaymentFailedEventV1 (inside its transaction)
/// based on the balance. The MassTransit EF inbox (on PaymentDbContext) makes the consume
/// exactly-once, so a redelivered request never double-charges.
/// </summary>
public sealed class ProcessPaymentRequestedConsumer : IConsumer<ProcessPaymentRequestedEventV1>
{
    private readonly IPaymentService _payments;
    private readonly ILogger<ProcessPaymentRequestedConsumer> _log;

    public ProcessPaymentRequestedConsumer(IPaymentService payments, ILogger<ProcessPaymentRequestedConsumer> log)
    {
        _payments = payments;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ProcessPaymentRequestedEventV1> context)
    {
        var msg = context.Message;

        using var _ = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = msg.CorrelationId,
            ["OrderId"] = msg.OrderId
        });

        _log.LogInformation("Payment requested for order {OrderId}: {Amount}.", msg.OrderId, msg.Amount);

        await _payments.DebitForOrderAsync(
            msg.UserId, msg.OrderId, msg.CorrelationId, msg.Amount, context.CancellationToken);
    }
}

using MassTransit;
using SimpleStore.Contracts;
using SimpleStore.Inventory.API.Application.Reservations;

namespace SimpleStore.Inventory.API.Consumers;

/// <summary>
/// Bridges the checkout saga's ReserveStockRequestedEvent onto the CreateReservationHandler.
/// Idempotency is provided by AppendCondition.NoStream in the handler (a redelivered request with
/// the same ReservationId collapses onto the existing stream), so no inbox is required here.
/// </summary>
public sealed class ReserveStockRequestedConsumer : IConsumer<ReserveStockRequestedEvent>
{
    private readonly CreateReservationHandler _handler;

    public ReserveStockRequestedConsumer(CreateReservationHandler handler) => _handler = handler;

    public async Task Consume(ConsumeContext<ReserveStockRequestedEvent> context)
    {
        var msg = context.Message;
        var cmd = new CreateReservationCommand(
            msg.CorrelationId,
            msg.ReservationId,
            msg.OrderId,
            msg.Lines.Select(l => new ReservationCommandLine(l.ProductId, l.Quantity)).ToList());

        await _handler.HandleAsync(cmd, context.CancellationToken);
    }
}

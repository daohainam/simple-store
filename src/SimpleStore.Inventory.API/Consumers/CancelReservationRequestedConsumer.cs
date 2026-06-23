using MassTransit;
using SimpleStore.Contracts;
using SimpleStore.Inventory.API.Application.Reservations;

namespace SimpleStore.Inventory.API.Consumers;

/// <summary>
/// Bridges the checkout saga's StockReservationCancelRequestedEventV1 (compensation) onto the
/// CancelReservationHandler. Idempotency is provided by the handler (rehydrate + IsCancelled guard,
/// plus an expected-revision append), so no inbox is required here — same posture as
/// ReserveStockRequestedConsumer.
/// </summary>
public sealed partial class CancelReservationRequestedConsumer : IConsumer<StockReservationCancelRequestedEventV1>
{
    private readonly CancelReservationHandler _handler;
    private readonly ILogger<CancelReservationRequestedConsumer> _log;

    public CancelReservationRequestedConsumer(
        CancelReservationHandler handler,
        ILogger<CancelReservationRequestedConsumer> log)
    {
        _handler = handler;
        _log = log;
    }

    public async Task Consume(ConsumeContext<StockReservationCancelRequestedEventV1> context)
    {
        var msg = context.Message;

        using var _ = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = msg.CorrelationId,
            ["ReservationId"] = msg.ReservationId,
            ["OrderId"] = msg.OrderId
        });

        LogReceived(_log, msg.ReservationId, msg.OrderId);

        var cmd = new CancelReservationCommand(msg.CorrelationId, msg.ReservationId, msg.OrderId);
        await _handler.HandleAsync(cmd, context.CancellationToken);
    }

    [LoggerMessage(
        EventId = 1210,
        Level = LogLevel.Information,
        Message = "Reservation cancel (compensation) request received: {ReservationId} for order {OrderId}.")]
    private static partial void LogReceived(ILogger logger, Guid reservationId, int orderId);
}

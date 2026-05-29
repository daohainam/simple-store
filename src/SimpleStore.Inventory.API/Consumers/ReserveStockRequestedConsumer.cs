using MassTransit;
using SimpleStore.Contracts;
using SimpleStore.Inventory.API.Application.Reservations;
using SimpleStore.Inventory.API.Observability;

namespace SimpleStore.Inventory.API.Consumers;

/// <summary>
/// Bridges the checkout saga's ReserveStockRequestedEvent onto the CreateReservationHandler.
/// Idempotency is provided by AppendCondition.NoStream in the handler (a redelivered request with
/// the same ReservationId collapses onto the existing stream), so no inbox is required here.
///
/// v10: uses LoggerMessage source generation — this consumer is on every checkout's hot path
/// and the cost of one ILogger.LogInformation per request is small but non-zero.
/// </summary>
public sealed partial class ReserveStockRequestedConsumer : IConsumer<ReserveStockRequestedEvent>
{
    private readonly CreateReservationHandler _handler;
    private readonly ILogger<ReserveStockRequestedConsumer> _log;

    public ReserveStockRequestedConsumer(
        CreateReservationHandler handler,
        ILogger<ReserveStockRequestedConsumer> log)
    {
        _handler = handler;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ReserveStockRequestedEvent> context)
    {
        var msg = context.Message;

        // v10: scope by CorrelationId. The Aspire dashboard's log view filters on this so every
        // log line emitted while handling this reservation joins the broader saga trail.
        using var _ = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = msg.CorrelationId,
            ["ReservationId"] = msg.ReservationId,
            ["OrderId"] = msg.OrderId
        });

        Telemetry.ReservationsRequested.Add(1, new KeyValuePair<string, object?>("line_count", msg.Lines.Count));
        LogReceived(_log, msg.ReservationId, msg.OrderId, msg.Lines.Count);

        var cmd = new CreateReservationCommand(
            msg.CorrelationId,
            msg.ReservationId,
            msg.OrderId,
            msg.Lines.Select(l => new ReservationCommandLine(l.ProductId, l.Quantity)).ToList());

        await _handler.HandleAsync(cmd, context.CancellationToken);
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Reservation request received: {ReservationId} for order {OrderId} ({LineCount} lines).")]
    private static partial void LogReceived(ILogger logger, Guid reservationId, int orderId, int lineCount);
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using KurrentDB.Client;
using SimpleStore.Inventory.API.Domain;

namespace SimpleStore.Inventory.API.EventStore;

// The ONLY file in this service that imports KurrentDB.Client.
// Swap this single class to swap event stores.
//
// Stream naming convention: "<category>-<aggregateId>" (e.g. "deliveryNote-{guid}").
// The category prefix is what enables KurrentDB's $by_category projection and the
// $ce-<category> link stream. We don't depend on those projections in v7; we use
// $all with a stream-name prefix filter, which is the simplest correct approach.
public sealed class KurrentEventStore : IEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly KurrentDBClient _client;
    private readonly EventTypeRegistry _types;

    public KurrentEventStore(KurrentDBClient client, EventTypeRegistry types)
    {
        _client = client;
        _types = types;
    }

    public async Task AppendAsync(
        string streamName,
        IReadOnlyList<IInventoryDomainEvent> events,
        AppendCondition condition,
        CancellationToken ct)
    {
        var data = events.Select(e =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType(), JsonOptions);
            return new EventData(
                eventId: Uuid.NewUuid(),
                type: _types.WireTypeFor(e),
                data: bytes);
        });

        try
        {
            switch (condition)
            {
                case AppendCondition.NoStreamCondition:
                    await _client.AppendToStreamAsync(
                        streamName,
                        StreamState.NoStream,
                        data,
                        cancellationToken: ct);
                    break;

                case AppendCondition.StreamRevision rev:
                    await _client.AppendToStreamAsync(
                        streamName,
                        StreamState.StreamRevision(rev.ExpectedRevision),
                        data,
                        cancellationToken: ct);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported append condition: {condition}");
            }
        }
        catch (WrongExpectedVersionException ex)
        {
            throw new ConcurrencyConflictException(streamName, ex);
        }
    }

    public async IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(
        string[] streamNamePrefixes,
        EventStorePosition? fromPosition,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var start = fromPosition.HasValue
            ? FromAll.After(new Position(fromPosition.Value.CommitPosition, fromPosition.Value.PreparePosition))
            : FromAll.Start;

        var filter = new SubscriptionFilterOptions(
            StreamFilter.Prefix(streamNamePrefixes));

        await using var subscription = _client.SubscribeToAll(
            start,
            filterOptions: filter,
            cancellationToken: ct);

        // KurrentDB emits a CaughtUp marker once the subscription reaches the live tail. Before it,
        // we are replaying history (cold start) and stamp IsLive=false so the projector suppresses
        // integration-event publishing; after it, events are live and IsLive=true.
        var caughtUp = false;
        await foreach (var message in subscription.Messages.WithCancellation(ct))
        {
            switch (message)
            {
                case StreamMessage.Event evt:
                    yield return ToEnvelope(evt.ResolvedEvent, caughtUp);
                    break;
                case StreamMessage.CaughtUp:
                    caughtUp = true;
                    break;
            }
        }
    }

    public async IAsyncEnumerable<EventEnvelope> ReadStreamAsync(
        string streamName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var result = _client.ReadStreamAsync(
            Direction.Forwards,
            streamName,
            StreamPosition.Start,
            cancellationToken: ct);

        if (await result.ReadState == ReadState.StreamNotFound)
            yield break;

        await foreach (var resolved in result.WithCancellation(ct))
        {
            yield return ToEnvelope(resolved, isLive: false);
        }
    }

    private EventEnvelope ToEnvelope(ResolvedEvent resolved, bool isLive)
    {
        var data = resolved.Event.Data;
        IInventoryDomainEvent? domainEvent = null;

        var clrType = _types.ClrTypeFor(resolved.Event.EventType);
        if (clrType is not null)
        {
            domainEvent = JsonSerializer.Deserialize(data.Span, clrType, JsonOptions)
                as IInventoryDomainEvent;
        }

        return new EventEnvelope
        {
            EventId = resolved.Event.EventId.ToGuid(),
            Type = resolved.Event.EventType,
            StreamName = resolved.Event.EventStreamId,
            Data = data,
            Position = resolved.OriginalPosition is { } p
                ? new EventStorePosition(p.CommitPosition, p.PreparePosition)
                : null,
            DomainEvent = domainEvent,
            IsLive = isLive,
        };
    }
}

# API & Event Versioning Policy

Three streams in SimpleStore carry cross-boundary contracts; each has its own versioning rule. The aim of this doc is that whoever is about to change one of them reads one page, not five.

---

## 1. HTTP APIs

**URL shape:** `/api/v{N}/<service>/<resource>` — version is a URL segment, never a header or query parameter.

**Library:** `Asp.Versioning.Http` (URL-segment reader). Set up once in `SimpleStore.ServiceDefaults/ApiVersioningExtensions.cs` and called via `builder.AddSimpleStoreApiVersioning()` from each backend's `Program.cs`.

**Gateway:** YARP routes match the versioned path verbatim and forward without transform (`src/SimpleStore.Gateway/appsettings.json`). Backends serve `/api/v{version}/...` natively.

**OpenAPI:** one document per version. `AddOpenApi("v1")` → `/openapi/v1.json`. To add v2: `AddOpenApi("v2", ...)` → `/openapi/v2.json`.

### When to bump

| Change | New version? |
|---|---|
| Adding a new endpoint | No — same `Vn` |
| Adding a new optional query parameter or response field | No — same `Vn` |
| Adding a new required request field | **Yes** — `Vn+1` |
| Removing or renaming an endpoint, parameter, or field | **Yes** — `Vn+1` |
| Changing the type or meaning of an existing field | **Yes** — `Vn+1` |
| Changing the HTTP status code returned in a given case | **Yes** — `Vn+1` |
| Tightening a validation rule (e.g. max length lowered) | **Yes** — `Vn+1` |

### How to bump

1. Add the new version to the relevant `MapApiVnGroup` (the v1 helper in `ApiVersioningExtensions.cs` shows the shape — clone it as `MapApiV2Group` or extend it to accept the version explicitly).
2. On each new endpoint: `.MapToApiVersion(new ApiVersion(2, 0))`.
3. Add a v2 OpenAPI document: `builder.Services.AddOpenApi("v2", ...)`.
4. Add a v2 gateway route to `src/SimpleStore.Gateway/appsettings.json` matching `/api/v2/<service>/{**catch-all}` (or per-route if auth policy differs).
5. Keep v1 endpoints live in parallel — do not delete v1 in the same commit that introduces v2.

### Deprecation

When v1 is on its way out:

```csharp
versionSet.HasDeprecatedApiVersion(new ApiVersion(1, 0));
```

Asp.Versioning emits the `Sunset` HTTP header on every v1 response so well-behaved clients can plan their migration. Keep v1 running until traffic falls to ~zero, then delete the endpoints + the gateway route + the v1 OpenAPI doc in one commit.

---

## 2. Integration events (RabbitMQ via MassTransit)

**Contract carrier:** `SimpleStore.Contracts`. Every event record is `Vn`-suffixed, carries an `int Version` field with a default initializer, and pins its wire URN via `[MessageUrn]`. The wire URN — not the CLR type name — is the cross-service identifier.

```csharp
[MessageUrn("urn:message:SimpleStore.Contracts:OrderSubmittedEvent")]
public sealed record OrderSubmittedEventV1
{
    public int Version { get; init; } = 1;
    // ... fields
}
```

### When to bump

| Change | New version? |
|---|---|
| Adding a new optional field (with a sensible default) | No — same `Vn` |
| Renaming a field | **Yes** — `Vn+1` |
| Removing a field | **Yes** — `Vn+1` |
| Changing a field's type | **Yes** — `Vn+1` |
| Changing the meaning of a field while keeping the same name | **Yes** — `Vn+1` |
| Splitting one event into two | **Yes** — new event type (don't repurpose `Vn`) |

Within a `Vn`, additive changes are safe because System.Text.Json (Web defaults) fills missing properties with the C# default initializer on the consumer side, and silently drops unknown properties on the deserialize side. Anything else needs a new version.

### How to bump

1. Add a new record under `SimpleStore.Contracts` with the `Vn+1` suffix. **Do not** modify or delete the `Vn` record — it must still deserialize for events already on the bus or in any consumer's inbox.
2. Give the new record its own `[MessageUrn]` — typically the same scheme with `V2` in the type segment (e.g. `urn:message:SimpleStore.Contracts:OrderSubmittedEventV2`). The URN string is what consumers route on; never reuse a URN for a different shape.
3. Set `public int Version { get; init; } = 2;` on the new record.
4. Update each publisher: publish the latest version it knows about.
5. Update each consumer: implement `IConsumer<NewEventV2>` alongside `IConsumer<OldEventV1>` for the transition window. The `int Version` field lets a single consumer branch internally if shapes are close enough to share code, but separate `IConsumer<T>` implementations are usually cleaner.
6. Keep `Vn` consumers live until every publisher is on `Vn+1` and the `Vn` queue has drained.

### Wire-format guarantee

Existing wire URNs are pinned to the pre-v11 MassTransit default (`urn:message:SimpleStore.Contracts:<TypeName>` from the original, un-suffixed type names). They stay that way forever — renaming the CLR type with a `Vn` suffix did **not** change the wire string. Don't change a pinned URN; ship a new type with a new URN instead.

---

## 3. Inventory domain events (KurrentDB)

**Contract carrier:** `src/SimpleStore.Inventory.API/Domain/**/Events/*.cs`. These are **internal** to the Inventory bounded context — they belong to KurrentDB streams and never appear in `SimpleStore.Contracts`. The convention has been in place since v7.

**Wire string format:** `simplestore.<context>.<aggregate>.<verb>.v{N}` — e.g. `simplestore.inventory.reservation.reserved.v1`. Stored in `EventTypeRegistry` (`src/SimpleStore.Inventory.API/EventStore/EventTypeRegistry.cs`) as a bidirectional `Type ↔ string` map.

### When to bump

Same table as §2 — additive within `Vn`, anything else → `Vn+1`. The difference is that **historic events live forever** in KurrentDB: a v1 event written to a stream in production today must still be deserializable a decade from now. The registry stays multi-version forever.

### How to bump (V2 introduction recipe)

The 5-step recipe is also pinned at the top of `EventTypeRegistry.cs`:

1. **Add the new record** under `Domain/<Aggregate>/Events/<Verb>V2.cs`. Keep the V1 record — it must still deserialize for every historic event in the stream.
2. **Register both wire strings** in `EventTypeRegistry`:
   ```csharp
   public const string StockReservedV1Type = "simplestore.inventory.reservation.reserved.v1";
   public const string StockReservedV2Type = "simplestore.inventory.reservation.reserved.v2";
   ```
   and the matching entries in `_clrToWire` / `_wireToClr`.
3. **Add an `Apply` overload** in `InventoryProjector` and a switch case in `InventoryProjectionService.ApplyOneAsync` so the new shape gets projected.
4. **Optionally implement `IEventUpcaster<StockReservedV1, StockReservedV2>`** and resolve it from DI in the projector. This lets a cold-start replay convert historic V1 events into V2 shape on the fly so only the V2 `Apply` is needed. Skip this if the projector branches on both versions explicitly.
5. **Old replicas keep working.** They see V2 events on the wire, find no entry in their (older) `EventTypeRegistry`, log a warning ("Projector skipped unknown event type..."), and checkpoint past — the projector is forward-compatible by design. The `simplestore.inventory.projector.unknown_events` metric (v11) alerts on this happening — if it ticks during a deploy, a newer replica is somewhere else writing V2s before this replica caught up.

### Replay vs upcast

- **Upcast** (cheap, in-process): use when V2 is a small lossless reshape of V1 — adding a derived field, restructuring nested types. The `IEventUpcaster<TOld, TNew>` transform runs per-event during projection.
- **Full replay** (heavier, ops-driven): use when V2 requires read-model schema changes that can't be derived from V1 alone. Wipe the read DB tables, restart the projector, it replays from `FromAll.Start` and rebuilds the read model from the events as it streams them. Documented in `InventoryProjectionService.cs` as the cold-start procedure.

---

## Summary table

|   | HTTP APIs | Integration events | Domain events (Inventory) |
|---|---|---|---|
| Where | `/api/v{N}/<service>/...` | `SimpleStore.Contracts` | `Inventory.API/Domain/**/Events/` |
| Wire identifier | URL segment | `[MessageUrn]` | `EventTypeRegistry` wire string |
| Library | `Asp.Versioning.Http` | `MassTransit.Abstractions` (`[MessageUrn]`) | hand-rolled registry |
| Forward compat | Server returns 404 for unknown version | System.Text.Json drops unknown fields | Projector skips unknown wire types + bumps `unknown_events` counter |
| Multi-version coexistence | Same backend hosts `vN` and `vN+1` simultaneously | Separate types + URNs; queues are auto-bound per type | Both `Vn` types in the registry; projector dispatches by CLR type |
| Migration triggers replay? | No (HTTP is stateless) | No (consumers handle on demand) | Sometimes — see §3 "Replay vs upcast" |

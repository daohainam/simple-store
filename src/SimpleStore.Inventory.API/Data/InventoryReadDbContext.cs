using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data.ReadModels;

namespace SimpleStore.Inventory.API.Data;

// CQRS read side. The write side is the KurrentDB event store; this context
// only ever sees projected rows produced by InventoryProjectionService.
//
// To rebuild the read model from scratch: truncate every table here, restart
// the service, and the projector will replay from FromAll.Start.
public class InventoryReadDbContext : DbContext
{
    public InventoryReadDbContext(DbContextOptions<InventoryReadDbContext> options) : base(options) { }

    public DbSet<DeliveryNoteRow> DeliveryNotes => Set<DeliveryNoteRow>();
    public DbSet<DeliveryNoteLineRow> DeliveryNoteLines => Set<DeliveryNoteLineRow>();
    public DbSet<ReceiptNoteRow> ReceiptNotes => Set<ReceiptNoteRow>();
    public DbSet<ReceiptNoteLineRow> ReceiptNoteLines => Set<ReceiptNoteLineRow>();
    public DbSet<ReservationRow> Reservations => Set<ReservationRow>();
    public DbSet<ReservationLineRow> ReservationLines => Set<ReservationLineRow>();
    public DbSet<StockLevelRow> StockLevels => Set<StockLevelRow>();
    public DbSet<StockMovementRow> StockMovements => Set<StockMovementRow>();
    public DbSet<ProjectionCheckpointRow> ProjectionCheckpoints => Set<ProjectionCheckpointRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DeliveryNoteRow>(e =>
        {
            e.ToTable("delivery_notes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Date).HasColumnType("date");
            e.Property(x => x.Reference).HasMaxLength(100);
            e.HasMany(x => x.Lines)
                .WithOne()
                .HasForeignKey(l => l.DeliveryNoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeliveryNoteLineRow>(e =>
        {
            e.ToTable("delivery_note_lines");
            e.HasKey(x => new { x.DeliveryNoteId, x.LineNumber });
        });

        builder.Entity<ReceiptNoteRow>(e =>
        {
            e.ToTable("receipt_notes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Date).HasColumnType("date");
            e.Property(x => x.Reference).HasMaxLength(100);
            e.HasMany(x => x.Lines)
                .WithOne()
                .HasForeignKey(l => l.ReceiptNoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReceiptNoteLineRow>(e =>
        {
            e.ToTable("receipt_note_lines");
            e.HasKey(x => new { x.ReceiptNoteId, x.LineNumber });
        });

        builder.Entity<ReservationRow>(e =>
        {
            e.ToTable("reservations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.OrderId);
            e.HasMany(x => x.Lines)
                .WithOne()
                .HasForeignKey(l => l.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReservationLineRow>(e =>
        {
            e.ToTable("reservation_lines");
            e.HasKey(x => new { x.ReservationId, x.LineNumber });
        });

        builder.Entity<StockLevelRow>(e =>
        {
            e.ToTable("stock_levels");
            e.HasKey(x => x.ProductId);
            // ProductId is a SOFT REFERENCE to Catalog.Products.Id — Inventory does NOT
            // own this identifier and must never auto-generate one.
            e.Property(x => x.ProductId).ValueGeneratedNever();
        });

        builder.Entity<StockMovementRow>(e =>
        {
            e.ToTable("stock_movements");
            e.HasKey(x => x.Id);
            e.Property(x => x.MovementType).HasMaxLength(32);
            e.HasIndex(x => new { x.ProductId, x.OccurredAt })
                .HasDatabaseName("ix_stock_movements_product_occurred")
                .IsDescending(false, true);
            // Additional index to support queries filtered by movement type (e.g. reservations only).
            e.HasIndex(x => new { x.ProductId, x.MovementType })
                .HasDatabaseName("ix_stock_movements_product_type");
        });

        builder.Entity<ProjectionCheckpointRow>(e =>
        {
            e.ToTable("projection_checkpoints");
            e.HasKey(x => x.ProjectionName);
            e.Property(x => x.ProjectionName).HasMaxLength(64);
        });

        // MassTransit transactional outbox. v8 wires Inventory onto the bus: the projector
        // publishes StockReservedEventV1 / StockLevelChangedEventV1 inside the same Postgres
        // transaction as the read-model write, and the ReserveStockRequestedConsumer uses the
        // inbox for exactly-once consumption.
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();
    }
}

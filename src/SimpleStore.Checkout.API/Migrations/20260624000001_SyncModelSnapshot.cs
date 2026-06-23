using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleStore.Checkout.API.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelSnapshot : Migration
    {
        /// <inheritdoc />
        /// No schema change is required. MassTransit 9.1.2 now declares
        /// HasAlternateKey("MessageId", "ConsumerId") explicitly on InboxState,
        /// which was previously only implied via HasPrincipalKey on the OutboxMessage
        /// FK. The AK_InboxState_MessageId_ConsumerId constraint already exists in the
        /// database (created by InitialCreate), so Up/Down are intentionally empty.
        /// This migration exists solely to bring the model snapshot hash into sync with
        /// the runtime model that MassTransit 9.1.2 builds.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

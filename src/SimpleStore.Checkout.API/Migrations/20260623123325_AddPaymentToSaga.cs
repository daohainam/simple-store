using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleStore.Checkout.API.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentToSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "checkout_saga_state",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentTimeoutTokenId",
                table: "checkout_saga_state",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "checkout_saga_state");

            migrationBuilder.DropColumn(
                name: "PaymentTimeoutTokenId",
                table: "checkout_saga_state");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleStore.Order.API.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use gen_random_uuid() so any pre-existing rows get unique values; otherwise the
            // unique index below would fail on multiple rows defaulting to Guid.Empty. New rows
            // inserted by OrderService always set CorrelationId explicitly via Guid.NewGuid().
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "Orders",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CorrelationId",
                table: "Orders",
                column: "CorrelationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_CorrelationId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "Orders");
        }
    }
}

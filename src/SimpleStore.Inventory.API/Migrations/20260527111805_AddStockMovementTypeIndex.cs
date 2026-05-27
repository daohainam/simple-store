using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleStore.Inventory.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStockMovementTypeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_product_type",
                table: "stock_movements",
                columns: new[] { "ProductId", "MovementType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_movements_product_type",
                table: "stock_movements");
        }
    }
}

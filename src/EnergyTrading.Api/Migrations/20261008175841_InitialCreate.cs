using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTrading.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketQuotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PricePerMwh = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketQuotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientTradeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Portfolio = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    QuantityMwh = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    PricePerMwh = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    DeliveryStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveryEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketQuotes_Product_ObservedAt",
                table: "MarketQuotes",
                columns: new[] { "Product", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_PublishedAt_CreatedAt",
                table: "Outbox",
                columns: new[] { "PublishedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ClientTradeId",
                table: "Trades",
                column: "ClientTradeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Portfolio_Product_Status",
                table: "Trades",
                columns: new[] { "Portfolio", "Product", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketQuotes");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "Trades");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTrading.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitTradeInstruments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketQuotes_Product_ObservedAt",
                table: "MarketQuotes");

            migrationBuilder.AddColumn<int>(
                name: "Commodity",
                table: "Trades",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Trades",
                type: "character varying(11)",
                maxLength: 11,
                nullable: false,
                defaultValue: "UNSPECIFIED");

            migrationBuilder.AddColumn<int>(
                name: "AggregateVersion",
                table: "Outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Commodity",
                table: "MarketQuotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "MarketQuotes",
                type: "character varying(11)",
                maxLength: 11,
                nullable: false,
                defaultValue: "UNSPECIFIED");

            // Preserve historical records without guessing commodity or currency from product names.
            // Backfill the two lifecycle versions before enforcing per-trade uniqueness.
            migrationBuilder.Sql("""
                UPDATE "Outbox" SET "AggregateVersion" = CASE
                    WHEN "Payload"::jsonb ->> 'eventType' = 'TradeCreated' THEN 1
                    WHEN "Payload"::jsonb ->> 'eventType' = 'TradeCancelled' THEN 2
                    ELSE 0 END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_AggregateId_AggregateVersion",
                table: "Outbox",
                columns: new[] { "AggregateId", "AggregateVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketQuotes_Product_Commodity_Currency_ObservedAt",
                table: "MarketQuotes",
                columns: new[] { "Product", "Commodity", "Currency", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_AggregateId_AggregateVersion",
                table: "Outbox");

            migrationBuilder.DropIndex(
                name: "IX_MarketQuotes_Product_Commodity_Currency_ObservedAt",
                table: "MarketQuotes");

            migrationBuilder.DropColumn(
                name: "Commodity",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "AggregateVersion",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Commodity",
                table: "MarketQuotes");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "MarketQuotes");

            migrationBuilder.CreateIndex(
                name: "IX_MarketQuotes_Product_ObservedAt",
                table: "MarketQuotes",
                columns: new[] { "Product", "ObservedAt" });
        }
    }
}

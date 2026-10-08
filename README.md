# Energy Trading Management System

A C# / .NET 8 backend for recording electricity and gas trades, tracking net positions, ingesting market quotes, and publishing trade lifecycle events to Kafka.

## Run locally

Requirements: Docker Engine with Compose. For development outside containers, install the .NET 8 SDK.

```sh
docker compose up --build -d
curl --fail http://localhost:8080/health/ready
```

Development Swagger UI is at `/swagger`. Compose starts PostgreSQL 16, Kafka 3.9 (single-node KRaft), a one-shot EF migration, and the API. Local database credentials are development defaults; never reuse them in deployment. Set `POSTGRES_PASSWORD` in an ignored `.env` before the database volume is first created to override them. Changing it later requires changing the database user password too.

```sh
curl --fail -X POST http://localhost:8080/api/trades \
  -H 'Content-Type: application/json' \
  -d '{"clientTradeId":"trade-001","portfolio":"DE-POWER","product":"POWER-DE-2027-01-01-H00","commodity":"Electricity","currency":"EUR","side":"Buy","quantityMwh":10,"pricePerMwh":50,"deliveryStart":"2027-01-01T00:00:00Z","deliveryEnd":"2027-01-01T01:00:00Z"}'
curl --fail 'http://localhost:8080/api/positions?portfolio=DE-POWER'

# A gas trade uses the same endpoint and energy unit, with an explicit commodity.
curl --fail -X POST http://localhost:8080/api/trades \
  -H 'Content-Type: application/json' \
  -d '{"clientTradeId":"gas-001","portfolio":"GAS-BOOK","product":"TTF","commodity":"Gas","currency":"EUR","side":"Sell","quantityMwh":20,"pricePerMwh":30,"deliveryStart":"2027-01-01T00:00:00Z","deliveryEnd":"2027-01-02T00:00:00Z"}'
```

## Development and tests

```sh
docker compose up -d postgres kafka
export ConnectionStrings__Trading='Host=localhost;Database=energy;Username=energy;Password=local-development-only'
dotnet run --project src/EnergyTrading.Api -- --migrate
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/EnergyTrading.Api
```

In another terminal:

```sh
docker compose exec -T postgres createdb -U energy energy_test # once
export TEST_DATABASE='Host=localhost;Database=energy_test;Username=energy;Password=local-development-only'
dotnet test
```

For an end-to-end check including Kafka, run `python3 scripts/smoke.py` while the local services and API are running. The script creates uniquely named test trades and quotes.

The integration tests use a dedicated PostgreSQL database and unique records; they do not delete existing data. Without `TEST_DATABASE`, they are explicitly skipped. CI provides PostgreSQL and runs them. Unit tests cover validation, negative electricity prices, position signs, and cancelled trade exclusion. `dotnet build -c Release` builds the entire solution.

## How recording works

A client submits an already agreed trade to `POST /api/trades`. The API validates identifiers, commodity/currency, quantity, price and UTC delivery interval. EF Core writes a `Trades` row and its `TradeCreated` outbox row in one PostgreSQL transaction, then returns HTTP 201 with the trade ID. A duplicate clientTradeId returns 409 without inserting another trade or event. The background publisher delivers the saved event to Kafka and marks it published after acknowledgement. Positions are calculated from active trade rows; cancellation preserves the original trade and writes a separate lifecycle event. The API records deals submitted by users or upstream systems; it does not connect to an exchange, match orders, settle payments, or physically dispatch energy.

For example, buying 10 MWh of electricity at EUR 50/MWh records 10 MWh and EUR -500 cash flow. Selling 20 MWh of gas at EUR 30/MWh records -20 MWh and EUR +600 cash flow in a separate gas position.

## Upgrade existing installations

This revision changes the request contract: callers must send commodity and currency; latest-quote queries must include both. Stop old API instances/publishers before applying the new migration, then start the new version. Do not roll old writers alongside the new outbox schema. The migration keeps existing trades/quotes as `commodity: Unknown`, `currency: UNSPECIFIED`, and backfills outbox lifecycle versions. It intentionally does not infer metadata from product names. Reconcile those legacy records from authoritative trade data before using them in commodity/currency reporting. New API requests cannot create unknown records. Deployments of this revision require a coordinated maintenance window; the AWS workflow now defaults to maintenance mode and stops API tasks before migration. If migration fails in maintenance mode, the service stays stopped for diagnosis.

## API

| Method | Route | Behavior |
|---|---|---|
| POST | `/api/trades` | Record a trade; unique clientTradeId, 201 or 409 |
| GET | `/api/trades/{id}` | Retrieve trade or 404 |
| GET | `/api/trades?portfolio=P&page=1&pageSize=50` | List trades, pageSize limited to 200 |
| POST | `/api/trades/{id}/cancel` | Cancel once; sequential repeats are idempotent |
| GET | `/api/positions?portfolio=P` | Net MWh and cash flow by portfolio/product/commodity/currency/delivery interval |
| POST | `/api/market-data` | Store product, commodity, currency, pricePerMwh, observedAt |
| GET | `/api/market-data/{id}` | Retrieve a quote |
| GET | `/api/market-data/latest?product=P&commodity=Gas&currency=EUR` | Latest quote for a product |
| GET | `/health/live`, `/health/ready` | Process and database/schema health |

New trades and quotes require `commodity` (`Electricity` or `Gas`) and a three-letter uppercase `currency` code. Both commodities use MWh of energy; gas volumes in cubic metres must be converted using the appropriate calorific value before submission. There is no currency conversion. `side` and `pricePerMwh` must be supplied explicitly; zero prices are valid. All timestamps use UTC. Prices can be negative. Quantities are positive MWh. Cash flow is negative for buys and positive for sells; it is a trade cash flow measure, not mark-to-market P&L. Positions group by portfolio, product, commodity, currency, deliveryStart and deliveryEnd, so different delivery periods and currencies cannot be accidentally netted. This groups identical delivery intervals; it does not split overlapping hourly/daily contracts into common time buckets. Trades are immutable except cancellation. Concurrent cancellation conflicts return 409; retry safely.

Production requires an `ApiKey`, sent as `X-Api-Key` on API requests. Development permits requests without a key unless one is configured. Health probes are unauthenticated. Use HTTPS and restricted network ingress in deployment. This personal-project authentication is a starting point; multi-user production use needs OIDC, portfolio authorization, audit policy, and key rotation.

## Architecture and events

`Domain` contains entities/contracts; `Application` implements validation and lifecycle rules; `Infrastructure` contains EF persistence and the Kafka outbox publisher. PostgreSQL migrations are committed under `src/EnergyTrading.Api/Migrations`. Run migrations once before starting replicas; startup deliberately does not create schemas implicitly.

Trade updates and outbox entries commit in one database transaction. A hosted worker publishes to `energy.trades.v1`, using the trade ID as the Kafka key and a PostgreSQL transaction advisory lock to coordinate replicas. Delivery is **at least once**: consumers must deduplicate `eventId`. New event envelopes use `schemaVersion: 2`, include `eventType`, `aggregateVersion`, `occurredAt`, and `trade`, and use camelCase properties with string enums. Lifecycle versions are 1 for creation and 2 for cancellation. The publisher selects only the earliest pending version per trade; cancellation cannot overtake an unpublished creation even after clock reversal. Historical schema-v1 events retain their original payloads (PascalCase trade fields and numeric enums); consumers of the existing topic must handle both versions. Failed publishes remain pending and retry. Cancellation events follow creation for each trade. Kafka failure does not discard accepted trades; database readiness does not imply Kafka readiness. Monitor pending outbox age in deployment. Published outbox records are retained; add an operational retention job as needed.

```sh
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka:29092 --topic energy.trades.v1 --from-beginning
```

Local Kafka is plaintext and has no durable volume; it is only for development. AWS infrastructure instructions are in [infra/aws/README.md](infra/aws/README.md). No AWS resources are deployed by local setup. GitHub CI runs build, unit/integration tests and a Docker build; deployment is a separate manually triggered workflow.

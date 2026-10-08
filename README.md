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
  -d '{"clientTradeId":"trade-001","portfolio":"DE-POWER","product":"POWER-DE-2027-01-01-H00","side":"Buy","quantityMwh":10,"pricePerMwh":50,"deliveryStart":"2027-01-01T00:00:00Z","deliveryEnd":"2027-01-01T01:00:00Z"}'
curl --fail 'http://localhost:8080/api/positions?portfolio=DE-POWER'
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

The integration test uses a dedicated PostgreSQL database and unique records; it does not delete existing data. Without `TEST_DATABASE`, it is explicitly skipped. CI provides PostgreSQL and runs it. Unit tests cover validation, negative electricity prices, position signs, and cancelled trade exclusion. `dotnet build -c Release` builds the entire solution.

## API

| Method | Route | Behavior |
|---|---|---|
| POST | `/api/trades` | Record a trade; unique clientTradeId, 201 or 409 |
| GET | `/api/trades/{id}` | Retrieve trade or 404 |
| GET | `/api/trades?portfolio=P&page=1&pageSize=50` | List trades, pageSize limited to 200 |
| POST | `/api/trades/{id}/cancel` | Cancel once; sequential repeats are idempotent |
| GET | `/api/positions?portfolio=P` | Net MWh and cash flow by portfolio/product |
| POST | `/api/market-data` | Store product, pricePerMwh, observedAt |
| GET | `/api/market-data/{id}` | Retrieve a quote |
| GET | `/api/market-data/latest?product=P` | Latest quote for a product |
| GET | `/health/live`, `/health/ready` | Process and database/schema health |

All timestamps use UTC. Prices can be negative. Quantities are positive MWh. Cash flow is negative for buys and positive for sells; it is a trade cash flow measure, not mark-to-market P&L. Use delivery-specific product identifiers because positions group by portfolio and product, not delivery intervals. Trades are immutable except cancellation. Concurrent cancellation conflicts return 409; retry safely.

Production requires an `ApiKey`, sent as `X-Api-Key` on API requests. Development permits requests without a key unless one is configured. Health probes are unauthenticated. Use HTTPS and restricted network ingress in deployment. This personal-project authentication is a starting point; multi-user production use needs OIDC, portfolio authorization, audit policy, and key rotation.

## Architecture and events

`Domain` contains entities/contracts; `Application` implements validation and lifecycle rules; `Infrastructure` contains EF persistence and the Kafka outbox publisher. PostgreSQL migrations are committed under `Infrastructure/Migrations` or `Migrations`. Run migrations once before starting replicas; startup deliberately does not create schemas implicitly.

Trade updates and outbox entries commit in one database transaction. A hosted worker publishes to `energy.trades.v1`, using the trade ID as the Kafka key and a PostgreSQL transaction advisory lock to coordinate replicas. Delivery is **at least once**: consumers must deduplicate `eventId`. Event envelopes include `eventType`, `schemaVersion`, `occurredAt`, and `trade`; the v1 payload currently represents side/status as numeric enums (Buy/Active = 0, Sell/Cancelled = 1). Failed publishes remain pending and retry. Cancellation events follow creation for each trade. Kafka failure does not discard accepted trades; database readiness does not imply Kafka readiness. Monitor pending outbox age in deployment. Published outbox records are retained; add an operational retention job as needed.

```sh
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka:29092 --topic energy.trades.v1 --from-beginning
```

Local Kafka is plaintext and has no durable volume; it is only for development. AWS infrastructure instructions are in [infra/aws/README.md](infra/aws/README.md). No AWS resources are deployed by local setup. GitHub CI runs build, unit/integration tests and a Docker build; deployment is a separate manually triggered workflow.

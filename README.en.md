# Didar CRM — Automatic Request Assignment

[نسخهٔ فارسی](README.md)

An ASP.NET Core API registers requests. A background worker assigns them to eligible experts based on specialty, capacity, and priority. Two independent application instances can share one PostgreSQL database.

## Run the acceptance tests

Install the **.NET 10 SDK** and **Docker**, then run these commands in PowerShell from the repository root. You can also use an existing PostgreSQL 16 instance. The test role needs permission to create and drop temporary databases.

~~~powershell
docker run --detach --name didar-review-db --publish 127.0.0.1:54330:5432 --env POSTGRES_USER=didar --env POSTGRES_PASSWORD=didar-local --env POSTGRES_DB=postgres postgres:16
docker exec didar-review-db pg_isready -U didar -d postgres
~~~

Once PostgreSQL reports `accepting connections`, run the suite. To restart an existing stopped container, use `docker start didar-review-db`.

~~~powershell
$env:DidarTests__Postgres = 'Host=127.0.0.1;Port=54330;Database=postgres;Username=didar;Password=didar-local'
dotnet test tests/Didar.IntegrationTests/Didar.IntegrationTests.csproj
~~~

The fixture creates a temporary database on PostgreSQL, applies the migrations, seeds the scenarios, and drops the database at the end. The multi-instance tests launch two independent application processes. No manual seed data is needed for the tests.

| Scenario | Test and expected result |
| --- | --- |
| Expert selection | [AssignmentTests](tests/Didar.IntegrationTests/AssignmentTests.cs) verifies that the eligible expert with the lowest load-to-capacity ratio wins. Without an eligible expert, the request stays `Pending`. |
| Twenty requests, capacity five | [MultiInstanceAssignmentTests](tests/Didar.IntegrationTests/MultiInstanceAssignmentTests.cs) seeds 20 distinct pending requests. Two workers process the shared queue, leaving exactly 5 `Assigned` and 15 `Pending`. |
| Concurrent registration with one key | [ConcurrentRegistrationTests](tests/Didar.IntegrationTests/ConcurrentRegistrationTests.cs) sends 20 HTTP calls with one `RequestKey` and verifies that they produce one stored request and one `RequestId`. |
| Assignment races | [DeliveryAcceptanceTests](tests/Didar.IntegrationTests/DeliveryAcceptanceTests.cs) and [MultiInstanceAssignmentTests](tests/Didar.IntegrationTests/MultiInstanceAssignmentTests.cs) cover single assignment and reranking experts after a conflict. |
| Capacity release, priority, and aging | [RequestLifecycleTests](tests/Didar.IntegrationTests/RequestLifecycleTests.cs) and [QueuePolicyTests](tests/Didar.IntegrationTests/QueuePolicyTests.cs) cover freeing capacity, urgent precedence, aging, and independent specialty queues. |

To run just the capacity-five scenario after setting the test connection string, use the following command.

~~~powershell
dotnet test tests/Didar.IntegrationTests/Didar.IntegrationTests.csproj --filter 'FullyQualifiedName~TwentyRequestsForCapacityFiveAssignExactlyFive'
~~~

## Optional API and Swagger setup

Use the same PostgreSQL container with a separate development database. EF Core creates the database and applies the migrations.

~~~powershell
$env:ConnectionStrings__Didar = 'Host=127.0.0.1;Port=54330;Database=didar_dev;Username=didar;Password=didar-local'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet tool install --global dotnet-ef --version 10.0.14
dotnet ef database update --project src/Didar.Api --startup-project src/Didar.Api
dotnet run --project src/Didar.Api --no-launch-profile -- --urls http://127.0.0.1:5080
~~~

[Swagger UI](http://127.0.0.1:5080/swagger/index.html) lists the registration, lookup, close, cancel, and expert management endpoints. To register a request manually, use an existing `specialtyId`. To observe automatic assignment, the specialty needs an active expert with an active specialty link and available capacity. The test fixture seeds only its temporary databases.

## Design decisions

- **Expert selection.** Load counts all `Assigned` requests for an expert across specialties. When ratios tie, an expert with no previous assignment or the oldest `LastAssignedAt` takes precedence, giving equally loaded experts a turn. The lowest `ExpertId` resolves the final tie deterministically.
- **Concurrency.** PostgreSQL enforces unique `RequestKey` values. Assignment uses a `Serializable` transaction with locks in specialty, expert, and request order. The request transition and the expert timestamp commit together. After a serialization failure, a fresh transaction selects the queue head and ranks the candidates again.
- **Queue policy.** Each specialty gives urgent requests precedence. After five minutes, a normal request is treated as urgent for ordering. Within a priority level, the oldest request wins, with request ID as the final tie-breaker. For example, a six-minute-old normal request precedes a one-minute-old urgent request in the same specialty. The policy defines no global ordering or wait bound across specialties competing for a shared expert.
- **Recovery.** The worker checks PostgreSQL for `Pending` requests at startup and periodically. Committed pending work remains available after a process restart.


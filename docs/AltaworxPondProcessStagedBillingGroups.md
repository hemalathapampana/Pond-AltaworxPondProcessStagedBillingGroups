## AltaworxPondProcessStagedBillingGroups Lambda – Technical Flow and API Documentation

### Overview

The `AltaworxPondProcessStagedBillingGroups` AWS Lambda processes staged Pond Billing Group data into final AMOP 2.0 tables. It:
- Listens to SQS page-level progress messages produced by the upstream “Get” phase
- Validates and transforms staged rows
- Merges/upserts transformed rows into final tables
- Tracks per-page progress in a DB table
- Can initialize processing when invoked without a `ServiceProviderId` (fan-out of page messages)

### Triggers and Integrations

- **Trigger**: SQS (messages from the “Get” phase and/or fan-out init)
- **DB**: Central AMOP 2.0 SQL database
- **Repositories**: `PondRepository`, `ServiceProviderRepository`
- **Infra Base**: `AwsFunctionBase` (logging, configuration, DB connections, cleanup)
- **Messaging**: `SqsService` (publishing messages)
- **Retry**: `RetryPolicyHelper` (SQL transient retry policy)

### Environment Variables

| Name | Required | Default | Description |
| ---- | -------- | ------- | ----------- |
| `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL` | Yes | — | SQS queue this Lambda enqueues processing messages to (page fan-out). |
| `PAGE_SIZE` | No | 10 | Number of staged rows per page fetched from staging. |
| `RECORDS_PER_CYCLE` | No | — | Chunk size for DB merge operations within a page. |
| `CENTRAL_DB_CONNECTION_STRING` | Implicit via base | — | Connection string used by repositories (managed via `AwsFunctionBase`). |

### SQS Message Attributes

| Attribute | Type | Required | Description |
| --------- | ---- | -------- | ----------- |
| `SERVICE_PROVIDER_ID` | Number | In processing mode | The service provider whose rows are being processed. |
| `PAGE_NUMBER` | Number | In processing mode | The page identifier being processed. |
| `IS_SUCCESSFUL` | Boolean | No | From upstream “Get” phase; can short-circuit failed pages. |

### Data Stores and Procedures (Representative)

- Tables
  - `PondBillingGroupStaging` (input staging rows)
  - `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS` (page tracking: queued/processing/success/failed)
  - Final AMOP 2.0 Billing Group tables (target of merges)

- Procedures / Merge Ops (examples)
  - `UPDATE_POND_BILLING_GROUPS_FROM_STAGING`
  - `MERGE_POND_BILLING_GROUPS`

---

## High-Level Flow

1) SQS event arrives and is processed message-by-message in `FunctionHandler`.
2) If `ServiceProviderId` is missing or <= 0, initialization mode runs:
   - Seed page markers per Service Provider
   - Enqueue one SQS message per page to the processing queue
3) If `ServiceProviderId` is present, processing mode runs:
   - Load the staged rows for the given page
   - Validate and transform
   - Merge/upsert into final tables (optionally chunked)
   - Mark the page as processed (success/failure)
   - Optionally emit a downstream progress message

---

## Detailed Method Documentation

### FunctionHandler

- Signature: `Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)`
- Purpose: Main entry point. Orchestrates initialization mode vs processing mode on a per-message basis.
- Inputs
  - `SQSEvent sqsEvent`: Batched SQS trigger event
  - `ILambdaContext context`: Lambda runtime context
- Reads
  - Environment variables via `TryGetAllEnvironmentVariables`
  - SQS message attributes via `GetMessageValues`
- Writes
  - Logs (diagnostics)
  - May enqueue messages in initialization path (`InitProcessStagedBillingGroupPages`)
  - Database progress markers (indirectly through called methods)
- Returns: `Task` (no explicit return payload)
- Exceptions: Wrapped and logged; handler ensures `CleanUp` via `AwsFunctionBase`
- Behavior
  1. Initialize `AmopLambdaContext` via base `AwsFunctionBase` handler.
  2. Call `TryGetAllEnvironmentVariables` to load queue URL, `PAGE_SIZE`, `RECORDS_PER_CYCLE`.
  3. Validate SQS trigger payload; iterate each `SQSEvent.SQSMessage`.
  4. For each message, parse attributes via `GetMessageValues`:
     - `ServiceProviderId`, `PageNumber`, `IsSuccessful`
  5. If `ServiceProviderId` is not provided or <= 0 → call `InitializeProcessStagedBillingGroups`.
  6. Else → call `ProcessStagedBillingGroupsByServiceProviderId`.
  7. On exception, log and continue to next message; ensure `CleanUp` runs at the end.
- Idempotency & Concurrency
  - Per-message isolation; page-level dedupe is enforced in downstream calls (`GetPageFromPageToProcessTable`).
  - Safe to process messages in parallel Lambdas; page tracker prevents duplicate work.

### InitializeProcessStagedBillingGroups

- Signature: `Task InitializeProcessStagedBillingGroups(AmopLambdaContext context, ServiceProviderRepository serviceProviderRepository)`
- Purpose: Initialization mode; seeds page processing for all Pond service providers.
- Inputs
  - `AmopLambdaContext context`
  - `ServiceProviderRepository serviceProviderRepository`
- Reads
  - `GetAllServiceProviderIds(IntegrationType.Pond)`
  - Page tracker table state (via `PondRepository`)
- Writes
  - Page markers in `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS` (if missing)
  - Enqueued SQS messages per page via `InitProcessStagedBillingGroupPages`
- Behavior
  1. Enumerate all Pond service provider IDs.
  2. For each service provider:
     - `SeedBillingGroupPagesToProcessIfNeeded(context, serviceProviderId)` to ensure page markers exist.
     - `InitProcessStagedBillingGroupPages(context, serviceProviderId, page)` to enqueue processing messages.
- Failure Modes
  - If enumeration fails, logs and aborts initialization.
  - Per-SP failures are logged; subsequent SPs continue.
- Idempotency
  - Seeding is additive and safe to re-run; only missing markers are inserted.

### ProcessStagedBillingGroupsByServiceProviderId

- Signature: `Task ProcessStagedBillingGroupsByServiceProviderId(AmopLambdaContext context, SqsValues sqsValues)`
- Purpose: Processes a single page for a specific service provider.
- Inputs
  - `AmopLambdaContext context`
  - `SqsValues sqsValues` (contains `ServiceProviderId`, `PageNumber`, `IsSuccessful`)
- Reads
  - Staged rows from `PondBillingGroupStaging`
  - Page tracker state
- Writes
  - Final tables via merge procedures
  - Page tracker update (success/failure)
  - Optional downstream progress message
- Behavior
  1. Instantiate repositories (`PondRepository`, `ServiceProviderRepository`) via `InitializeRepositories`.
  2. Create `sqlTransientRetryPolicy` via `RetryPolicyHelper`.
  3. Call `ProcessBillingGroupPage(context, sqsValues, sqlTransientRetryPolicy)` to perform the actual work.
- Failure Modes
  - Any exception marks page as failed (if page claimed) and is logged.
- Idempotency
  - Multiple deliveries of the same page are guarded by page tracker status checks.

### ProcessBillingGroupPage

- Signature: `Task ProcessBillingGroupPage(AmopLambdaContext context, SqsValues sqsValues, IAsyncPolicy sqlTransientRetryPolicy)`
- Purpose: Encapsulates the unit-of-work for a single page.
- Inputs
  - `ServiceProviderId`, `PageNumber`, `IsSuccessful`
- Behavior
  1. `GetPageFromPageToProcessTable(serviceProviderId, pageNumber)` to retrieve and claim page context; bail out if already completed or in-progress by another worker.
  2. If `IsSuccessful` from upstream is false, short-circuit: `MarkPageAsProcessed(..., isSuccessful: false)` and optionally emit progress; return.
  3. `LoadStagedPageBatch(serviceProviderId, pageNumber, pageSize)` to obtain staged rows.
  4. `ValidateAndTransformStagedBillingGroups(stagedRows)` to prepare rows for merging.
  5. `MergeBillingGroupsIntoFinalTables(transformedRows)` using stored procedures or `SqlBulkCopy + MERGE`; chunk by `RECORDS_PER_CYCLE` if set.
  6. `MarkPageAsProcessed(serviceProviderId, pageNumber, isSuccessful: true)`.
  7. `EmitProgressMessage(serviceProviderId, pageNumber, isSuccessful: true)` if downstream coordination is enabled.
- Error Handling
  - On error, mark page as failed and rethrow or log; retryable exceptions are handled by `sqlTransientRetryPolicy` around DB calls.
- Idempotency & Concurrency
  - Page claim-and-check prevents duplicate work; merges are idempotent upserts.

### GetPageFromPageToProcessTable

- Signature: `Task<PageContext> GetPageFromPageToProcessTable(long serviceProviderId, int pageNumber)`
- Purpose: Reads and optionally claims page work; prevents duplicate concurrent processing.
- Inputs
  - `serviceProviderId`, `pageNumber`
- Reads
  - `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS`
- Writes
  - May set status to "Processing" and stamp timestamps/worker identifiers
- Behavior
  1. Load page record. If status is `Success` or `Failed`, return a no-op indicator.
  2. If status is `Queued` (or null), set status to `Processing` with a lease/lock.
  3. Return page context (status, timestamps, row counts, etc.).
- Failure Modes
  - If the page record is missing, function may seed or error based on policy.
- Idempotency
  - Uses atomic update semantics to claim work; safe for concurrent lambdas.

### LoadStagedPageBatch

- Signature: `Task<IReadOnlyList<StagedBillingGroupRow>> LoadStagedPageBatch(long serviceProviderId, int pageNumber, int pageSize)`
- Purpose: Loads staged billing group rows for a single page and service provider.
- Inputs
  - `serviceProviderId`, `pageNumber`, `pageSize`
- Reads
  - `PondBillingGroupStaging`
- Returns
  - Materialized list or `DataTable` of staging rows for the page
- Behavior
  1. Query staging table filtered by SP and page.
  2. Shape results into memory-friendly form (DTOs or `DataTable`).
  3. Optionally include metadata (source record IDs, timestamps, audit fields).
- Failure Modes
  - Empty pages are allowed; downstream logic should handle by marking page successful.

### ValidateAndTransformStagedBillingGroups

- Signature: `IReadOnlyList<TransformedBillingGroupRow> ValidateAndTransformStagedBillingGroups(IReadOnlyList<StagedBillingGroupRow> stagedRows)`
- Purpose: Applies domain validation and transforms rows to target schema.
- Inputs
  - `stagedRows`
- Returns
  - `transformedRows` aligned to final merge schema
- Behavior
  1. Validate required fields, data types, referential constraints (where applicable).
  2. Normalize strings, trim, canonicalize enumerations, deduplicate rows.
  3. Map staging fields to target schema (column projection and renaming).
  4. Attach audit context (created/updated timestamps, source markers, SP ID, page number).
- Failure Modes
  - If validation errors exceed threshold, can fail the page and record diagnostics; otherwise, might filter invalid rows depending on policy.
- Idempotency
  - Pure function over input rows; deterministic.

### MergeBillingGroupsIntoFinalTables

- Signature: `Task MergeBillingGroupsIntoFinalTables(IReadOnlyList<TransformedBillingGroupRow> transformedRows, int? recordsPerCycle)`
- Purpose: Performs the upsert into final AMOP 2.0 tables.
- Inputs
  - `transformedRows`
  - Optional `recordsPerCycle` for chunked merges
- Writes
  - Final billing group tables via stored procedures or `MERGE` statements
- Behavior
  1. If large payload, split `transformedRows` into chunks of `recordsPerCycle` size.
  2. For each chunk, either:
     - Bulk copy into a merge-staging table, then execute `MERGE`, or
     - Call stored procedures like `UPDATE_POND_BILLING_GROUPS_FROM_STAGING` and `MERGE_POND_BILLING_GROUPS`.
  3. Ensure upsert semantics (insert new, update existing) and maintain audit columns.
- Error Handling
  - Wrap DB operations with `RetryPolicyHelper` for transient faults.
  - Partial chunk failures can be retried independently.
- Idempotency
  - Merges are written to be idempotent; re-running the same chunk is safe.

### MarkPageAsProcessed

- Signature: `Task MarkPageAsProcessed(long serviceProviderId, int pageNumber, bool isSuccessful, string? failureReason = null)`
- Purpose: Updates the page tracker with final status and completion timestamps.
- Inputs
  - `serviceProviderId`, `pageNumber`, `isSuccessful`, optional `failureReason`
- Writes
  - `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS` (status to `Success` or `Failed`, timestamps)
- Behavior
  1. Set end timestamp, status, and optional reason.
  2. Optionally compute whether all pages for the SP are now completed (for roll-up checks).
- Idempotency
  - Setting `Success` again is no-op; failure overrides only if policy allows.

### EmitProgressMessage

- Signature: `Task EmitProgressMessage(long serviceProviderId, int pageNumber, bool isSuccessful)`
- Purpose: Optionally informs downstream orchestrators of per-page completion.
- Inputs
  - `serviceProviderId`, `pageNumber`, `isSuccessful`
- Writes
  - SQS message with attributes: `SERVICE_PROVIDER_ID`, `PAGE_NUMBER`, `IS_SUCCESSFUL`
- Behavior
  1. Publish message via `SqsService` to configured destination.
  2. Include correlation IDs and audit properties from `AmopLambdaContext` as needed.

### GetMessageValues

- Signature: `SqsValues GetMessageValues(SQSEvent.SQSMessage message)`
- Purpose: Parses SQS attributes into a strongly-typed struct.
- Inputs
  - `message` (with attributes)
- Returns
  - `SqsValues { long ServiceProviderId, int PageNumber, bool? IsSuccessful }`
- Behavior
  1. Extracts and converts attributes using `SQSMessageKeyConstant` names.
  2. Applies defaults (e.g., missing `IsSuccessful` results in `null`).
  3. Validates numeric parsing; logs on malformed attributes.

### TryGetAllEnvironmentVariables

- Signature: `EnvironmentConfig TryGetAllEnvironmentVariables()`
- Purpose: Loads Lambda configuration from environment variables.
- Inputs: none
- Returns
  - `EnvironmentConfig { string QueueUrl, int PageSize, int? RecordsPerCycle, string CentralDbConnectionString }`
- Behavior
  1. Read `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL` (required).
  2. Read `PAGE_SIZE` (default 10 if not set or invalid).
  3. Read `RECORDS_PER_CYCLE` (optional, null if not set or invalid).
  4. Read `CENTRAL_DB_CONNECTION_STRING` via base/environment management.
  5. Validate presence of required settings; throw or log-and-fail early if missing.

### InitializeRepositories

- Signature: `(void) InitializeRepositories(AmopLambdaContext context, out PondRepository pondRepository, out ServiceProviderRepository serviceProviderRepository)`
- Purpose: Centralized repository initialization with shared DB connection.
- Inputs
  - `context` containing `CentralDbConnectionString`
- Returns
  - Instantiated `pondRepository`, `serviceProviderRepository`
- Behavior
  1. Build repositories using a common connection factory (from `AwsFunctionBase`).
  2. Ensure connection reuse/pooling where available.

### InitProcessStagedBillingGroupPages

- Signature: `Task InitProcessStagedBillingGroupPages(AmopLambdaContext context, long serviceProviderId, PageMarker page)`
- Purpose: Enqueues a processing message per page.
- Inputs
  - `serviceProviderId`, `page` (contains `PageNumber`)
- Writes
  - SQS message to `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL` with attributes:
    - `SERVICE_PROVIDER_ID`
    - `PAGE_NUMBER`
- Behavior
  1. Construct message with attributes and correlation metadata.
  2. Publish via `SqsService` with FIFO/deduplication parameters if applicable.

### RetryPolicyHelper

- Signature: `IAsyncPolicy CreateSqlTransientRetryPolicy()`
- Purpose: Supplies Polly (or equivalent) retry policy for transient SQL failures.
- Behavior
  - Configures exponential backoff, max retry count, and classification of transient errors (e.g., deadlocks, timeouts, throttling).
  - Used to wrap DB calls in `ProcessBillingGroupPage` and merge operations.

---

## Control Flow Diagrams (Textual)

### Initialization Mode

1. `FunctionHandler` (no `ServiceProviderId` in message)
2. `TryGetAllEnvironmentVariables`
3. `GetAllServiceProviderIds(IntegrationType.Pond)`
4. For each SP:
   - `SeedBillingGroupPagesToProcessIfNeeded`
   - For each page marker → `InitProcessStagedBillingGroupPages` (enqueue)

### Processing Mode

1. `FunctionHandler` (with `ServiceProviderId` and `PAGE_NUMBER`)
2. `InitializeRepositories`
3. `ProcessBillingGroupPage`
   - `GetPageFromPageToProcessTable`
   - If upstream `IS_SUCCESSFUL == false` → `MarkPageAsProcessed(false)` and optional `EmitProgressMessage`; stop
   - `LoadStagedPageBatch`
   - `ValidateAndTransformStagedBillingGroups`
   - `MergeBillingGroupsIntoFinalTables`
   - `MarkPageAsProcessed(true)`
   - Optional `EmitProgressMessage(true)`

---

## Operational Notes

- Concurrency: Safe to run many Lambdas in parallel; page claiming prevents duplicate work.
- Replays: Replaying the same page message is safe due to idempotent merges and page status checks.
- Monitoring: Use logs and page tracker table to observe progress; consider metrics per SP/page.
- Backpressure: Tune `PAGE_SIZE` and `RECORDS_PER_CYCLE` to balance memory and DB throughput.
- Error Handling: Transient DB failures are retried; persistent failures mark page as `Failed` with reason.

---

## Glossary

- Page Marker: A row in `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS` identifying a page for a specific SP.
- Staged Rows: Source records in `PondBillingGroupStaging` to be validated and merged.
- Transformed Rows: In-memory representation aligned with target schema and audit requirements.


### SeedBillingGroupPagesToProcessIfNeeded

- Signature: `Task SeedBillingGroupPagesToProcessIfNeeded(AmopLambdaContext context, long serviceProviderId)`
- Purpose: Ensures the page tracker contains all required page markers for the service provider before processing begins.
- Inputs
  - `serviceProviderId`
- Reads/Writes
  - Reads staging to determine page count or boundaries
  - Writes missing rows into `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS`
- Behavior
  1. Determine total pages for the SP based on `PAGE_SIZE` and staged row counts.
  2. Insert missing page marker rows with initial status `Queued` (or equivalent).
  3. Avoid duplicate inserts using upsert/exists checks.
- Idempotency
  - Safe to re-run; existing markers are preserved.

### GetAllServiceProviderIds (Repository)

- Signature: `IReadOnlyList<long> GetAllServiceProviderIds(IntegrationType integrationType)`
- Purpose: Enumerates all service provider IDs configured for Pond integration.
- Notes: Called only during initialization mode to seed and enqueue page processing per SP.

### CleanUp (Base)

- Signature: `void CleanUp()`
- Purpose: Disposes connections, flushes logs, and performs any teardown after processing a batch of messages.
- Notes: Invoked from the main handler’s finally block via `AwsFunctionBase` to ensure resource cleanup.


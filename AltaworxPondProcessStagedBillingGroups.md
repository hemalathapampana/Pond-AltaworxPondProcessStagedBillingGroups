## AltaworxPondProcessStagedBillingGroups Lambda Flow Documentation

### Overview
The AltaworxPondProcessStagedBillingGroups Lambda function processes staged Pond Billing Group data into final AMOP 2.0 tables. It listens for page-level progress messages (from the “Get” phase), validates and merges staged records, and updates per-page processing progress in the database. It can also initialize processing when invoked without a `ServiceProviderId`.

---

## HIGH-LEVEL FLOW (Sequential Function Flow)

### Main Entry Point
- `FunctionHandler (SQSEvent sqsEvent, ILambdaContext context)`
  - Receives SQS event and Lambda context
  - Initializes base function handler
  - Iterates through SQS records and routes per-message

### Initialization Flow (ServiceProviderId not supplied in SQS message)
- `InitializeProcessStagedBillingGroups`
  - `TryGetAllEnvironmentVariables` (reads configuration)
  - `GetAllServiceProviderIds(IntegrationType.Pond)`
  - For each Service Provider (SP):
    - `SeedBillingGroupPagesToProcessIfNeeded`
    - `InitProcessStagedBillingGroupPages` (enqueue one SQS message per page)
  - Shape

### Processing Flow (ServiceProviderId supplied in SQS message)
- `ProcessStagedBillingGroupsByServiceProviderId`
  - `Instantiate PondRepository` and other repositories
  - `ProcessBillingGroupPage`
    - `GetPageFromPageToProcessTable` (current page context)
    - `LoadStagedPageBatch` (fetch staged billing groups for this page/SP)
    - `ValidateAndTransformStagedBillingGroups`
    - `MergeBillingGroupsIntoFinalTables` (DB stored procedures/merge)
    - `MarkPageAsProcessed` (DB progress tracking)
    - `EmitProgressMessage` (optional downstream handshake)
  - Shape

---

## LOW-LEVEL FLOW (Detailed Method Explanations)

### FunctionHandler (Main Entry Point)
- Input: `SQSEvent sqsEvent`, `ILambdaContext context`
- Purpose: Processes SQS messages to orchestrate staged billing group processing
- What happens:
  - Initializes `AmopLambdaContext` via `BaseAmopFunctionHandler()`
  - Reads environment variables via `TryGetAllEnvironmentVariables()`:
    - `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL` (queue for this processor)
    - `PAGE_SIZE` (optional; for paging staged work if applicable; default 10)
    - `RECORDS_PER_CYCLE` (optional; batch size for DB merges within a page)
  - Ensures SQS trigger validity and iterates each record
  - For each record:
    - Logs diagnostics
    - Parses attributes with `GetMessageValues()`:
      - `ServiceProviderId` (required for processing mode)
      - `PageNumber` (required for processing mode; ties to staged page)
      - `IsSuccessful` (from upstream “Get” stage; may be used to skip)
    - If `ServiceProviderId <= 0` or missing: routes to `InitializeProcessStagedBillingGroups()`
    - Else: routes to `ProcessStagedBillingGroupsByServiceProviderId()`
  - Handles exceptions and calls `CleanUp()`

### InitializeProcessStagedBillingGroups (Initialization Mode)
- Input: `AmopLambdaContext context`, `ServiceProviderRepository serviceProviderRepository`
- Purpose: Seeds processing across staged pages and fans out messages
- What happens:
  - Retrieve all Pond service provider IDs via `GetAllServiceProviderIds`
  - For each `serviceProviderId`:
    - `SeedBillingGroupPagesToProcessIfNeeded(context, serviceProviderId)`
      - Ensures DB contains page markers for staged billing groups
      - Table example: `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS`
    - For each page marker:
      - `InitProcessStagedBillingGroupPages(context, serviceProviderId, page)`
        - Enqueues SQS message to `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL`
  - Shape

### ProcessStagedBillingGroupsByServiceProviderId (Processing Mode)
- Input: `AmopLambdaContext context`, `SqsValues sqsValues`
- Purpose: Processes one page of staged billing groups into final tables
- What happens:
  - Create repositories (e.g., `PondRepository`, `ServiceProviderRepository`)
  - `ProcessBillingGroupPage(context, sqsValues, sqlTransientRetryPolicy)`
    - `GetPageFromPageToProcessTable(serviceProviderId, pageNumber)`
      - Reads page status and guards against duplicate work
    - `LoadStagedPageBatch(serviceProviderId, pageNumber, pageSize)`
      - Loads staged rows from `PondBillingGroupStaging` filtered by page/SP
    - `ValidateAndTransformStagedBillingGroups(stagedRows)`
      - Performs schema mapping, normalization, and required validations
    - `MergeBillingGroupsIntoFinalTables(transformedRows)`
      - Executes `SqlBulkCopy` to a merge staging or invokes stored procedures:
        - Examples:
          - `UPDATE_POND_BILLING_GROUPS_FROM_STAGING`
          - `MERGE_POND_BILLING_GROUPS`
      - May execute in chunks using `RECORDS_PER_CYCLE` for large pages
    - `MarkPageAsProcessed(serviceProviderId, pageNumber, isSuccessful)`
      - Updates page status (e.g., Success/Failed) and timestamps
      - Optionally checks if all pages are done for the SP
    - `EmitProgressMessage(serviceProviderId, pageNumber, isSuccessful)`
      - Optional SQS message for downstream orchestration
  - Shape

---

## Utility Functions

- `GetMessageValues`
  - Parses SQS attributes into `SqsValues`
  - Attributes used (via `SQSMessageKeyConstant`):
    - `SERVICE_PROVIDER_ID`
    - `PAGE_NUMBER`
    - `IS_SUCCESSFUL` (carried from upstream; may short-circuit failed pages)

- `TryGetAllEnvironmentVariables`
  - Reads Lambda and sync configuration from environment variables:
    - `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL`
    - `PAGE_SIZE` (optional; default 10)
    - `RECORDS_PER_CYCLE` (optional; database merge batch size)
    - `CENTRAL_DB_CONNECTION_STRING` (implicit via shared base)

- `InitializeRepositories`
  - Instantiates `PondRepository` and `ServiceProviderRepository` using `CentralDbConnectionString`

- `LoadStagedPageBatch`
  - Reads staged billing groups from `PondBillingGroupStaging` for the page/SP
  - Optionally shapes to an in-memory `DataTable` or typed list

- `ValidateAndTransformStagedBillingGroups`
  - Applies domain validation, dedupe, normalization, and mapping to final schema

- `MergeBillingGroupsIntoFinalTables`
  - Executes DB stored procedures or `SqlBulkCopy` + MERGE to final tables
  - Ensures idempotent upsert semantics and records audit columns as required

- `MarkPageAsProcessed`
  - Updates page-to-process tracker (e.g., `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS`)
  - Persists success/failure and completion timestamps

- `InitProcessStagedBillingGroupPages`
  - Sends an SQS message per page to `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL` with attributes:
    - `SERVICE_PROVIDER_ID`
    - `PAGE_NUMBER`

- `EmitProgressMessage`
  - Sends an SQS message (optional) to a downstream processor with attributes:
    - `SERVICE_PROVIDER_ID`
    - `PAGE_NUMBER`
    - `IS_SUCCESSFUL`

- `RetryPolicyHelper`
  - Provides SQL transient retry policy for DB operations

---

## Key Dependencies and Integrations
- `AwsFunctionBase`: logging, config, DB connections, bulk copy, cleanup
- `PondRepository`: DB CRUD for Pond sync, staging, and progress tracking
- `ServiceProviderRepository`: service provider enumeration and metadata
- `EnvironmentRepository`: environment variable access
- `SqsService`: SQS message publishing
- `RetryPolicyHelper`: SQL transient retry policy
- `Shape`

---

## Data Flow Summary
- Initialization: seed page markers per service provider (if missing) and enqueue SQS messages per page for processing
- Read: load one page of staged billing groups from `PondBillingGroupStaging` by `serviceProviderId` and `pageNumber`
- Validate/Transform: normalize domain-specific data for merge
- Merge: upsert into final AMOP 2.0 tables via stored procedures or MERGE
- Advance: update page status in the page-to-process table and optionally emit progress SQS messages
- Note: Page-to-process tracking persists in DB (e.g., `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS`); repository methods can check overall completion per SP
- Shape

---

## AltaworxPondProcessStagedBillingGroups — Integration & Operations Guide

### 1) Triggers & Scheduling
- Publisher of initial SQS messages:
  - This Lambda, when invoked without `ServiceProviderId` or with an empty SQS event, enumerates pages to process and enqueues one SQS message per page to the Process-Staged-Billing-Groups SQS queue.
- EventBridge schedule: Triggered by AWS EventBridge.
  - Cron: `0 9 * * ? *`
  - Time zone: UTC
  - Frequency: Daily at 09:00 UTC
  - Next runs (examples): Fri, 19 Sep 2025 09:00 UTC; Sat, 20 Sep 2025 09:00 UTC (and daily thereafter)
  - This means your Lambda will run once daily at 9:00 AM UTC, which is 2:30 PM IST.

### 2) Message Handling
- SQS message attributes (seed/page messages):
  - `SERVICE_PROVIDER_ID`
  - `PAGE_NUMBER`
- SQS message attributes (optional progress messages):
  - `SERVICE_PROVIDER_ID`
  - `PAGE_NUMBER`
  - `IS_SUCCESSFUL`
- Continuation for pagination: One SQS message per page; each message processes exactly one page.
- Manual/default invocation: If the event has no `SERVICE_PROVIDER_ID`, the Lambda initializes a full run: seeds page markers (if needed) and enqueues page messages.

### 3) Batch & Pagination
- Configured page size: Default 10; can be overridden via env var `PAGE_SIZE`.
- Pagination mechanics: Uses existing page markers from the page-to-process table.
- Completion determination: DB page status is updated; overall completion per SP can be determined by repository methods once all pages are processed.

### 4) Integration Details (Authentication)
- Credential source: Database connectivity only; no external Pond API calls in this processor.
- Usage: SQL connection via `CentralDbConnectionString`.

### 5) Data Handling & Staging
- Staging tables:
  - `PondBillingGroupStaging` (input to this Lambda)
  - Page tracking: `POND_GET_BILLING_GROUPS_PAGE_TO_PROCESS` (example name)
- Final sync to AMOP 2.0 tables:
  - Performed here via stored procedures (e.g., `UPDATE_POND_BILLING_GROUPS_FROM_STAGING`) or MERGE logic.

### 6) Error Handling & Retry
- SQL retries (Polly):
  - Attempts: Config-driven (e.g., `CommonConstants.NUMBER_OF_RETRIES`)
  - Backoff: Exponential (e.g., `API_ERROR_DELAY_IN_SECONDS^attempt` seconds)
  - Retries on transient DB exceptions; logs details.
- Failures: Logged; page marked unsuccessful. Problem pages are retried on the next scheduled run.
- Re-enqueue of incomplete jobs: Managed by daily schedule or operator intervention.

### 7) Failed/Unprocessed Records
- Validation failures: Logged; rows may be skipped or parked depending on stored procedure behavior.
- Failure logging: CloudWatch logs; page-level success/failure recorded in DB.
- Retry policy: Via daily schedule; no per-record retry in this Lambda.

### 8) Cleanup Processes
- Retention (`DaysToKeep`): Not implemented in this Lambda.
- Cleanup batch size (`RecordsPerCycle`): Optional for merge chunking; otherwise not used.
- Cleanup logging: Not applicable.

### 9) Notifications & Reporting
- Notifications: None in this Lambda beyond CloudWatch logs.
- Sync summary reports: Not produced here. Progress captured via DB page flags.

### 10) External Dependencies (Prerequisites)
- Environment variables:
  - `POND_PROCESS_STAGED_BILLING_GROUPS_QUEUE_URL`
  - `PAGE_SIZE` (optional; default 10)
  - `RECORDS_PER_CYCLE` (optional)
- Infrastructure:
  - EventBridge rule with cron `0 9 * * ? *` (UTC) targeting this Lambda
  - SQS queue for “process staged billing groups”
  - DB connectivity for reading staging and merging into final tables
- Credentials and API info:
  - Not applicable (no external API calls in this processor)

---

## API Request Details
- Endpoint: Not applicable (this processor operates on database-staged data; no outgoing Pond API calls).
- Example: N/A
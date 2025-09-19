## AltaworxPondGetDevices Lambda – Detailed Flow Documentation

### Overview
The `AltaworxPondGetDevices` AWS Lambda synchronizes device data from the Pond API into the database. It supports two modes, orchestrated by SQS messages:
- **Initialization mode**: Seeds all pages to be processed for each service provider and enqueues a page-processing message for each page.
- **Processing mode**: Fetches one page of devices from Pond, bulk loads into a staging table, and emits a progress message for downstream processing.

This document describes every method, function, and integration in detail to support maintenance and onboarding.

---

## Architecture at a Glance
- **Trigger**: AWS SQS (Get Devices queue)
- **Orchestration**: Lambda reads SQS messages and routes to init/processing paths
- **External API**: Pond Distributor PPU Service (`PondGetDeviceEndpoint`)
- **Storage**: SQL Server (staging + page tracking tables)
- **Messaging**: AWS SQS (Get Devices queue, Process Staged Devices queue)
- **Resilience**: SQL transient retry policy, per-page idempotence via staging and page tracking

---

## Environment Variables
These are read at startup via `TryGetAllEnvironmentVariables()`.

- `POND_GET_DEVICES_QUEUE_URL` (queue for page processing)
- `POND_PROCESS_STAGED_DEVICES_QUEUE_URL` (downstream/progress queue)
- `POND_GET_DEVICE_ENDPOINT` (relative or absolute endpoint for Pond devices API)
- `PAGE_SIZE` (integer; default falls back to `PondHelper.CommonConfig.DEFAULT_PAGE_SIZE`)

Best practice: store sensitive endpoints/keys in AWS Secrets Manager or Parameter Store, and inject references into env vars. Do not embed secrets directly in code or documentation.

---

## SQS Message Contract
Attributes parsed by `GetMessageValues()` using `SQSMessageKeyConstant` keys:
- `SERVICE_PROVIDER_ID` (number)
- `PAGE_NUMBER` (0-based number)
- `IS_SUCCESSFUL` (boolean string; used when signaling downstream progress)

Message body is not used for routing; attributes drive control flow.

---

## Database Tables
- `DatabaseTableNames.PondDeviceStaging`
  - Columns: `Id`, `Iccid`, `Imei`, `Msisdn`, `Status`, `CreatedDate`, `ServiceProviderId`
  - Loaded via `SqlBulkCopy`
- `DatabaseTableNames.POND_GET_DEVICES_PAGE_TO_PROCESS`
  - Columns: `PageNumber`, `ServiceProviderId`
  - Seeded during initialization to track page work

Downstream components may update per-page status and compute overall sync progress via repository helpers (e.g., `UpdateDevicesPageStatusAndCheckSyncProgress`).

---

## Main Entry Point
### Function: `FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)`
**Purpose**: Processes an SQS batch, routing each record to init or processing flow.

**Inputs**:
- `sqsEvent`: The SQS batch event
- `context`: AWS Lambda context

**High-level behavior**:
1. Initialize `AmopLambdaContext` via `BaseAmopFunctionHandler()` (logging, configuration, DB connection, clients, etc.).
2. Read environment via `TryGetAllEnvironmentVariables()` and validate required values.
3. Iterate SQS records:
   - Parse attributes using `GetMessageValues()`
   - If `ServiceProviderId` is missing or `<= 0`, call `InitializeSyncDeviceProcess()`
   - Else, call `ProcessSyncDevicePageByServiceProviderId()`
4. Handle exceptions per-record; ensure `CleanUp()` runs.

```csharp
public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
{
    var amopContext = BaseAmopFunctionHandler(context);
    var env = TryGetAllEnvironmentVariables();

    ValidateEnv(env);

    foreach (var record in sqsEvent.Records)
    {
        try
        {
            var values = GetMessageValues(record);
            if (!values.ServiceProviderId.HasValue || values.ServiceProviderId.Value <= 0)
            {
                await InitializeSyncDeviceProcess(amopContext);
            }
            else
            {
                await ProcessSyncDevicePageByServiceProviderId(amopContext, values);
            }
        }
        catch (Exception ex)
        {
            amopContext.Logger.LogError(ex, "Unhandled error processing SQS record");
            // Optionally: DLQ, metrics, or error notifications
        }
    }

    await CleanUp(amopContext);
}
```

---

## Initialization Mode
### Function: `InitializeSyncDeviceProcess(AmopLambdaContext context)`
**Purpose**: Seed page work and enqueue page-processing messages for all Pond service providers.

**Detailed steps**:
1. `pondRepository.TruncateDeviceStagingTables()` resets device staging state.
2. `serviceProviderRepository.GetAllServiceProviderIds(IntegrationType.Pond)` enumerates all service providers configured for Pond.
3. For each `serviceProviderId`:
   - `pondRepository.GetPondAuthentication(serviceProviderId)` retrieves credentials/authorization details.
   - `pondApiService.TryGetTotalPageCount<T>()` calls Pond device endpoint once using configured `PAGE_SIZE` to compute total pages.
   - `LoadDevicePagesToProcessTable(context, serviceProviderId, totalPages)` bulk-inserts page markers to `POND_GET_DEVICES_PAGE_TO_PROCESS`.
   - For `pageNumber` in `[0, totalPages)` call `InitGetDevicePages(context, serviceProviderId, pageNumber)` to enqueue one SQS message per page to `POND_GET_DEVICES_QUEUE_URL`.

```csharp
private async Task InitializeSyncDeviceProcess(AmopLambdaContext context)
{
    var pondRepository = InitializeRepositories(context).PondRepository;
    var serviceProviderRepository = InitializeRepositories(context).ServiceProviderRepository;

    await pondRepository.TruncateDeviceStagingTables();

    var serviceProviderIds = await serviceProviderRepository
        .GetAllServiceProviderIds(IntegrationType.Pond);

    foreach (var spId in serviceProviderIds)
    {
        var auth = await pondRepository.GetPondAuthentication(spId);
        var pondApi = new PondApiService(auth, context.Env.PondGetDeviceEndpoint);

        var totalPages = await pondApi.TryGetTotalPageCount(context.Env.PageSize);

        await LoadDevicePagesToProcessTable(context, spId, totalPages);

        for (var page = 0; page < totalPages; page++)
        {
            await InitGetDevicePages(context, spId, page);
        }
    }
}
```

---

## Processing Mode
### Function: `ProcessSyncDevicePageByServiceProviderId(AmopLambdaContext context, SqsValues sqsValues)`
**Purpose**: Fetch one page of devices from Pond and load them into staging.

**Detailed steps**:
1. Retrieve authentication via `pondRepository.GetPondAuthentication(sqsValues.ServiceProviderId)`.
2. Instantiate `PondApiService` with endpoint + credentials.
3. Call `SyncDevices(context, sqsValues, sqlTransientRetryPolicy, pondApiService)` which:
   - Calls `GetSinglePageDeviceListFromPondAPIAsync` to fetch page data.
   - Calls `LoadDevicesToStagingTable` to bulk copy into staging.
   - Calls `CheckSyncDeviceStepProgress` to emit downstream progress/completion signal to `POND_PROCESS_STAGED_DEVICES_QUEUE_URL`.

```csharp
private async Task ProcessSyncDevicePageByServiceProviderId(AmopLambdaContext context, SqsValues values)
{
    var repositories = InitializeRepositories(context);
    var auth = await repositories.PondRepository
        .GetPondAuthentication(values.ServiceProviderId!.Value);

    var pondApi = new PondApiService(auth, context.Env.PondGetDeviceEndpoint);
    var retryPolicy = RetryPolicyHelper.CreateSqlTransientRetryPolicy(context.Logger);

    await SyncDevices(context, values, retryPolicy, pondApi);
}
```

---

## Core Orchestration
### Function: `SyncDevices(AmopLambdaContext context, SqsValues values, IAsyncPolicy retryPolicy, PondApiService pondApi)`
**Purpose**: Orchestrates fetch → stage → progress for a single page.

**Behavior**:
1. Fetch one page via `GetSinglePageDeviceListFromPondAPIAsync` using offset = `values.PageNumber * context.Env.PageSize`.
2. Transform response into a `DataTable` and bulk insert via `LoadDevicesToStagingTable`.
3. Emit a progress message via `CheckSyncDeviceStepProgress` with attributes `SERVICE_PROVIDER_ID`, `PAGE_NUMBER`, `IS_SUCCESSFUL`.

Error handling: exceptions during fetch or stage are logged and `IS_SUCCESSFUL=false` should be signaled to downstream if applicable.

```csharp
private async Task SyncDevices(
    AmopLambdaContext context,
    SqsValues values,
    IAsyncPolicy retryPolicy,
    PondApiService pondApi)
{
    var pageSize = context.Env.PageSize;
    var offset = values.PageNumber!.Value * pageSize;

    var deviceList = await GetSinglePageDeviceListFromPondAPIAsync<PondDeviceItem, PondDeviceListResponse>(
        pondApi, offset, pageSize, r => r.Elements);

    await retryPolicy.ExecuteAsync(async () =>
    {
        await LoadDevicesToStagingTable(context, values.ServiceProviderId!.Value, deviceList);
    });

    await CheckSyncDeviceStepProgress(context, values.ServiceProviderId!.Value, values.PageNumber!.Value, isSuccessful: true);
}
```

---

## Fetching from Pond API
### Function: `GetSinglePageDeviceListFromPondAPIAsync<TItem, TResponse>(PondApiService pondApi, int offset, int pageSize, Func<TResponse, IEnumerable<TItem>> selector)`
**Purpose**: Fetches a single page of devices from Pond using the configured endpoint.

**Behavior**:
- Computes `offset = pageNumber * pageSize`.
- Uses `PondApiService.GetPondListAsync<TResponse>(HttpClientSingleton.Instance, PondGetDeviceEndpoint, offset, pageSize)`.
- Maps the response to items via `selector` (e.g., `r => r.Elements`).

```csharp
private async Task<IEnumerable<TItem>> GetSinglePageDeviceListFromPondAPIAsync<TItem, TResponse>(
    PondApiService pondApi,
    int offset,
    int pageSize,
    Func<TResponse, IEnumerable<TItem>> selector)
{
    var response = await pondApi.GetPondListAsync<TResponse>(
        HttpClientSingleton.Instance,
        endpoint: pondApi.Endpoint,
        offset: offset,
        limit: pageSize);

    return selector(response) ?? Enumerable.Empty<TItem>();
}
```

---

## Staging Load
### Function: `LoadDevicesToStagingTable(AmopLambdaContext context, int serviceProviderId, IEnumerable<PondDeviceItem> devices)`
**Purpose**: Creates a `DataTable` matching the staging schema and bulk-inserts rows.

**Behavior**:
1. Build `DataTable` with columns: `Id`, `Iccid`, `Imei`, `Msisdn`, `Status`, `CreatedDate`, `ServiceProviderId`.
2. For each device, add a row to the table (ensuring data types and null handling align with DB schema).
3. Use `SqlBulkCopy` to insert into `DatabaseTableNames.PondDeviceStaging`.
4. Errors are retried by `retryPolicy` in `SyncDevices`.

```csharp
private async Task LoadDevicesToStagingTable(
    AmopLambdaContext context,
    int serviceProviderId,
    IEnumerable<PondDeviceItem> devices)
{
    var table = new DataTable();
    table.Columns.Add("Id", typeof(string));
    table.Columns.Add("Iccid", typeof(string));
    table.Columns.Add("Imei", typeof(string));
    table.Columns.Add("Msisdn", typeof(string));
    table.Columns.Add("Status", typeof(string));
    table.Columns.Add("CreatedDate", typeof(DateTime));
    table.Columns.Add("ServiceProviderId", typeof(int));

    foreach (var d in devices)
    {
        table.Rows.Add(d.Id, d.Iccid, d.Imei, d.Msisdn, d.Status, d.CreatedDate, serviceProviderId);
    }

    await context.BulkCopyAsync(table, DatabaseTableNames.PondDeviceStaging);
}
```

---

## Page Seeding
### Function: `LoadDevicePagesToProcessTable(AmopLambdaContext context, int serviceProviderId, int totalPages)`
**Purpose**: Seeds DB with page markers indicating which pages should be processed.

**Behavior**:
1. Build a `DataTable` with `PageNumber`, `ServiceProviderId` for each page.
2. `SqlBulkCopy` into `DatabaseTableNames.POND_GET_DEVICES_PAGE_TO_PROCESS`.

```csharp
private async Task LoadDevicePagesToProcessTable(
    AmopLambdaContext context,
    int serviceProviderId,
    int totalPages)
{
    var table = new DataTable();
    table.Columns.Add("PageNumber", typeof(int));
    table.Columns.Add("ServiceProviderId", typeof(int));

    for (var page = 0; page < totalPages; page++)
    {
        table.Rows.Add(page, serviceProviderId);
    }

    await context.BulkCopyAsync(table, DatabaseTableNames.POND_GET_DEVICES_PAGE_TO_PROCESS);
}
```

---

## Enqueue Page Processing
### Function: `InitGetDevicePages(AmopLambdaContext context, int serviceProviderId, int pageNumber)`
**Purpose**: Sends an SQS message to the Get Devices queue for a single page.

**Behavior**:
- Publishes to `POND_GET_DEVICES_QUEUE_URL` with attributes:
  - `SERVICE_PROVIDER_ID = serviceProviderId`
  - `PAGE_NUMBER = pageNumber`

```csharp
private async Task InitGetDevicePages(AmopLambdaContext context, int serviceProviderId, int pageNumber)
{
    await context.SqsService.SendMessageAsync(
        queueUrl: context.Env.PondGetDevicesQueueUrl,
        attributes: new Dictionary<string, string>
        {
            [SQSMessageKeyConstant.SERVICE_PROVIDER_ID] = serviceProviderId.ToString(),
            [SQSMessageKeyConstant.PAGE_NUMBER] = pageNumber.ToString(),
        });
}
```

---

## Downstream Progress Signaling
### Function: `CheckSyncDeviceStepProgress(AmopLambdaContext context, int serviceProviderId, int pageNumber, bool isSuccessful)`
**Purpose**: Emits a message indicating page processing success/failure for downstream processing.

**Behavior**:
- Publishes to `POND_PROCESS_STAGED_DEVICES_QUEUE_URL` with attributes:
  - `SERVICE_PROVIDER_ID = serviceProviderId`
  - `PAGE_NUMBER = pageNumber`
  - `IS_SUCCESSFUL = isSuccessful`

```csharp
private async Task CheckSyncDeviceStepProgress(
    AmopLambdaContext context,
    int serviceProviderId,
    int pageNumber,
    bool isSuccessful)
{
    await context.SqsService.SendMessageAsync(
        queueUrl: context.Env.PondProcessStagedDevicesQueueUrl,
        attributes: new Dictionary<string, string>
        {
            [SQSMessageKeyConstant.SERVICE_PROVIDER_ID] = serviceProviderId.ToString(),
            [SQSMessageKeyConstant.PAGE_NUMBER] = pageNumber.ToString(),
            [SQSMessageKeyConstant.IS_SUCCESSFUL] = isSuccessful.ToString(),
        });
}
```

---

## Utility and Support Methods
### Function: `GetMessageValues(SQSEvent.SQSMessage record)`
**Purpose**: Parses message attributes into a strongly-typed `SqsValues` structure used by the handler.

**Behavior**:
- Reads `SERVICE_PROVIDER_ID`, `PAGE_NUMBER`, `IS_SUCCESSFUL` attributes.
- Converts to nullable integers/bools with validation and logging.

```csharp
private SqsValues GetMessageValues(SQSEvent.SQSMessage record)
{
    var attributes = record.MessageAttributes;

    int? spId = TryParseInt(attributes, SQSMessageKeyConstant.SERVICE_PROVIDER_ID);
    int? page = TryParseInt(attributes, SQSMessageKeyConstant.PAGE_NUMBER);
    bool? ok = TryParseBool(attributes, SQSMessageKeyConstant.IS_SUCCESSFUL);

    return new SqsValues
    {
        ServiceProviderId = spId,
        PageNumber = page,
        IsSuccessful = ok
    };
}
```

### Function: `TryGetAllEnvironmentVariables()`
**Purpose**: Loads and validates environment configuration for queues, endpoints, and page size.

**Behavior**:
- Reads `POND_GET_DEVICES_QUEUE_URL`, `POND_PROCESS_STAGED_DEVICES_QUEUE_URL`, `POND_GET_DEVICE_ENDPOINT`, `PAGE_SIZE`.
- Assigns defaults where necessary; throws or logs on missing critical variables.

```csharp
private EnvConfig TryGetAllEnvironmentVariables()
{
    var env = new EnvConfig
    {
        PondGetDevicesQueueUrl = EnvironmentRepository.Get(PondHelper.CommonString.POND_GET_DEVICES_QUEUE_URL_VARIABLE_KEY),
        PondProcessStagedDevicesQueueUrl = EnvironmentRepository.Get(PondHelper.CommonString.POND_PROCESS_STAGED_DEVICES_QUEUE_URL_VARIABLE_KEY),
        PondGetDeviceEndpoint = EnvironmentRepository.Get(PondHelper.CommonString.POND_GET_DEVICE_ENDPOINT_VARIABLE_KEY),
        PageSize = EnvironmentRepository.GetInt(PondHelper.CommonString.PAGE_SIZE, PondHelper.CommonConfig.DEFAULT_PAGE_SIZE)
    };

    ValidateEnv(env);
    return env;
}
```

### Function: `InitializeRepositories(AmopLambdaContext context)`
**Purpose**: Constructs repository instances bound to the central DB connection and shared services.

**Behavior**:
- Uses `context.CentralDbConnectionString` to create `PondRepository` and `ServiceProviderRepository`.
- May also hydrate `SqsService`, `HttpClientSingleton`, etc., via dependency injection or factory methods.

```csharp
private (PondRepository PondRepository, ServiceProviderRepository ServiceProviderRepository) InitializeRepositories(AmopLambdaContext context)
{
    var pondRepository = new PondRepository(context.CentralDbConnectionString, context.Logger);
    var spRepository = new ServiceProviderRepository(context.CentralDbConnectionString, context.Logger);
    return (pondRepository, spRepository);
}
```

---

## Key Dependencies and Responsibilities
- `AwsFunctionBase` (base class):
  - Logging, configuration hydration, DB connections, `BulkCopyAsync`, lifecycle hooks (`BaseAmopFunctionHandler`, `CleanUp`).
- `PondRepository`:
  - CRUD for authentication records, staging, and page progress; `TruncateDeviceStagingTables`, `GetPondAuthentication`.
- `ServiceProviderRepository`:
  - Enumerates Pond-enabled service providers; `GetAllServiceProviderIds(IntegrationType.Pond)`.
- `PondApiService`:
  - Builds Pond API requests; `GetPondListAsync<TResponse>`; computes `TryGetTotalPageCount` given page size.
- `EnvironmentRepository`:
  - Reads env vars; parsing and validation helpers.
- `SqsService`:
  - Publishes messages with attributes to SQS queues.
- `RetryPolicyHelper`:
  - Provides SQL transient retry policy (e.g., Polly-based).
- `HttpClientSingleton` and `HttpRequestFactory`:
  - HTTP client lifetime management and request construction.

---

## Error Handling, Retries, and Idempotence
- **SQL retries**: `RetryPolicyHelper` wraps bulk copy operations.
- **Per-record try/catch**: Isolates failures to individual SQS messages.
- **Idempotence**:
  - Page seeding table tracks intended pages.
  - Staging table can be truncated on initialization; per-page load should be additive or upserted downstream.
  - SQS visibility timeout and retries should be tuned to page processing time.

---

## Observability
- **Structured logging**: Service provider, page number, counts fetched, staging rows inserted.
- **Metrics** (recommended):
  - `devices.pages.seeded`, `devices.pages.processed`, `devices.rows.staged`, `devices.fetch.errors`, `devices.bulkcopy.errors`.
- **Traceability**: Correlate by `ServiceProviderId` and `PageNumber` in logs and message attributes.

---

## Deployment and Configuration Notes
- Configure two SQS queues:
  - `POND_GET_DEVICES_QUEUE_URL`
  - `POND_PROCESS_STAGED_DEVICES_QUEUE_URL`
- Set `POND_GET_DEVICE_ENDPOINT` to the Pond devices endpoint base.
- Choose a `PAGE_SIZE` aligned with API rate limits and DB bulk copy efficiency.
- Ensure DB permissions for bulk copy into staging and page tracking tables.
- Store API credentials in AWS Secrets Manager; resolve them in `PondRepository.GetPondAuthentication` at runtime.

---

## Security Considerations
- Do not store or display secrets in code or documentation.
- The following placeholders represent sensitive values and must be sourced securely at runtime:
  - `APIKey = <REDACTED>`
  - `Username = <REDACTED>`
  - `EncodedPassword = <REDACTED>`
  - `TokenValue = <REDACTED>`
- Use IAM roles, KMS, and Secrets Manager to protect credentials. Rotate tokens regularly.

---

## External API Endpoints (Reference)
- Base URL: `https://www.mydashboard.pondmobile.com/`
- Production URL: `https://www.mydashboard.pondmobile.com/ds/u/distributorPPUService/v1`
- Sandbox URL: `https://www.mydashboard.pondmobile.com/ds/u/distributorPPUService/v1`

The specific device list path is configured via `POND_GET_DEVICE_ENDPOINT` and composed inside `PondApiService`.

---

## Example Messages
### Initialization-triggering message (no ServiceProviderId)
```json
{
  "MessageAttributes": {}
}
```

### Page-processing message
```json
{
  "MessageAttributes": {
    "SERVICE_PROVIDER_ID": { "StringValue": "42", "DataType": "Number" },
    "PAGE_NUMBER": { "StringValue": "0", "DataType": "Number" }
  }
}
```

### Downstream progress message
```json
{
  "MessageAttributes": {
    "SERVICE_PROVIDER_ID": { "StringValue": "42", "DataType": "Number" },
    "PAGE_NUMBER": { "StringValue": "0", "DataType": "Number" },
    "IS_SUCCESSFUL": { "StringValue": "true", "DataType": "String" }
  }
}
```

---

## Troubleshooting
- **Missing env vars**: Ensure all required env keys are set; Lambda will log missing keys during `TryGetAllEnvironmentVariables`.
- **Zero total pages**: Validate API credentials and endpoint; check `PAGE_SIZE` is positive.
- **Bulk copy failures**: Verify destination table schema matches DataTable and that DB user has `BULK INSERT` permissions.
- **SQS throttling or DLQ growth**: Increase Lambda concurrency, adjust visibility timeout, or reduce `PAGE_SIZE`.

---

## Glossary
- **Initialization mode**: Seeds page work for all Pond service providers.
- **Processing mode**: Fetches and stages one page of devices.
- **Staging**: Temporary table into which raw device data is bulk inserted before downstream processing.
- **Page tracking**: DB records indicating which pages exist and their processing status.

---

## Appendix: End-to-End Pseudocode
```csharp
// Handler
for each record in sqsEvent.Records:
    values = GetMessageValues(record)
    if values.ServiceProviderId is null or <= 0:
        InitializeSyncDeviceProcess(context)
    else:
        ProcessSyncDevicePageByServiceProviderId(context, values)

// Initialize
TruncateDeviceStagingTables()
spIds = GetAllServiceProviderIds(Pond)
for spId in spIds:
    auth = GetPondAuthentication(spId)
    totalPages = TryGetTotalPageCount(endpoint, PAGE_SIZE)
    LoadDevicePagesToProcessTable(spId, totalPages)
    for page in [0..totalPages):
        InitGetDevicePages(spId, page)

// Process page
auth = GetPondAuthentication(spId)
pondApi = new PondApiService(auth, endpoint)
list = GetSinglePageDeviceListFromPondAPIAsync(pondApi, page * PAGE_SIZE, PAGE_SIZE)
LoadDevicesToStagingTable(spId, list)
CheckSyncDeviceStepProgress(spId, page, true)
```

---

## Ownership and Contacts
- Primary owner: Data Integrations team
- Secondary owner: Platform Engineering
- Runbooks: Refer to on-call docs for DLQ handling and DB maintenance tasks

---

This document is intended to be a comprehensive technical reference for `AltaworxPondGetDevices`. Update it alongside code changes to keep it accurate.
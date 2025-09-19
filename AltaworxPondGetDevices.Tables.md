## Data Tables Used by AltaworxPondGetDevices Lambda

### PondDeviceStaging
- **purpose**: Temporary staging for devices fetched from Pond API per page.
- **loaded by**: `LoadDevicesToStagingTable` (via `SqlBulkCopy`).
- **lifecycle**: Truncated during initialization (`TruncateDeviceStagingTables`), populated during processing.
- **columns**:
  - `Id` (string)
  - `Iccid` (string)
  - `Imei` (string)
  - `Msisdn` (string)
  - `Status` (string)
  - `CreatedDate` (datetime)
  - `ServiceProviderId` (int)

### POND_GET_DEVICES_PAGE_TO_PROCESS
- **purpose**: Tracks which pages must be processed per service provider.
- **loaded by**: `LoadDevicePagesToProcessTable` (via `SqlBulkCopy`) during initialization.
- **usage**: Seeded at initialization; used for orchestration/monitoring by downstream components.
- **columns**:
  - `PageNumber` (int)
  - `ServiceProviderId` (int)

### Related (Read-Only) Tables Accessed via Repositories
- **Service provider metadata**: Enumerated via `ServiceProviderRepository.GetAllServiceProviderIds(IntegrationType.Pond)`.
- **Pond authentication store**: Retrieved via `PondRepository.GetPondAuthentication(serviceProviderId)`.

Note: Exact names/schemas for related read-only tables depend on the central database and are not modified by this Lambda.
using System;
using System.Collections.Generic;
using System.Linq;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using Amop.Core.Helpers.Pond;
using Amop.Core.Logger;
using Amop.Core.Models.DeviceBulkChange;
using Amop.Core.Models;
using Amop.Core.Models.Pond;
using Amop.Core.Resilience;
using Amop.Core.Services.Base64Service;
using Microsoft.Data.SqlClient;
using Polly;

namespace Amop.Core.Repositories.Pond
{
    public class PondRepository
    {
        private const int MaxRetries = PondHelper.CommonConfig.RETRY_NUMBER;
        private readonly string connectionString;
        private readonly ISyncPolicy sqlRetryPolicy;

        public PondRepository(string connectionString)
            : this(connectionString, new NoOpLogger())
        {
        }

        public PondRepository(string connectionString, IKeysysLogger logger)
            : this(connectionString, new PolicyFactory(logger))
        {
        }

        public PondRepository(string connectionString, IPolicyFactory policyFactory)
            : this(connectionString, policyFactory.GetSqlRetryPolicy(MaxRetries))
        {
        }

        public PondRepository(string connectionString, ISyncPolicy sqlRetryPolicy)
        {
            this.connectionString = connectionString;
            this.sqlRetryPolicy = sqlRetryPolicy;
        }

        public virtual PondAuthentication GetPondAuthentication(Action<string, string> logFunction, IBase64Service base64Service, int serviceProviderId = 0)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
            };
            var pondAuthenticationList = sqlRetryPolicy.Execute(() =>
                SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction, connectionString,
                    SQLConstant.StoredProcedureName.GET_POND_AUTHENTICATION,
                    (dataReader) => ReadPondAuthentication(dataReader, base64Service),
                    parameters,
                    SQLConstant.ShortTimeoutSeconds));
            return pondAuthenticationList.FirstOrDefault();
        }

        public void TruncateStagingTables(Action<string, string> logFunction)
        {
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithRowCountResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_TRUNCATE_STAGING,
                new List<SqlParameter>(),
                SQLConstant.ShortTimeoutSeconds));
        }

        public int GetNextInventoryId(Action<string, string> logFunction, int serviceProviderId, int currentInventoryId = 0)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.CURRENT_INVENTORY_ID_PASCAL_CASE, currentInventoryId),

            };
            return sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_GET_NEXT_INVENTORY_ID,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public void FilterICCIDsToProcess(Action<string, string> logFunction, int batchSize)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.BATCH_SIZE, batchSize),
            };
            sqlRetryPolicy.Execute(() => SqlQueryHelper.ExecuteStoredProcedureWithRowCountResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_FILTER_ICCIDS_TO_PROCESS,
                parameters,
                SQLConstant.TimeoutSeconds));
        }

        public List<PondDeviceICCIDsToProcess> FilterICCIDsToSyncSimCarrierRatePlansProcess(Action<string, string> logFunction)
        {
            List<PondDeviceICCIDsToProcess> deviceListToProcess = new List<PondDeviceICCIDsToProcess>();
            var parameters = new List<SqlParameter>();
            sqlRetryPolicy.Execute(() =>
            {
                deviceListToProcess = SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction,
                    connectionString,
                    SQLConstant.StoredProcedureName.POND_CARRIER_RATE_PLAN_FILTER_ICCIDS_TO_PROCESS,
                    (dataReader) => new PondDeviceICCIDsToProcess(dataReader),
                    parameters,
                    SQLConstant.TimeoutSeconds);
            });
            return deviceListToProcess;
        }

        public List<PondDeviceStatusICCIDsToProcess> GetDeviceListToProcess(Action<string, string> logFunction, int batchSize, int groupNumber)
        {
            List<PondDeviceStatusICCIDsToProcess> deviceListToProcess = new List<PondDeviceStatusICCIDsToProcess>();

            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.BATCH_SIZE, batchSize),
                new SqlParameter(CommonSQLParameterNames.GROUP_NUMBER, groupNumber),
            };
            sqlRetryPolicy.Execute(() =>
            {
                deviceListToProcess = SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction, connectionString,
                    SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_GET_BATCH_TO_PROCESS,
                    (dataReader) => new PondDeviceStatusICCIDsToProcess(dataReader),
                    parameters,
                    SQLConstant.ShortTimeoutSeconds);
            });
            return deviceListToProcess;
        }

        public int UpdateAndCheckSimCarrierRatePlanListToProcess(Action<string, string> logFunction, int serviceProviderId, string iccid)
        {
            var simsToProcess = 0;
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.ICCID, iccid),
            };
            sqlRetryPolicy.Execute(() =>
            {
                simsToProcess = SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction, connectionString,
                    SQLConstant.StoredProcedureName.POND_UPDATE_AND_CHECK_DEVICE_CARRIER_RATE_PLAN_PROCESSING,
                    parameters,
                    SQLConstant.ShortTimeoutSeconds);
            });
            return simsToProcess;
        }

        public void LoadInventoryFromStagingTable(Action<string, string> logFunction, int serviceProviderId, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY, processedBy),
            };
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.UPDATE_POND_INVENTORY_FROM_STAGING,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public void LoadPondDeviceCarrierRatePlanFromStagingTable(Action<string, string> logFunction, int serviceProviderId, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY_PASCAL_CASE, processedBy),
            };
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.UPDATE_POND_DEVICE_CARRIER_RATE_PLAN_FROM_STAGING,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }
        public void UpdatePondDeviceCarrierRatePlanToDeviceTable(Action<string, string> logFunction)
        {
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.UPDATE_POND_DEVICE_CARRIER_RATE_PLAN_TO_DEVICE,
                commandTimeout: SQLConstant.ShortTimeoutSeconds));
        }
        public int GetMaxGroupNumber(Action<string, string> logFunction)
        {
            return sqlRetryPolicy.Execute(() => SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_GET_MAX_GROUP_NUMBER,
                commandTimeout: SQLConstant.TimeoutSeconds));
        }

        public List<int> GetDeviceStatusServiceProviderIds(Action<string, string> logFunction)
        {
            return sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_GET_SERVICE_PROVIDER_IDS,
                (dataReader) => ReadStagedServiceProviderId(dataReader),
                commandTimeout: SQLConstant.TimeoutSeconds));
        }

        public virtual void LoadBillingGroupFromStagingTable(Action<string, string> logFunction, int serviceProviderId, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY, processedBy),

            };
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.UPDATE_POND_BILLING_GROUP_FROM_STAGING,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public virtual void LoadDevicesFromStagingTable(Action<string, string> logFunction, int serviceProviderId, string processedBy, int billMonth, int billYear, DateTime nextBillCycleDate)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY, processedBy),
                new SqlParameter(CommonSQLParameterNames.BILL_MONTH, billMonth),
                new SqlParameter(CommonSQLParameterNames.BILL_YEAR, billYear),
                new SqlParameter(CommonSQLParameterNames.NEXT_BILL_CYCLE_DATE, nextBillCycleDate),
            };

            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.UPDATE_POND_DEVICE_FROM_STAGING,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public void UpdateDeviceStatus(Action<string, string> logFunction, int serviceProviderId, int billPeriodEndDay, int billPeriodEndHour)
        {
            var parameters = new List<SqlParameter>()
                {
                    new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                    new SqlParameter(CommonSQLParameterNames.BILLING_CYCLE_END_DAY, billPeriodEndDay),
                    new SqlParameter(CommonSQLParameterNames.BILLING_CYCLE_END_HOUR, billPeriodEndHour),
                };
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithRowCountResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_DEVICE_STATUS,
                parameters));
        }

        public void RemoveDeviceFromQueue(Action<string, string> logFunction, int groupNumber, string ICCID = null)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.GROUP_NUMBER, groupNumber),
                new SqlParameter(CommonSQLParameterNames.ICCID, ICCID),
            };
            sqlRetryPolicy.Execute(() =>
                SqlQueryHelper.ExecuteStoredProcedureWithRowCountResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_REMOVE_DEVICE_FROM_QUEUE,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public List<int> GetAllInventoryIds(Action<string, string> logFunction, int serviceProviderId)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
            };
            return sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_GET_ALL_INVENTORY_IDS,
                (dataReader) => ReadId(dataReader),
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public List<int> GetAllBillingGroupIds(Action<string, string> logFunction, int serviceProviderId, int currentInventoryId = 0)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
            };
            return sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_GET_ALL_BILLING_GROUP_IDS,
                (dataReader) => ReadId(dataReader),
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        public virtual int UpdateInventoriesPageStatusAndCheckSyncProgress(Action<string, string> logFunction, int serviceProviderId, int pageNumber, bool isSuccessful)
        {
            var parameters = new List<SqlParameter>()
                {
                    new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                    new SqlParameter(CommonSQLParameterNames.PAGE_NUMBER, pageNumber),
                    new SqlParameter(CommonSQLParameterNames.IS_SUCCESSFUL, isSuccessful),
                };
            return sqlRetryPolicy.Execute(() =>
             SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_INVENTORIES_PAGE_SYNC_PROGRESS,
                parameters,
                defaultValue: -1));
        }

        public virtual int UpdateBillingGroupsPageStatusAndCheckSyncProgress(Action<string, string> logFunction, int serviceProviderId, int inventoryId, int pageNumber, bool isSuccessful)
        {
            var parameters = new List<SqlParameter>()
                {
                    new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                    new SqlParameter(CommonSQLParameterNames.INVENTORY_ID, inventoryId),
                    new SqlParameter(CommonSQLParameterNames.PAGE_NUMBER, pageNumber),
                    new SqlParameter(CommonSQLParameterNames.IS_SUCCESSFUL, isSuccessful),
                };
            return sqlRetryPolicy.Execute(() =>
             SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_BILLING_GROUPS_PAGE_SYNC_PROGRESS,
                parameters,
                defaultValue: -1));
        }

        public virtual int UpdateDevicesPageStatusAndCheckSyncProgress(Action<string, string> logFunction, int serviceProviderId, int billingGroupId, int pageNumber, bool isSuccessful)
        {
            var parameters = new List<SqlParameter>()
                {
                    new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID, serviceProviderId),
                    new SqlParameter(CommonSQLParameterNames.BILLING_GROUP_ID, billingGroupId),
                    new SqlParameter(CommonSQLParameterNames.PAGE_NUMBER, pageNumber),
                    new SqlParameter(CommonSQLParameterNames.IS_SUCCESSFUL, isSuccessful),
                };
            return sqlRetryPolicy.Execute(() =>
             SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_DEVICES_PAGE_SYNC_PROGRESS,
                parameters,
                defaultValue: -1));
        }

        public int CountRemainingDeviceStatusToProcess(Action<string, string> logFunction)
        {
            return sqlRetryPolicy.Execute(() =>
                SqlQueryHelper.ExecuteStoredProcedureWithIntResult(
                logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_COUNT_REMAINING_DEVICE_STATUS_TO_PROCESS,
                null,
                SQLConstant.ShortTimeoutSeconds));
        }

        public void TruncatePondDeviceStatusICCIDsToProcessTable(Action<string, string> logFunction)
        {
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithRowCountResult(
                logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_DEVICE_STATUS_ICCIDS_TRUNCATE_OPERATION,
                null,
                SQLConstant.ShortTimeoutSeconds));
        }

        public DeviceChangeResult<string, string> AddDeviceCarrierRatePlan(Action<string, string> logFunction, PondDeviceCarrierRatePlanResponse pondDeviceCarrierRatePlan, int serviceProviderId, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.ICCID, pondDeviceCarrierRatePlan.SimIccid),
                new SqlParameter(CommonSQLParameterNames.PACKAGE_IDS_PASCAL_CASE, pondDeviceCarrierRatePlan.PackageId),
                new SqlParameter(CommonSQLParameterNames.PACKAGE_TYPE_ID_PASCAL_CASE, pondDeviceCarrierRatePlan.PackageTypeId),
                new SqlParameter(CommonSQLParameterNames.STATUS_PASCAL_CASE, pondDeviceCarrierRatePlan.Status),
                new SqlParameter(CommonSQLParameterNames.DATA_USAGE_REMAINING_IN_BYTES_PASCAL_CASE, pondDeviceCarrierRatePlan.DataUsageRemainingInBytes),
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY_PASCAL_CASE, processedBy),
            };
            sqlRetryPolicy.Execute(() => SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_ADD_DEVICE_CARRIER_RATE_PLAN,
                parameters,
                SQLConstant.ShortTimeoutSeconds));

            return new DeviceChangeResult<string, string>()
            {
                ActionText = SQLConstant.StoredProcedureName.DEVICE_UPDATE_CARRIER_RATE_PLAN,
                RequestObject = string.Empty,
                HasErrors = false,
                ResponseObject = CommonConstants.OK
            };
        }

        public DeviceChangeResult<string, string> UpdateDeviceCarrierRatePlanStatus(Action<string, string> logFunction, string packageIds, string status, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.PACKAGE_IDS_PASCAL_CASE, packageIds),
                new SqlParameter(CommonSQLParameterNames.STATUS_PASCAL_CASE, status),
                new SqlParameter(CommonSQLParameterNames.PROCESSED_BY_PASCAL_CASE, processedBy),
            };
            sqlRetryPolicy.Execute(() => SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_DEVICE_CARRIER_RATE_PLAN_STATUS,
                parameters,
                SQLConstant.ShortTimeoutSeconds));

            return new DeviceChangeResult<string, string>()
            {
                ActionText = SQLConstant.StoredProcedureName.POND_UPDATE_DEVICE_CARRIER_RATE_PLAN_STATUS,
                RequestObject = packageIds,
                HasErrors = false,
                ResponseObject = CommonConstants.OK
            };
        }

        public List<string> GetExistingPackages(Action<string, string> logFunction, string iccid, int serviceProviderId, string status)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.ICCID, iccid),
                new SqlParameter(CommonSQLParameterNames.STATUS_PASCAL_CASE, status),
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
            };
            return sqlRetryPolicy.Execute(() => SqlQueryHelper.ExecuteStoredProcedureWithListResult<string>(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_GET_EXISTING_PACKAGES_BY_ICCID_AND_STATUS,
                (dataReader) => ReadExistingCarrierRatePlans(dataReader),
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }
        public void LoadRatePlanFromStagingTable(Action<string, string> logFunction, int serviceProviderId, string processedBy)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.SERVICE_PROVIDER_ID_PASCAL_CASE, serviceProviderId),
            };
            sqlRetryPolicy.Execute(() =>
            SqlQueryHelper.ExecuteStoredProcedureWithIntResult(logFunction,
                connectionString,
                SQLConstant.StoredProcedureName.POND_UPDATE_CARRIER_RATE_PLAN_FROM_STAGING,
                parameters,
                SQLConstant.ShortTimeoutSeconds));
        }

        private string ReadExistingCarrierRatePlans(SqlDataReader dataReader)
        {
            var columns = dataReader.GetColumnsFromReader();
            return dataReader.StringFromReader(columns, CommonColumnNames.PackageId);
        }

        private PondAuthentication ReadPondAuthentication(SqlDataReader pondDataReader, IBase64Service base64Service)
        {
            return new PondAuthentication(base64Service, pondDataReader);
        }

        private int ReadId(SqlDataReader dataReader)
        {
            var columns = dataReader.GetColumnsFromReader();
            return dataReader.IntFromReader(columns, CommonColumnNames.Id);
        }

        private int ReadStagedServiceProviderId(SqlDataReader dataReader)
        {
            var columns = dataReader.GetColumnsFromReader();
            return dataReader.IntFromReader(columns, CommonColumnNames.ServiceProviderId);
        }
    }
}

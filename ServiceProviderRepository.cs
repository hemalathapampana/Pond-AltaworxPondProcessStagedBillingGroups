using System;
using System.Collections.Generic;
using System.Data;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using Amop.Core.Logger;
using Amop.Core.Models;
using Amop.Core.Resilience;
using Microsoft.Data.SqlClient;
using Polly;

namespace Amop.Core.Repositories
{
    public class ServiceProviderRepository : IServiceProviderRepository
    {
        private const int MaxRetries = 3;
        private readonly string connectionString;
        private readonly ISyncPolicy sqlRetryPolicy;

        public ServiceProviderRepository(string connectionString)
            : this(connectionString, new NoOpLogger())
        {
        }

        public ServiceProviderRepository(string connectionString, IKeysysLogger logger)
            : this(connectionString, new PolicyFactory(logger))
        {
        }

        public ServiceProviderRepository(string connectionString, IPolicyFactory policyFactory)
            : this(connectionString, policyFactory.GetSqlRetryPolicy(MaxRetries))
        {
        }

        public ServiceProviderRepository(string connectionString, ISyncPolicy sqlRetryPolicy)
        {
            this.connectionString = connectionString;
            this.sqlRetryPolicy = sqlRetryPolicy;
        }

        public ICollection<ServiceProvider> GetEbondingServiceProviders()
        {
            return sqlRetryPolicy.Execute(() =>
            {
                var serviceProviders = new List<ServiceProvider>();
                using (var connection = new SqlConnection(connectionString))
                {
                    using (var command = new SqlCommand("usp_eBonding_Get_Active_ServiceProviders", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        connection.Open();

                        var rdr = command.ExecuteReader();
                        while (rdr.Read())
                        {
                            serviceProviders.Add(new ServiceProvider { Id = (int)rdr["ServiceProviderId"], TenantId = (int)rdr["TenantId"] });
                        }
                    }
                }

                return serviceProviders;
            });
        }

        public virtual List<int> GetAllServiceProviderIds(Action<string, string> logFunction, IntegrationType integrationType)
        {
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.INTEGRATION_ID, (int)integrationType),
            };
            var serviceProviderIds = sqlRetryPolicy.Execute(() =>
                SqlQueryHelper.ExecuteStoredProcedureWithListResult(logFunction, connectionString,
                    SQLConstant.StoredProcedureName.GET_ALL_SERVICE_PROVIDER_IDS_BY_INTEGRATION,
                    (dataReader) => ReadServiceProviderId(dataReader),
                    parameters,
                    SQLConstant.ShortTimeoutSeconds));
            return serviceProviderIds;
        }

        private int ReadServiceProviderId(SqlDataReader dataReader)
        {
            var columns = dataReader.GetColumnsFromReader();
            return dataReader.IntFromReader(columns, CommonColumnNames.Id);
        }
    }
}

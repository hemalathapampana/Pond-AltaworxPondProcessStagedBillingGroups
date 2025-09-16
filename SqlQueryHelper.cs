using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Amop.Core.Constants;
using Microsoft.Data.SqlClient;

namespace Amop.Core.Helpers
{
    public static class SqlQueryHelper
    {
        public static List<T> ExecuteStoredProcedureWithListResult<T>(Action<string, string> logFunction, string connectionString, string storedProcedureName, Func<SqlDataReader, T> parseFunction, List<SqlParameter> parameters = null, int commandTimeout = SQLConstant.TimeoutSeconds, bool shouldThrowOnException = false)
        {
            CheckAllExecuteStoredProcedureParameters(logFunction, connectionString, storedProcedureName);
            CheckValidObjectParameter(parseFunction, "parseFunction");
            logFunction(CommonConstants.SUB, "");
            var resultList = new List<T>();
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    using (var command = new SqlCommand(storedProcedureName, connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.CommandTimeout = commandTimeout;
                        if (parameters != null && parameters.Count > 0)
                        {
                            command.Parameters.AddRange(parameters.CloneParameters().ToArray());
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName) + string.Join(Environment.NewLine, parameters.Select(parameter => $"{parameter.ParameterName}: {parameter.Value}")));
                        }
                        else
                        {
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName));
                        }
                        connection.Open();

                        SqlDataReader dataReader = command.ExecuteReader();
                        while (dataReader.Read())
                        {
                            resultList.Add(parseFunction(dataReader));
                        }
                        // Clearing existing parameters before adding new ones.
                        // This is useful when reusing the same SqlCommand for multiple queries.
                        command.Parameters.Clear();
                    }
                }
            }
            catch (SqlException ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_EXECUTING_SQL_COMMAND, string.Join(". ", ex.Message, ex.ErrorCode, ex.Number, ex.StackTrace)));
            }
            catch (InvalidOperationException ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_CONNECTING_DATABASE, string.Join(". ", ex.Message, ex.StackTrace)));
            }
            catch (Exception ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Join(". ", ex.Message, ex.StackTrace));
            }
            return resultList;
        }

        public static int ExecuteStoredProcedureWithRowCountResult(Action<string, string> logFunction, string connectionString, string storedProcedureName, List<SqlParameter> parameters = null, int commandTimeout = SQLConstant.TimeoutSeconds, bool shouldExceptionOnNoRowsAffected = false, bool shouldThrowOnException = false)
        {
            CheckAllExecuteStoredProcedureParameters(logFunction, connectionString, storedProcedureName);
            logFunction(CommonConstants.SUB, "");
            var affectedRows = 0;
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    using (var command = new SqlCommand(storedProcedureName, connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.CommandTimeout = commandTimeout;
                        if (parameters != null && parameters.Count > 0)
                        {
                            command.Parameters.AddRange(parameters.ToArray());
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName) + string.Join(Environment.NewLine, parameters.Select(parameter => $"{parameter.ParameterName}: {parameter.Value}")));
                        }
                        else
                        {
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName));
                        }
                        connection.Open();

                        affectedRows = command.ExecuteNonQuery();
                        logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.ROWS_AFFECTED_WHEN_EXECUTING_STORED_PROCEDURE, affectedRows, storedProcedureName));
                        if (affectedRows <= 0)
                        {
                            // Possible that affected rows being 0 is expected or not an error. Like when truncating empty tables.
                            var logTypeForNoAffectedRow = CommonConstants.INFO;
                            if (shouldExceptionOnNoRowsAffected)
                            {
                                logTypeForNoAffectedRow = CommonConstants.EXCEPTION;
                            }
                            // Log as "No row(s)..." since the returned affectedRows could also be "-1" if "SET NOCOUNT ON" command is in the stored procedure 
                            logFunction(logTypeForNoAffectedRow, string.Format(LogCommonStrings.ROWS_AFFECTED_WHEN_EXECUTING_STORED_PROCEDURE, "No", storedProcedureName));
                        }
                        // Clearing existing parameters before adding new ones.
                        // This is useful when reusing the same SqlCommand for multiple queries.
                        command.Parameters.Clear();
                    }
                }
            }
            catch (SqlException ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_EXECUTING_SQL_COMMAND, string.Join(". ", ex.Message, ex.ErrorCode, ex.Number, ex.StackTrace)));
            }
            catch (InvalidOperationException ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_CONNECTING_DATABASE, string.Join(". ", ex.Message, ex.StackTrace)));
            }
            catch (Exception ex)
            {
                if (shouldThrowOnException)
                {
                    throw ex;
                }
                logFunction(CommonConstants.EXCEPTION, string.Join(". ", ex.Message, ex.StackTrace));
            }
            return affectedRows;
        }

        public static int ExecuteStoredProcedureWithIntResult(Action<string, string> logFunction, string connectionString, string storedProcedureName, List<SqlParameter> parameters = null, int commandTimeout = SQLConstant.TimeoutSeconds, bool shouldExceptionOnNoRowsAffected = false, int defaultValue = 0, bool shouldThrowOnException = false)
        {
            CheckAllExecuteStoredProcedureParameters(logFunction, connectionString, storedProcedureName);
            logFunction(CommonConstants.SUB, "");
            var result = defaultValue;
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    using (var command = new SqlCommand(storedProcedureName, connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.CommandTimeout = commandTimeout;
                        if (parameters != null && parameters.Count > 0)
                        {
                            command.Parameters.AddRange(parameters.ToArray());
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName) + string.Join(Environment.NewLine, parameters.Select(parameter => $"{parameter.ParameterName}: {parameter.Value}")));
                        }
                        else
                        {
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName));
                        }
                        connection.Open();

                        var databaseValue = command.ExecuteScalar();
                        if (databaseValue != null && databaseValue != DBNull.Value)
                        {
                            result = (int)databaseValue;
                        }
                        logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.VALUE_FOUND_WHEN_EXECUTING_STORED_PROCEDURE, result, storedProcedureName));
                        if (result <= 0)
                        {
                            // Possible that id being 0 is expected when there are no records.
                            var logTypeForNoAffectedRow = CommonConstants.INFO;
                            if (shouldExceptionOnNoRowsAffected)
                            {
                                logTypeForNoAffectedRow = CommonConstants.EXCEPTION;
                            }
                            logFunction(logTypeForNoAffectedRow, string.Format(LogCommonStrings.NO_VALUE_FOUND_WHEN_EXECUTING_STORED_PROCEDURE, storedProcedureName));
                        }
                        // Clearing existing parameters before adding new ones.
                        // This is useful when reusing the same SqlCommand for multiple queries.
                        command.Parameters.Clear();
                    }
                }
            }
            catch (SqlException ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_EXECUTING_SQL_COMMAND, string.Join(". ", ex.Message, ex.ErrorCode, ex.Number, ex.StackTrace)));
            }
            catch (InvalidOperationException ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_CONNECTING_DATABASE, string.Join(". ", ex.Message, ex.StackTrace)));
            }
            catch (Exception ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Join(". ", ex.Message, ex.StackTrace));
            }
            return result;
        }

        public static T ExecuteStoredProcedureWithSingleValueResult<T>(Action<string, string> logFunction, string connectionString, string storedProcedureName, string outputParamName, T defaultValue, List<SqlParameter> parameters = null, int commandTimeout = SQLConstant.TimeoutSeconds, bool shouldExceptionOnNoRowsAffected = false, bool shouldThrowOnException = false)
        {
            CheckAllExecuteStoredProcedureParameters(logFunction, connectionString, storedProcedureName);
            logFunction(CommonConstants.SUB, "");
            var result = defaultValue;
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    using (var command = new SqlCommand(storedProcedureName, connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.CommandTimeout = commandTimeout;
                        if (parameters != null && parameters.Count > 0)
                        {
                            command.Parameters.AddRange(parameters.CloneParameters().ToArray());
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName) + string.Join(Environment.NewLine, parameters.Select(parameter => $"{parameter.ParameterName}: {parameter.Value}")));
                        }
                        else
                        {
                            logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.EXECUTING_STORED_PROCEDURE_WITH_PARAMETERS, storedProcedureName));
                        }
                        connection.Open();

                        command.ExecuteNonQuery();
                        if (!string.IsNullOrWhiteSpace(command.Parameters[outputParamName].Value.ToString()))
                        {
                            result = (T)command.Parameters[outputParamName].Value;
                        }
                        logFunction(CommonConstants.INFO, string.Format(LogCommonStrings.VALUE_FOUND_WHEN_EXECUTING_STORED_PROCEDURE, result, storedProcedureName));

                        // Clearing existing parameters before adding new ones.
                        // This is useful when reusing the same SqlCommand for multiple queries.
                        command.Parameters.Clear();
                    }
                }
            }
            catch (SqlException ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_EXECUTING_SQL_COMMAND, string.Join(". ", ex.Message, ex.ErrorCode, ex.Number, ex.StackTrace)));
            }
            catch (InvalidOperationException ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.EXCEPTION_WHEN_CONNECTING_DATABASE, string.Join(". ", ex.Message, ex.StackTrace)));
            }
            catch (Exception ex)
            {
                if (shouldThrowOnException)
                {
                    throw;
                }
                logFunction(CommonConstants.EXCEPTION, string.Join(". ", ex.Message, ex.StackTrace));
            }
            return result;
        }

        private static void CheckAllExecuteStoredProcedureParameters(Action<string, string> logFunction, string connectionString, string storedProcedureName)
        {
            // Check for all the required information to run the stored procedure
            CheckValidObjectParameter(logFunction, "logFunction");
            CheckValidStringParameter(connectionString, "connectionString");
            CheckValidStringParameter(storedProcedureName, "storedProcedureName");
        }

        private static void CheckValidStringParameter(string parameter, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameter))
            {
                // Throw exception since this indicates bug in code logic
                throw new ArgumentNullException(string.Join(". ", CommonConstants.EXCEPTION, string.Format(LogCommonStrings.INVALID_GENERIC_FUNCTION_INITIALIZATION, parameterName)));
            }
        }
        private static void CheckValidObjectParameter(object parameter, string parameterName)
        {
            if (parameter == null)
            {
                // Throw exception since this indicates bug in code logic
                throw new ArgumentNullException(string.Join(". ", CommonConstants.EXCEPTION, string.Format(LogCommonStrings.INVALID_GENERIC_FUNCTION_INITIALIZATION, parameterName)));
            }
        }
        public static List<SqlParameter> CloneParameters(this List<SqlParameter> parameters)
        {
            return parameters.Select(x =>
            {
                var clonedParam = new SqlParameter(x.ParameterName, x.Value);
                clonedParam.Direction = x.Direction;
                clonedParam.DbType = x.DbType;
                clonedParam.SqlDbType = x.SqlDbType;
                return clonedParam;
            }
            ).ToList();
        }
    }
}

using Altaworx.AWS.Core;
using Altaworx.AWS.Core.Models;
using Altaworx.AWS.Core.Services.SQS;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amop.Core.Constants;
using Amop.Core.Helpers.Pond;
using Amop.Core.Models;
using Amop.Core.Repositories;
using Amop.Core.Repositories.Environment;
using Amop.Core.Repositories.Pond;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AltaworxPondProcessStagedBillingGroups;

public class Function : AwsFunctionBase
{
    private string GetDevicesQueueURL = string.Empty;
    public PondRepository pondRepository;
    public ServiceProviderRepository serviceProviderRepository;
    protected SqsService sqsService = new SqsService();
    private readonly EnvironmentRepository environmentRepo = new EnvironmentRepository();

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        AmopLambdaContext? lambdaContext = null;
        try
        {
            lambdaContext = BaseAmopFunctionHandler(context);
            ArgumentNullException.ThrowIfNull(lambdaContext);

            InitializeRepositories(lambdaContext);

            TryGetAllEnvironmentVariables(lambdaContext);

            await ProcessEventAsync(lambdaContext, sqsEvent);
        }
        catch (Exception ex)
        {
            if (lambdaContext == null)
            {
                context.Logger.Log(CommonConstants.EXCEPTION, ex.Message + " " + ex.StackTrace);
            }
            else
            {
                LogInfo(lambdaContext, CommonConstants.EXCEPTION, ex.Message + " " + ex.StackTrace);
            }
        }

        base.CleanUp(lambdaContext);
    }

    protected virtual void InitializeRepositories(AmopLambdaContext lambdaContext)
    {
        pondRepository = new PondRepository(lambdaContext.CentralDbConnectionString);
        serviceProviderRepository = new ServiceProviderRepository(lambdaContext.CentralDbConnectionString);
    }

    protected void TryGetAllEnvironmentVariables(AmopLambdaContext lambdaContext)
    {
        // Lambda related configurations
        GetDevicesQueueURL = GetStringValueFromEnvironmentVariable(lambdaContext.Context, environmentRepo, PondHelper.CommonString.POND_GET_DEVICES_QUEUE_URL_VARIABLE_KEY);
    }

    protected SqsValues GetMessageValues(AmopLambdaContext context, SQSEvent.SQSMessage message)
    {
        return new SqsValues(context, message);
    }

    protected async Task ProcessEventAsync(AmopLambdaContext context, SQSEvent sqsEvent)
    {
        LogInfo(context, CommonConstants.SUB);
        if (sqsEvent?.Records != null)
        {
            var processedRecordCount = sqsEvent.Records.Count;
            LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.BEGINNING_PROCESS, processedRecordCount));
            foreach (var record in sqsEvent.Records)
            {
                LogInfo(context, CommonConstants.INFO, $"MessageId: {record.MessageId}");
                var sqsValues = GetMessageValues(context, record);
                await ProcessSyncBillingGroupsPageStatus(context, sqsValues);
            }
        }
    }

    protected async Task ProcessSyncBillingGroupsPageStatus(AmopLambdaContext context, SqsValues sqsValues)
    {
        // Update the page status
        // Also in same stored procedure, get the total page count that are still processing
        // This is to check the sync progress of all service providers
        var remainingPagesCount = pondRepository.UpdateBillingGroupsPageStatusAndCheckSyncProgress(ParameterizedLog(context), sqsValues.ServiceProviderId, sqsValues.InventoryId, sqsValues.PageNumber, sqsValues.IsSuccessful);
        // If all done, merge data to main table
        // Then send message to trigger sync billing groups
        if (remainingPagesCount == 0)
        {
            LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.ALL_PAGES_OF_STEP_HAVE_BEEN_STAGED, PondHelper.CommonString.POND_BILLING_GROUPS_STEP_NAME, DatabaseTableNames.POND_BILLING_GROUP));

            var serviceProviderIds = serviceProviderRepository.GetAllServiceProviderIds(ParameterizedLog(context), IntegrationType.Pond);
            foreach (var serviceProviderId in serviceProviderIds)
            {
                pondRepository.LoadBillingGroupFromStagingTable(ParameterizedLog(context), serviceProviderId, context.Context.FunctionName);
            }
            await InitializePondDeviceSyncProcess(context);
        }
        else
        {
            LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.REMAINING_PAGES_TO_PROCESS, remainingPagesCount, PondHelper.CommonString.POND_BILLING_GROUPS_STEP_NAME));
        }
    }

    protected async Task InitializePondDeviceSyncProcess(AmopLambdaContext context)
    {
        await sqsService.SendSQSMessage(ParameterizedLog(context), AwsCredentials(context), GetDevicesQueueURL);
    }
}
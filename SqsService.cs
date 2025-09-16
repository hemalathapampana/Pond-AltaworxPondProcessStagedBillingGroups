using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using System.Linq;

namespace Altaworx.AWS.Core.Services.SQS
{
    public class SqsService
    {
        public async Task SendSQSMessage(Action<string, string> logFunction, BasicAWSCredentials awsCredentials, string destinationQueueUrl, Dictionary<string, string> attributeDictionary = null, int delaySeconds = 0)
        {
            try
            {
                logFunction(CommonConstants.SUB, $"{(attributeDictionary != null ? string.Join(Environment.NewLine, attributeDictionary) : string.Empty)}");
                if (string.IsNullOrWhiteSpace(destinationQueueUrl) || !UrlHelper.CheckURLValid(destinationQueueUrl))
                {
                    logFunction(CommonConstants.EXCEPTION, string.Format(LogCommonStrings.INVALID_SQS_QUEUE_URL, destinationQueueUrl));
                    return;
                }
                using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
                {
                    var request = new SendMessageRequest
                    {
                        DelaySeconds = delaySeconds,
                        MessageBody = string.Format(LogCommonStrings.SENDING_SQS_MESSAGE_TO_URL, destinationQueueUrl),
                        QueueUrl = destinationQueueUrl,
                    };

                    if (attributeDictionary != null)
                    {
                        foreach (var attribute in attributeDictionary)
                        {
                            request.MessageAttributes.Add(attribute.Key, new MessageAttributeValue { DataType = nameof(String), StringValue = attribute.Value });
                        }
                    }

                    logFunction(CommonConstants.INFO, request.MessageBody);

                    var response = await RetryPolicyHelper.PollyRetryForSQSMessage().ExecuteAsync(async () => await client.SendMessageAsync(request));

                    logFunction(CommonConstants.INFO, $"{response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
            catch (Exception ex)
            {
                logFunction(CommonConstants.EXCEPTION, $"{ex.Message} - {ex.StackTrace}");
            }
        }
    }
}

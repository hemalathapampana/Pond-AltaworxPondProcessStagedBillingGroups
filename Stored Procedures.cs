CREATE PROCEDURE [dbo].[usp_Teal_Update_Device_From_Staging]  
    @ServiceProviderId INT,  
    @BillingCycleEndDay INT,  
    @BillingCycleEndHour INT,  
    @BillMonth INT,  
    @BillYear INT,  
    @NextBillCycleDate DATETIME  
AS  
BEGIN  
    BEGIN TRANSACTION  
  
    DECLARE @ActivatedStatusId INT = 59;  
    DECLARE @UnknownStatusId INT = 67;  
    DECLARE @TealIntegrationId INT = 12;  
    DECLARE @ProcessedBy NVARCHAR(50) = 'usp_Teal_Update_Device';  
    DECLARE @UnknownStatus NVARCHAR(50) = 'Unknown';  
  
    --============================================================================  
    -- Add Last Sync Date    
    -- *Keeping it consistent with Jasper  
    --============================================================================  
    INSERT INTO   
        [dbo].[TealDeviceDetailLastSyncDate]([LastSyncDate], [QueueCount], [ServiceProviderId])  
    SELECT   
        GETUTCDATE(),   
        COUNT(1),  
        @ServiceProviderId  
    FROM [dbo].[TealDeviceStaging];  
  
    MERGE [dbo].[TealDevice] AS TARGET  
    USING   
    (  
        SELECT   
            [ICCID],  
            [IMEI],  
            [MSISDN],  
            [EID],  
            [PlanName],  
            [PlanUuid],  
            [PlanId],  
            [DeviceStatus],  
            [DeviceStatusId],  
            [DeviceName],  
            [PlanChangeStatus],  
            [ClientId],  
            [ClientName],  
            [ClientTitle],  
            [ClientUuid],  
            [Sku],  
            [CreatedBy],  
            [CreatedDate],  
            [ServiceProviderId]  
        FROM (  
            SELECT   
                [ICCID],  
                [IMEI],  
                [MSISDN],  
                [EID],  
                [PlanName],  
                [PlanUuid],  
                [PlanId],  
                ROW_NUMBER() OVER(PARTITION BY [EID] ORDER BY [TealDeviceStaging].[CreatedDate] DESC) AS [RecordNumber],  
                [DeviceStatus].[Status] AS [DeviceStatus],  
                [DeviceStatus].[Id] as [DeviceStatusId],  
                [DeviceName],  
                [PlanChangeStatus],  
                [ClientId],  
                [ClientName],  
                [ClientTitle],  
                [ClientUuid],  
                [Sku],  
                [ServiceProviderId],  
                [CreatedBy],  
                TealDeviceStaging.[CreatedDate]  
            FROM [dbo].[TealDeviceStaging]  
            LEFT JOIN [dbo].[DeviceStatus] ON LOWER([TealDeviceStaging].[DeviceStatus]) = LOWER([DeviceStatus].[Status])  
            WHERE [TealDeviceStaging].[ICCID] IS NOT NULL  
            AND [DeviceStatus].[IntegrationId] = @TealIntegrationId  
        ) [tealDeviceStagingInformation]  
        WHERE [RecordNumber] = 1  
    )  AS [SOURCE]  
    ON  
        [TARGET].[EID] = [SOURCE].[EID] AND [TARGET].[IsActive] = 1  
    WHEN MATCHED  
        THEN  
            UPDATE   
            SET   
                [TARGET].[IMEI] = [SOURCE].[IMEI],  
                [TARGET].[MSISDN] = [SOURCE].[MSISDN],  
                [TARGET].[ICCID] = [SOURCE].[ICCID],  
                [TARGET].[PlanName] = [SOURCE].[PlanName],  
                [TARGET].[PlanUuid] = [SOURCE].[PlanUuid],  
                [TARGET].[PlanId] = [SOURCE].[PlanId],  
                [TARGET].[DeviceStatus] = [SOURCE].[DeviceStatus],  
                [TARGET].[DeviceStatusId] = [SOURCE].[DeviceStatusId],  
                [TARGET].[OldDeviceStatusId] = [TARGET].[DeviceStatusId],  
                [TARGET].[DeviceName] = [SOURCE].[DeviceName],  
                [TARGET].[PlanChangeStatus] = [SOURCE].[PlanChangeStatus],  
                [TARGET].[ClientId] = [SOURCE].[ClientId],  
                [TARGET].[ClientName] = [SOURCE].[ClientName],  
                [TARGET].[ClientTitle] = [SOURCE].[ClientTitle],  
                [TARGET].[ClientUuid] = [SOURCE].[ClientUuid],  
                [TARGET].[BillMonth] = @BillMonth,  
                [TARGET].[BillYear] = @BillYear,  
                [TARGET].[NextBillCycleDate] = @NextBillCycleDate,  
                [TARGET].[Sku] = [SOURCE].[Sku],  
                -- The LastActivatedDate is the change status date from another status to 'Activated'.  
                [TARGET].[LastActivatedDate] = CASE   
                                                    WHEN   
                                                        [TARGET].[DeviceStatusId] = @ActivatedStatusId AND [SOURCE].[DeviceStatusId] <> @ActivatedStatusId   
                                                    THEN   
                                                        GETUTCDATE()  
                                                    ELSE   
                                                        [TARGET].[LastActivatedDate]   
                                                    END  
    WHEN NOT MATCHED BY TARGET AND [SOURCE].[ServiceProviderId] = @ServiceProviderId  
        THEN  
            INSERT (  
                [ICCID],  
                [IMEI],  
                [MSISDN],  
                [EID],  
                [PlanName],  
                [PlanUuid],  
                [PlanId],  
                [DeviceStatus],  
                [DeviceStatusId],  
                [DeviceName],  
                [PlanChangeStatus],  
                [ClientId],  
                [ClientName],  
                [ClientTitle],  
                [ClientUuid],  
                [Sku],  
                [IsActive],  
                [CreatedBy],  
                [CreatedDate],  
                [ServiceProviderId],  
                [BillMonth],  
                [BillYear],  
                [NextBillCycleDate]  
            )   
            VALUES (  
                [SOURCE].[ICCID],  
                [SOURCE].[IMEI],  
                [SOURCE].[MSISDN],  
                [SOURCE].[EID],  
                [SOURCE].[PlanName],  
                [SOURCE].[PlanUuid],  
                [SOURCE].[PlanId],  
                [SOURCE].[DeviceStatus],  
                [SOURCE].[DeviceStatusId],  
                [SOURCE].[DeviceName],  
                [SOURCE].[PlanChangeStatus],  
                [SOURCE].[ClientId],  
                [SOURCE].[ClientName],  
                [SOURCE].[ClientTitle],  
                [SOURCE].[ClientUuid],  
                [SOURCE].[Sku],  
                1,  
                @ProcessedBy,  
                GETUTCDATE(),  
                [ServiceProviderId],  
                @BillMonth,  
                @BillYear,  
                @NextBillCycleDate  
            )  
        WHEN NOT MATCHED BY SOURCE AND [TARGET].[ServiceProviderId] = @ServiceProviderId  
        THEN  
            UPDATE   
            SET  
                [TARGET].[DeviceStatusId] = @UnknownStatusId,  
                [TARGET].[OldDeviceStatusId] = [TARGET].[DeviceStatusId],  
                [TARGET].[DeviceStatus] = @UnknownStatus,  
                [TARGET].[ModifiedBy] = @ProcessedBy,  
                [TARGET].[ModifiedDate] = GETUTCDATE();  
  
    --INSERT TealDeviceSyncAudit  
    DECLARE @DetailsCurrentDateTime DATETIME = GETUTCDATE();  
    DECLARE @BillingYear INT;  
    DECLARE @BillingMonth INT;  
    IF (DATEPART(day, @DetailsCurrentDateTime) > @BillingCycleEndDay)  
    BEGIN  
        SELECT @DetailsCurrentDateTime = CONVERT(DATETIME, CONVERT(VARCHAR(4), DATEPART(year, @DetailsCurrentDateTime)) + '/' + CONVERT(VARCHAR(2), DATEPART(month, @DetailsCurrentDateTime)) + '/1');  
        SELECT @DetailsCurrentDateTime = DATEADD(month, 1, @DetailsCurrentDateTime);  
        SELECT @BillingYear = DATEPART(year, @DetailsCurrentDateTime), @BillingMonth = DATEPART(month, @DetailsCurrentDateTime);  
    END  
    ELSE  
    BEGIN  
        SELECT @BillingYear = DATEPART(year, @DetailsCurrentDateTime), @BillingMonth = DATEPART(month, @DetailsCurrentDateTime);  
    END;  
  
    INSERT INTO [dbo].[TealDeviceSyncAudit] (  
            [LastSyncDate],  
            [ActiveCount],  
            [SuspendCount],  
            [CreatedBy],  
            [CreatedDate],  
            [IsActive],  
            [IsDeleted],  
            [BillYear],  
            [BillMonth],  
            [ServiceProviderId])  
        SELECT  
            CAST(GETUTCDATE() AS [DATE]),  
            [ACTIVATED] AS [ActiveCount],  
            [DEACTIVATED] AS [SuspendCount],  
            @ProcessedBy,  
            CURRENT_TIMESTAMP,  
            1,  
            0,  
            @BillingMonth,  
            @BillingYear,  
            [ServiceProviderId]  
        FROM  
            (  
                SELECT   
                    [TealDevice].[ServiceProviderId],  
                    [DeviceStatus].[Status],  
                    COUNT(*) AS [total]  
                FROM [dbo].[TealDevice]  
                INNER JOIN [dbo].[DeviceStatus] ON [TealDevice].[DeviceStatusId] = [DeviceStatus].[id]  
                GROUP BY [TealDevice].[ServiceProviderId], [DeviceStatus].[Status]  
            ) AS [summary] PIVOT(SUM([total]) FOR [Status] IN ([ACTIVATED], [DEACTIVATED]))    
            AS [tealDeviceSyncAudit]  
        WHERE [ServiceProviderId] = @ServiceProviderId;  
  
    COMMIT TRANSACTION;  
ENDCREATE PROCEDURE [dbo].[usp_Teal_Update_Device_From_Staging]  
    @ServiceProviderId INT,  
    @BillingCycleEndDay INT,  
    @BillingCycleEndHour INT,  
    @BillMonth INT,  
    @BillYear INT,  
    @NextBillCycleDate DATETIME  
AS  
BEGIN  
    BEGIN TRANSACTION  
  
    DECLARE @ActivatedStatusId INT = 59;  
    DECLARE @UnknownStatusId INT = 67;  
    DECLARE @TealIntegrationId INT = 12;  
    DECLARE @ProcessedBy NVARCHAR(50) = 'usp_Teal_Update_Device';  
    DECLARE @UnknownStatus NVARCHAR(50) = 'Unknown';  
  
    --============================================================================  
    -- Add Last Sync Date    
    -- *Keeping it consistent with Jasper  
    --============================================================================  
    INSERT INTO   
        [dbo].[TealDeviceDetailLastSyncDate]([LastSyncDate], [QueueCount], [ServiceProviderId])  
    SELECT   
        GETUTCDATE(),   
        COUNT(1),  
        @ServiceProviderId  
    FROM [dbo].[TealDeviceStaging];  
  
    MERGE [dbo].[TealDevice] AS TARGET  
    USING   
    (  
        SELECT   
            [ICCID],  
            [IMEI],  
            [MSISDN],  
            [EID],  
            [PlanName],  
            [PlanUuid],  
            [PlanId],  
            [DeviceStatus],  
            [DeviceStatusId],  
            [DeviceName],  
            [PlanChangeStatus],  
            [ClientId],  
            [ClientName],  
            [ClientTitle],  
            [ClientUuid],  
            [Sku],  
            [CreatedBy],  
            [CreatedDate],  
            [ServiceProviderId]  
        FROM (  
            SELECT   
                [ICCID],  
                [IMEI],  
                [MSISDN],  
                [EID],  
                [PlanName],  
                [PlanUuid],  
                [PlanId],  
                ROW_NUMBER() OVER(PARTITION BY [EID] ORDER BY [TealDeviceStaging].[CreatedDate] DESC) AS [RecordNumber],  
                [DeviceStatus].[Status] AS [DeviceStatus],  
                [DeviceStatus].[Id] as [DeviceStatusId],  
                [DeviceName],  
                [PlanChangeStatus],  
                [ClientId],  
                [ClientName],  
                [ClientTitle],  
                [ClientUuid],  
                [Sku],  
                [ServiceProviderId],  
                [CreatedBy],  
                TealDeviceStaging.[CreatedDate]  
            FROM [dbo].[TealDeviceStaging]  
            LEFT JOIN [dbo].[DeviceStatus] ON LOWER([TealDeviceStaging].[DeviceStatus]) = LOWER([DeviceStatus].[Status])  
            WHERE [TealDeviceStaging].[ICCID] IS NOT NULL  
            AND [DeviceStatus].[IntegrationId] = @TealIntegrationId  
        ) [tealDeviceStagingInformation]  
        WHERE [RecordNumber] = 1  
    )  AS [SOURCE]  
    ON  
        [TARGET].[EID] = [SOURCE].[EID] AND [TARGET].[IsActive] = 1  
    WHEN MATCHED  
        THEN  
            UPDATE   
            SET   
                [TARGET].[IMEI] = [SOURCE].[IMEI],  
                [TARGET].[MSISDN] = [SOURCE].[MSISDN],  
                [TARGET].[ICCID] = [SOURCE].[ICCID],  
                [TARGET].[PlanName] = [SOURCE].[PlanName],  
                [TARGET].[PlanUuid] = [SOURCE].[PlanUuid],  
                [TARGET].[PlanId] = [SOURCE].[PlanId],  
                [TARGET].[DeviceStatus] = [SOURCE].[DeviceStatus],  
                [TARGET].[DeviceStatusId] = [SOURCE].[DeviceStatusId],  
                [TARGET].[OldDeviceStatusId] = [TARGET].[DeviceStatusId],  
                [TARGET].[DeviceName] = [SOURCE].[DeviceName],  
                [TARGET].[PlanChangeStatus] = [SOURCE].[PlanChangeStatus],  
                [TARGET].[ClientId] = [SOURCE].[ClientId],  
                [TARGET].[ClientName] = [SOURCE].[ClientName],  
                [TARGET].[ClientTitle] = [SOURCE].[ClientTitle],  
                [TARGET].[ClientUuid] = [SOURCE].[ClientUuid],  
                [TARGET].[BillMonth] = @BillMonth,  
                [TARGET].[BillYear] = @BillYear,  
                [TARGET].[NextBillCycleDate] = @NextBillCycleDate,  
                [TARGET].[Sku] = [SOURCE].[Sku],  
                -- The LastActivatedDate is the change status date from another status to 'Activated'.  
                [TARGET].[LastActivatedDate] = CASE   
                                                    WHEN   
                                                        [TARGET].[DeviceStatusId] = @ActivatedStatusId AND [SOURCE].[DeviceStatusId] <> @ActivatedStatusId   
                                                    THEN   
                                                        GETUTCDATE()  
                                                    ELSE   
                                                        [TARGET].[LastActivatedDate]   
                                                    END  
    WHEN NOT MATCHED BY TARGET AND [SOURCE].[ServiceProviderId] = @ServiceProviderId  
        THEN  
            INSERT (  
                [ICCID],  
                [IMEI],  
                [MSISDN],  
                [EID],  
                [PlanName],  
                [PlanUuid],  
                [PlanId],  
                [DeviceStatus],  
                [DeviceStatusId],  
                [DeviceName],  
                [PlanChangeStatus],  
                [ClientId],  
                [ClientName],  
                [ClientTitle],  
                [ClientUuid],  
                [Sku],  
                [IsActive],  
                [CreatedBy],  
                [CreatedDate],  
                [ServiceProviderId],  
                [BillMonth],  
                [BillYear],  
                [NextBillCycleDate]  
            )   
            VALUES (  
                [SOURCE].[ICCID],  
                [SOURCE].[IMEI],  
                [SOURCE].[MSISDN],  
                [SOURCE].[EID],  
                [SOURCE].[PlanName],  
                [SOURCE].[PlanUuid],  
                [SOURCE].[PlanId],  
                [SOURCE].[DeviceStatus],  
                [SOURCE].[DeviceStatusId],  
                [SOURCE].[DeviceName],  
                [SOURCE].[PlanChangeStatus],  
                [SOURCE].[ClientId],  
                [SOURCE].[ClientName],  
                [SOURCE].[ClientTitle],  
                [SOURCE].[ClientUuid],  
                [SOURCE].[Sku],  
                1,  
                @ProcessedBy,  
                GETUTCDATE(),  
                [ServiceProviderId],  
                @BillMonth,  
                @BillYear,  
                @NextBillCycleDate  
            )  
        WHEN NOT MATCHED BY SOURCE AND [TARGET].[ServiceProviderId] = @ServiceProviderId  
        THEN  
            UPDATE   
            SET  
                [TARGET].[DeviceStatusId] = @UnknownStatusId,  
                [TARGET].[OldDeviceStatusId] = [TARGET].[DeviceStatusId],  
                [TARGET].[DeviceStatus] = @UnknownStatus,  
                [TARGET].[ModifiedBy] = @ProcessedBy,  
                [TARGET].[ModifiedDate] = GETUTCDATE();  
  
    --INSERT TealDeviceSyncAudit  
    DECLARE @DetailsCurrentDateTime DATETIME = GETUTCDATE();  
    DECLARE @BillingYear INT;  
    DECLARE @BillingMonth INT;  
    IF (DATEPART(day, @DetailsCurrentDateTime) > @BillingCycleEndDay)  
    BEGIN  
        SELECT @DetailsCurrentDateTime = CONVERT(DATETIME, CONVERT(VARCHAR(4), DATEPART(year, @DetailsCurrentDateTime)) + '/' + CONVERT(VARCHAR(2), DATEPART(month, @DetailsCurrentDateTime)) + '/1');  
        SELECT @DetailsCurrentDateTime = DATEADD(month, 1, @DetailsCurrentDateTime);  
        SELECT @BillingYear = DATEPART(year, @DetailsCurrentDateTime), @BillingMonth = DATEPART(month, @DetailsCurrentDateTime);  
    END  
    ELSE  
    BEGIN  
        SELECT @BillingYear = DATEPART(year, @DetailsCurrentDateTime), @BillingMonth = DATEPART(month, @DetailsCurrentDateTime);  
    END;  
  
    INSERT INTO [dbo].[TealDeviceSyncAudit] (  
            [LastSyncDate],  
            [ActiveCount],  
            [SuspendCount],  
            [CreatedBy],  
            [CreatedDate],  
            [IsActive],  
            [IsDeleted],  
            [BillYear],  
            [BillMonth],  
            [ServiceProviderId])  
        SELECT  
            CAST(GETUTCDATE() AS [DATE]),  
            [ACTIVATED] AS [ActiveCount],  
            [DEACTIVATED] AS [SuspendCount],  
            @ProcessedBy,  
            CURRENT_TIMESTAMP,  
            1,  
            0,  
            @BillingMonth,  
            @BillingYear,  
            [ServiceProviderId]  
        FROM  
            (  
                SELECT   
                    [TealDevice].[ServiceProviderId],  
                    [DeviceStatus].[Status],  
                    COUNT(*) AS [total]  
                FROM [dbo].[TealDevice]  
                INNER JOIN [dbo].[DeviceStatus] ON [TealDevice].[DeviceStatusId] = [DeviceStatus].[id]  
                GROUP BY [TealDevice].[ServiceProviderId], [DeviceStatus].[Status]  
            ) AS [summary] PIVOT(SUM([total]) FOR [Status] IN ([ACTIVATED], [DEACTIVATED]))    
            AS [tealDeviceSyncAudit]  
        WHERE [ServiceProviderId] = @ServiceProviderId;  
  
    COMMIT TRANSACTION;
END;



CREATE PROCEDURE[dbo].[usp_Teal_Truncate_Device_And_Usage_Staging]
AS
BEGIN  
 SET NOCOUNT ON;

TRUNCATE TABLE[dbo].[TealDeviceStaging] ;
TRUNCATE TABLE[dbo].[TealDeviceUsageStaging] ;
TRUNCATE TABLE[dbo].[TealDeviceUsageDailyStaging] ;
TRUNCATE TABLE[dbo].[TealDeviceSMSUsageStaging] ;
END
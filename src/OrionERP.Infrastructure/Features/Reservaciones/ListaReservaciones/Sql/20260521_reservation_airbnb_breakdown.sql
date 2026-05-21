SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReservationAirbnbBreakdown
    (
        ReservationID int NOT NULL CONSTRAINT PK_ReservationAirbnbBreakdown PRIMARY KEY,
        PayoutAmount decimal(18,2) NOT NULL,
        TaxableBase decimal(18,2) NOT NULL,
        RoomRateAmount decimal(18,2) NOT NULL,
        CleaningFee decimal(18,2) NOT NULL,
        IvaTransferredAmount decimal(18,2) NOT NULL,
        IvaRetainedAmount decimal(18,2) NOT NULL,
        IsrRetainedAmount decimal(18,2) NOT NULL,
        HostServiceFeeBaseAmount decimal(18,2) NOT NULL,
        HostServiceFeeIvaAmount decimal(18,2) NOT NULL,
        HostServiceFeeTotalAmount decimal(18,2) NOT NULL,
        GrossCfdiTotal decimal(18,2) NOT NULL,
        IvaRate decimal(9,6) NOT NULL,
        IvaRetentionRate decimal(9,6) NOT NULL,
        IsrRetentionRate decimal(9,6) NOT NULL,
        HostServiceFeeRate decimal(9,6) NOT NULL,
        HostServiceFeeIvaRate decimal(9,6) NOT NULL,
        UsedDefaultRates bit NOT NULL CONSTRAINT DF_ReservationAirbnbBreakdown_UsedDefaultRates DEFAULT (1),
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_ReservationAirbnbBreakdown_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_ReservationAirbnbBreakdown_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_ReservationAirbnbBreakdown_Reservation
            FOREIGN KEY (ReservationID) REFERENCES dbo.RESERVATION (ID)
            ON DELETE CASCADE
    );
END;
GO

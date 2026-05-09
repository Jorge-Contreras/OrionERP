SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('dbo.BusinessPartnerCfdiProfile', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BusinessPartnerCfdiProfile
    (
        BusinessPartnerId int NOT NULL CONSTRAINT PK_BusinessPartnerCfdiProfile PRIMARY KEY,
        FiscalName varchar(200) NOT NULL,
        TaxZipCode varchar(20) NOT NULL,
        FiscalRegime varchar(10) NOT NULL,
        DefaultCfdiUse varchar(10) NOT NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartnerCfdiProfile_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartnerCfdiProfile_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_BusinessPartnerCfdiProfile_BusinessPartner
            FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_BusinessPartnerCfdiProfile_FiscalName'
      AND object_id = OBJECT_ID('dbo.BusinessPartnerCfdiProfile')
)
BEGIN
    CREATE INDEX IX_BusinessPartnerCfdiProfile_FiscalName
        ON dbo.BusinessPartnerCfdiProfile (FiscalName);
END;
GO

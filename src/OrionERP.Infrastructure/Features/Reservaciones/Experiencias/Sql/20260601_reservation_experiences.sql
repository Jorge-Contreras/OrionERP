SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.ExperienceProvider', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExperienceProvider
    (
        ExperienceProviderID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ExperienceProvider PRIMARY KEY,
        Code nvarchar(80) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Description] nvarchar(1000) NULL,
        IsActive bit NOT NULL
            CONSTRAINT DF_ExperienceProvider_IsActive DEFAULT (1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperienceProvider_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperienceProvider_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.Experience', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Experience
    (
        ExperienceID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Experience PRIMARY KEY,
        ExperienceProviderID int NULL,
        Code nvarchar(100) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Description] nvarchar(2000) NULL,
        Category nvarchar(80) NOT NULL
            CONSTRAINT DF_Experience_Category DEFAULT (N'General'),
        SeasonStart date NULL,
        SeasonEnd date NULL,
        MinimumParticipants int NOT NULL
            CONSTRAINT DF_Experience_MinimumParticipants DEFAULT (1),
        MaximumParticipants int NULL,
        IsPublic bit NOT NULL
            CONSTRAINT DF_Experience_IsPublic DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_Experience_IsActive DEFAULT (1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Experience_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Experience_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Experience_Provider
            FOREIGN KEY (ExperienceProviderID) REFERENCES dbo.ExperienceProvider (ExperienceProviderID),
        CONSTRAINT CK_Experience_Participants
            CHECK (MinimumParticipants > 0 AND (MaximumParticipants IS NULL OR MaximumParticipants >= MinimumParticipants)),
        CONSTRAINT CK_Experience_Season
            CHECK (SeasonStart IS NULL OR SeasonEnd IS NULL OR SeasonEnd >= SeasonStart)
    );
END;

IF OBJECT_ID(N'dbo.ExperiencePackage', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExperiencePackage
    (
        ExperiencePackageID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ExperiencePackage PRIMARY KEY,
        ExperienceID int NOT NULL,
        Code nvarchar(80) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        ProviderPackageName nvarchar(160) NULL,
        [Description] nvarchar(1000) NULL,
        Includes nvarchar(2000) NULL,
        UnitPrice decimal(18,2) NOT NULL
            CONSTRAINT DF_ExperiencePackage_UnitPrice DEFAULT (0),
        TaxMode nvarchar(40) NOT NULL
            CONSTRAINT DF_ExperiencePackage_TaxMode DEFAULT (N'TaxableExclusive'),
        DisplayOrder int NOT NULL
            CONSTRAINT DF_ExperiencePackage_DisplayOrder DEFAULT (0),
        IsPublic bit NOT NULL
            CONSTRAINT DF_ExperiencePackage_IsPublic DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_ExperiencePackage_IsActive DEFAULT (1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperiencePackage_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperiencePackage_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_ExperiencePackage_Experience
            FOREIGN KEY (ExperienceID) REFERENCES dbo.Experience (ExperienceID),
        CONSTRAINT CK_ExperiencePackage_UnitPrice
            CHECK (UnitPrice >= 0),
        CONSTRAINT CK_ExperiencePackage_TaxMode
            CHECK (TaxMode IN (N'TaxableExclusive', N'TaxIncluded', N'NonTaxable'))
    );
END;

IF OBJECT_ID(N'dbo.ExperienceAddOn', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExperienceAddOn
    (
        ExperienceAddOnID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ExperienceAddOn PRIMARY KEY,
        ExperienceID int NOT NULL,
        Code nvarchar(80) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Description] nvarchar(1000) NULL,
        UnitPrice decimal(18,2) NOT NULL
            CONSTRAINT DF_ExperienceAddOn_UnitPrice DEFAULT (0),
        AppliesPerParticipant bit NOT NULL
            CONSTRAINT DF_ExperienceAddOn_AppliesPerParticipant DEFAULT (1),
        TaxMode nvarchar(40) NOT NULL
            CONSTRAINT DF_ExperienceAddOn_TaxMode DEFAULT (N'TaxableExclusive'),
        DisplayOrder int NOT NULL
            CONSTRAINT DF_ExperienceAddOn_DisplayOrder DEFAULT (0),
        IsPublic bit NOT NULL
            CONSTRAINT DF_ExperienceAddOn_IsPublic DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_ExperienceAddOn_IsActive DEFAULT (1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperienceAddOn_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_ExperienceAddOn_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_ExperienceAddOn_Experience
            FOREIGN KEY (ExperienceID) REFERENCES dbo.Experience (ExperienceID),
        CONSTRAINT CK_ExperienceAddOn_TaxMode
            CHECK (TaxMode IN (N'TaxableExclusive', N'TaxIncluded', N'NonTaxable'))
    );
END;

IF OBJECT_ID(N'dbo.Reservation_Experience', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reservation_Experience
    (
        ReservationExperienceID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Reservation_Experience PRIMARY KEY,
        ReservationID int NOT NULL,
        ExperienceID int NOT NULL,
        ExperiencePackageID int NOT NULL,
        ExperienceDate date NOT NULL,
        ExperienceNameSnapshot nvarchar(200) NOT NULL,
        PackageNameSnapshot nvarchar(160) NOT NULL,
        ProviderNameSnapshot nvarchar(200) NULL,
        PackageIncludesSnapshot nvarchar(2000) NULL,
        PayingParticipants int NOT NULL,
        NonPayingParticipants int NOT NULL
            CONSTRAINT DF_Reservation_Experience_NonPayingParticipants DEFAULT (0),
        UnitPriceSnapshot decimal(18,2) NOT NULL,
        PackageSubtotalSnapshot decimal(18,2) NOT NULL,
        AddOnsTotalSnapshot decimal(18,2) NOT NULL
            CONSTRAINT DF_Reservation_Experience_AddOnsTotalSnapshot DEFAULT (0),
        TotalSnapshot decimal(18,2) NOT NULL,
        TaxMode nvarchar(40) NOT NULL
            CONSTRAINT DF_Reservation_Experience_TaxMode DEFAULT (N'TaxableExclusive'),
        Notes nvarchar(1000) NULL,
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Reservation_Experience_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Reservation_Experience_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Reservation_Experience_Reservation
            FOREIGN KEY (ReservationID) REFERENCES dbo.RESERVATION (ID),
        CONSTRAINT FK_Reservation_Experience_Experience
            FOREIGN KEY (ExperienceID) REFERENCES dbo.Experience (ExperienceID),
        CONSTRAINT FK_Reservation_Experience_Package
            FOREIGN KEY (ExperiencePackageID) REFERENCES dbo.ExperiencePackage (ExperiencePackageID),
        CONSTRAINT CK_Reservation_Experience_Participants
            CHECK (PayingParticipants > 0 AND NonPayingParticipants >= 0),
        CONSTRAINT CK_Reservation_Experience_TaxMode
            CHECK (TaxMode IN (N'TaxableExclusive', N'TaxIncluded', N'NonTaxable'))
    );
END;

IF OBJECT_ID(N'dbo.Reservation_ExperienceAddOn', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reservation_ExperienceAddOn
    (
        ReservationExperienceAddOnID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Reservation_ExperienceAddOn PRIMARY KEY,
        ReservationExperienceID int NOT NULL,
        ExperienceAddOnID int NOT NULL,
        AddOnNameSnapshot nvarchar(160) NOT NULL,
        Quantity int NOT NULL,
        UnitPriceSnapshot decimal(18,2) NOT NULL,
        TotalSnapshot decimal(18,2) NOT NULL,
        TaxMode nvarchar(40) NOT NULL
            CONSTRAINT DF_Reservation_ExperienceAddOn_TaxMode DEFAULT (N'TaxableExclusive'),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Reservation_ExperienceAddOn_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Reservation_ExperienceAddOn_ReservationExperience
            FOREIGN KEY (ReservationExperienceID) REFERENCES dbo.Reservation_Experience (ReservationExperienceID)
            ON DELETE CASCADE,
        CONSTRAINT FK_Reservation_ExperienceAddOn_AddOn
            FOREIGN KEY (ExperienceAddOnID) REFERENCES dbo.ExperienceAddOn (ExperienceAddOnID),
        CONSTRAINT CK_Reservation_ExperienceAddOn_Quantity
            CHECK (Quantity > 0),
        CONSTRAINT CK_Reservation_ExperienceAddOn_TaxMode
            CHECK (TaxMode IN (N'TaxableExclusive', N'TaxIncluded', N'NonTaxable'))
    );
END;

IF COL_LENGTH(N'dbo.ExperiencePackage', N'ProviderPackageName') IS NULL
BEGIN
    ALTER TABLE dbo.ExperiencePackage
    ADD ProviderPackageName nvarchar(160) NULL;
END;

IF COL_LENGTH(N'dbo.ExperiencePackage', N'UnitPrice') IS NULL
BEGIN
    ALTER TABLE dbo.ExperiencePackage
    ADD UnitPrice decimal(18,2) NOT NULL
        CONSTRAINT DF_ExperiencePackage_UnitPrice DEFAULT (0) WITH VALUES;
END;

IF COL_LENGTH(N'dbo.ExperiencePackage', N'BaseCost') IS NOT NULL
BEGIN
    EXEC(N'
UPDATE dbo.ExperiencePackage
SET UnitPrice = BaseCost
WHERE ISNULL(UnitPrice, 0) = 0
  AND ISNULL(BaseCost, 0) > 0;');
END;

IF COL_LENGTH(N'dbo.ExperienceAddOn', N'AppliesPerParticipant') IS NULL
BEGIN
    ALTER TABLE dbo.ExperienceAddOn
    ADD AppliesPerParticipant bit NOT NULL
        CONSTRAINT DF_ExperienceAddOn_AppliesPerParticipant DEFAULT (1) WITH VALUES;
END;

IF COL_LENGTH(N'dbo.ExperienceAddOn', N'AppliesPerPayingParticipant') IS NOT NULL
BEGIN
    EXEC(N'
UPDATE dbo.ExperienceAddOn
SET AppliesPerParticipant = AppliesPerPayingParticipant;');
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ExperienceProvider') AND name = N'UX_ExperienceProvider_Code')
    CREATE UNIQUE INDEX UX_ExperienceProvider_Code ON dbo.ExperienceProvider (Code);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Experience') AND name = N'UX_Experience_Code')
    CREATE UNIQUE INDEX UX_Experience_Code ON dbo.Experience (Code);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ExperiencePackage') AND name = N'UX_ExperiencePackage_Experience_Code')
    CREATE UNIQUE INDEX UX_ExperiencePackage_Experience_Code ON dbo.ExperiencePackage (ExperienceID, Code);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ExperienceAddOn') AND name = N'UX_ExperienceAddOn_Experience_Code')
    CREATE UNIQUE INDEX UX_ExperienceAddOn_Experience_Code ON dbo.ExperienceAddOn (ExperienceID, Code);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Reservation_Experience') AND name = N'IX_Reservation_Experience_ReservationID')
    CREATE INDEX IX_Reservation_Experience_ReservationID ON dbo.Reservation_Experience (ReservationID);

DECLARE @ProviderId int;

SELECT @ProviderId = ExperienceProviderID
FROM dbo.ExperienceProvider
WHERE Code = N'avistamiento-las-4e';

IF @ProviderId IS NULL
BEGIN
    INSERT INTO dbo.ExperienceProvider (Code, [Name], [Description], IsActive)
    VALUES (N'avistamiento-las-4e', N'Avistamiento las 4E', N'Proveedor subcontratado para el avistamiento de luciernagas.', 1);

    SET @ProviderId = CAST(SCOPE_IDENTITY() AS int);
END
ELSE
BEGIN
    UPDATE dbo.ExperienceProvider
    SET [Name] = N'Avistamiento las 4E',
        [Description] = N'Proveedor subcontratado para el avistamiento de luciernagas.',
        IsActive = 1,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE ExperienceProviderID = @ProviderId;
END;

DECLARE @ExperienceId int;

SELECT @ExperienceId = ExperienceID
FROM dbo.Experience
WHERE Code = N'luciernagas-calpulalpan';

IF @ExperienceId IS NULL
BEGIN
    SELECT @ExperienceId = ExperienceID
    FROM dbo.Experience
    WHERE Code = N'luciernagas-calpulalpan-2026';
END;

IF @ExperienceId IS NULL
BEGIN
    INSERT INTO dbo.Experience
    (
        ExperienceProviderID,
        Code,
        [Name],
        [Description],
        Category,
        SeasonStart,
        SeasonEnd,
        MinimumParticipants,
        MaximumParticipants,
        IsPublic,
        IsActive
    )
    VALUES
    (
        @ProviderId,
        N'luciernagas-calpulalpan',
        N'Avistamiento de Luciernagas en Calpulalpan',
        N'Avistamiento de luciernagas en Calpulalpan con transporte incluido en el precio de la experiencia.',
        N'Turismo',
        '20260615',
        '20260815',
        1,
        NULL,
        1,
        1
    );

    SET @ExperienceId = CAST(SCOPE_IDENTITY() AS int);
END
ELSE
BEGIN
    UPDATE dbo.Experience
    SET ExperienceProviderID = @ProviderId,
        Code = N'luciernagas-calpulalpan',
        [Name] = N'Avistamiento de Luciernagas en Calpulalpan',
        [Description] = N'Avistamiento de luciernagas en Calpulalpan con transporte incluido en el precio de la experiencia.',
        Category = N'Turismo',
        SeasonStart = '20260615',
        SeasonEnd = '20260815',
        MinimumParticipants = 1,
        MaximumParticipants = NULL,
        IsPublic = 1,
        IsActive = 1,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE ExperienceID = @ExperienceId;
END;

DECLARE @Packages table
(
    Code nvarchar(80) NOT NULL,
    [Name] nvarchar(160) NOT NULL,
    ProviderPackageName nvarchar(160) NULL,
    [Description] nvarchar(1000) NULL,
    Includes nvarchar(2000) NULL,
    UnitPrice decimal(18,2) NOT NULL,
    DisplayOrder int NOT NULL
);

INSERT INTO @Packages (Code, [Name], ProviderPackageName, [Description], Includes, UnitPrice, DisplayOrder)
VALUES
    (N'esencial', N'Experiencia Esencial', N'Paquete Esencial', N'Recorrido base de luciernagas.', N'Estacionamiento; banos; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas.', 800, 10),
    (N'clasico', N'Experiencia Clasica', N'Paquete Clasico', N'Experiencia de luciernagas con atole y pan.', N'Estacionamiento; banos; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas; degustacion de atole y pan.', 900, 20),
    (N'gastronomico', N'Experiencia Gastronomica', N'Paquete Gastronomico', N'Experiencia de luciernagas con atole, pan y comida regional.', N'Estacionamiento; banos; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas; degustacion de atole y pan; comida tradicional.', 1200, 30);

MERGE dbo.ExperiencePackage AS target
USING @Packages AS source
    ON target.ExperienceID = @ExperienceId
   AND target.Code = source.Code
WHEN MATCHED THEN
    UPDATE SET
        [Name] = source.[Name],
        ProviderPackageName = source.ProviderPackageName,
        [Description] = source.[Description],
        Includes = source.Includes,
        UnitPrice = source.UnitPrice,
        TaxMode = N'TaxableExclusive',
        DisplayOrder = source.DisplayOrder,
        IsPublic = 1,
        IsActive = 1,
        UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT
    (
        ExperienceID,
        Code,
        [Name],
        ProviderPackageName,
        [Description],
        Includes,
        UnitPrice,
        TaxMode,
        DisplayOrder,
        IsPublic,
        IsActive
    )
    VALUES
    (
        @ExperienceId,
        source.Code,
        source.[Name],
        source.ProviderPackageName,
        source.[Description],
        source.Includes,
        source.UnitPrice,
        N'TaxableExclusive',
        source.DisplayOrder,
        1,
        1
    );

DECLARE @AddOns table
(
    Code nvarchar(80) NOT NULL,
    [Name] nvarchar(160) NOT NULL,
    [Description] nvarchar(1000) NULL,
    UnitPrice decimal(18,2) NOT NULL,
    AppliesPerParticipant bit NOT NULL,
    DisplayOrder int NOT NULL
);

INSERT INTO @AddOns (Code, [Name], [Description], UnitPrice, AppliesPerParticipant, DisplayOrder)
VALUES
    (N'tecoaque', N'Tecoaque', N'Visita libre opcional a Zona Arqueologica Tecoaque.', 300, 1, 10);

MERGE dbo.ExperienceAddOn AS target
USING @AddOns AS source
    ON target.ExperienceID = @ExperienceId
   AND target.Code = source.Code
WHEN MATCHED THEN
    UPDATE SET
        [Name] = source.[Name],
        [Description] = source.[Description],
        UnitPrice = source.UnitPrice,
        AppliesPerParticipant = source.AppliesPerParticipant,
        TaxMode = N'TaxableExclusive',
        DisplayOrder = source.DisplayOrder,
        IsPublic = 1,
        IsActive = 1,
        UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT
    (
        ExperienceID,
        Code,
        [Name],
        [Description],
        UnitPrice,
        AppliesPerParticipant,
        TaxMode,
        DisplayOrder,
        IsPublic,
        IsActive
    )
    VALUES
    (
        @ExperienceId,
        source.Code,
        source.[Name],
        source.[Description],
        source.UnitPrice,
        source.AppliesPerParticipant,
        N'TaxableExclusive',
        source.DisplayOrder,
        1,
        1
    );

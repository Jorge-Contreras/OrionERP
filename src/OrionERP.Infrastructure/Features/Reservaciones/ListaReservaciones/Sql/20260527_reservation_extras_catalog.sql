SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.Extra', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Extra
    (
        ExtraID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Extra PRIMARY KEY,
        [Name] nvarchar(150) NOT NULL,
        [Description] nvarchar(500) NULL,
        Price decimal(18,2) NOT NULL
            CONSTRAINT DF_Extra_Price DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_Extra_IsActive DEFAULT (1),
        LegacyRoomID int NULL,
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Extra_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Extra_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.Reservation_Extra', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reservation_Extra
    (
        ReservationExtraID int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Reservation_Extra PRIMARY KEY,
        ReservationID int NOT NULL,
        ExtraID int NOT NULL,
        ExtraNameSnapshot nvarchar(150) NOT NULL,
        ExtraDescriptionSnapshot nvarchar(500) NULL,
        UnitPriceSnapshot decimal(18,2) NOT NULL,
        Quantity int NOT NULL
            CONSTRAINT DF_Reservation_Extra_Quantity DEFAULT (1),
        Notes nvarchar(1000) NULL,
        LegacyReservationDetailID int NULL,
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Reservation_Extra_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Reservation_Extra_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Reservation_Extra_Reservation
            FOREIGN KEY (ReservationID) REFERENCES dbo.RESERVATION (ID),
        CONSTRAINT FK_Reservation_Extra_Extra
            FOREIGN KEY (ExtraID) REFERENCES dbo.Extra (ExtraID),
        CONSTRAINT CK_Reservation_Extra_Quantity_Positive
            CHECK (Quantity > 0)
    );
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Extra')
      AND name = N'UX_Extra_LegacyRoomID'
)
BEGIN
    CREATE UNIQUE INDEX UX_Extra_LegacyRoomID
        ON dbo.Extra (LegacyRoomID)
        WHERE LegacyRoomID IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Extra')
      AND name = N'IX_Extra_Name'
)
BEGIN
    CREATE INDEX IX_Extra_Name
        ON dbo.Extra ([Name]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Reservation_Extra')
      AND name = N'IX_Reservation_Extra_ReservationID'
)
BEGIN
    CREATE INDEX IX_Reservation_Extra_ReservationID
        ON dbo.Reservation_Extra (ReservationID);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Reservation_Extra')
      AND name = N'UX_Reservation_Extra_LegacyReservationDetailID'
)
BEGIN
    CREATE UNIQUE INDEX UX_Reservation_Extra_LegacyReservationDetailID
        ON dbo.Reservation_Extra (LegacyReservationDetailID)
        WHERE LegacyReservationDetailID IS NOT NULL;
END;

INSERT INTO dbo.Extra
(
    [Name],
    [Description],
    Price,
    IsActive,
    LegacyRoomID
)
SELECT
    LTRIM(RTRIM(r.ROOM_NAME)) AS [Name],
    NULLIF(LTRIM(RTRIM(r.ROOM_DESCRIPTION)), '') AS [Description],
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS Price,
    CAST(1 AS bit) AS IsActive,
    r.ID AS LegacyRoomID
FROM dbo.ROOM AS r
WHERE UPPER(LTRIM(RTRIM(ISNULL(r.ROOM_TYPE, '')))) = 'SERVICIO'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Extra AS e
      WHERE e.LegacyRoomID = r.ID
  )
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Extra AS e
      WHERE UPPER(LTRIM(RTRIM(e.[Name]))) = UPPER(LTRIM(RTRIM(r.ROOM_NAME)))
        AND e.LegacyRoomID IS NULL
  );

UPDATE e
SET LegacyRoomID = r.ID,
    UpdatedAtUtc = SYSUTCDATETIME()
FROM dbo.Extra AS e
INNER JOIN dbo.ROOM AS r
    ON UPPER(LTRIM(RTRIM(e.[Name]))) = UPPER(LTRIM(RTRIM(r.ROOM_NAME)))
WHERE e.LegacyRoomID IS NULL
  AND UPPER(LTRIM(RTRIM(ISNULL(r.ROOM_TYPE, '')))) = 'SERVICIO'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Extra AS existing
      WHERE existing.LegacyRoomID = r.ID
  );

INSERT INTO dbo.Reservation_Extra
(
    ReservationID,
    ExtraID,
    ExtraNameSnapshot,
    ExtraDescriptionSnapshot,
    UnitPriceSnapshot,
    Quantity,
    Notes,
    LegacyReservationDetailID
)
SELECT
    rd.RESERVATION_ID AS ReservationID,
    e.ExtraID,
    e.[Name] AS ExtraNameSnapshot,
    e.[Description] AS ExtraDescriptionSnapshot,
    CAST(ISNULL(rd.PRICE, e.Price) AS decimal(18,2)) AS UnitPriceSnapshot,
    1 AS Quantity,
    NULLIF(LTRIM(RTRIM(rd.NOTES)), '') AS Notes,
    rd.ID AS LegacyReservationDetailID
FROM dbo.RESERVATION_DETAIL AS rd
INNER JOIN dbo.ROOM AS r
    ON r.ID = rd.ROOM_ID
INNER JOIN dbo.Extra AS e
    ON e.LegacyRoomID = r.ID
WHERE rd.RESERVATION_ID IS NOT NULL
  AND UPPER(LTRIM(RTRIM(ISNULL(r.ROOM_TYPE, '')))) = 'SERVICIO'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Reservation_Extra AS re
      WHERE re.LegacyReservationDetailID = rd.ID
  );

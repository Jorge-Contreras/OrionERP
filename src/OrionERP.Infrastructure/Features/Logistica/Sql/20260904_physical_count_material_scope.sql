SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

/*
    Conteos por material (multi-ubicacion).

    Hasta ahora una sesion de conteo nacia siempre de UNA ubicacion y recorria su subarbol.
    Este script generaliza el alcance: una sesion puede nacer de una lista de materiales y
    recorrer todas las ubicaciones donde esos materiales tienen saldo. Contar-por-ubicacion
    queda como el caso sin filtro de material, sin cambio de comportamiento.
*/

-------------------------------------------------------------------------------
-- 1. Alcance de la sesion
-------------------------------------------------------------------------------

IF COL_LENGTH('logistica.PhysicalCountSession', 'ScopeType') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSession
        ADD ScopeType varchar(20) NOT NULL
            CONSTRAINT DF_PhysicalCountSession_ScopeType DEFAULT ('Location');
END;
GO

IF COL_LENGTH('logistica.PhysicalCountSession', 'MaxLocationsPerMaterial') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSession
        ADD MaxLocationsPerMaterial int NULL;
END;
GO

UPDATE logistica.PhysicalCountSession
SET ScopeType = 'Location'
WHERE ScopeType IS NULL
   OR LTRIM(RTRIM(ScopeType)) = '';
GO

/*
    LocationId pasa a ser opcional: un conteo por material no vive en una sola ubicacion.
    Las llaves foraneas se recrean alrededor del ALTER para no depender del comportamiento
    de SQL Server al alterar una columna referenciada.
*/
IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('logistica.PhysicalCountSession')
      AND name = 'LocationId'
      AND is_nullable = 0
)
BEGIN
    IF OBJECT_ID('logistica.FK_PhysicalCountSession_Location_Rfc', 'F') IS NOT NULL
        ALTER TABLE logistica.PhysicalCountSession DROP CONSTRAINT FK_PhysicalCountSession_Location_Rfc;

    IF OBJECT_ID('logistica.FK_PhysicalCountSession_Location', 'F') IS NOT NULL
        ALTER TABLE logistica.PhysicalCountSession DROP CONSTRAINT FK_PhysicalCountSession_Location;

    ALTER TABLE logistica.PhysicalCountSession ALTER COLUMN LocationId int NULL;
END;
GO

IF OBJECT_ID('logistica.FK_PhysicalCountSession_Location', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountSession
        ADD CONSTRAINT FK_PhysicalCountSession_Location
            FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id);
GO

IF OBJECT_ID('logistica.FK_PhysicalCountSession_Location_Rfc', 'F') IS NULL
   AND COL_LENGTH('logistica.PhysicalCountSession', 'Rfc') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Location') AND name = 'UX_Location_RfcId')
    ALTER TABLE logistica.PhysicalCountSession
        ADD CONSTRAINT FK_PhysicalCountSession_Location_Rfc
            FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
GO

-------------------------------------------------------------------------------
-- 2. Materiales que definen el alcance
-------------------------------------------------------------------------------

IF OBJECT_ID('logistica.PhysicalCountSessionMaterial', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountSessionMaterial
    (
        Id int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PhysicalCountSessionMaterial PRIMARY KEY,
        SessionId int NOT NULL,
        MaterialId int NOT NULL,
        CONSTRAINT FK_PhysicalCountSessionMaterial_Session
            FOREIGN KEY (SessionId) REFERENCES logistica.PhysicalCountSession (Id),
        CONSTRAINT FK_PhysicalCountSessionMaterial_Material
            FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF COL_LENGTH('logistica.PhysicalCountSessionMaterial', 'Rfc') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSessionMaterial ADD Rfc varchar(50) NULL;
END;
GO

UPDATE sessionMaterial
SET Rfc = countSession.Rfc
FROM logistica.PhysicalCountSessionMaterial sessionMaterial
JOIN logistica.PhysicalCountSession countSession
  ON countSession.Id = sessionMaterial.SessionId
WHERE sessionMaterial.Rfc IS NULL;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('logistica.PhysicalCountSessionMaterial')
      AND name = 'Rfc'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE logistica.PhysicalCountSessionMaterial ALTER COLUMN Rfc varchar(50) NOT NULL;
END;
GO

IF OBJECT_ID('logistica.DF_PhysicalCountSessionMaterial_Rfc', 'D') IS NULL
    ALTER TABLE logistica.PhysicalCountSessionMaterial
        ADD CONSTRAINT DF_PhysicalCountSessionMaterial_Rfc
            DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSessionMaterial') AND name = 'UX_PhysicalCountSessionMaterial_SessionMaterial')
    CREATE UNIQUE INDEX UX_PhysicalCountSessionMaterial_SessionMaterial
        ON logistica.PhysicalCountSessionMaterial (Rfc, SessionId, MaterialId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSessionMaterial') AND name = 'UX_PhysicalCountSessionMaterial_RfcId')
    CREATE UNIQUE INDEX UX_PhysicalCountSessionMaterial_RfcId
        ON logistica.PhysicalCountSessionMaterial (Rfc, Id);
GO

IF OBJECT_ID('logistica.FK_PhysicalCountSessionMaterial_Session_Rfc', 'F') IS NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSession') AND name = 'UX_PhysicalCountSession_RfcId')
    ALTER TABLE logistica.PhysicalCountSessionMaterial
        ADD CONSTRAINT FK_PhysicalCountSessionMaterial_Session_Rfc
            FOREIGN KEY (Rfc, SessionId) REFERENCES logistica.PhysicalCountSession (Rfc, Id);
GO

IF OBJECT_ID('logistica.FK_PhysicalCountSessionMaterial_Material_Rfc', 'F') IS NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Material') AND name = 'UX_Material_RfcId')
    ALTER TABLE logistica.PhysicalCountSessionMaterial
        ADD CONSTRAINT FK_PhysicalCountSessionMaterial_Material_Rfc
            FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
GO

-------------------------------------------------------------------------------
-- 3. Orden de recorrido del conteo
-------------------------------------------------------------------------------

/*
    CountSequence fija el orden ubicacion-primero (sala, codigo de ubicacion, material).
    Sin el, un conteo que cruza ubicaciones manda al contador de un extremo al otro del almacen.
*/
IF COL_LENGTH('logistica.PhysicalCountLine', 'CountSequence') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountLine ADD CountSequence int NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountLine') AND name = 'IX_PhysicalCountLine_SessionSequence')
    CREATE INDEX IX_PhysicalCountLine_SessionSequence
        ON logistica.PhysicalCountLine (SessionId, CountSequence, Id);
GO

UPDATE countLine
SET CountSequence = sequenced.NewSequence
FROM logistica.PhysicalCountLine countLine
JOIN
(
    SELECT
        pendingLine.Id,
        ROW_NUMBER() OVER
        (
            PARTITION BY pendingLine.SessionId
            ORDER BY room.ROOM_NAME, loc.LocationCode, material.[Description], material.MaterialCode, pendingLine.Id
        ) AS NewSequence
    FROM logistica.PhysicalCountLine pendingLine
    JOIN logistica.Location loc
      ON loc.Id = pendingLine.LocationId
    LEFT JOIN dbo.ROOM room
      ON room.ID = loc.RoomId
    JOIN logistica.Material material
      ON material.Id = pendingLine.MaterialId
) sequenced
  ON sequenced.Id = countLine.Id
WHERE countLine.CountSequence IS NULL;
GO

-------------------------------------------------------------------------------
-- 4. Row Level Security por RFC para la tabla nueva
-------------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.security_policies WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('logistica'))
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.security_predicates predicate
       JOIN sys.security_policies policy
         ON policy.object_id = predicate.object_id
       WHERE policy.[name] = 'RfcSecurityPolicy'
         AND policy.schema_id = SCHEMA_ID('logistica')
         AND predicate.target_object_id = OBJECT_ID('logistica.PhysicalCountSessionMaterial')
   )
BEGIN
    ALTER SECURITY POLICY logistica.RfcSecurityPolicy
        ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSessionMaterial,
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSessionMaterial AFTER INSERT,
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSessionMaterial AFTER UPDATE;
END;
GO

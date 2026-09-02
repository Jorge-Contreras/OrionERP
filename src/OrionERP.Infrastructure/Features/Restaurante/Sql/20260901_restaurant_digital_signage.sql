/*
  Pantallas de señalización digital por RFC.

  Simulación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260901_restaurant_digital_signage.sql
  Aplicación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260901_restaurant_digital_signage.sql

  ApplyChanges=0 ejecuta la misma migración y validaciones dentro de una
  transacción que se revierte.

  La migración crea las pantallas de Bruno's pero NO carga las imágenes: el
  servidor SQL es remoto y no puede leer archivos de la estación de trabajo.
  Los tableros se suben desde /restaurante/admin › Pantallas. Mientras tanto la
  ruta heredada /menus sigue sirviendo el respaldo estático de wwwroot.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51800, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51801, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChangesText NOT IN (N'0', N'1')
  THROW 51802, 'ApplyChanges debe ser 0 o 1.', 1;
SET @ApplyChanges = CONVERT(bit, @ApplyChangesText);
IF OBJECT_ID('restaurante.Site', 'U') IS NULL
  THROW 51803, 'Ejecuta primero las migraciones base de Restaurante.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  /* Una fila por televisor físico. */
  IF OBJECT_ID('restaurante.SignageScreen', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.SignageScreen
    (
      Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SignageScreen PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      SiteId int NULL,
      ScreenKey varchar(40) NOT NULL,
      [Name] varchar(120) NOT NULL,
      RotationSeconds int NOT NULL CONSTRAINT DF_SignageScreen_Rotation DEFAULT (8),
      RefreshSeconds int NOT NULL CONSTRAINT DF_SignageScreen_Refresh DEFAULT (300),
      TransitionMs int NOT NULL CONSTRAINT DF_SignageScreen_Transition DEFAULT (450),
      SortOrder int NOT NULL CONSTRAINT DF_SignageScreen_Sort DEFAULT (0),
      IsEnabled bit NOT NULL CONSTRAINT DF_SignageScreen_Enabled DEFAULT (1),
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SignageScreen_Created DEFAULT (SYSUTCDATETIME()),
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_SignageScreen_Updated DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(256) NULL,
      /* La colación binaria es la que realmente fuerza minúsculas: un LIKE
         insensible a mayúsculas dejaría pasar 'MENU'. 'media' y 'manifest.json'
         se reservan porque son segmentos hermanos en /menus/{rfc}/{...}. */
      CONSTRAINT CK_SignageScreen_Key CHECK
      (
        LEN(ScreenKey) BETWEEN 2 AND 40
        AND ScreenKey COLLATE Latin1_General_BIN2 NOT LIKE '%[^a-z0-9-]%'
        AND ScreenKey NOT IN ('media', 'manifest.json')
      ),
      CONSTRAINT CK_SignageScreen_Rotation CHECK (RotationSeconds BETWEEN 3 AND 3600),
      CONSTRAINT CK_SignageScreen_Refresh CHECK (RefreshSeconds BETWEEN 30 AND 86400),
      CONSTRAINT CK_SignageScreen_Transition CHECK (TransitionMs BETWEEN 0 AND 5000),
      CONSTRAINT FK_SignageScreen_Site_Rfc FOREIGN KEY (Rfc, SiteId)
        REFERENCES restaurante.Site (Rfc, Id),
      CONSTRAINT UX_SignageScreen_Key UNIQUE (Rfc, ScreenKey)
    );
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.SignageScreen') AND [name] = 'UX_SignageScreen_RfcId'
  )
    CREATE UNIQUE INDEX UX_SignageScreen_RfcId ON restaurante.SignageScreen (Rfc, Id);

  /* Imágenes ordenadas de cada pantalla. */
  IF OBJECT_ID('restaurante.SignageScreenImage', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.SignageScreenImage
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SignageScreenImage PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      ScreenId int NOT NULL,
      SortOrder int NOT NULL CONSTRAINT DF_SignageScreenImage_Sort DEFAULT (0),
      [FileName] nvarchar(260) NOT NULL,
      ContentType varchar(60) NOT NULL CONSTRAINT DF_SignageScreenImage_Type DEFAULT ('image/png'),
      ByteLength int NOT NULL,
      Width int NULL,
      Height int NULL,
      Content varbinary(max) NOT NULL,
      Thumbnail varbinary(max) NULL,
      /* SHA-256 del contenido. Sirve de ETag y de token de caché en la URL
         pública; a diferencia de un rowversion sobrevive restauraciones y
         réplicas, y permite servir la imagen como inmutable. */
      ContentHash binary(32) NOT NULL,
      AltText nvarchar(300) NULL,
      IsEnabled bit NOT NULL CONSTRAINT DF_SignageScreenImage_Enabled DEFAULT (1),
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SignageScreenImage_Created DEFAULT (SYSUTCDATETIME()),
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_SignageScreenImage_Updated DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(256) NULL,
      CONSTRAINT CK_SignageScreenImage_Type CHECK (ContentType IN ('image/png', 'image/jpeg', 'image/webp')),
      CONSTRAINT CK_SignageScreenImage_Bytes CHECK (ByteLength > 0 AND ByteLength <= 26214400),
      CONSTRAINT CK_SignageScreenImage_Sort CHECK (SortOrder >= 0),
      CONSTRAINT FK_SignageScreenImage_Screen_Rfc FOREIGN KEY (Rfc, ScreenId)
        REFERENCES restaurante.SignageScreen (Rfc, Id)
    );
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.SignageScreenImage') AND [name] = 'UX_SignageScreenImage_RfcId'
  )
    CREATE UNIQUE INDEX UX_SignageScreenImage_RfcId ON restaurante.SignageScreenImage (Rfc, Id);

  /* SortOrder se indexa pero NO es único: reordenar reescribe el conjunto
     completo y una restricción única provocaría colisiones transitorias.
     El orden determinista es ORDER BY SortOrder, Id. */
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.SignageScreenImage') AND [name] = 'IX_SignageScreenImage_Screen'
  )
    CREATE INDEX IX_SignageScreenImage_Screen
      ON restaurante.SignageScreenImage (Rfc, ScreenId, SortOrder, Id)
      INCLUDE (ContentType, ContentHash, IsEnabled, AltText);

  /* Pantallas iniciales de Bruno's: una por televisor. Sin imágenes; se cargan
     desde la pestaña Pantallas. */
  DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';
  DECLARE @BrunoSiteId int =
  (
    SELECT Id FROM restaurante.Site WHERE Rfc = @BrunoRfc AND SiteCode = 'BRUNOS-01'
  );

  IF @BrunoSiteId IS NOT NULL
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM restaurante.SignageScreen WHERE Rfc = @BrunoRfc AND ScreenKey = 'comida')
      INSERT restaurante.SignageScreen (Rfc, SiteId, ScreenKey, [Name], RotationSeconds, SortOrder, UpdatedBy)
        VALUES (@BrunoRfc, @BrunoSiteId, 'comida', N'Pantalla 1 — Comida', 8, 0, N'20260901_restaurant_digital_signage');

    IF NOT EXISTS (SELECT 1 FROM restaurante.SignageScreen WHERE Rfc = @BrunoRfc AND ScreenKey = 'bebidas')
      INSERT restaurante.SignageScreen (Rfc, SiteId, ScreenKey, [Name], RotationSeconds, SortOrder, UpdatedBy)
        VALUES (@BrunoRfc, @BrunoSiteId, 'bebidas', N'Pantalla 2 — Bebidas', 8, 1, N'20260901_restaurant_digital_signage');
  END;

  /* Incorporar tablas nuevas a la política RLS compartida. */
  IF EXISTS
  (
    SELECT 1 FROM sys.security_policies
    WHERE [name]='RfcSecurityPolicy' AND schema_id=SCHEMA_ID('logistica')
  )
  BEGIN
    DECLARE @RlsSchema sysname;
    DECLARE @RlsTable sysname;
    DECLARE @RlsSql nvarchar(max);
    DECLARE RlsCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT schemaInfo.[name],tableInfo.[name]
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
    WHERE schemaInfo.[name] IN('restaurante','fidelidad')
      AND EXISTS
      (
        SELECT 1 FROM sys.columns columnInfo
        WHERE columnInfo.object_id=tableInfo.object_id AND columnInfo.[name]='Rfc'
      )
      AND NOT EXISTS
      (
        SELECT 1 FROM sys.security_predicates predicateInfo
        WHERE predicateInfo.object_id=OBJECT_ID('logistica.RfcSecurityPolicy')
          AND predicateInfo.target_object_id=tableInfo.object_id
      );

    OPEN RlsCursor;
    FETCH NEXT FROM RlsCursor INTO @RlsSchema,@RlsTable;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @RlsSql=N'ALTER SECURITY POLICY logistica.RfcSecurityPolicy
        ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N',
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER INSERT,
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER UPDATE;';
      EXEC sys.sp_executesql @RlsSql;
      FETCH NEXT FROM RlsCursor INTO @RlsSchema,@RlsTable;
    END;
    CLOSE RlsCursor;
    DEALLOCATE RlsCursor;
  END;

  /* Validaciones. */
  IF EXISTS
  (
    SELECT 1 FROM restaurante.SignageScreenImage imageInfo
    WHERE NOT EXISTS
    (
      SELECT 1 FROM restaurante.SignageScreen screenInfo
      WHERE screenInfo.Rfc = imageInfo.Rfc AND screenInfo.Id = imageInfo.ScreenId
    )
  )
    THROW 51910, 'Existe una imagen de señalización sin pantalla en el mismo RFC.', 1;

  IF EXISTS (SELECT 1 FROM restaurante.SignageScreenImage WHERE DATALENGTH(Content) <> ByteLength)
    THROW 51911, 'ByteLength no coincide con el contenido almacenado.', 1;

  IF EXISTS
  (
    SELECT Rfc, ScreenKey FROM restaurante.SignageScreen
    GROUP BY Rfc, ScreenKey HAVING COUNT(*) > 1
  )
    THROW 51912, 'Hay claves de pantalla duplicadas dentro de un mismo RFC.', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.security_policies
    WHERE [name]='RfcSecurityPolicy' AND schema_id=SCHEMA_ID('logistica')
  )
  AND NOT EXISTS
  (
    SELECT 1 FROM sys.security_predicates
    WHERE object_id = OBJECT_ID('logistica.RfcSecurityPolicy')
      AND target_object_id = OBJECT_ID('restaurante.SignageScreen')
  )
    THROW 51913, 'Las tablas de señalización no quedaron protegidas por la política RLS.', 1;

  DECLARE @ScreenCount int = (SELECT COUNT(*) FROM restaurante.SignageScreen);
  DECLARE @ScreenImageCount int = (SELECT COUNT(*) FROM restaurante.SignageScreenImage);
  DECLARE @BrunoScreenCount int = (SELECT COUNT(*) FROM restaurante.SignageScreen WHERE Rfc = @BrunoRfc);

  SELECT DB_NAME() AS DatabaseName,
         @ApplyChanges AS ApplyChanges,
         @ScreenCount AS ScreenCount,
         @ScreenImageCount AS ScreenImageCount,
         @BrunoScreenCount AS BrunoScreenCount,
         CASE WHEN @ApplyChanges = 1 THEN 'COMMITTED' ELSE 'DRY_RUN_VALIDATED' END AS MigrationStatus;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

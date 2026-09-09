/*
  El calendario de reservaciones acotado al dueno, en produccion.

  Paquete productivo de 20260909_calendar_owner_scope_sandbox, ya aplicado y observado
  en Orion_Sandbox. El cuerpo NO se copio del sandbox: se volco de grupocarpio con
  OBJECT_DEFINITION y resulto identico al que se versiono alla, caracter por caracter.

  Un unico cambio sobre esa definicion: el parametro @OwnerId.

  **Por omision es NULL, y con NULL el resultado es exactamente el de hoy.** Por eso
  esta migracion va ANTES que los binarios y no despues: aplicarla sola no cambia nada
  para nadie, mientras que publicar binarios que mandan @OwnerId contra un procedimiento
  de tres parametros rompe cada carga del calendario.

  Con valor, el filtro entra en el INSERT de #Resources, que es de donde salen los tres
  conjuntos de resultados: recursos, celdas por dia y eventos. Acotar ahi acota los tres;
  acotar solo el primero habria dejado las reservaciones ajenas visibles en las celdas.

  Quien decide el valor es la identidad de la sesion, nunca la pantalla: lo resuelve
  HospitalityViewerScopeResolver leyendo AspNetUsers.ArrendadorProveedorId. Un arrendador
  sin proveedor ligado no recibe NULL, recibe un error: NULL seria "ver todo".

  No se toca 20260907_fiscal_declaracion_deploy.ps1.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit = 0;
DECLARE @MigrationId nvarchar(200) = N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128) = '$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64) = N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64) = NULL;

IF @ApplyChangesInput NOT LIKE N'$' + N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0', N'1')
    THROW 52140, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 52141, 'Esta migracion admite grupocarpio o el ensayo aislado Orion_CutoverValidation_20260908.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 52142, 'La conexion no coincide con ExpectedDatabase.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52143, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52144, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Calendar_GetRoomTimeline', N'P') IS NULL
  THROW 52145, 'Falta el procedimiento del calendario que esta migracion versiona.', 1;
IF COL_LENGTH('dbo.ROOM', 'OWNER_ID') IS NULL
  THROW 52146, 'dbo.ROOM no tiene OWNER_ID: no hay por donde acotar al arrendador.', 1;
/* El aislamiento administrativo llego a cada base con el nombre de su paquete. */
IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration
               WHERE MigrationId IN (N'20260908_hospitality_administration_scope_sandbox',
                                     N'20260908_production_hospitality_administration_scope'))
  THROW 52147, 'El acotamiento por dueno se apoya en el aislamiento administrativo de Hospedaje.', 1;
/* El procedimiento que se versiona aqui es el de tres parametros que se volco. Si tiene
   otros, alguien lo cambio despues del volcado y esta migracion no lo va a pisar a ciegas. */
IF (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) <> 3
  THROW 52148, 'El procedimiento no es el de tres parametros que se volco; no se reemplaza a ciegas.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52149, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_CORTE_20260909' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

PRINT 'Estado previo del procedimiento del calendario.';
SELECT
  CONVERT(bit, CASE WHEN EXISTS
  (
    SELECT 1 FROM sys.parameters q
    WHERE q.object_id = OBJECT_ID(N'dbo.Calendar_GetRoomTimeline') AND q.name = '@OwnerId'
  ) THEN 1 ELSE 0 END) AS YaTieneElParametro,
  CONVERT(int, (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.Calendar_GetRoomTimeline'))) AS ParametrosHoy;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Hospitality:CalendarOwnerScope:Produccion',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52150, 'No se obtuvo el candado de la migracion del calendario.', 1;

  DECLARE @TimelineSql nvarchar(max) = CONVERT(nvarchar(max), N'');
  SET @TimelineSql = @TimelineSql + N'
CREATE OR ALTER PROCEDURE dbo.Calendar_GetRoomTimeline
    @StartDate date,
    @EndDateExclusive date,
    @RoomType varchar(50) = NULL,
    /* El dueno de las habitaciones. NULL conserva exactamente el comportamiento de hoy;
       con valor, la linea del tiempo entera se acota, porque los tres conjuntos de
       resultados salen de #Resources. */
    @OwnerId int = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @EndDateExclusive <= @StartDate
    BEGIN
        THROW 50001, ''EndDateExclusive must be after StartDate.'', 1;
    END;

    CREATE TABLE #Resources
    (
        RoomId int NOT NULL,
        RoomCode varchar(50) NOT NULL,
        RoomName varchar(50) NOT NULL,
        RoomType varchar(50) NOT NULL,
        BasePrice decimal(18,2) NOT NULL,
        DisplayOrder int NOT NULL,
        CalendarEnabled bit NOT NULL
    );

    INSERT INTO #Resources (RoomId, RoomCode, RoomName, RoomType, BasePrice, DisplayOrder, CalendarEnabled)
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomCode,
        r.ROOM_NAME AS RoomName,
        r.ROOM_TYPE AS RoomType,
        CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice,
        ROW_NUMBER() OVER (ORDER BY r.ROOM_NAME) AS DisplayOrder,
        CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.ROOM_CALENDAR rc
            WHERE rc.ROOM = r.ROOM_NAME
        ) THEN 1 ELSE 0 END AS bit) AS CalendarEnabled
    FROM dbo.ROOM r
    WHERE (@RoomType IS NULL OR r.ROOM_TYPE = @RoomType)
      AND (@OwnerId IS NULL OR r.OWNER_ID = @OwnerId)
      AND EXISTS (
          SELECT 1
          FROM dbo.ROOM_CALENDAR rc
          WHERE rc.ROOM = r.ROOM_NAME
      );

    CREATE TABLE #Dates
    (
        RoomDate date NOT NULL PRIMARY KEY
    );

    ;WITH date_series AS
    (
        SELECT CAST(@StartDate AS date) AS RoomDate
        UNION ALL
        SELECT DATEADD(day, 1, RoomDate)
        FROM date_series
        WHERE RoomDate < DATEADD(day, -1, CAST(@EndDateExclusive AS date))
    )
    INSERT INTO #Dates (RoomDate)
    SELECT RoomDate
    FROM date_series
    OPTION (MAXRECURSION 32767);

    CREATE TABLE #CalendarBase
    (
        RoomId int NOT NULL,
        RoomCode varchar(50) NOT NULL,
        RoomName varchar(50) NOT NULL,
        RoomDate date NOT NULL,
        RoomCalendarId int NULL,
        IsLocked bit NOT NULL,
        LockedBy varchar(50) NULL,
        LockDescription varchar(500) NULL,
        StateCode varchar(20) NOT NULL,
        ReservationId int NULL,
        ReservationStatus varchar(50) NULL,
        IsArrival bit NOT NULL,
        IsDeparture bit NOT NULL,
        HasExtras bit NOT NULL,
        HasDeepCleaning bit NOT NULL,
        HasDailyCheck bit NOT NULL,
        Price decimal(18,2) NOT NULL,
        Notes varchar(500) NULL,
        DataQualityFlag varchar(50) NULL
    );

    INSERT INTO #CalendarBase
    (
        RoomId,
        RoomCode,
        RoomName,
        RoomDate,
        RoomCalendarId,
        IsLocked,
        LockedBy,
        LockDescription,
        StateCode,
        ReservationId,
        ReservationStatus,
        IsArrival,
        IsDeparture,
        HasExtras,
        HasDeepCleaning,
        HasDailyCheck,
        Price,
        Notes,
        DataQualityFlag
    )
    SELECT
        rr.RoomId,
        rr.RoomCode,
        rr.RoomName,
        d.RoomDate,
        rc.ID AS RoomCalendarId,
        CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) AS IsLocked,
        NULLIF(LTRIM(RTRIM(rc.LOCKED_BY)), '''') AS LockedBy,
        NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '''') AS LockDescription,
        CASE
            WHEN rc.ID IS NULL THEN ''missing''
            WHEN ISNULL(rc.IS_LOCKED, 0) = 0 THEN ''available''
            WHEN UPPER(LTRIM(RTRIM(ISNULL(rc.LOCKED_BY, '''')))) COLLATE Latin1_General_100_CI_AI = N''COTIZACION'' THEN ''soft_hold''
            WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL AND r.ID IS NULL THEN ''orphan''
            WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL AND r.ID IS NOT NULL THEN ''reserved''
            ELSE ''blocked''
        END AS StateCode,
        r.ID AS ReservationId,
        r.STATUS AS ReservationStatus,
        CAST(CASE WHEN r.ID IS NOT NULL AND d.RoomDate = r.CHECKIN THEN 1 ELSE 0 END AS bit) AS IsArrival,
        CAST(CASE WHEN r.ID IS NOT NULL AND d.RoomDate = DATEADD(day, -1, r.CHECKOUT) THEN 1 ELSE 0 END AS bit) AS IsDeparture,
        CAST(CASE WHEN r.ID IS NOT NULL AND EXISTS (
            SELECT 1
            FROM dbo.RESERVATION_DETAIL rd
            WHERE rd.RESERVATION_ID = r.ID
        ) THEN 1 ELSE 0 END AS bit) AS HasExtras,
        CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS HasDeepCleaning,
        CAST(ISNULL(rc.CHECK_DIARIO, 0) AS bit) AS HasDailyCheck,
        CAST(ISNULL(rc.PRECIO, rr.BasePrice) AS decimal(18,2)) AS Price,
        rc.NOTES AS Notes,
        CASE
            WHEN rc.ID IS NULL THEN ''missing-room-calendar''
            WHEN ISNULL(rc.IS_LOCKED, 0) = 1
              AND TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL
              AND r.ID IS NULL THEN ''missing-reservation''
            ELSE NULL
        END AS DataQualityFlag
    FROM #Resources rr
    CROSS JOIN #Dates d
    LEFT JOIN dbo.ROOM_CALENDAR rc
      ON rc.ROOM = rr.RoomCode
     AND rc.ROOM_DATE = d.RoomDate
    LEFT JOIN dbo.RESERVATION r
      ON r.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int);

    SELECT
        RoomId,
        RoomCode,
        RoomName,
        RoomType,
        BasePrice,
        DisplayOrder,
        CalendarEnabled
    FROM #Resources
    ORDER BY DisplayOrder, RoomName;

    SELECT
        RoomId,
        RoomCode,
        RoomName,
        RoomDate,
        RoomCalendarId,
        IsLocked,
        LockedBy,
        LockDescription,
        StateCode,
        ReservationId,
        ReservationStatus,
        IsArrival,
        IsDeparture,
        HasExtras,
        HasDeepCleaning,
        HasDailyCheck,
        Price,
        Notes,
        DataQualityFlag
    FROM #CalendarBase
    ORDER BY RoomName, RoomDate;

    ;WITH event_source AS
    (
        SELECT
            cb.*,
            DATEADD(day, -ROW_NUMBER() OVER (
                PARTITION BY cb.RoomId,
                             cb.StateCode,
                             ISNULL(CONVERT(varchar(50), cb.ReservationId), ''''),
                             ISNULL(cb.LockDescription, ''''),
                             ISNULL(cb.LockedBy, '''')
                ORDER BY cb.RoomDate
            ), cb.RoomDate) AS GroupAnchor
        FROM #CalendarBase cb
        WHERE cb.IsLocked = 1
    )
    SELECT
        RoomId,
        RoomCode,
        RoomName,
        MIN(RoomDate) AS StartDate,
        DATEADD(day, 1, MAX(RoomDate)) AS EndDateExclusive,
        StateCode AS EventType,
        ReservationId,
        MAX(ReservationStatus) AS ReservationStatus,
        MAX(LockedBy) AS LockedBy,
        MAX(LockDescription) AS LockDescription,
        MAX(CASE
            WHEN ReservationId IS NOT NULL THEN CONCAT(RoomName, '' #'', ReservationId)
            WHEN LockDescription IS NOT NULL THEN CONCAT(RoomName, '' '', LockDescription)
            ELSE RoomName
        END) AS Title,
        MAX(CASE
            WHEN LockedBy IS NOT NULL THEN LockedBy
            WHEN ReservationStatus IS NOT NULL THEN ReservationStatus
            ELSE NULL
        END) AS Subtitle,
        MAX(DataQualityFlag) AS DataQualityFlag
    FROM event_source
    GROUP BY
        RoomId,
        RoomCode,
        RoomName,
        StateCode,
        ReservationId,
        GroupAnchor
    ORDER BY StartDate, RoomName;
END;
';

  EXEC sys.sp_executesql @TimelineSql;

  /* El parametro nuevo existe. */
  IF NOT EXISTS (SELECT 1 FROM sys.parameters
                 WHERE object_id = OBJECT_ID(N'dbo.Calendar_GetRoomTimeline') AND name = '@OwnerId')
    THROW 52151, 'El procedimiento no quedo con el parametro de dueno.', 1;
  /* sys.parameters.has_default_value queda en 0 para procedimientos T-SQL aunque el valor
     por omision exista, asi que el default se comprueba sobre la definicion. No se invoca
     el procedimiento: bajo XACT_ABORT ON cualquier error suyo condenaria esta transaccion,
     y ademas dbo.ROOM esta bajo la politica de Hospedaje, que sin contexto no devuelve nada. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) NOT LIKE '%@OwnerId int = NULL%'
    THROW 52152, 'El parametro de dueno debe declararse con valor por omision NULL.', 1;
  /* Y el calendario vigente sigue siendo el mismo: con NULL, ningun filtro se aplica. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) NOT LIKE '%@OwnerId IS NULL OR r.OWNER_ID = @OwnerId%'
    THROW 52153, 'El filtro por dueno debe ser condicional, no incondicional.', 1;
  /* El filtro sirve de algo solo si los tres conjuntos siguen saliendo de #Resources. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) NOT LIKE '%FROM #Resources rr%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) NOT LIKE '%FROM #CalendarBase cb%'
    THROW 52154, 'Los resultados dejaron de derivarse de #Resources: el acotamiento seria parcial.', 1;
  /* Y el filtro que ya existia no se perdio en el camino. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.Calendar_GetRoomTimeline')) NOT LIKE '%@RoomType IS NULL OR r.ROOM_TYPE = @RoomType%'
    THROW 52155, 'Se perdio el filtro por tipo de habitacion.', 1;

  SELECT
    CONVERT(int, (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.Calendar_GetRoomTimeline'))) AS ParametrosAhora,
    CONVERT(int, (SELECT COUNT(*) FROM auth.AspNetUsers WHERE ArrendadorProveedorId IS NOT NULL)) AS UsuariosArrendadorLigados;

  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum,
              COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
              @AppVersion, DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_CORTE_20260909' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

/*
  Corte productivo 2026-09-09: 20260909_production_workforce_attendance_scope.
  Semántica revisada desde 20260909_workforce_attendance_scope_sandbox, aplicada y
  observada allí; los bytes de aquella no se tocan. Producción exige respaldo autorizado
  aparte, preview revisado, y sólo entonces apply.

  E8c: RLS fail-closed del agregado de asistencia de RH. Seis tablas exactas —TimeEvent,
  AttendanceDay, AttendanceException, AttendanceCorrectionRequest, OvertimeDecision y
  AuditEvent—, con 312 filas productivas en dos empresas. Sus 18 predicados salen de
  rh.RfcSecurityPolicy, que conserva los otros 48 sobre las dieciséis tablas restantes.
  No se cambia de golpe una función compartida por decenas de tablas.

  **Nunca se habilita bypass por NULL.** El predicado nuevo exige que el Rfc de la fila
  coincida con SESSION_CONTEXT(N'OrionRfc'); una conexión sin inicializar no ve nada.

  ORDEN OBLIGATORIO: los binarios PRIMERO, esta migración DESPUÉS. El job de retención
  anterior borraba el contexto a propósito para valerse del bypass. Si esta migración
  entra antes que el código nuevo, ese job dejará de ver filas y purgará cero evidencias
  GPS **sin reportar ningún error**. Al revés es inocuo: fijar un RFC concreto funciona
  igual bajo la política anterior.

  No se alteran reglas laborales, nómina, expedientes ni biométricos: sólo cambia quién
  VE las filas. Ninguna fila se escribe ni se borra.
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
    THROW 52100, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 52101, 'Esta migracion admite grupocarpio o el ensayo aislado Orion_CutoverValidation_20260908.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 52102, 'La conexion no coincide con ExpectedDatabase.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52103, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52104, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

DECLARE @Lote TABLE (Tabla sysname NOT NULL PRIMARY KEY);
INSERT @Lote VALUES
  (N'TimeEvent'), (N'AttendanceDay'), (N'AttendanceException'),
  (N'AttendanceCorrectionRequest'), (N'OvertimeDecision'), (N'AuditEvent');

IF EXISTS (SELECT 1 FROM @Lote WHERE OBJECT_ID(N'rh.' + Tabla, N'U') IS NULL)
  THROW 52105, 'Falta alguna tabla del lote de asistencia.', 1;
IF EXISTS (SELECT 1 FROM @Lote WHERE COL_LENGTH(N'rh.' + Tabla, N'Rfc') IS NULL)
  THROW 52106, 'Una tabla del lote no tiene columna Rfc; no se puede delimitar.', 1;
IF OBJECT_ID(N'rh.RfcSecurityPolicy') IS NULL OR OBJECT_ID(N'rh.fn_RfcAccessPredicate') IS NULL
  THROW 52107, 'Falta la politica heredada de RH que este lote reemplaza para seis tablas.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52108, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'rh.WorkforceScopePolicy') IS NULL
    THROW 52109, 'La migracion esta registrada pero falta la politica.', 1;
  SELECT N'YA_APLICADO_CORTE_20260909' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;
IF OBJECT_ID(N'rh.WorkforceScopePolicy') IS NOT NULL OR OBJECT_ID(N'rh.fn_WorkforceScopePredicate') IS NOT NULL
  THROW 52110, 'Ya existe una politica o funcion de asistencia sin registrar; requiere reconciliacion explicita.', 1;

/* El codigo que consume estas tablas ya fija el contexto: la fabrica lo hace en cada
   conexion de sesion, y la retencion recorre empresas. Si quedara una fila cuyo Rfc no
   esta en orion.Company, la retencion no la alcanzaria nunca. */
DECLARE @Huerfanos int =
(
  SELECT COUNT(*)
  FROM
  (
    SELECT Rfc FROM rh.TimeEvent
    UNION SELECT Rfc FROM rh.AttendanceDay
    UNION SELECT Rfc FROM rh.AttendanceException
    UNION SELECT Rfc FROM rh.AttendanceCorrectionRequest
    UNION SELECT Rfc FROM rh.OvertimeDecision
    UNION SELECT Rfc FROM rh.AuditEvent
  ) AS usados
  WHERE NOT EXISTS (SELECT 1 FROM orion.Company c WHERE c.Rfc = usados.Rfc AND c.IsActive = 1)
);
IF @Huerfanos > 0
  THROW 52111, 'Hay filas de asistencia con un Rfc que no es una empresa activa; la retencion no las alcanzaria.', 1;

PRINT 'Estado previo: filas del lote por empresa, con el bypass todavia activo.';
SELECT usados.Rfc, CONVERT(bigint, SUM(usados.Filas)) AS Filas
FROM
(
  SELECT Rfc, COUNT_BIG(*) AS Filas FROM rh.TimeEvent GROUP BY Rfc
  UNION ALL SELECT Rfc, COUNT_BIG(*) FROM rh.AttendanceDay GROUP BY Rfc
  UNION ALL SELECT Rfc, COUNT_BIG(*) FROM rh.AttendanceException GROUP BY Rfc
  UNION ALL SELECT Rfc, COUNT_BIG(*) FROM rh.AttendanceCorrectionRequest GROUP BY Rfc
  UNION ALL SELECT Rfc, COUNT_BIG(*) FROM rh.OvertimeDecision GROUP BY Rfc
  UNION ALL SELECT Rfc, COUNT_BIG(*) FROM rh.AuditEvent GROUP BY Rfc
) AS usados
GROUP BY usados.Rfc ORDER BY SUM(usados.Filas) DESC;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Workforce:AttendanceScope:Production',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52112, 'No se obtuvo el candado del lote de asistencia.', 1;

  EXEC(N'
  CREATE FUNCTION rh.fn_WorkforceScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE @Rfc = CONVERT(varchar(50), SESSION_CONTEXT(N''OrionRfc''));');

  /* Sacar las seis tablas de la politica heredada, y solo esas. */
  DECLARE @Tabla sysname, @Sql nvarchar(max);
  DECLARE @Drop nvarchar(max) = N'ALTER SECURITY POLICY rh.RfcSecurityPolicy ';
  DECLARE @Add nvarchar(max) = N'CREATE SECURITY POLICY rh.WorkforceScopePolicy ';
  DECLARE lote_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Tabla FROM @Lote ORDER BY Tabla;
  OPEN lote_cursor;
  FETCH NEXT FROM lote_cursor INTO @Tabla;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Drop += N'DROP FILTER PREDICATE ON rh.' + QUOTENAME(@Tabla) + N',
      DROP BLOCK PREDICATE ON rh.' + QUOTENAME(@Tabla) + N' AFTER INSERT,
      DROP BLOCK PREDICATE ON rh.' + QUOTENAME(@Tabla) + N' AFTER UPDATE,';
    SET @Add += N'ADD FILTER PREDICATE rh.fn_WorkforceScopePredicate(Rfc) ON rh.' + QUOTENAME(@Tabla) + N',
      ADD BLOCK PREDICATE rh.fn_WorkforceScopePredicate(Rfc) ON rh.' + QUOTENAME(@Tabla) + N' AFTER INSERT,
      ADD BLOCK PREDICATE rh.fn_WorkforceScopePredicate(Rfc) ON rh.' + QUOTENAME(@Tabla) + N' AFTER UPDATE,';
    FETCH NEXT FROM lote_cursor INTO @Tabla;
  END;
  CLOSE lote_cursor;
  DEALLOCATE lote_cursor;

  SET @Drop = LEFT(@Drop, LEN(@Drop) - 1) + N';';
  SET @Add = LEFT(@Add, LEN(@Add) - 1) + N' WITH (STATE = ON, SCHEMABINDING = ON);';
  EXEC sys.sp_executesql @Drop;
  EXEC sys.sp_executesql @Add;

  /* Dieciocho predicados salieron de la politica heredada y entraron a la nueva. */
  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id = OBJECT_ID(N'rh.WorkforceScopePolicy')) <> 18
    THROW 52113, 'La politica nueva no quedo con los dieciocho predicados del lote.', 1;
  IF EXISTS
  (
    SELECT 1 FROM sys.security_predicates sp
    JOIN @Lote l ON sp.target_object_id = OBJECT_ID(N'rh.' + l.Tabla)
    WHERE sp.object_id = OBJECT_ID(N'rh.RfcSecurityPolicy')
  )
    THROW 52114, 'Una tabla del lote sigue en la politica heredada.', 1;
  /* Y las demas tablas de rh no se tocaron. */
  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id = OBJECT_ID(N'rh.RfcSecurityPolicy')) <> 48
    THROW 52115, 'La politica heredada de RH debe conservar sus otros cuarenta y ocho predicados.', 1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'rh.fn_WorkforceScopePredicate')) LIKE '%IS NULL%'
    THROW 52116, 'El predicado nuevo no debe admitir contexto nulo.', 1;

  SELECT
    p.name AS Politica, p.is_enabled AS Habilitada, p.is_schema_bound AS ConEsquema,
    CONVERT(int, (SELECT COUNT(*) FROM sys.security_predicates sp WHERE sp.object_id = p.object_id)) AS Predicados
  FROM sys.security_policies p
  WHERE p.object_id IN (OBJECT_ID(N'rh.WorkforceScopePolicy'), OBJECT_ID(N'rh.RfcSecurityPolicy'));

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
  IF CURSOR_STATUS('local', 'lote_cursor') >= 0 CLOSE lote_cursor;
  IF CURSOR_STATUS('local', 'lote_cursor') >= -1 DEALLOCATE lote_cursor;
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

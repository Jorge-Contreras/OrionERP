/*
  Provisions the deterministic fictional OrionERP cohort/scenarios. Run only
  through Sanitize-OrionTraining.ps1 after
  the destructive sanitization batch and capacitacion v1 installer succeed.

  Required ADO.NET parameters:
    @InstructorPasswordHash, @Trainee01PasswordHash, @Trainee02PasswordHash,
    @AuditorPasswordHash
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51720, 'PROVISIONING BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51721, 'PROVISIONING BLOCKED: the guarded PowerShell workflow did not authorize this session.', 1;

IF NULLIF(CONVERT(nvarchar(max), @InstructorPasswordHash), N'') IS NULL
   OR NULLIF(CONVERT(nvarchar(max), @Trainee01PasswordHash), N'') IS NULL
   OR NULLIF(CONVERT(nvarchar(max), @Trainee02PasswordHash), N'') IS NULL
   OR NULLIF(CONVERT(nvarchar(max), @AuditorPasswordHash), N'') IS NULL
  THROW 51722, 'PROVISIONING BLOCKED: application credential material is missing.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  IF EXISTS
  (
    SELECT 1
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    CROSS APPLY
    (
      -- ROWCOUNT is a reserved T-SQL keyword, so the aggregate cannot be aliased
      -- as RowCount without quoting; use an unreserved name instead.
      SELECT SUM(CASE WHEN partitionInfo.index_id IN (0, 1) THEN partitionInfo.rows ELSE 0 END) AS TableRowCount
      FROM sys.partitions partitionInfo
      WHERE partitionInfo.object_id = tableInfo.object_id
    ) rowInfo
    WHERE tableInfo.is_ms_shipped = 0
      AND rowInfo.TableRowCount > 0
      AND NOT (schemaInfo.name = N'dbo' AND tableInfo.name IN (N'__EFMigrationsHistory', N'DateDimension'))
      AND NOT
      (
        schemaInfo.name = N'capacitacion'
        AND tableInfo.name IN
        (
          N'EsquemaVersion', N'EntornoSeguridad', N'Curso', N'CursoVersion', N'Leccion',
          N'BloqueContenido', N'Recurso', N'Evaluacion', N'Pregunta', N'OpcionPregunta',
          N'Practica', N'PracticaPaso', N'RutaAprendizaje', N'RutaCurso'
        )
      )
  )
    THROW 51726, 'PROVISIONING BLOCKED: non-preserved rows appeared after sanitization.', 1;

  DECLARE @TrainingRfc varchar(50) = 'XAXX010101000';

  INSERT dbo.RazonSocial (Nombre)
  VALUES (N'ORION TRAINING · ORGANIZACIÓN FICTICIA');

  INSERT dbo.Capital_Humano
  (
    ID, Nombre, ApellidoPaterno, ApellidoMaterno, [Status], NombreCorto,
    RFC_Capital_Humano, Puesto, Fecha_Alta, Tipo_Contrato, Sede_Contratada,
    Tipo_Capital_Humano, Contrasena_Acceso, Usuario_Acceso, RFC
  )
  VALUES
    (990001, N'INSTRUCTOR', N'SINTÉTICO', N'ORION', N'ACTIVO', N'INSTRUCTOR TRAINING',
      N'TRAINING-INSTRUCTOR', N'Instructor de capacitación', CONVERT(date, '2026-01-05'), 'CAPACITACIÓN', 'ENTORNO FICTICIO',
      'SINTÉTICO', 'NO-ES-CREDENCIAL', 'instructor@training.orion.local', @TrainingRfc),
    (990002, N'COLABORADOR 01', N'SINTÉTICO', N'ORION', N'ACTIVO', N'TRAINEE 01',
      N'TRAINING-TRAINEE-01', N'Participante de capacitación', CONVERT(date, '2026-01-05'), 'CAPACITACIÓN', 'ENTORNO FICTICIO',
      'SINTÉTICO', 'NO-ES-CREDENCIAL', 'trainee01@training.orion.local', @TrainingRfc),
    (990003, N'COLABORADOR 02', N'SINTÉTICO', N'ORION', N'ACTIVO', N'TRAINEE 02',
      N'TRAINING-TRAINEE-02', N'Participante de capacitación', CONVERT(date, '2026-01-05'), 'CAPACITACIÓN', 'ENTORNO FICTICIO',
      'SINTÉTICO', 'NO-ES-CREDENCIAL', 'trainee02@training.orion.local', @TrainingRfc),
    (990004, N'AUDITOR', N'SINTÉTICO', N'ORION', N'ACTIVO', N'AUDITOR TRAINING',
      N'TRAINING-AUDITOR', N'Auditor de capacitación', CONVERT(date, '2026-01-05'), 'CAPACITACIÓN', 'ENTORNO FICTICIO',
      'SINTÉTICO', 'NO-ES-CREDENCIAL', 'auditor@training.orion.local', @TrainingRfc);

  -- Los cinco roles de módulo cubren, con el mínimo posible, todas las pantallas
  -- de práctica del currículo: Arrendadores, órdenes de trabajo (supervisor
  -- cubre tablero y plantillas), cuentas por pagar, Capital Humano (admin cubre
  -- asistencia, ausencias, configuración de tiempo, pre-nómina y mi equipo) y
  -- Restaurante (supervisor cubre POS, cocina, pantalla, caja y administración).
  -- Administrador existe únicamente para el instructor: es el único acceso a
  -- reportes financieros, ajustes, expediente y portal de seguridad, y es la
  -- cuenta con la que se administran roles dentro del entorno de capacitación.
  INSERT auth.AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
  VALUES
    (N'training-role-cap-admin', N'CapacitacionAdmin', N'CAPACITACIONADMIN', N'training-role-cap-admin-v1'),
    (N'training-role-cap-instructor', N'CapacitacionInstructor', N'CAPACITACIONINSTRUCTOR', N'training-role-cap-instructor-v1'),
    (N'training-role-cap-auditor', N'CapacitacionAuditor', N'CAPACITACIONAUDITOR', N'training-role-cap-auditor-v1'),
    (N'training-role-read', N'Lectura', N'LECTURA', N'training-role-read-v1'),
    (N'training-role-sat-operator', N'SatOperator', N'SATOPERATOR', N'training-role-sat-operator-v1'),
    (N'training-role-logistica', N'Logistica', N'LOGISTICA', N'training-role-logistica-v1'),
    (N'training-role-administrador', N'Administrador', N'ADMINISTRADOR', N'training-role-administrador-v1'),
    (N'training-role-arrendadores', N'Arrendadores', N'ARRENDADORES', N'training-role-arrendadores-v1'),
    (N'training-role-ot-supervisor', N'OrdenTrabajoSupervisor', N'ORDENTRABAJOSUPERVISOR', N'training-role-ot-supervisor-v1'),
    (N'training-role-ap-operator', N'APOperator', N'APOPERATOR', N'training-role-ap-operator-v1'),
    (N'training-role-ch-admin', N'CapitalHumanoAdmin', N'CAPITALHUMANOADMIN', N'training-role-ch-admin-v1'),
    (N'training-role-rest-supervisor', N'RestauranteSupervisor', N'RESTAURANTESUPERVISOR', N'training-role-rest-supervisor-v1');

  INSERT auth.AspNetUsers
  (
    Id, EmployeeId, UserName, NormalizedUserName, Email, NormalizedEmail,
    EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp,
    PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd,
    LockoutEnabled, AccessFailedCount, ArrendadorProveedorId
  )
  VALUES
    (N'training-user-instructor-v1', 990001, N'instructor@training.orion.local', N'INSTRUCTOR@TRAINING.ORION.LOCAL', N'instructor@training.orion.local', N'INSTRUCTOR@TRAINING.ORION.LOCAL',
      1, @InstructorPasswordHash, CONVERT(nvarchar(36), NEWID()), CONVERT(nvarchar(36), NEWID()), NULL, 0, 0, NULL, 1, 0, NULL),
    (N'training-user-trainee01-v1', 990002, N'trainee01@training.orion.local', N'TRAINEE01@TRAINING.ORION.LOCAL', N'trainee01@training.orion.local', N'TRAINEE01@TRAINING.ORION.LOCAL',
      1, @Trainee01PasswordHash, CONVERT(nvarchar(36), NEWID()), CONVERT(nvarchar(36), NEWID()), NULL, 0, 0, NULL, 1, 0, NULL),
    (N'training-user-trainee02-v1', 990003, N'trainee02@training.orion.local', N'TRAINEE02@TRAINING.ORION.LOCAL', N'trainee02@training.orion.local', N'TRAINEE02@TRAINING.ORION.LOCAL',
      1, @Trainee02PasswordHash, CONVERT(nvarchar(36), NEWID()), CONVERT(nvarchar(36), NEWID()), NULL, 0, 0, NULL, 1, 0, NULL),
    (N'training-user-auditor-v1', 990004, N'auditor@training.orion.local', N'AUDITOR@TRAINING.ORION.LOCAL', N'auditor@training.orion.local', N'AUDITOR@TRAINING.ORION.LOCAL',
      1, @AuditorPasswordHash, CONVERT(nvarchar(36), NEWID()), CONVERT(nvarchar(36), NEWID()), NULL, 0, 0, NULL, 1, 0, NULL);

  INSERT auth.AspNetUserClaims (UserId, ClaimType, ClaimValue)
  SELECT Id, N'rfc', CONVERT(nvarchar(50), @TrainingRfc)
  FROM auth.AspNetUsers;

  INSERT auth.AspNetUserRoles (UserId, RoleId)
  VALUES
    (N'training-user-instructor-v1', N'training-role-cap-admin'),
    (N'training-user-instructor-v1', N'training-role-cap-instructor'),
    (N'training-user-instructor-v1', N'training-role-sat-operator'),
    (N'training-user-instructor-v1', N'training-role-logistica'),
    (N'training-user-instructor-v1', N'training-role-administrador'),
    (N'training-user-trainee01-v1', N'training-role-read'),
    (N'training-user-trainee01-v1', N'training-role-sat-operator'),
    (N'training-user-trainee01-v1', N'training-role-logistica'),
    (N'training-user-trainee01-v1', N'training-role-arrendadores'),
    (N'training-user-trainee01-v1', N'training-role-ot-supervisor'),
    (N'training-user-trainee01-v1', N'training-role-ap-operator'),
    (N'training-user-trainee01-v1', N'training-role-ch-admin'),
    (N'training-user-trainee01-v1', N'training-role-rest-supervisor'),
    (N'training-user-trainee02-v1', N'training-role-read'),
    (N'training-user-trainee02-v1', N'training-role-sat-operator'),
    (N'training-user-trainee02-v1', N'training-role-logistica'),
    (N'training-user-trainee02-v1', N'training-role-arrendadores'),
    (N'training-user-trainee02-v1', N'training-role-ot-supervisor'),
    (N'training-user-trainee02-v1', N'training-role-ap-operator'),
    (N'training-user-trainee02-v1', N'training-role-ch-admin'),
    (N'training-user-trainee02-v1', N'training-role-rest-supervisor'),
    (N'training-user-auditor-v1', N'training-role-cap-auditor');

  DECLARE @TrainingClientId int;
  INSERT dbo.Clientes (EmpresaId, Nombre, Ciudad, Estado, Email, Notas, Tokens, UserName, Password)
  VALUES (NULL, N'HUÉSPED FICTICIO · CAPACITACIÓN', 'CIUDAD DE PRUEBA', 'ESTADO DE PRUEBA',
          'guest@training.orion.local', 'DATOS SINTÉTICOS; NO CONTACTAR', 0, 'training-guest', NULL);
  SET @TrainingClientId = CONVERT(int, SCOPE_IDENTITY());

  INSERT dbo.ROOM (ROOM_NAME, ROOM_TYPE, ROOM_DESCRIPTION, BASE_PRICE, OWNER_ID, NOTES)
  VALUES
    ('TRN-SUITE-01', 'TRAINING', 'Habitación ficticia para prácticas', 1200, 990001, 'DATOS SINTÉTICOS'),
    ('TRN-SUITE-02', 'TRAINING', 'Habitación ficticia para prácticas', 1500, 990001, 'DATOS SINTÉTICOS');

  ;WITH TrainingDays AS
  (
    SELECT TOP (60) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS DayOffset
    FROM sys.all_objects
  )
  INSERT dbo.ROOM_CALENDAR
    (ROOM_DATE, ROOM, IS_LOCKED, CHECKED, CHECK_DIARIO, LIMPIEZA_PROFUNDA, PRECIO,
     VENCIMIENTO_BLOQUEO, PORCENTAJE_ARRENDAMIENTO, [STATUS], NOTES)
  SELECT DATEADD(day, dayInfo.DayOffset, CONVERT(date, SYSUTCDATETIME())), roomInfo.ROOM_NAME,
         0, 0, 0, 0, roomInfo.BASE_PRICE, SYSUTCDATETIME(), 0, 'DISPONIBLE', 'DATOS SINTÉTICOS'
  FROM TrainingDays dayInfo
  CROSS JOIN dbo.ROOM roomInfo;

  DECLARE @TrainingReservationId int;
  INSERT dbo.RESERVATION
    (CLIENTE_ID, CHECKIN, CHECKOUT, DATE_CREATED, TAXABLE, RECOMMENED_BY,
     TOTAL_PRICE, [STATUS], NOTES, AIRBNB_UPDATED, RFC, SUITE_DISCOUNT_PERCENT)
  VALUES
    (@TrainingClientId, DATEADD(day, 7, CONVERT(date, SYSUTCDATETIME())),
     DATEADD(day, 9, CONVERT(date, SYSUTCDATETIME())), CONVERT(date, SYSUTCDATETIME()),
     0, 'TRAINING', 2400, 'ACTIVA', 'RESERVACIÓN FICTICIA · NO OPERATIVA', 0, @TrainingRfc, 0);
  SET @TrainingReservationId = CONVERT(int, SCOPE_IDENTITY());

  INSERT dbo.RESERVATION_DETAIL (RESERVATION_ID, ROOM_ID, PRICE, DISCOUNTED_PRICE, DISCOUNT, NOTES)
  SELECT @TrainingReservationId, roomInfo.ID, 2400, 2400, 0, 'DATOS SINTÉTICOS'
  FROM dbo.ROOM roomInfo
  WHERE roomInfo.ROOM_NAME = 'TRN-SUITE-01';

  INSERT logistica.UnitOfMeasure (UnitName, Abbreviation, Description, IsActive)
  VALUES
    ('TRAINING PIEZA', 'TRN-PZA', 'Unidad ficticia para capacitación', 1),
    ('TRAINING CAJA', 'TRN-CJ', 'Unidad ficticia para capacitación', 1);

  INSERT logistica.MaterialCategory (LegacyCategoryId, CategoryName, Description, IsActive, Rfc)
  VALUES
    (NULL, 'TRAINING LIMPIEZA', 'Categoría ficticia', 1, @TrainingRfc),
    (NULL, 'TRAINING AMENIDADES', 'Categoría ficticia', 1, @TrainingRfc);

  INSERT logistica.Location
    (LocationCode, LegacyEspacioId, LegacyRoomId, ParentLocationId, RoomId,
     LocationName, LocationType, Description, IsInventoryEnabled, IsActive, Rfc)
  VALUES
    ('TRN-ALM-01', NULL, NULL, NULL, NULL, 'Almacén ficticio 01', 'WAREHOUSE', 'DATOS SINTÉTICOS', 1, 1, @TrainingRfc),
    ('TRN-ALM-02', NULL, NULL, NULL, NULL, 'Almacén ficticio 02', 'WAREHOUSE', 'DATOS SINTÉTICOS', 1, 1, @TrainingRfc);

  DECLARE @PieceUnitId int = (SELECT Id FROM logistica.UnitOfMeasure WHERE UnitName = 'TRAINING PIEZA');
  DECLARE @BoxUnitId int = (SELECT Id FROM logistica.UnitOfMeasure WHERE UnitName = 'TRAINING CAJA');
  DECLARE @CleaningCategoryId int = (SELECT Id FROM logistica.MaterialCategory WHERE Rfc = @TrainingRfc AND CategoryName = 'TRAINING LIMPIEZA');
  DECLARE @AmenitiesCategoryId int = (SELECT Id FROM logistica.MaterialCategory WHERE Rfc = @TrainingRfc AND CategoryName = 'TRAINING AMENIDADES');

  INSERT logistica.Material
    (MaterialCode, Description, BaseUnitId, PurchaseQuantity, PurchaseUnitId,
     BaseUnitPrice, Brand, IsPerishable, MaterialStatus, CategoryId, MaterialClass,
     IsActive, Rfc, ProductType, FulfillmentMode, TrackLots)
  VALUES
    ('TRN-MAT-001', 'Toalla ficticia de capacitación', @PieceUnitId, 1, @PieceUnitId, 100, 'TRAINING', 0, 'ACTIVO', @AmenitiesCategoryId, 'Consumable', 1, @TrainingRfc, 'RawMaterial', 'StockItem', 0),
    ('TRN-MAT-002', 'Amenidad ficticia de capacitación', @BoxUnitId, 1, @BoxUnitId, 50, 'TRAINING', 0, 'ACTIVO', @AmenitiesCategoryId, 'Consumable', 1, @TrainingRfc, 'RawMaterial', 'StockItem', 0),
    ('TRN-MAT-003', 'Limpiador ficticio de capacitación', @PieceUnitId, 1, @PieceUnitId, 75, 'TRAINING', 0, 'ACTIVO', @CleaningCategoryId, 'Consumable', 1, @TrainingRfc, 'RawMaterial', 'StockItem', 0);

  INSERT logistica.StockBalance
    (LocationId, MaterialId, Quantity, MaxQuantity, MinQuantity, CountFrequencyDays,
     Notes, IsRemoved, Rfc, ReservedQuantity, AverageUnitCost)
  SELECT locationInfo.Id, materialInfo.Id,
         CONVERT(decimal(18,4), 10 + ((locationInfo.Id + materialInfo.Id) % 5)),
         30, 5, 7, 'DATOS SINTÉTICOS', 0, @TrainingRfc, 0, materialInfo.BaseUnitPrice
  FROM logistica.Location locationInfo
  CROSS JOIN logistica.Material materialInfo
  WHERE locationInfo.Rfc = @TrainingRfc
    AND materialInfo.Rfc = @TrainingRfc;

  IF (SELECT COUNT(*) FROM capacitacion.Curso WHERE Rfc = N'*' AND Clave IN
      (N'ORION-FUNDAMENTOS', N'RES-END-TO-END', N'CFDI-CONTABILIDAD', N'LOGISTICA-OPERACION', N'RH-CAPITAL-HUMANO')) <> 5
    THROW 51727, 'PROVISIONING FAILED: the expected seeded course catalog is incomplete.', 1;

  -- The complete module curriculum is the manifest of what a trainee must master.
  -- Its learning path enumerates every published course exactly once, so it is
  -- also the list used below to assign the full program.
  IF NOT EXISTS (SELECT 1 FROM capacitacion.RutaAprendizaje WHERE Rfc = N'*' AND Clave = N'ORION-EXPERTO' AND Activa = 1)
    THROW 51728, 'PROVISIONING FAILED: the complete OrionERP learning path is missing.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM capacitacion.Curso courseInfo
    JOIN capacitacion.CursoVersion versionInfo
      ON versionInfo.CursoId = courseInfo.CursoId AND versionInfo.NumeroVersion = 1
    WHERE courseInfo.Rfc = N'*'
      AND
      (
        versionInfo.Estado <> N'PUBLICADA'
        OR NOT EXISTS
        (
          SELECT 1
          FROM capacitacion.RutaCurso pathCourse
          JOIN capacitacion.RutaAprendizaje pathInfo ON pathInfo.RutaId = pathCourse.RutaId
          WHERE pathInfo.Rfc = N'*'
            AND pathInfo.Clave = N'ORION-EXPERTO'
            AND pathCourse.CursoVersionId = versionInfo.CursoVersionId
        )
      )
  )
    THROW 51729, 'PROVISIONING FAILED: a seeded course is unpublished or missing from the complete learning path.', 1;

  -- trainee01 receives the whole program: completing it is what makes the
  -- synthetic trainee an expert across every OrionERP module. trainee02 keeps
  -- the shorter pilot cohort so both shapes stay exercised.
  INSERT capacitacion.Asignacion
    (Rfc, EmployeeId, CursoVersionId, InstructorEmployeeId, Estado, Porcentaje,
     FechaLimite, AsignadaPorEmployeeId, AsignadaPor)
  SELECT CONVERT(nvarchar(50), @TrainingRfc), 990002, versionInfo.CursoVersionId,
         990001, N'ASIGNADA', 0, DATEADD(day, 30, SYSUTCDATETIME()),
         990001, N'instructor@training.orion.local'
  FROM capacitacion.RutaAprendizaje pathInfo
  JOIN capacitacion.RutaCurso pathCourse ON pathCourse.RutaId = pathInfo.RutaId
  JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = pathCourse.CursoVersionId
  WHERE pathInfo.Rfc = N'*'
    AND pathInfo.Clave = N'ORION-EXPERTO'
    AND versionInfo.Estado = N'PUBLICADA';

  INSERT capacitacion.Asignacion
    (Rfc, EmployeeId, CursoVersionId, InstructorEmployeeId, Estado, Porcentaje,
     FechaLimite, AsignadaPorEmployeeId, AsignadaPor)
  SELECT CONVERT(nvarchar(50), @TrainingRfc), 990003, versionInfo.CursoVersionId,
         990001, N'ASIGNADA', 0, DATEADD(day, 30, SYSUTCDATETIME()),
         990001, N'instructor@training.orion.local'
  FROM capacitacion.Curso courseInfo
  JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoId = courseInfo.CursoId
  WHERE courseInfo.Rfc = N'*'
    AND courseInfo.Clave IN
      (N'ORION-FUNDAMENTOS', N'RES-END-TO-END', N'CFDI-CONTABILIDAD', N'LOGISTICA-OPERACION', N'RH-CAPITAL-HUMANO')
    AND versionInfo.NumeroVersion = 1
    AND versionInfo.Estado = N'PUBLICADA';

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF OBJECT_ID('dbo.OrdenTrabajoCategoria', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoCategoria
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoCategoria PRIMARY KEY,
    Codigo varchar(50) NOT NULL,
    Nombre nvarchar(100) NOT NULL,
    Activa bit NOT NULL CONSTRAINT DF_OrdenTrabajoCategoria_Activa DEFAULT (1),
    Orden int NOT NULL CONSTRAINT DF_OrdenTrabajoCategoria_Orden DEFAULT (0),
    CreadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoCategoria_CreadaEn DEFAULT SYSUTCDATETIME()
  );

  CREATE UNIQUE INDEX UX_OrdenTrabajoCategoria_Codigo ON dbo.OrdenTrabajoCategoria (Codigo);
END;

MERGE dbo.OrdenTrabajoCategoria AS target
USING
(
  VALUES
    ('LIMPIEZA', 'Limpieza', 1),
    ('MANTENIMIENTO', 'Mantenimiento', 2),
    ('CHECKLIST', 'Checklist', 3),
    ('SERVICIO', 'Servicio', 4)
) AS source (Codigo, Nombre, Orden)
ON target.Codigo = source.Codigo
WHEN MATCHED THEN
  UPDATE SET Nombre = source.Nombre, Orden = source.Orden, Activa = 1
WHEN NOT MATCHED THEN
  INSERT (Codigo, Nombre, Orden)
  VALUES (source.Codigo, source.Nombre, source.Orden);

IF OBJECT_ID('dbo.OrdenTrabajoFolioAnual', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoFolioAnual
  (
    Anio int NOT NULL CONSTRAINT PK_OrdenTrabajoFolioAnual PRIMARY KEY,
    UltimoConsecutivo int NOT NULL,
    ActualizadoEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoFolioAnual_ActualizadoEn DEFAULT SYSUTCDATETIME()
  );
END;

IF OBJECT_ID('dbo.OrdenTrabajoPlantilla', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoPlantilla
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoPlantilla PRIMARY KEY,
    CategoriaId int NOT NULL,
    Rfc varchar(50) NOT NULL,
    Nombre nvarchar(200) NOT NULL,
    Activa bit NOT NULL CONSTRAINT DF_OrdenTrabajoPlantilla_Activa DEFAULT (1),
    CreadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantilla_CreadaEn DEFAULT SYSUTCDATETIME(),
    CreadaPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantilla_CreadaPor DEFAULT ('OrionERP'),
    ActualizadaEn datetime2(0) NULL,
    ActualizadaPor nvarchar(256) NULL,
    CONSTRAINT FK_OrdenTrabajoPlantilla_Categoria FOREIGN KEY (CategoriaId) REFERENCES dbo.OrdenTrabajoCategoria (Id)
  );

  CREATE INDEX IX_OrdenTrabajoPlantilla_RfcCategoria ON dbo.OrdenTrabajoPlantilla (Rfc, CategoriaId, Activa);
  CREATE UNIQUE INDEX UX_OrdenTrabajoPlantilla_RfcNombre ON dbo.OrdenTrabajoPlantilla (Rfc, Nombre);
END;

IF OBJECT_ID('dbo.OrdenTrabajoPlantillaVersion', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoPlantillaVersion
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoPlantillaVersion PRIMARY KEY,
    PlantillaId int NOT NULL,
    NumeroVersion int NOT NULL,
    Estado varchar(30) NOT NULL,
    CreadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaVersion_CreadaEn DEFAULT SYSUTCDATETIME(),
    CreadaPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaVersion_CreadaPor DEFAULT ('OrionERP'),
    PublicadaEn datetime2(0) NULL,
    PublicadaPor nvarchar(256) NULL,
    CONSTRAINT FK_OrdenTrabajoPlantillaVersion_Plantilla FOREIGN KEY (PlantillaId) REFERENCES dbo.OrdenTrabajoPlantilla (Id) ON DELETE CASCADE,
    CONSTRAINT CK_OrdenTrabajoPlantillaVersion_Estado CHECK (Estado IN ('BORRADOR', 'PUBLICADA', 'ARCHIVADA'))
  );

  CREATE UNIQUE INDEX UX_OrdenTrabajoPlantillaVersion_Numero ON dbo.OrdenTrabajoPlantillaVersion (PlantillaId, NumeroVersion);
  CREATE UNIQUE INDEX UX_OrdenTrabajoPlantillaVersion_Publicada ON dbo.OrdenTrabajoPlantillaVersion (PlantillaId) WHERE Estado = 'PUBLICADA';
END;

IF OBJECT_ID('dbo.OrdenTrabajoPlantillaPaso', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoPlantillaPaso
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoPlantillaPaso PRIMARY KEY,
    PlantillaVersionId int NOT NULL,
    Secuencia decimal(9,2) NOT NULL,
    Titulo nvarchar(200) NOT NULL,
    Descripcion nvarchar(1000) NOT NULL,
    PoliticaFoto varchar(30) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaPaso_PoliticaFoto DEFAULT ('NO_PERMITIDA'),
    RequiereNotasEnIncidencia bit NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaPaso_ReqInc DEFAULT (1),
    RequiereNotasEnNoAplica bit NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaPaso_ReqNA DEFAULT (1),
    ProcedimientoId int NULL,
    CONSTRAINT FK_OrdenTrabajoPlantillaPaso_Version FOREIGN KEY (PlantillaVersionId) REFERENCES dbo.OrdenTrabajoPlantillaVersion (Id) ON DELETE CASCADE,
    CONSTRAINT CK_OrdenTrabajoPlantillaPaso_PoliticaFoto CHECK (PoliticaFoto IN ('NO_PERMITIDA', 'OPCIONAL', 'REQUERIDA'))
  );

  CREATE INDEX IX_OrdenTrabajoPlantillaPaso_VersionSecuencia ON dbo.OrdenTrabajoPlantillaPaso (PlantillaVersionId, Secuencia, Id);
END;

IF OBJECT_ID('dbo.OrdenTrabajoPlantillaRoom', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoPlantillaRoom
  (
    RoomId int NOT NULL CONSTRAINT PK_OrdenTrabajoPlantillaRoom PRIMARY KEY,
    PlantillaId int NOT NULL,
    ActualizadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaRoom_ActualizadaEn DEFAULT SYSUTCDATETIME(),
    ActualizadaPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoPlantillaRoom_ActualizadaPor DEFAULT ('OrionERP'),
    CONSTRAINT FK_OrdenTrabajoPlantillaRoom_Room FOREIGN KEY (RoomId) REFERENCES dbo.ROOM (ID),
    CONSTRAINT FK_OrdenTrabajoPlantillaRoom_Plantilla FOREIGN KEY (PlantillaId) REFERENCES dbo.OrdenTrabajoPlantilla (Id)
  );

  CREATE INDEX IX_OrdenTrabajoPlantillaRoom_Plantilla ON dbo.OrdenTrabajoPlantillaRoom (PlantillaId);
END;

IF OBJECT_ID('dbo.OrdenTrabajo', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajo
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajo PRIMARY KEY,
    Folio varchar(30) NOT NULL,
    Rfc varchar(50) NOT NULL,
    CategoriaId int NOT NULL,
    Estado varchar(30) NOT NULL,
    Prioridad varchar(20) NOT NULL CONSTRAINT DF_OrdenTrabajo_Prioridad DEFAULT ('NORMAL'),
    Titulo nvarchar(200) NOT NULL,
    Descripcion nvarchar(2000) NULL,
    OwnerEmployeeId int NOT NULL,
    FechaProgramada date NOT NULL,
    HoraInicioProgramada time(0) NULL,
    HoraFinProgramada time(0) NULL,
    FechaVencimiento date NULL,
    InicioReal datetime2(0) NULL,
    FinReal datetime2(0) NULL,
    RoomId int NULL,
    RoomCalendarId int NULL,
    ReservationId int NULL,
    Ubicacion nvarchar(500) NULL,
    PlantillaId int NULL,
    PlantillaVersionId int NULL,
    EstimatedCost decimal(18,2) NOT NULL CONSTRAINT DF_OrdenTrabajo_EstimatedCost DEFAULT (0),
    CanceladaEn datetime2(0) NULL,
    CanceladaPor nvarchar(256) NULL,
    MotivoCancelacion nvarchar(2000) NULL,
    RechazadaEn datetime2(0) NULL,
    RechazadaPor nvarchar(256) NULL,
    MotivoRechazo nvarchar(2000) NULL,
    CreadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajo_CreadaEn DEFAULT SYSUTCDATETIME(),
    CreadaPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajo_CreadaPor DEFAULT ('OrionERP'),
    ActualizadaEn datetime2(0) NULL,
    ActualizadaPor nvarchar(256) NULL,
    CONSTRAINT FK_OrdenTrabajo_Categoria FOREIGN KEY (CategoriaId) REFERENCES dbo.OrdenTrabajoCategoria (Id),
    CONSTRAINT FK_OrdenTrabajo_Owner FOREIGN KEY (OwnerEmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_OrdenTrabajo_Room FOREIGN KEY (RoomId) REFERENCES dbo.ROOM (ID),
    CONSTRAINT FK_OrdenTrabajo_RoomCalendar FOREIGN KEY (RoomCalendarId) REFERENCES dbo.ROOM_CALENDAR (id),
    CONSTRAINT FK_OrdenTrabajo_Plantilla FOREIGN KEY (PlantillaId) REFERENCES dbo.OrdenTrabajoPlantilla (Id),
    CONSTRAINT FK_OrdenTrabajo_PlantillaVersion FOREIGN KEY (PlantillaVersionId) REFERENCES dbo.OrdenTrabajoPlantillaVersion (Id),
    CONSTRAINT CK_OrdenTrabajo_Estado CHECK (Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','EN_REVISION','CERRADA','CANCELADA','RECHAZADA')),
    CONSTRAINT CK_OrdenTrabajo_Prioridad CHECK (Prioridad IN ('BAJA','NORMAL','ALTA','URGENTE'))
  );

  CREATE UNIQUE INDEX UX_OrdenTrabajo_Folio ON dbo.OrdenTrabajo (Folio);
  CREATE INDEX IX_OrdenTrabajo_EstadoFecha ON dbo.OrdenTrabajo (Estado, FechaProgramada);
  CREATE INDEX IX_OrdenTrabajo_OwnerEstado ON dbo.OrdenTrabajo (OwnerEmployeeId, Estado);
  CREATE INDEX IX_OrdenTrabajo_RoomFechaCategoriaEstado ON dbo.OrdenTrabajo (RoomId, FechaProgramada, CategoriaId, Estado);
  CREATE INDEX IX_OrdenTrabajo_RoomCalendar ON dbo.OrdenTrabajo (RoomCalendarId);
  CREATE INDEX IX_OrdenTrabajo_Reservation ON dbo.OrdenTrabajo (ReservationId);
END;

DROP INDEX IF EXISTS UX_OrdenTrabajo_OpenCleaningRoomDate ON dbo.OrdenTrabajo;

DECLARE @CleaningCategoryId int;
SELECT @CleaningCategoryId = Id
FROM dbo.OrdenTrabajoCategoria
WHERE Codigo = 'LIMPIEZA';

IF @CleaningCategoryId IS NOT NULL
BEGIN
  DECLARE @OpenCleaningIndexSql nvarchar(max) =
    N'CREATE UNIQUE INDEX UX_OrdenTrabajo_OpenCleaningRoomDate
      ON dbo.OrdenTrabajo (RoomId, FechaProgramada)
      WHERE Estado IN (''BORRADOR'',''ASIGNADA'',''EN_PROCESO'',''EN_REVISION'',''RECHAZADA'')
        AND RoomId IS NOT NULL
        AND CategoriaId = ' + CONVERT(varchar(20), @CleaningCategoryId) + N';';

  EXEC sys.sp_executesql @OpenCleaningIndexSql;
END;

IF OBJECT_ID('dbo.OrdenTrabajoParticipante', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoParticipante
  (
    OrdenTrabajoId int NOT NULL,
    EmployeeId int NOT NULL,
    CreadoEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoParticipante_CreadoEn DEFAULT SYSUTCDATETIME(),
    CreadoPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoParticipante_CreadoPor DEFAULT ('OrionERP'),
    CONSTRAINT PK_OrdenTrabajoParticipante PRIMARY KEY (OrdenTrabajoId, EmployeeId),
    CONSTRAINT FK_OrdenTrabajoParticipante_Orden FOREIGN KEY (OrdenTrabajoId) REFERENCES dbo.OrdenTrabajo (Id) ON DELETE CASCADE,
    CONSTRAINT FK_OrdenTrabajoParticipante_Empleado FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID)
  );

  CREATE INDEX IX_OrdenTrabajoParticipante_Employee ON dbo.OrdenTrabajoParticipante (EmployeeId, OrdenTrabajoId);
END;

IF OBJECT_ID('dbo.OrdenTrabajoPaso', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoPaso
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoPaso PRIMARY KEY,
    OrdenTrabajoId int NOT NULL,
    PlantillaPasoId int NULL,
    Secuencia decimal(9,2) NOT NULL,
    Titulo nvarchar(200) NOT NULL,
    Descripcion nvarchar(1000) NOT NULL,
    Estado varchar(30) NOT NULL CONSTRAINT DF_OrdenTrabajoPaso_Estado DEFAULT ('PENDIENTE'),
    PoliticaFoto varchar(30) NOT NULL CONSTRAINT DF_OrdenTrabajoPaso_PoliticaFoto DEFAULT ('NO_PERMITIDA'),
    RequiereNotasEnIncidencia bit NOT NULL CONSTRAINT DF_OrdenTrabajoPaso_ReqInc DEFAULT (1),
    RequiereNotasEnNoAplica bit NOT NULL CONSTRAINT DF_OrdenTrabajoPaso_ReqNA DEFAULT (1),
    ProcedimientoId int NULL,
    Notas nvarchar(2000) NULL,
    CompletadoEn datetime2(0) NULL,
    CompletadoPor nvarchar(256) NULL,
    CONSTRAINT FK_OrdenTrabajoPaso_Orden FOREIGN KEY (OrdenTrabajoId) REFERENCES dbo.OrdenTrabajo (Id) ON DELETE CASCADE,
    CONSTRAINT FK_OrdenTrabajoPaso_PlantillaPaso FOREIGN KEY (PlantillaPasoId) REFERENCES dbo.OrdenTrabajoPlantillaPaso (Id),
    CONSTRAINT CK_OrdenTrabajoPaso_Estado CHECK (Estado IN ('PENDIENTE','HECHO','INCIDENCIA','NO_APLICA')),
    CONSTRAINT CK_OrdenTrabajoPaso_PoliticaFoto CHECK (PoliticaFoto IN ('NO_PERMITIDA','OPCIONAL','REQUERIDA'))
  );

  CREATE INDEX IX_OrdenTrabajoPaso_OrdenSecuencia ON dbo.OrdenTrabajoPaso (OrdenTrabajoId, Secuencia, Id);
END;

IF OBJECT_ID('dbo.OrdenTrabajoEvidencia', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoEvidencia
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoEvidencia PRIMARY KEY,
    PasoId int NOT NULL,
    FileName nvarchar(200) NOT NULL,
    ContentType varchar(100) NOT NULL,
    ImageBytes varbinary(max) NOT NULL,
    ThumbnailBytes varbinary(max) NULL,
    ThumbnailContentType varchar(100) NULL,
    SizeBytes bigint NOT NULL,
    DeviceInfo nvarchar(500) NULL,
    CapturadaEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoEvidencia_CapturadaEn DEFAULT SYSUTCDATETIME(),
    CapturadaPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoEvidencia_CapturadaPor DEFAULT ('OrionERP'),
    Eliminada bit NOT NULL CONSTRAINT DF_OrdenTrabajoEvidencia_Eliminada DEFAULT (0),
    EliminadaEn datetime2(0) NULL,
    EliminadaPor nvarchar(256) NULL,
    CONSTRAINT FK_OrdenTrabajoEvidencia_Paso FOREIGN KEY (PasoId) REFERENCES dbo.OrdenTrabajoPaso (Id) ON DELETE CASCADE
  );

  CREATE INDEX IX_OrdenTrabajoEvidencia_Paso ON dbo.OrdenTrabajoEvidencia (PasoId, Eliminada, CapturadaEn);
END;

IF OBJECT_ID('dbo.OrdenTrabajoTransaccion', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoTransaccion
  (
    OrdenTrabajoId int NOT NULL,
    TransaccionId int NOT NULL,
    CreadoEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoTransaccion_CreadoEn DEFAULT SYSUTCDATETIME(),
    CreadoPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoTransaccion_CreadoPor DEFAULT ('OrionERP'),
    CONSTRAINT PK_OrdenTrabajoTransaccion PRIMARY KEY (OrdenTrabajoId, TransaccionId),
    CONSTRAINT FK_OrdenTrabajoTransaccion_Orden FOREIGN KEY (OrdenTrabajoId) REFERENCES dbo.OrdenTrabajo (Id) ON DELETE CASCADE,
    CONSTRAINT FK_OrdenTrabajoTransaccion_Transaccion FOREIGN KEY (TransaccionId) REFERENCES dbo.Transacciones (ID)
  );

  CREATE INDEX IX_OrdenTrabajoTransaccion_Transaccion ON dbo.OrdenTrabajoTransaccion (TransaccionId);
END;

IF OBJECT_ID('dbo.OrdenTrabajoAuditoria', 'U') IS NULL
BEGIN
  CREATE TABLE dbo.OrdenTrabajoAuditoria
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrdenTrabajoAuditoria PRIMARY KEY,
    OrdenTrabajoId int NOT NULL,
    Evento varchar(80) NOT NULL,
    Detalle nvarchar(2000) NULL,
    CreadoEn datetime2(0) NOT NULL CONSTRAINT DF_OrdenTrabajoAuditoria_CreadoEn DEFAULT SYSUTCDATETIME(),
    CreadoPor nvarchar(256) NOT NULL CONSTRAINT DF_OrdenTrabajoAuditoria_CreadoPor DEFAULT ('OrionERP'),
    CONSTRAINT FK_OrdenTrabajoAuditoria_Orden FOREIGN KEY (OrdenTrabajoId) REFERENCES dbo.OrdenTrabajo (Id) ON DELETE CASCADE
  );

  CREATE INDEX IX_OrdenTrabajoAuditoria_Orden ON dbo.OrdenTrabajoAuditoria (OrdenTrabajoId, CreadoEn DESC, Id DESC);
END;

COMMIT TRANSACTION;
GO

-- Identity roles for the app seeder are also created in code. These inserts are safe for direct SQL-only deployments.
IF OBJECT_ID('auth.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO auth.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(varchar(36), NEWID()), roleName, UPPER(roleName), CONVERT(varchar(36), NEWID())
  FROM (VALUES ('OrdenTrabajoAdmin'), ('OrdenTrabajoSupervisor'), ('OrdenTrabajoOperador')) AS roles(roleName)
  WHERE NOT EXISTS (SELECT 1 FROM auth.AspNetRoles existing WHERE existing.NormalizedName = UPPER(roleName));
END
ELSE IF OBJECT_ID('dbo.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO dbo.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(varchar(36), NEWID()), roleName, UPPER(roleName), CONVERT(varchar(36), NEWID())
  FROM (VALUES ('OrdenTrabajoAdmin'), ('OrdenTrabajoSupervisor'), ('OrdenTrabajoOperador')) AS roles(roleName)
  WHERE NOT EXISTS (SELECT 1 FROM dbo.AspNetRoles existing WHERE existing.NormalizedName = UPPER(roleName));
END;
GO

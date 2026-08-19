-- UTF-8 usage example:
-- sqlcmd -S <servidor> -d Orion_Training -E -v ExpectedDatabase="Orion_Training" -f 65001 -i 20260817_capacitacion_v1.sql
-- Override ExpectedDatabase with -v for Orion_Sandbox or grupocarpio when intentionally deploying there.
:ON ERROR EXIT
:setvar ExpectedDatabase "Orion_Training"

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
IF @ExpectedDatabase NOT IN (N'Orion_Training', N'Orion_Sandbox', N'grupocarpio')
  THROW 51610, 'ExpectedDatabase debe ser Orion_Training, Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51611, 'La base conectada no coincide con ExpectedDatabase.', 1;

BEGIN TRANSACTION;

IF SCHEMA_ID(N'capacitacion') IS NULL
  EXEC(N'CREATE SCHEMA capacitacion AUTHORIZATION dbo;');

IF OBJECT_ID(N'capacitacion.EsquemaVersion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.EsquemaVersion
  (
    Version int NOT NULL CONSTRAINT PK_CapacitacionEsquemaVersion PRIMARY KEY,
    AplicadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionEsquemaVersion_AplicadaEn DEFAULT (SYSUTCDATETIME()),
    Descripcion nvarchar(300) NOT NULL
  );
END;

IF OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.EntornoSeguridad
  (
    EntornoSeguridadId tinyint NOT NULL CONSTRAINT PK_CapacitacionEntornoSeguridad PRIMARY KEY,
    Entorno nvarchar(20) NOT NULL,
    DatosSanitizados bit NOT NULL,
    DatosSinteticos bit NOT NULL,
    RevisadoEn datetime2(3) NULL,
    RevisadoPor nvarchar(256) NULL,
    VersionEsquema int NOT NULL,
    CONSTRAINT CK_CapacitacionEntornoSeguridad_Unica CHECK (EntornoSeguridadId = 1),
    CONSTRAINT CK_CapacitacionEntornoSeguridad_Entorno CHECK (Entorno = N'Training'),
    CONSTRAINT FK_CapacitacionEntornoSeguridad_Version FOREIGN KEY (VersionEsquema)
      REFERENCES capacitacion.EsquemaVersion(Version)
  );
END;

IF OBJECT_ID(N'capacitacion.Curso', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Curso
  (
    CursoId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionCurso PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL CONSTRAINT DF_CapacitacionCurso_Rfc DEFAULT (N'*'),
    Clave nvarchar(64) NOT NULL,
    Categoria nvarchar(80) NOT NULL,
    Nombre nvarchar(160) NOT NULL,
    Descripcion nvarchar(1000) NOT NULL,
    DuracionMinutos int NOT NULL,
    Activo bit NOT NULL CONSTRAINT DF_CapacitacionCurso_Activo DEFAULT (1),
    CreadoEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionCurso_CreadoEn DEFAULT (SYSUTCDATETIME()),
    CreadoPor nvarchar(256) NOT NULL,
    CONSTRAINT UQ_CapacitacionCurso_RfcClave UNIQUE (Rfc, Clave),
    CONSTRAINT CK_CapacitacionCurso_Duracion CHECK (DuracionMinutos > 0)
  );
END;

IF OBJECT_ID(N'capacitacion.CursoVersion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.CursoVersion
  (
    CursoVersionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionCursoVersion PRIMARY KEY,
    CursoId int NOT NULL,
    NumeroVersion int NOT NULL,
    Estado nvarchar(20) NOT NULL,
    Objetivos nvarchar(2000) NOT NULL,
    Prerequisitos nvarchar(1000) NULL,
    CalificacionMinima decimal(5,2) NOT NULL CONSTRAINT DF_CapacitacionCursoVersion_Calificacion DEFAULT (80),
    PublicadaEn datetime2(3) NULL,
    PublicadaPor nvarchar(256) NULL,
    CreadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionCursoVersion_CreadaEn DEFAULT (SYSUTCDATETIME()),
    CreadaPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionCursoVersion_Curso FOREIGN KEY (CursoId) REFERENCES capacitacion.Curso(CursoId),
    CONSTRAINT UQ_CapacitacionCursoVersion UNIQUE (CursoId, NumeroVersion),
    CONSTRAINT CK_CapacitacionCursoVersion_Numero CHECK (NumeroVersion > 0),
    CONSTRAINT CK_CapacitacionCursoVersion_Estado CHECK (Estado IN (N'BORRADOR', N'PUBLICADA', N'RETIRADA')),
    CONSTRAINT CK_CapacitacionCursoVersion_Calificacion CHECK (CalificacionMinima BETWEEN 0 AND 100)
  );
END;

IF OBJECT_ID(N'capacitacion.Leccion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Leccion
  (
    LeccionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionLeccion PRIMARY KEY,
    CursoVersionId int NOT NULL,
    Orden int NOT NULL,
    Clave nvarchar(64) NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Objetivo nvarchar(1000) NOT NULL,
    DuracionMinutos int NOT NULL,
    Requerida bit NOT NULL CONSTRAINT DF_CapacitacionLeccion_Requerida DEFAULT (1),
    CONSTRAINT FK_CapacitacionLeccion_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT UQ_CapacitacionLeccion_Orden UNIQUE (CursoVersionId, Orden),
    CONSTRAINT UQ_CapacitacionLeccion_Clave UNIQUE (CursoVersionId, Clave),
    CONSTRAINT CK_CapacitacionLeccion_Orden CHECK (Orden > 0),
    CONSTRAINT CK_CapacitacionLeccion_Duracion CHECK (DuracionMinutos > 0)
  );
END;

IF OBJECT_ID(N'capacitacion.BloqueContenido', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.BloqueContenido
  (
    BloqueId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionBloque PRIMARY KEY,
    LeccionId int NOT NULL,
    Orden int NOT NULL,
    Tipo nvarchar(24) NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Contenido nvarchar(max) NOT NULL,
    ConfiguracionJson nvarchar(max) NULL,
    Requerido bit NOT NULL CONSTRAINT DF_CapacitacionBloque_Requerido DEFAULT (1),
    CONSTRAINT FK_CapacitacionBloque_Leccion FOREIGN KEY (LeccionId) REFERENCES capacitacion.Leccion(LeccionId),
    CONSTRAINT UQ_CapacitacionBloque_Orden UNIQUE (LeccionId, Orden),
    CONSTRAINT CK_CapacitacionBloque_Orden CHECK (Orden > 0),
    CONSTRAINT CK_CapacitacionBloque_Tipo CHECK (Tipo IN (N'OBJETIVOS', N'TEORIA', N'IMAGEN', N'PASOS', N'DEMOSTRACION', N'PRACTICA', N'EVALUACION', N'RESUMEN', N'ALERTA')),
    CONSTRAINT CK_CapacitacionBloque_Json CHECK (ConfiguracionJson IS NULL OR ISJSON(ConfiguracionJson) = 1)
  );
END;

IF OBJECT_ID(N'capacitacion.Recurso', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Recurso
  (
    RecursoId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionRecurso PRIMARY KEY,
    BloqueId int NOT NULL,
    Orden int NOT NULL,
    Tipo nvarchar(30) NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Ruta nvarchar(500) NOT NULL,
    TextoAlternativo nvarchar(500) NULL,
    HashContenido nvarchar(128) NULL,
    CapturadoEn datetime2(3) NULL,
    VersionAplicacion nvarchar(50) NULL,
    CONSTRAINT FK_CapacitacionRecurso_Bloque FOREIGN KEY (BloqueId) REFERENCES capacitacion.BloqueContenido(BloqueId),
    CONSTRAINT UQ_CapacitacionRecurso_Orden UNIQUE (BloqueId, Orden),
    CONSTRAINT CK_CapacitacionRecurso_Tipo CHECK (Tipo IN (N'IMAGEN', N'DIAGRAMA', N'VIDEO', N'ARCHIVO', N'ENLACE'))
  );
END;

IF OBJECT_ID(N'capacitacion.Evaluacion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Evaluacion
  (
    EvaluacionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionEvaluacion PRIMARY KEY,
    CursoVersionId int NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Instrucciones nvarchar(1000) NOT NULL,
    CalificacionMinima decimal(5,2) NOT NULL,
    Requerida bit NOT NULL CONSTRAINT DF_CapacitacionEvaluacion_Requerida DEFAULT (1),
    CONSTRAINT FK_CapacitacionEvaluacion_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT UQ_CapacitacionEvaluacion_Titulo UNIQUE (CursoVersionId, Titulo),
    CONSTRAINT CK_CapacitacionEvaluacion_Calificacion CHECK (CalificacionMinima BETWEEN 0 AND 100)
  );
END;

IF OBJECT_ID(N'capacitacion.Pregunta', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Pregunta
  (
    PreguntaId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionPregunta PRIMARY KEY,
    EvaluacionId int NOT NULL,
    Orden int NOT NULL,
    Texto nvarchar(1000) NOT NULL,
    Explicacion nvarchar(1000) NULL,
    Critica bit NOT NULL CONSTRAINT DF_CapacitacionPregunta_Critica DEFAULT (0),
    CONSTRAINT FK_CapacitacionPregunta_Evaluacion FOREIGN KEY (EvaluacionId) REFERENCES capacitacion.Evaluacion(EvaluacionId),
    CONSTRAINT UQ_CapacitacionPregunta_Orden UNIQUE (EvaluacionId, Orden)
  );
END;

IF OBJECT_ID(N'capacitacion.OpcionPregunta', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.OpcionPregunta
  (
    OpcionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionOpcion PRIMARY KEY,
    PreguntaId int NOT NULL,
    Orden int NOT NULL,
    Texto nvarchar(1000) NOT NULL,
    EsCorrecta bit NOT NULL,
    CONSTRAINT FK_CapacitacionOpcion_Pregunta FOREIGN KEY (PreguntaId) REFERENCES capacitacion.Pregunta(PreguntaId),
    CONSTRAINT UQ_CapacitacionOpcion_Orden UNIQUE (PreguntaId, Orden)
  );
END;

IF OBJECT_ID(N'capacitacion.Practica', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Practica
  (
    PracticaId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionPractica PRIMARY KEY,
    CursoVersionId int NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Instrucciones nvarchar(2000) NOT NULL,
    RutaSandbox nvarchar(500) NULL,
    Requerida bit NOT NULL CONSTRAINT DF_CapacitacionPractica_Requerida DEFAULT (1),
    CONSTRAINT FK_CapacitacionPractica_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT UQ_CapacitacionPractica_Titulo UNIQUE (CursoVersionId, Titulo)
  );
END;

IF OBJECT_ID(N'capacitacion.PracticaPaso', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.PracticaPaso
  (
    PracticaPasoId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionPracticaPaso PRIMARY KEY,
    PracticaId int NOT NULL,
    Orden int NOT NULL,
    Descripcion nvarchar(1000) NOT NULL,
    Critico bit NOT NULL CONSTRAINT DF_CapacitacionPracticaPaso_Critico DEFAULT (0),
    CONSTRAINT FK_CapacitacionPracticaPaso_Practica FOREIGN KEY (PracticaId) REFERENCES capacitacion.Practica(PracticaId),
    CONSTRAINT UQ_CapacitacionPracticaPaso_Orden UNIQUE (PracticaId, Orden)
  );
END;

IF OBJECT_ID(N'capacitacion.RutaAprendizaje', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.RutaAprendizaje
  (
    RutaId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionRuta PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    Clave nvarchar(64) NOT NULL,
    Nombre nvarchar(160) NOT NULL,
    Descripcion nvarchar(1000) NOT NULL,
    Activa bit NOT NULL CONSTRAINT DF_CapacitacionRuta_Activa DEFAULT (1),
    CreadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionRuta_CreadaEn DEFAULT (SYSUTCDATETIME()),
    CreadaPor nvarchar(256) NOT NULL,
    CONSTRAINT UQ_CapacitacionRuta_RfcClave UNIQUE (Rfc, Clave)
  );
END;

IF OBJECT_ID(N'capacitacion.RutaCurso', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.RutaCurso
  (
    RutaId int NOT NULL,
    CursoVersionId int NOT NULL,
    Orden int NOT NULL,
    Requerido bit NOT NULL CONSTRAINT DF_CapacitacionRutaCurso_Requerido DEFAULT (1),
    CONSTRAINT PK_CapacitacionRutaCurso PRIMARY KEY (RutaId, CursoVersionId),
    CONSTRAINT FK_CapacitacionRutaCurso_Ruta FOREIGN KEY (RutaId) REFERENCES capacitacion.RutaAprendizaje(RutaId),
    CONSTRAINT FK_CapacitacionRutaCurso_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT UQ_CapacitacionRutaCurso_Orden UNIQUE (RutaId, Orden)
  );
END;

IF OBJECT_ID(N'capacitacion.Asignacion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Asignacion
  (
    AsignacionId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionAsignacion PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    CursoVersionId int NOT NULL,
    InstructorEmployeeId int NULL,
    Estado nvarchar(24) NOT NULL CONSTRAINT DF_CapacitacionAsignacion_Estado DEFAULT (N'ASIGNADA'),
    Porcentaje decimal(5,2) NOT NULL CONSTRAINT DF_CapacitacionAsignacion_Porcentaje DEFAULT (0),
    FechaLimite datetime2(3) NULL,
    AsignadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionAsignacion_AsignadaEn DEFAULT (SYSUTCDATETIME()),
    AsignadaPorEmployeeId int NOT NULL,
    AsignadaPor nvarchar(256) NOT NULL,
    IniciadaEn datetime2(3) NULL,
    CompletadaEn datetime2(3) NULL,
    CONSTRAINT FK_CapacitacionAsignacion_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT CK_CapacitacionAsignacion_Estado CHECK (Estado IN (N'ASIGNADA', N'EN_CURSO', N'ESPERA_FIRMA', N'ESPERA_ACUSE', N'COMPLETADA', N'CANCELADA')),
    CONSTRAINT CK_CapacitacionAsignacion_Porcentaje CHECK (Porcentaje BETWEEN 0 AND 100)
  );
  CREATE UNIQUE INDEX UX_CapacitacionAsignacion_Activa
    ON capacitacion.Asignacion(Rfc, EmployeeId, CursoVersionId)
    WHERE Estado <> N'CANCELADA';
  CREATE INDEX IX_CapacitacionAsignacion_Empleado ON capacitacion.Asignacion(Rfc, EmployeeId, Estado, FechaLimite);
END;

IF OBJECT_ID(N'capacitacion.Sesion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Sesion
  (
    SesionId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionSesion PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    CursoVersionId int NOT NULL,
    Nombre nvarchar(160) NOT NULL,
    CodigoAcceso char(8) NOT NULL,
    Estado nvarchar(20) NOT NULL CONSTRAINT DF_CapacitacionSesion_Estado DEFAULT (N'PROGRAMADA'),
    InstructorEmployeeId int NOT NULL,
    BloqueActualId int NULL,
    ProgramadaEn datetime2(3) NOT NULL,
    IniciadaEn datetime2(3) NULL,
    FinalizadaEn datetime2(3) NULL,
    CreadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionSesion_CreadaEn DEFAULT (SYSUTCDATETIME()),
    CreadaPorEmployeeId int NOT NULL,
    CreadaPor nvarchar(256) NOT NULL,
    VersionFila rowversion NOT NULL,
    CONSTRAINT FK_CapacitacionSesion_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT FK_CapacitacionSesion_Bloque FOREIGN KEY (BloqueActualId) REFERENCES capacitacion.BloqueContenido(BloqueId),
    CONSTRAINT UQ_CapacitacionSesion_Codigo UNIQUE (CodigoAcceso),
    CONSTRAINT CK_CapacitacionSesion_Estado CHECK (Estado IN (N'PROGRAMADA', N'EN_CURSO', N'FINALIZADA', N'CANCELADA'))
  );
  CREATE INDEX IX_CapacitacionSesion_RfcEstado ON capacitacion.Sesion(Rfc, Estado, ProgramadaEn);
END;

IF OBJECT_ID(N'capacitacion.SesionParticipante', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.SesionParticipante
  (
    SesionId bigint NOT NULL,
    EmployeeId int NOT NULL,
    AsignacionId bigint NULL,
    Rol nvarchar(20) NOT NULL,
    UnidoEn datetime2(3) NULL,
    CONSTRAINT PK_CapacitacionSesionParticipante PRIMARY KEY (SesionId, EmployeeId),
    CONSTRAINT FK_CapacitacionSesionParticipante_Sesion FOREIGN KEY (SesionId) REFERENCES capacitacion.Sesion(SesionId),
    CONSTRAINT FK_CapacitacionSesionParticipante_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT CK_CapacitacionSesionParticipante_Rol CHECK (Rol IN (N'INSTRUCTOR', N'COLABORADOR', N'OBSERVADOR'))
  );
END;

IF OBJECT_ID(N'capacitacion.ProgresoBloque', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.ProgresoBloque
  (
    ProgresoBloqueId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionProgresoBloque PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    AsignacionId bigint NOT NULL,
    SesionId bigint NULL,
    EmployeeId int NOT NULL,
    BloqueId int NOT NULL,
    Estado nvarchar(20) NOT NULL,
    CompletadoEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionProgresoBloque_CompletadoEn DEFAULT (SYSUTCDATETIME()),
    RegistradoPorEmployeeId int NOT NULL,
    RegistradoPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionProgresoBloque_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT FK_CapacitacionProgresoBloque_Sesion FOREIGN KEY (SesionId) REFERENCES capacitacion.Sesion(SesionId),
    CONSTRAINT FK_CapacitacionProgresoBloque_Bloque FOREIGN KEY (BloqueId) REFERENCES capacitacion.BloqueContenido(BloqueId),
    CONSTRAINT UQ_CapacitacionProgresoBloque UNIQUE (AsignacionId, EmployeeId, BloqueId),
    CONSTRAINT CK_CapacitacionProgresoBloque_Estado CHECK (Estado IN (N'COMPLETADO', N'OMITIDO'))
  );
END;

IF OBJECT_ID(N'capacitacion.IntentoEvaluacion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.IntentoEvaluacion
  (
    IntentoId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionIntento PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    AsignacionId bigint NOT NULL,
    SesionId bigint NULL,
    EvaluacionId int NOT NULL,
    EmployeeId int NOT NULL,
    NumeroIntento int NOT NULL,
    Calificacion decimal(5,2) NOT NULL,
    Aprobada bit NOT NULL,
    FalloPreguntaCritica bit NOT NULL,
    PresentadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionIntento_PresentadaEn DEFAULT (SYSUTCDATETIME()),
    RegistradoPorEmployeeId int NOT NULL,
    RegistradoPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionIntento_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT FK_CapacitacionIntento_Sesion FOREIGN KEY (SesionId) REFERENCES capacitacion.Sesion(SesionId),
    CONSTRAINT FK_CapacitacionIntento_Evaluacion FOREIGN KEY (EvaluacionId) REFERENCES capacitacion.Evaluacion(EvaluacionId),
    CONSTRAINT UQ_CapacitacionIntento_Numero UNIQUE (AsignacionId, EvaluacionId, NumeroIntento),
    CONSTRAINT CK_CapacitacionIntento_Calificacion CHECK (Calificacion BETWEEN 0 AND 100)
  );
END;

IF OBJECT_ID(N'capacitacion.RespuestaEvaluacion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.RespuestaEvaluacion
  (
    IntentoId bigint NOT NULL,
    PreguntaId int NOT NULL,
    OpcionId int NOT NULL,
    EsCorrecta bit NOT NULL,
    CONSTRAINT PK_CapacitacionRespuesta PRIMARY KEY (IntentoId, PreguntaId),
    CONSTRAINT FK_CapacitacionRespuesta_Intento FOREIGN KEY (IntentoId) REFERENCES capacitacion.IntentoEvaluacion(IntentoId),
    CONSTRAINT FK_CapacitacionRespuesta_Pregunta FOREIGN KEY (PreguntaId) REFERENCES capacitacion.Pregunta(PreguntaId),
    CONSTRAINT FK_CapacitacionRespuesta_Opcion FOREIGN KEY (OpcionId) REFERENCES capacitacion.OpcionPregunta(OpcionId)
  );
END;

IF OBJECT_ID(N'capacitacion.ResultadoPractico', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.ResultadoPractico
  (
    ResultadoPracticoId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionResultadoPractico PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    AsignacionId bigint NOT NULL,
    SesionId bigint NULL,
    PracticaId int NOT NULL,
    EmployeeId int NOT NULL,
    NumeroIntento int NOT NULL,
    Aprobada bit NOT NULL,
    Observaciones nvarchar(1000) NULL,
    EvaluadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionResultadoPractico_EvaluadaEn DEFAULT (SYSUTCDATETIME()),
    EvaluadaPorEmployeeId int NOT NULL,
    EvaluadaPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionResultadoPractico_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT FK_CapacitacionResultadoPractico_Sesion FOREIGN KEY (SesionId) REFERENCES capacitacion.Sesion(SesionId),
    CONSTRAINT FK_CapacitacionResultadoPractico_Practica FOREIGN KEY (PracticaId) REFERENCES capacitacion.Practica(PracticaId),
    CONSTRAINT UQ_CapacitacionResultadoPractico_Numero UNIQUE (AsignacionId, PracticaId, NumeroIntento)
  );
END;

IF OBJECT_ID(N'capacitacion.ResultadoPracticoPaso', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.ResultadoPracticoPaso
  (
    ResultadoPracticoId bigint NOT NULL,
    PracticaPasoId int NOT NULL,
    Aprobado bit NOT NULL,
    Observaciones nvarchar(500) NULL,
    CONSTRAINT PK_CapacitacionResultadoPracticoPaso PRIMARY KEY (ResultadoPracticoId, PracticaPasoId),
    CONSTRAINT FK_CapacitacionResultadoPracticoPaso_Resultado FOREIGN KEY (ResultadoPracticoId) REFERENCES capacitacion.ResultadoPractico(ResultadoPracticoId),
    CONSTRAINT FK_CapacitacionResultadoPracticoPaso_Paso FOREIGN KEY (PracticaPasoId) REFERENCES capacitacion.PracticaPaso(PracticaPasoId)
  );
END;

IF OBJECT_ID(N'capacitacion.FirmaInstructor', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.FirmaInstructor
  (
    FirmaInstructorId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionFirmaInstructor PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    AsignacionId bigint NOT NULL,
    InstructorEmployeeId int NOT NULL,
    Comentarios nvarchar(1000) NULL,
    FirmadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionFirmaInstructor_FirmadaEn DEFAULT (SYSUTCDATETIME()),
    FirmadaPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionFirmaInstructor_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT UQ_CapacitacionFirmaInstructor_Asignacion UNIQUE (AsignacionId)
  );
END;

IF OBJECT_ID(N'capacitacion.Finalizacion', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.Finalizacion
  (
    FinalizacionId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionFinalizacion PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    AsignacionId bigint NOT NULL,
    EmployeeId int NOT NULL,
    CursoId int NOT NULL,
    CursoVersionId int NOT NULL,
    NumeroVersion int NOT NULL,
    CursoClave nvarchar(64) NOT NULL,
    CursoNombre nvarchar(160) NOT NULL,
    Calificacion decimal(5,2) NULL,
    PracticaAprobada bit NOT NULL,
    FirmaInstructorId bigint NOT NULL,
    AcusadaEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionFinalizacion_AcusadaEn DEFAULT (SYSUTCDATETIME()),
    AcusadaPor nvarchar(256) NOT NULL,
    CONSTRAINT FK_CapacitacionFinalizacion_Asignacion FOREIGN KEY (AsignacionId) REFERENCES capacitacion.Asignacion(AsignacionId),
    CONSTRAINT FK_CapacitacionFinalizacion_Curso FOREIGN KEY (CursoId) REFERENCES capacitacion.Curso(CursoId),
    CONSTRAINT FK_CapacitacionFinalizacion_Version FOREIGN KEY (CursoVersionId) REFERENCES capacitacion.CursoVersion(CursoVersionId),
    CONSTRAINT FK_CapacitacionFinalizacion_Firma FOREIGN KEY (FirmaInstructorId) REFERENCES capacitacion.FirmaInstructor(FirmaInstructorId),
    CONSTRAINT UQ_CapacitacionFinalizacion_Asignacion UNIQUE (AsignacionId)
  );
END;

IF OBJECT_ID(N'capacitacion.EventoAuditoria', N'U') IS NULL
BEGIN
  CREATE TABLE capacitacion.EventoAuditoria
  (
    EventoId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CapacitacionEvento PRIMARY KEY,
    Rfc nvarchar(50) NOT NULL,
    Entidad nvarchar(40) NOT NULL,
    EntidadId bigint NOT NULL,
    Evento nvarchar(64) NOT NULL,
    Detalle nvarchar(2000) NULL,
    DatosJson nvarchar(max) NULL,
    ActorEmployeeId int NOT NULL,
    Actor nvarchar(256) NOT NULL,
    CreadoEn datetime2(3) NOT NULL CONSTRAINT DF_CapacitacionEvento_CreadoEn DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_CapacitacionEvento_Json CHECK (DatosJson IS NULL OR ISJSON(DatosJson) = 1)
  );
  CREATE INDEX IX_CapacitacionEvento_Entidad ON capacitacion.EventoAuditoria(Rfc, Entidad, EntidadId, CreadoEn);
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Finalizacion_AppendOnly
ON capacitacion.Finalizacion
INSTEAD OF UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  THROW 51600, 'Las finalizaciones de capacitación son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_EventoAuditoria_AppendOnly
ON capacitacion.EventoAuditoria
INSTEAD OF UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  THROW 51601, 'La auditoría de capacitación es inmutable.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_CursoVersion_PublicadaInmutable
ON capacitacion.CursoVersion
AFTER UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1
    FROM deleted oldRow
    LEFT JOIN inserted newRow ON newRow.CursoVersionId = oldRow.CursoVersionId
    WHERE oldRow.PublicadaEn IS NOT NULL
      AND
      (
        oldRow.Estado <> N'PUBLICADA'
        OR
        newRow.CursoVersionId IS NULL
        OR newRow.Estado <> N'RETIRADA'
        OR newRow.CursoId <> oldRow.CursoId
        OR newRow.NumeroVersion <> oldRow.NumeroVersion
        OR newRow.Objetivos <> oldRow.Objetivos
        OR ISNULL(newRow.Prerequisitos, N'') <> ISNULL(oldRow.Prerequisitos, N'')
        OR newRow.CalificacionMinima <> oldRow.CalificacionMinima
        OR ISNULL(newRow.PublicadaEn, CONVERT(datetime2(3), '19000101')) <> ISNULL(oldRow.PublicadaEn, CONVERT(datetime2(3), '19000101'))
        OR ISNULL(newRow.PublicadaPor, N'') <> ISNULL(oldRow.PublicadaPor, N'')
        OR newRow.CreadaEn <> oldRow.CreadaEn
        OR newRow.CreadaPor <> oldRow.CreadaPor
      )
  )
  BEGIN
    THROW 51602, 'Una versión publicada solo puede cambiar de PUBLICADA a RETIRADA sin modificar su contenido.', 1;
  END;
END;
GO

BEGIN TRANSACTION;

DECLARE @SeedActor nvarchar(256) = N'OrionERP.Capacitacion.Seed.v1';

DECLARE @Cursos TABLE
(
  Clave nvarchar(64) NOT NULL,
  Categoria nvarchar(80) NOT NULL,
  Nombre nvarchar(160) NOT NULL,
  Descripcion nvarchar(1000) NOT NULL,
  Duracion int NOT NULL,
  Objetivos nvarchar(2000) NOT NULL,
  Prerequisitos nvarchar(1000) NULL
);

INSERT INTO @Cursos (Clave, Categoria, Nombre, Descripcion, Duracion, Objetivos, Prerequisitos)
VALUES
  (N'ORION-FUNDAMENTOS', N'Fundamentos', N'Fundamentos de OrionERP', N'Introducción segura y orientada a procesos para toda persona que utilizará OrionERP.', 75, N'Reconocer los entornos, navegar por módulos, proteger información y pedir ayuda sin arriesgar datos operativos.', N'Contar con usuario OrionERP ligado al colaborador.'),
  (N'RES-END-TO-END', N'Reservaciones', N'Reservaciones de principio a fin', N'Flujo guiado desde la captura de una reservación hasta pago, CFDI, calendario y orden de trabajo.', 120, N'Capturar, validar y dar seguimiento a una reservación completa manteniendo trazabilidad entre áreas.', N'Haber completado Fundamentos de OrionERP.'),
  (N'CFDI-CONTABILIDAD', N'Fiscal y contabilidad', N'Del CFDI a la contabilidad', N'Análisis estructural de un CFDI ficticio y diseño guiado de su propuesta de póliza y trazabilidad, sin publicar efectos contables o bancarios.', 150, N'Interpretar un CFDI, prevenir duplicados, elaborar una propuesta contable balanceada y explicar su trazabilidad sin crear movimientos fuera del escenario disponible.', N'Haber completado Fundamentos de OrionERP y conocer los conceptos contables básicos.'),
  (N'LOGISTICA-OPERACION', N'Logística', N'Logística: materiales, compras e inventario', N'Flujo seguro desde el catálogo de materiales y proveedores hasta compras, recepciones, ubicaciones y conteos.', 120, N'Reconocer la cadena logística, prevenir recepciones duplicadas y documentar existencias y diferencias con trazabilidad.', N'Haber completado Fundamentos de OrionERP.'),
  (N'RH-CAPITAL-HUMANO', N'Capital Humano', N'Capital Humano: autoservicio del colaborador', N'Uso privado y responsable de Mi trabajo para consultar información propia y gestionar incidencias, correcciones y ausencias.', 90, N'Usar el autoservicio personal sin exponer datos de terceros, reunir evidencia y escalar solicitudes por el canal correcto.', N'Haber completado Fundamentos de OrionERP y contar con una identidad de colaborador vinculada.');

INSERT INTO capacitacion.Curso (Rfc, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, CreadoPor)
SELECT N'*', source.Clave, source.Categoria, source.Nombre, source.Descripcion, source.Duracion, @SeedActor
FROM @Cursos source
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Curso target WHERE target.Rfc = N'*' AND target.Clave = source.Clave
);

INSERT INTO capacitacion.CursoVersion
  (CursoId, NumeroVersion, Estado, Objetivos, Prerequisitos, CalificacionMinima, PublicadaEn, PublicadaPor, CreadaPor)
SELECT curso.CursoId, 1, N'BORRADOR', source.Objetivos, source.Prerequisitos, 80, NULL, NULL, @SeedActor
FROM @Cursos source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.Clave
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.CursoVersion versionInfo WHERE versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
);

DECLARE @Fundamentos int =
(
  SELECT versionInfo.CursoVersionId
  FROM capacitacion.CursoVersion versionInfo
  JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
  WHERE curso.Rfc = N'*' AND curso.Clave = N'ORION-FUNDAMENTOS' AND versionInfo.NumeroVersion = 1
);
DECLARE @Reservaciones int =
(
  SELECT versionInfo.CursoVersionId
  FROM capacitacion.CursoVersion versionInfo
  JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
  WHERE curso.Rfc = N'*' AND curso.Clave = N'RES-END-TO-END' AND versionInfo.NumeroVersion = 1
);
DECLARE @Cfdi int =
(
  SELECT versionInfo.CursoVersionId
  FROM capacitacion.CursoVersion versionInfo
  JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
  WHERE curso.Rfc = N'*' AND curso.Clave = N'CFDI-CONTABILIDAD' AND versionInfo.NumeroVersion = 1
);
DECLARE @Logistica int =
(
  SELECT versionInfo.CursoVersionId
  FROM capacitacion.CursoVersion versionInfo
  JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
  WHERE curso.Rfc = N'*' AND curso.Clave = N'LOGISTICA-OPERACION' AND versionInfo.NumeroVersion = 1
);
DECLARE @CapitalHumano int =
(
  SELECT versionInfo.CursoVersionId
  FROM capacitacion.CursoVersion versionInfo
  JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
  WHERE curso.Rfc = N'*' AND curso.Clave = N'RH-CAPITAL-HUMANO' AND versionInfo.NumeroVersion = 1
);

DECLARE @Lecciones TABLE
(
  CursoVersionId int NOT NULL,
  Orden int NOT NULL,
  Clave nvarchar(64) NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Objetivo nvarchar(1000) NOT NULL,
  Duracion int NOT NULL
);

INSERT INTO @Lecciones VALUES
  (@Fundamentos, 1, N'ENTORNOS', N'Entornos y seguridad', N'Distinguir producción de práctica y aplicar las reglas de protección de datos.', 20),
  (@Fundamentos, 2, N'NAVEGACION', N'Navegación orientada a procesos', N'Encontrar módulos, filtros, acciones y ayuda contextual.', 25),
  (@Fundamentos, 3, N'BUENAS-PRACTICAS', N'Buenas prácticas operativas', N'Validar antes de guardar, conservar trazabilidad y escalar incidentes.', 30),
  (@Reservaciones, 1, N'CAPTURA', N'Captura y validación', N'Registrar datos de huésped, habitación, fechas, conceptos y condiciones.', 35),
  (@Reservaciones, 2, N'PAGO-CFDI', N'Pago y facturación', N'Aplicar pagos y decidir correctamente cuándo y cómo emitir CFDI.', 35),
  (@Reservaciones, 3, N'CALENDARIO-OT', N'Calendario y orden de trabajo', N'Confirmar sincronización operativa y preparar la entrega de la habitación.', 50),
  (@Cfdi, 1, N'LECTURA-CFDI', N'Lectura fiscal del CFDI', N'Reconocer emisor, receptor, tipo, conceptos, impuestos, UUID y relaciones.', 45),
  (@Cfdi, 2, N'REGISTRO', N'Registro y póliza', N'Clasificar el comprobante y plantear un registro contable balanceado sin publicarlo.', 55),
  (@Cfdi, 3, N'BANCO-TRAZABILIDAD', N'Banco, conciliación y trazabilidad', N'Vincular movimientos sin duplicar efectos y conservar evidencia auditable.', 50),
  (@Logistica, 1, N'PREPARAR-LOGISTICA', N'Preparar el flujo logístico', N'Identificar contexto, referencias y datos mínimos antes de tocar existencias.', 25),
  (@Logistica, 2, N'FLUJO-LOGISTICO', N'Materiales, compras y existencias', N'Recorrer materiales, proveedores, compras, recepciones y ubicaciones sin duplicar efectos.', 55),
  (@Logistica, 3, N'CONTROL-LOGISTICO', N'Control, evidencia y cierre', N'Realizar conteos, explicar diferencias y conservar evidencia para su autorización.', 40),
  (@CapitalHumano, 1, N'PREPARAR-RH', N'Identidad, privacidad y alcance', N'Confirmar la identidad propia y distinguir el autoservicio de las funciones administrativas.', 20),
  (@CapitalHumano, 2, N'MI-TRABAJO', N'Autoservicio del colaborador', N'Consultar información personal y preparar correcciones o solicitudes con evidencia ficticia.', 40),
  (@CapitalHumano, 3, N'PRIVACIDAD-CIERRE', N'Privacidad, seguimiento y cierre', N'Dar seguimiento a solicitudes propias, proteger datos y escalar excepciones.', 30);

INSERT INTO capacitacion.Leccion (CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida)
SELECT source.CursoVersionId, source.Orden, source.Clave, source.Titulo, source.Objetivo, source.Duracion, 1
FROM @Lecciones source
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Leccion target
  WHERE target.CursoVersionId = source.CursoVersionId AND target.Clave = source.Clave
);

DECLARE @Bloques TABLE
(
  CursoVersionId int NOT NULL,
  LeccionClave nvarchar(64) NOT NULL,
  Orden int NOT NULL,
  Tipo nvarchar(24) NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Contenido nvarchar(max) NOT NULL,
  ConfiguracionJson nvarchar(max) NULL
);

INSERT INTO @Bloques VALUES
  (@Fundamentos,N'ENTORNOS',1,N'OBJETIVOS',N'Objetivos de la lección',N'Al finalizar podrás reconocer el entorno activo, identificar datos simulados y detener una acción cuando el contexto no sea seguro.',N'{"icon":"target"}'),
  (@Fundamentos,N'ENTORNOS',2,N'TEORIA',N'Producción y práctica no son intercambiables',N'Producción representa operaciones reales. El entorno Orion_Training usa identidades y escenarios ficticios, integraciones simuladas y puede reiniciarse. Verifica siempre el distintivo del entorno antes de capturar.',N'{"callout":"info"}'),
  (@Fundamentos,N'ENTORNOS',3,N'IMAGEN',N'Lista visual antes de actuar',N'Observa el distintivo del entorno, el RFC activo, tu identidad y el módulo. Si cualquiera no coincide con el ejercicio, detente y avisa al instructor.',N'{"layout":"checklist","items":["Entorno","RFC","Usuario","Módulo"]}'),
  (@Fundamentos,N'ENTORNOS',4,N'ALERTA',N'Nunca uses información real para practicar',N'No copies RFC, XML, cuentas bancarias, correos, teléfonos, FIEL, contraseñas ni datos de huéspedes reales al entorno de capacitación.',N'{"severity":"critical","notasInstructor":"Pida un ejemplo de dato sensible. Corrija la idea común de que anonimizar solo el nombre vuelve seguro un caso real."}'),
  (@Fundamentos,N'NAVEGACION',1,N'DEMOSTRACION',N'Ubicar un flujo',N'El instructor mostrará cómo abrir el menú, buscar una sección, interpretar permisos, aplicar filtros y regresar al tablero sin perder contexto.',N'{"demoSteps":["Buscar módulo","Leer encabezado","Aplicar filtro","Abrir detalle","Volver al tablero"],"notasInstructor":"Entregue el control al colaborador después del primer recorrido; no convierta esta parte en una demostración pasiva."}'),
  (@Fundamentos,N'NAVEGACION',2,N'PASOS',N'Patrón de trabajo en OrionERP',N'1. Confirma RFC y periodo. 2. Filtra antes de editar. 3. Abre el detalle correcto. 4. Revisa campos y evidencias. 5. Guarda una vez. 6. Confirma el resultado.',N'{"numbered":true}'),
  (@Fundamentos,N'NAVEGACION',3,N'PRACTICA',N'Recorrido de orientación',N'En Orion_Training localiza Capital Humano, Reservaciones, CFDI, Contabilidad y Logística; no guardes cambios fuera del escenario indicado.',N'{"sandbox":true,"notasInstructor":"Observe si confirma entorno y RFC sin recordatorio. No le indique de inmediato dónde está cada módulo; permita que use la búsqueda."}'),
  (@Fundamentos,N'BUENAS-PRACTICAS',1,N'TEORIA',N'Trazabilidad y responsabilidad',N'Cada acción debe poder explicarse: qué registro se modificó, por qué, con qué evidencia y por quién. Evita usuarios compartidos y comentarios ambiguos.',NULL),
  (@Fundamentos,N'BUENAS-PRACTICAS',2,N'RESUMEN',N'Regla de cierre',N'Antes de terminar confirma que el cambio aparece una sola vez, conserva su referencia y comunica cualquier excepción. Una pantalla sin error no garantiza que el proceso esté completo.',N'{"highlight":true}'),
  (@Fundamentos,N'BUENAS-PRACTICAS',3,N'EVALUACION',N'Comprobación de fundamentos',N'Responde la evaluación y completa el recorrido práctico con el instructor.',N'{"required":true}'),

  (@Reservaciones,N'CAPTURA',1,N'OBJETIVOS',N'Objetivo del flujo',N'Crear una reservación coherente cuyos datos comerciales, fiscales y operativos puedan continuar sin retrabajo.',NULL),
  (@Reservaciones,N'CAPTURA',2,N'TEORIA',N'La reservación conecta varias áreas',N'Fechas, habitación, huésped, conceptos, impuestos y condiciones alimentan cobro, factura, calendario y órdenes de trabajo. Un dato incompleto se propaga.',N'{"diagram":["Reservación","Pago/CFDI","Calendario","Orden de trabajo"]}'),
  (@Reservaciones,N'CAPTURA',3,N'PASOS',N'Validación previa',N'Confirma disponibilidad, identidad ficticia del huésped, fechas, ocupación, moneda, precio, extras, impuestos y política de facturación antes de guardar.',N'{"numbered":true}'),
  (@Reservaciones,N'PAGO-CFDI',1,N'DEMOSTRACION',N'Registrar cobro y necesidad fiscal',N'El instructor demostrará cómo documentar el pago simulado, revisar saldos y establecer la necesidad de CFDI sin timbrar en servicios reales.',N'{"integrationMode":"simulated","notasInstructor":"Muestre primero dónde se comprueba el saldo. La confusión habitual es asumir que guardar el pago equivale a confirmar su aplicación."}'),
  (@Reservaciones,N'PAGO-CFDI',2,N'ALERTA',N'Evita duplicados',N'Antes de repetir un pago o CFDI busca la referencia existente, revisa estatus y confirma si la primera operación quedó registrada.',N'{"severity":"critical","notasInstructor":"Plantee una respuesta lenta del sistema y pregunte qué haría. Debe buscar y verificar antes de reintentar."}'),
  (@Reservaciones,N'PAGO-CFDI',3,N'PRACTICA',N'Escenario de cobro',N'Aplica el pago ficticio del caso, verifica saldo y prepara el CFDI simulado con datos del escenario.',N'{"sandbox":true,"notasInstructor":"No confirme cada clic. Evalúe que relacione monto, referencia, saldo y condición fiscal antes de continuar."}'),
  (@Reservaciones,N'CALENDARIO-OT',1,N'TEORIA',N'Continuidad operativa',N'Una reservación confirmada debe reflejarse en la línea de tiempo correcta y generar el trabajo necesario para que la habitación esté lista.',NULL),
  (@Reservaciones,N'CALENDARIO-OT',2,N'PASOS',N'Cierre de la cadena',N'1. Confirma estado pagado o condición autorizada. 2. Verifica fechas en calendario. 3. Revisa habitación. 4. Confirma orden de limpieza o mantenimiento. 5. Documenta excepciones.',N'{"numbered":true}'),
  (@Reservaciones,N'CALENDARIO-OT',3,N'EVALUACION',N'Evaluación y práctica integral',N'Explica la cadena de impacto y completa el caso de principio a fin.',N'{"required":true}'),

  (@Cfdi,N'LECTURA-CFDI',1,N'OBJETIVOS',N'Objetivo fiscal',N'Interpretar el comprobante antes de decidir su tratamiento y detectar señales que impiden registrarlo.',NULL),
  (@Cfdi,N'LECTURA-CFDI',2,N'TEORIA',N'Anatomía mínima',N'Revisa tipo de comprobante, UUID, fechas, emisor, receptor, uso, conceptos, método y forma de pago, moneda, subtotal, descuentos, impuestos, total y relaciones. Usa el XML local marcado como ficticio: sus certificados y sellos inválidos permiten estudiar la estructura, pero nunca le dan validez fiscal.',N'{"diagram":["Encabezado","Partes","Conceptos","Impuestos","Relaciones"]}'),
  (@Cfdi,N'LECTURA-CFDI',3,N'ALERTA',N'No registres un CFDI sin validar',N'Confirma pertenencia al RFC, vigencia, ausencia de duplicado y coherencia aritmética. Los comprobantes de pago requieren revisar documentos relacionados.',N'{"severity":"critical","notasInstructor":"Use un CFDI ficticio con RFC receptor incorrecto. La equivocación común es validar solo el total y el nombre comercial."}'),
  (@Cfdi,N'REGISTRO',1,N'DEMOSTRACION',N'Del comprobante a la propuesta de póliza',N'El instructor explicará clasificación, cuentas, centros o unidades, impuestos, debe/haber, referencia al UUID y revisión del balance con una propuesta; el catálogo contable no forma parte del escenario inicial limpio.',N'{"demoSteps":["Clasificar","Proponer cuentas","Calcular movimientos","Comprobar balance","Explicar evidencia"],"notasInstructor":"Explique el porqué de cada cuenta y pida al colaborador anticipar el siguiente movimiento. No solicite crear o guardar una póliza porque el escenario inicial no provisiona catálogo contable ni bancos."}'),
  (@Cfdi,N'REGISTRO',2,N'PASOS',N'Control contable',N'Usa la fecha y periodo correctos, separa base e impuestos, respeta naturaleza de cuentas, documenta la contraparte y valida que cargos y abonos coincidan.',N'{"numbered":true}'),
  (@Cfdi,N'REGISTRO',3,N'PRACTICA',N'Análisis y propuesta contable simulada',N'Descarga el XML local no timbrable, confirma sus marcadores de capacitación y cárgalo únicamente en Orion_Training. Confirma el resultado del procesamiento y, si abres Declaración previa, filtra enero de 2026 para localizarlo. Calcula una propuesta debe/haber balanceada; no guardes pólizas ni movimientos bancarios porque esas referencias no se provisionan en el escenario inicial limpio.',N'{"sandbox":true,"checklist":["Gasto propuesto: 1000.00 al debe","IVA propuesto: 160.00 al debe","Contraparte propuesta: 1160.00 al haber","Balance propuesto: 1160.00 = 1160.00"],"notasInstructor":"Compruebe primero que identifica los RFC genéricos y los sellos NO_VALIDO_ENTRENAMIENTO. Evalúe clasificación, impuestos, balance y explicación del vínculo al UUID ficticio sin pedir escrituras contables o bancarias."}'),
  (@Cfdi,N'BANCO-TRAZABILIDAD',1,N'TEORIA',N'Conciliar no es volver a registrar',N'La conciliación enlaza evidencia bancaria con un efecto contable existente o crea el faltante según el flujo autorizado; nunca debe duplicar el gasto o ingreso.',NULL),
  (@Cfdi,N'BANCO-TRAZABILIDAD',2,N'PASOS',N'Prueba de trazabilidad',N'Desde el movimiento bancario debe ser posible llegar a la póliza, al registro contable, a la transacción y al CFDI; conserva referencias estables.',N'{"diagram":["Banco","Póliza","Registro","Transacción","CFDI"]}'),
  (@Cfdi,N'BANCO-TRAZABILIDAD',3,N'EVALUACION',N'Cierre fiscal-contable',N'Resuelve la evaluación y demuestra el flujo completo con datos simulados.',N'{"required":true}'),

  (@Logistica,N'PREPARAR-LOGISTICA',1,N'OBJETIVOS',N'Preparar: validar el contexto logístico',N'Al finalizar podrás recorrer la cadena logística con datos TRN, verificar RFC, unidad, ubicación y referencia, y detener un movimiento cuando falte evidencia.',N'{"icon":"target","flowStep":"Preparar"}'),
  (@Logistica,N'PREPARAR-LOGISTICA',2,N'TEORIA',N'Explicar: la cadena logística y sus referencias',N'Un material se relaciona con proveedor, compra, recepción, ubicación y conteo. Cada paso conserva una referencia: no recibir, mover ni ajustar existencias si no puedes identificar el origen y el estado anterior.',N'{"diagram":["Material","Proveedor","Compra","Recepción","Ubicación","Conteo"],"callout":"info","flowStep":"Explicar"}'),
  (@Logistica,N'FLUJO-LOGISTICO',1,N'DEMOSTRACION',N'Demostrar: consultar antes de modificar',N'El instructor mostrará cómo localizar un material ficticio TRN, leer su unidad y categoría, revisar ubicaciones y existencias, y buscar la referencia de compra o recepción antes de guardar.',N'{"demoSteps":["Confirmar entorno y RFC","Buscar material TRN","Leer unidad y categoría","Revisar ubicación y existencia","Buscar referencia antes de actuar"],"flowStep":"Demostrar","notasInstructor":"Muestre una referencia ya recibida y pregunte qué ocurriría al repetirla. La confusión habitual es asumir que una pantalla sin error garantiza una recepción única."}'),
  (@Logistica,N'FLUJO-LOGISTICO',2,N'PRACTICA',N'Practicar: material, ubicación y recepción segura',N'Usa únicamente el material ficticio asignado. Identifica sus datos maestros, explica el efecto de compra y recepción, confirma la ubicación y documenta cómo evitarías un doble movimiento.',N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Observe si busca la referencia y el estado antes de proponer una recepción. No permita ajustes directos para ocultar diferencias ni el uso de códigos que no empiecen con TRN."}'),
  (@Logistica,N'CONTROL-LOGISTICO',1,N'EVALUACION',N'Evaluar: controles y diferencias de inventario',N'Responde la evaluación y demuestra que sabes recontar, documentar una diferencia y escalarla sin forzar un ajuste ni duplicar una recepción.',N'{"required":true,"flowStep":"Evaluar","checklist":["Referencia única","Unidad correcta","Ubicación correcta","Evidencia de conteo"]}'),
  (@Logistica,N'CONTROL-LOGISTICO',2,N'RESUMEN',N'Cerrar: conservar trazabilidad logística',N'Antes de cerrar confirma material, unidad, ubicación, cantidad, referencia y estado. Si el físico y el sistema difieren, recontar y documentar precede a cualquier autorización de ajuste.',N'{"highlight":true,"flowStep":"Cerrar"}'),

  (@CapitalHumano,N'PREPARAR-RH',1,N'OBJETIVOS',N'Preparar: confirmar identidad y alcance personal',N'Al finalizar podrás usar Mi trabajo con tu identidad vinculada, consultar únicamente datos propios y reconocer cuándo una solicitud necesita evidencia o escalamiento.',N'{"icon":"target","flowStep":"Preparar"}'),
  (@CapitalHumano,N'PREPARAR-RH',2,N'TEORIA',N'Explicar: privacidad y autoservicio del colaborador',N'Mi trabajo permite consultar información personal y dar seguimiento a incidencias, correcciones de asistencia y ausencias propias. Los expedientes, nómina y acciones sobre otras personas pertenecen a roles administrativos y quedan fuera de esta capacitación.',N'{"diagram":["Mi identidad","Mi asistencia","Mi corrección","Mi ausencia","Seguimiento"],"callout":"privacy","flowStep":"Explicar"}'),
  (@CapitalHumano,N'MI-TRABAJO',1,N'DEMOSTRACION',N'Demostrar: revisar un caso propio sin exponer terceros',N'El instructor mostrará cómo confirmar la identidad visible, consultar el estado personal, reconocer un evento faltante y reunir fecha, motivo y evidencia ficticia antes de iniciar una corrección o ausencia.',N'{"demoSteps":["Confirmar identidad","Revisar estado propio","Identificar la incidencia","Preparar evidencia ficticia","Explicar el seguimiento"],"flowStep":"Demostrar","notasInstructor":"Compruebe que el colaborador no intenta buscar a otra persona. La equivocación común es crear un segundo registro de asistencia en vez de solicitar la corrección del evento faltante."}'),
  (@CapitalHumano,N'MI-TRABAJO',2,N'PRACTICA',N'Practicar: autoservicio con datos propios y ficticios',N'En Mi trabajo confirma tu identidad de capacitación, revisa solo tu escenario sintético y explica cómo solicitarías una corrección o ausencia. Si una sección no está habilitada, documenta el límite y escala al instructor.',N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Use solo las identidades sintéticas previstas. Evalúe privacidad, evidencia y elección del trámite; no otorgue ni simule permisos administrativos para completar el ejercicio."}'),
  (@CapitalHumano,N'PRIVACIDAD-CIERRE',1,N'EVALUACION',N'Evaluar: privacidad, evidencia y seguimiento',N'Responde la evaluación y demuestra que sabes corregir una omisión mediante solicitud, consultar el saldo propio y escalar sin editar registros ni acceder a información ajena.',N'{"required":true,"flowStep":"Evaluar","checklist":["Identidad propia","Evidencia ficticia","Solicitud correcta","Seguimiento seguro"]}'),
  (@CapitalHumano,N'PRIVACIDAD-CIERRE',2,N'RESUMEN',N'Cerrar: proteger información y escalar excepciones',N'Antes de cerrar verifica que la solicitud pertenezca a tu identidad, que no contenga datos reales del ejercicio, que conserve evidencia y que tenga un estado consultable. Nunca resuelvas una excepción usando permisos de otra persona.',N'{"highlight":true,"flowStep":"Cerrar"}');

INSERT INTO capacitacion.BloqueContenido (LeccionId, Orden, Tipo, Titulo, Contenido, ConfiguracionJson, Requerido)
SELECT lesson.LeccionId, source.Orden, source.Tipo, source.Titulo, source.Contenido, source.ConfiguracionJson, 1
FROM @Bloques source
JOIN capacitacion.Leccion lesson ON lesson.CursoVersionId = source.CursoVersionId AND lesson.Clave = source.LeccionClave
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.BloqueContenido target WHERE target.LeccionId = lesson.LeccionId AND target.Orden = source.Orden
);

INSERT INTO capacitacion.Recurso (BloqueId, Orden, Tipo, Titulo, Ruta, TextoAlternativo, VersionAplicacion)
SELECT blockInfo.BloqueId, 1, N'IMAGEN', N'Pantalla principal de OrionERP', N'/Images/OrionERPMainPage.png',
  N'Pantalla principal de OrionERP utilizada para identificar navegación, RFC e identidad activa.', N'v1'
FROM capacitacion.Leccion lesson
JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId AND blockInfo.Orden = 3
WHERE lesson.CursoVersionId = @Fundamentos AND lesson.Clave = N'ENTORNOS'
  AND NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Recurso target WHERE target.BloqueId = blockInfo.BloqueId AND target.Orden = 1
  );

INSERT INTO capacitacion.Recurso (BloqueId, Orden, Tipo, Titulo, Ruta, TextoAlternativo, VersionAplicacion)
SELECT blockInfo.BloqueId, 1, source.Tipo, source.Titulo, source.Ruta, source.TextoAlternativo, N'v1'
FROM
(
  VALUES
    (@Cfdi, N'LECTURA-CFDI', 2, N'Abrir XML ficticio no timbrable', N'/training/fixtures/cfdi-ficticio-no-timbrable.xml', N'Archivo XML local exclusivo de capacitación, con RFC genéricos, certificados y sellos deliberadamente inválidos.', N'ARCHIVO'),
    (@Logistica, N'FLUJO-LOGISTICO', 2, N'Abrir práctica de materiales', N'/logistica/materiales', N'Acceso local al catálogo de materiales del entorno de capacitación.', N'ENLACE'),
    (@CapitalHumano, N'MI-TRABAJO', 2, N'Abrir Mi trabajo', N'/mi-trabajo', N'Acceso local al autoservicio personal del colaborador en capacitación.', N'ENLACE')
) source(CursoVersionId, LeccionClave, BloqueOrden, Titulo, Ruta, TextoAlternativo, Tipo)
JOIN capacitacion.Leccion lesson
  ON lesson.CursoVersionId = source.CursoVersionId AND lesson.Clave = source.LeccionClave
JOIN capacitacion.BloqueContenido blockInfo
  ON blockInfo.LeccionId = lesson.LeccionId AND blockInfo.Orden = source.BloqueOrden
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Recurso target WHERE target.BloqueId = blockInfo.BloqueId AND target.Orden = 1
);

DECLARE @Evaluaciones TABLE
(
  CursoVersionId int NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Instrucciones nvarchar(1000) NOT NULL
);
INSERT INTO @Evaluaciones VALUES
  (@Fundamentos,N'Comprobación de fundamentos',N'Elige la mejor respuesta. Las preguntas críticas deben responderse correctamente.'),
  (@Reservaciones,N'Validación del flujo de reservación',N'Responde con base en el orden operativo y la prevención de duplicados.'),
  (@Cfdi,N'Validación fiscal y contable',N'Responde con base en el control fiscal, contable y bancario.'),
  (@Logistica,N'Validación de operación logística',N'Responde con base en referencias, ubicaciones, existencias y prevención de duplicados.'),
  (@CapitalHumano,N'Validación de autoservicio y privacidad',N'Responde con base en identidad propia, evidencia, privacidad y seguimiento.');

INSERT INTO capacitacion.Evaluacion (CursoVersionId, Titulo, Instrucciones, CalificacionMinima, Requerida)
SELECT source.CursoVersionId, source.Titulo, source.Instrucciones, 80, 1
FROM @Evaluaciones source
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Evaluacion target WHERE target.CursoVersionId = source.CursoVersionId AND target.Titulo = source.Titulo
);

DECLARE @Preguntas TABLE
(
  CursoVersionId int NOT NULL,
  Orden int NOT NULL,
  Texto nvarchar(1000) NOT NULL,
  Explicacion nvarchar(1000) NULL,
  Critica bit NOT NULL,
  Correcta nvarchar(1000) NOT NULL,
  Incorrecta1 nvarchar(1000) NOT NULL,
  Incorrecta2 nvarchar(1000) NOT NULL
);
INSERT INTO @Preguntas VALUES
  (@Fundamentos,1,N'¿Qué debes verificar antes de capturar un ejercicio?',N'Entorno, RFC, identidad y módulo definen el contexto seguro.',1,N'Entorno, RFC, usuario y módulo.',N'Solo que la página abra.',N'Solo la fecha del equipo.'),
  (@Fundamentos,2,N'¿Qué información debe usarse en Orion_Training?',N'La capacitación no necesita datos personales u operativos reales.',1,N'Únicamente datos ficticios del escenario.',N'Una copia de un caso real.',N'Credenciales del instructor.'),
  (@Fundamentos,3,N'¿Qué confirma el cierre correcto de una acción?',N'El resultado debe verificarse y conservar una referencia.',0,N'Que aparezca una vez y exista trazabilidad.',N'Que no se muestre un error.',N'Que el navegador se cierre.'),
  (@Reservaciones,1,N'¿Por qué se validan fechas, habitación y conceptos antes de guardar?',N'Esos datos alimentan procesos posteriores.',0,N'Porque afectan cobro, CFDI, calendario y trabajo operativo.',N'Solo para ordenar la lista.',N'Porque el sistema no permite editar nunca.'),
  (@Reservaciones,2,N'¿Qué haces antes de repetir un pago o CFDI?',N'La búsqueda previa previene dobles efectos.',1,N'Buscar la referencia y revisar el estado de la primera operación.',N'Repetirlo y comparar después.',N'Cambiar de usuario.'),
  (@Reservaciones,3,N'¿Qué completa la continuidad operativa?',N'El calendario y la orden de trabajo conectan venta y operación.',0,N'Verificar calendario, habitación y orden de trabajo.',N'Imprimir la reservación.',N'Cerrar sesión.'),
  (@Cfdi,1,N'¿Qué validación es indispensable antes del registro?',N'Pertenencia, vigencia y unicidad protegen el registro.',1,N'RFC correcto, vigencia, ausencia de duplicado y coherencia.',N'Solo que el PDF sea legible.',N'Solo el nombre del proveedor.'),
  (@Cfdi,2,N'¿Qué propiedad debe cumplir la póliza?',N'La partida doble exige igualdad entre cargos y abonos.',0,N'Cargos y abonos deben coincidir.',N'Debe tener una sola cuenta.',N'Debe usar la fecha actual siempre.'),
  (@Cfdi,3,N'¿Qué significa conciliar un movimiento?',N'Conciliar vincula evidencia; no repite efectos existentes.',1,N'Enlazarlo con el efecto autorizado sin duplicarlo.',N'Crear siempre una póliza nueva.',N'Eliminar la transacción original.'),
  (@Logistica,1,N'¿Qué debes confirmar antes de mover una existencia?',N'RFC, material, unidad, ubicación y referencia definen el movimiento correcto.',0,N'RFC, material, unidad, ubicación y referencia.',N'Solo que la cantidad sea positiva.',N'Solo el nombre del proveedor.'),
  (@Logistica,2,N'¿Qué haces antes de repetir una recepción cuya respuesta fue incierta?',N'Buscar la referencia y su estado evita una recepción duplicada.',1,N'Buscar la referencia y confirmar el estado de la primera recepción.',N'Repetirla con otra referencia.',N'Ajustar la existencia para compensar.'),
  (@Logistica,3,N'¿Qué haces si el conteo físico difiere del sistema?',N'Una diferencia requiere verificación y evidencia antes de autorizar un ajuste.',1,N'Recontar, documentar la diferencia y escalarla.',N'Forzar el ajuste hasta igualar.',N'Cambiar la unidad del material.'),
  (@CapitalHumano,1,N'¿Qué información puedes consultar durante el ejercicio de Mi trabajo?',N'El autoservicio es personal y la práctica usa únicamente identidades sintéticas.',1,N'Solo la información propia y ficticia del escenario asignado.',N'El expediente de cualquier compañero.',N'Una copia de datos reales para compararlos.'),
  (@CapitalHumano,2,N'¿Qué haces cuando falta un evento de asistencia?',N'La corrección conserva el evento original, el motivo, la evidencia y la autorización.',1,N'Enviar una solicitud de corrección propia con evidencia ficticia.',N'Crear otro evento hasta que el total coincida.',N'Editar directamente la base de datos.'),
  (@CapitalHumano,3,N'¿Cómo gestionas una ausencia desde Mi trabajo?',N'El flujo correcto respeta el saldo propio y deja seguimiento de la solicitud.',0,N'Revisar el saldo propio y enviar la solicitud por el flujo autorizado.',N'Usar la identidad de otra persona.',N'Pedir permisos administrativos para aprobarla.');

INSERT INTO capacitacion.Pregunta (EvaluacionId, Orden, Texto, Explicacion, Critica)
SELECT evaluation.EvaluacionId, source.Orden, source.Texto, source.Explicacion, source.Critica
FROM @Preguntas source
JOIN capacitacion.Evaluacion evaluation ON evaluation.CursoVersionId = source.CursoVersionId
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Pregunta target WHERE target.EvaluacionId = evaluation.EvaluacionId AND target.Orden = source.Orden
);

INSERT INTO capacitacion.OpcionPregunta (PreguntaId, Orden, Texto, EsCorrecta)
SELECT question.PreguntaId, optionInfo.Orden, optionInfo.Texto, optionInfo.EsCorrecta
FROM @Preguntas source
JOIN capacitacion.Evaluacion evaluation ON evaluation.CursoVersionId = source.CursoVersionId
JOIN capacitacion.Pregunta question ON question.EvaluacionId = evaluation.EvaluacionId AND question.Orden = source.Orden
CROSS APPLY
(
  VALUES (1, source.Correcta, CONVERT(bit,1)), (2, source.Incorrecta1, CONVERT(bit,0)), (3, source.Incorrecta2, CONVERT(bit,0))
) optionInfo(Orden, Texto, EsCorrecta)
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.OpcionPregunta target WHERE target.PreguntaId = question.PreguntaId AND target.Orden = optionInfo.Orden
);

DECLARE @Practicas TABLE
(
  CursoVersionId int NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Instrucciones nvarchar(2000) NOT NULL,
  Ruta nvarchar(500) NULL
);
INSERT INTO @Practicas VALUES
  (@Fundamentos,N'Recorrido seguro de orientación',N'En Orion_Training identifica el distintivo, confirma RFC y usuario, localiza cinco módulos y explica cómo verificarías una operación.',N'/'),
  (@Reservaciones,N'Caso integral de reservación',N'Crea la reservación ficticia asignada, registra pago simulado, prepara el CFDI, verifica calendario y confirma la orden de trabajo.',N'/reservaciones/lista'),
  (@Cfdi,N'Caso guiado CFDI-contabilidad',N'Descarga y analiza el XML local no timbrable, confirma sus marcadores ficticios y cárgalo solo en Orion_Training. Confirma el resultado y usa enero de 2026 si lo buscas en Declaración previa. Calcula una propuesta balanceada de 1160.00 y explica la trazabilidad que tendría; no guardes pólizas ni movimientos bancarios en el escenario inicial limpio.',N'/cfdi/cargar-xml-sat'),
  (@Logistica,N'Caso integral de logística',N'Con un material TRN identifica datos maestros, ubicación y existencia; explica compra y recepción, y documenta una diferencia de conteo sin duplicar movimientos.',N'/logistica/materiales'),
  (@CapitalHumano,N'Caso de autoservicio Capital Humano',N'En Mi trabajo confirma tu identidad sintética, revisa información propia y explica una corrección o ausencia con evidencia, privacidad y seguimiento.',N'/mi-trabajo');

INSERT INTO capacitacion.Practica (CursoVersionId, Titulo, Instrucciones, RutaSandbox, Requerida)
SELECT source.CursoVersionId, source.Titulo, source.Instrucciones, source.Ruta, 1
FROM @Practicas source
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Practica target WHERE target.CursoVersionId = source.CursoVersionId AND target.Titulo = source.Titulo
);

DECLARE @Pasos TABLE
(
  CursoVersionId int NOT NULL,
  Orden int NOT NULL,
  Descripcion nvarchar(1000) NOT NULL,
  Critico bit NOT NULL
);
INSERT INTO @Pasos VALUES
  (@Fundamentos,1,N'Identifica correctamente el entorno de práctica y el RFC activo.',1),
  (@Fundamentos,2,N'Localiza Capital Humano, Reservaciones, CFDI, Contabilidad y Logística.',0),
  (@Fundamentos,3,N'Explica cómo evitaría repetir una acción con resultado incierto.',1),
  (@Reservaciones,1,N'Crea una reservación coherente usando únicamente datos ficticios.',1),
  (@Reservaciones,2,N'Registra el pago simulado y confirma el saldo sin duplicados.',1),
  (@Reservaciones,3,N'Prepara el CFDI simulado con la condición fiscal del caso.',1),
  (@Reservaciones,4,N'Verifica calendario, habitación y orden de trabajo.',0),
  (@Cfdi,1,N'Descarga el XML local, identifica NO_VALIDO_ENTRENAMIENTO y valida RFC genéricos, UUID ficticio, tipo, partes, importes e impuestos.',1),
  (@Cfdi,2,N'Carga el archivo solo en Orion_Training y localiza el comprobante ficticio procesado.',1),
  (@Cfdi,3,N'Calcula y explica una propuesta con cargos por 1160.00 y abonos por 1160.00, sin guardarla.',1),
  (@Cfdi,4,N'Describe cómo conservarías la liga entre banco, póliza, transacción y CFDI sin crear movimientos en este escenario.',1),
  (@Logistica,1,N'Confirma el entorno, RFC y material ficticio cuyo código inicia con TRN.',1),
  (@Logistica,2,N'Revisa o actualiza el material asignado con unidad, categoría y referencia coherentes.',1),
  (@Logistica,3,N'Verifica ubicación y existencia, y explica cómo buscaría una compra o recepción antes de repetirla.',1),
  (@Logistica,4,N'Recuenta, documenta una diferencia ficticia y explica a quién la escalaría sin forzar un ajuste.',1),
  (@CapitalHumano,1,N'Confirma que Mi trabajo muestra únicamente tu identidad sintética asignada.',1),
  (@CapitalHumano,2,N'Revisa tu escenario propio de asistencia y explica el manejo seguro de un evento faltante.',1),
  (@CapitalHumano,3,N'Explica una corrección o ausencia propia usando fecha, motivo y evidencia ficticia.',1),
  (@CapitalHumano,4,N'Describe el límite de privacidad y el escalamiento sin usar funciones administrativas.',1);

INSERT INTO capacitacion.PracticaPaso (PracticaId, Orden, Descripcion, Critico)
SELECT practice.PracticaId, source.Orden, source.Descripcion, source.Critico
FROM @Pasos source
JOIN capacitacion.Practica practice ON practice.CursoVersionId = source.CursoVersionId
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.PracticaPaso target WHERE target.PracticaId = practice.PracticaId AND target.Orden = source.Orden
);

UPDATE versionInfo
SET Estado = N'PUBLICADA',
    PublicadaEn = SYSUTCDATETIME(),
    PublicadaPor = @SeedActor
FROM capacitacion.CursoVersion versionInfo
JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
JOIN @Cursos source ON source.Clave = curso.Clave
WHERE curso.Rfc = N'*'
  AND versionInfo.NumeroVersion = 1
  AND versionInfo.Estado = N'BORRADOR'
  AND versionInfo.PublicadaEn IS NULL;

COMMIT TRANSACTION;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_FirmaInstructor_AppendOnly
ON capacitacion.FirmaInstructor
INSTEAD OF UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  THROW 51603, 'La firma del instructor es inmutable.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Leccion_VersionPublicadaInmutable
ON capacitacion.Leccion
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51620, 'Las lecciones de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_BloqueContenido_VersionPublicadaInmutable
ON capacitacion.BloqueContenido
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.Leccion lesson ON lesson.LeccionId = changed.LeccionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.Leccion lesson ON lesson.LeccionId = changed.LeccionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51621, 'Los bloques de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Recurso_VersionPublicadaInmutable
ON capacitacion.Recurso
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.BloqueId = changed.BloqueId JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.BloqueId = changed.BloqueId JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51622, 'Los recursos de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Evaluacion_VersionPublicadaInmutable
ON capacitacion.Evaluacion
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51623, 'Las evaluaciones de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Pregunta_VersionPublicadaInmutable
ON capacitacion.Pregunta
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.Evaluacion assessment ON assessment.EvaluacionId = changed.EvaluacionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = assessment.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.Evaluacion assessment ON assessment.EvaluacionId = changed.EvaluacionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = assessment.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51624, 'Las preguntas de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_OpcionPregunta_VersionPublicadaInmutable
ON capacitacion.OpcionPregunta
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.Pregunta questionInfo ON questionInfo.PreguntaId = changed.PreguntaId JOIN capacitacion.Evaluacion assessment ON assessment.EvaluacionId = questionInfo.EvaluacionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = assessment.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.Pregunta questionInfo ON questionInfo.PreguntaId = changed.PreguntaId JOIN capacitacion.Evaluacion assessment ON assessment.EvaluacionId = questionInfo.EvaluacionId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = assessment.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51625, 'Las opciones de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_Practica_VersionPublicadaInmutable
ON capacitacion.Practica
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = changed.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51626, 'Las prácticas de una versión publicada son inmutables.', 1;
END;
GO

CREATE OR ALTER TRIGGER capacitacion.TR_PracticaPaso_VersionPublicadaInmutable
ON capacitacion.PracticaPaso
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS
  (
    SELECT 1 FROM inserted changed JOIN capacitacion.Practica practiceInfo ON practiceInfo.PracticaId = changed.PracticaId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = practiceInfo.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
    UNION ALL
    SELECT 1 FROM deleted changed JOIN capacitacion.Practica practiceInfo ON practiceInfo.PracticaId = changed.PracticaId JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = practiceInfo.CursoVersionId WHERE versionInfo.PublicadaEn IS NOT NULL
  )
    THROW 51627, 'Los pasos prácticos de una versión publicada son inmutables.', 1;
END;
GO

IF NOT EXISTS (SELECT 1 FROM capacitacion.EsquemaVersion WHERE Version = 1)
BEGIN
  INSERT capacitacion.EsquemaVersion (Version, Descripcion)
  VALUES (1, N'Capacitación interactiva, progreso, evaluación, práctica y evidencia');
END;

IF DB_NAME() = N'Orion_Training'
   AND NOT EXISTS (SELECT 1 FROM capacitacion.EntornoSeguridad WHERE EntornoSeguridadId = 1)
BEGIN
  -- The schema installer never claims that a cloned database is safe. The
  -- dedicated sanitization/reset workflow is the only artifact allowed to
  -- mark these flags after removing operational and personal data.
  INSERT capacitacion.EntornoSeguridad
    (EntornoSeguridadId, Entorno, DatosSanitizados, DatosSinteticos, VersionEsquema)
  VALUES
    (1, N'Training', 0, 0, 1);
END;

COMMIT TRANSACTION;
GO

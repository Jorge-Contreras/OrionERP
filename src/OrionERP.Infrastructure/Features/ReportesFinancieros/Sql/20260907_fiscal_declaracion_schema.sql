/*
  Declaracion mensual (ISR provisional + IVA definitivo) - esquema fiscal.

  Ejecutar con SQLCMD variables:
    ExpectedDatabase = Orion_Sandbox | grupocarpio
    ApplyChanges     = 0 | 1

  El modo ApplyChanges=0 ejecuta todas las validaciones y revierte la transaccion.
  Idempotente: cada objeto esta protegido por su propia guarda.

  Los objetos programables (funciones y procedimientos) viven en
  20260907_fiscal_declaracion_objetos.sql y se aplican despues de este script.

  Por que un esquema nuevo en vez de colgar de contabilidad o reporteFinanciero
  ----------------------------------------------------------------------------
  Lo fiscal tiene un ciclo de vida propio: el coeficiente de utilidad cambia por
  ejercicio, las declaraciones se re-presentan como complementarias, y lo que se
  guarda aqui es lo que se dijo al SAT, no lo que dice la contabilidad. Mezclarlo
  con reporteFinanciero haria que un reporte y una obligacion fiscal comparten
  namespace, que es justo la confusion que este trabajo viene a resolver.
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');

IF @ApplyChanges IS NULL
  THROW 51000, 'ApplyChanges debe ser 0 o 1.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51001, 'La base conectada no coincide con ExpectedDatabase.', 1;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'fiscal')
  EXEC('CREATE SCHEMA fiscal AUTHORIZATION dbo;');

/*--------------------------------------------------------------------------
  PerfilFiscal - que tipo de contribuyente es cada RFC.

  Es el interruptor entre Persona Moral y Persona Fisica. v1 solo calcula ISR
  de Persona Moral; con TipoPersona='F' la pagina avisa en vez de dar un numero
  equivocado. Se mantiene separada de dbo.SatRfcProfile, que guarda datos de la
  constancia de situacion fiscal y la FIEL: son dominios distintos y esa tabla
  ya carga certificados binarios.
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.PerfilFiscal', 'U') IS NULL
BEGIN
  CREATE TABLE fiscal.PerfilFiscal
  (
    Rfc varchar(50) NOT NULL CONSTRAINT PK_fiscal_PerfilFiscal PRIMARY KEY,
    TipoPersona char(1) NOT NULL,
    RegimenFiscal varchar(3) NULL,
    TasaIsr decimal(9,4) NOT NULL CONSTRAINT DF_fiscal_PerfilFiscal_TasaIsr DEFAULT (0.3000),
    ObligadoIva bit NOT NULL CONSTRAINT DF_fiscal_PerfilFiscal_ObligadoIva DEFAULT (1),
    DiaVencimiento tinyint NOT NULL CONSTRAINT DF_fiscal_PerfilFiscal_DiaVencimiento DEFAULT (17),
    Activo bit NOT NULL CONSTRAINT DF_fiscal_PerfilFiscal_Activo DEFAULT (1),
    ActualizadoEn datetime2(0) NOT NULL CONSTRAINT DF_fiscal_PerfilFiscal_Act DEFAULT SYSUTCDATETIME(),
    ActualizadoPor nvarchar(256) NULL,
    CONSTRAINT CK_fiscal_PerfilFiscal_TipoPersona CHECK (TipoPersona IN ('M','F')),
    CONSTRAINT CK_fiscal_PerfilFiscal_TasaIsr CHECK (TasaIsr BETWEEN 0 AND 1),
    CONSTRAINT CK_fiscal_PerfilFiscal_Dia CHECK (DiaVencimiento BETWEEN 1 AND 28)
  );
END;

/*--------------------------------------------------------------------------
  CoeficienteUtilidad - uno por cada declaracion anual presentada.

  Ejercicio es el ejercicio DEL QUE SALE el coeficiente (el de la anual), no
  aquel en que se aplica, y FechaPresentacion es la fecha en que esa anual se
  presento. El coeficiente que corresponde a un pago provisional es el de la
  ultima anual presentada antes de presentar ese pago.

  Esto no es un detalle: el coeficiente cambia A MEDIO ANO. En 2026, OHM declaro
  enero (17/02) y febrero (12/03) con 0.1166 -el de la anual 2024- y a partir de
  marzo (presentado el 27/04, ya despues de la anual 2025 del 10/04) con 0.3726.
  Un modelo de un coeficiente por ejercicio calcularia mal cinco de siete meses,
  y una complementaria de enero tiene que seguir usando 0.1166.

  dbo.SatRfcProfile.Coeficiente_Utilidad guarda un solo escalar (hoy 0.1166) y
  por eso contabilidad.fn_HojaTrabajo_Acumulados calcula el ISR de 2026 con el
  coeficiente de 2024.
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.CoeficienteUtilidad', 'U') IS NULL
BEGIN
  CREATE TABLE fiscal.CoeficienteUtilidad
  (
    Rfc varchar(50) NOT NULL,
    Ejercicio int NOT NULL,
    Coeficiente decimal(9,4) NOT NULL,
    Origen varchar(60) NULL,
    FechaPresentacion date NULL,
    ActualizadoEn datetime2(0) NOT NULL CONSTRAINT DF_fiscal_CoefUtil_Act DEFAULT SYSUTCDATETIME(),
    ActualizadoPor nvarchar(256) NULL,
    CONSTRAINT PK_fiscal_CoeficienteUtilidad PRIMARY KEY (Rfc, Ejercicio),
    CONSTRAINT CK_fiscal_CoefUtil_Ejercicio CHECK (Ejercicio BETWEEN 2000 AND 2100),
    CONSTRAINT CK_fiscal_CoefUtil_Valor CHECK (Coeficiente BETWEEN 0 AND 10)
  );
END;

/*--------------------------------------------------------------------------
  EjercicioFiscal - insumos del calculo de ISR que no salen de los CFDIs.
  Para OHM 2026 son cero, pero el renglon tiene que existir para que el
  formato cuadre contra el del SAT campo por campo.
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.EjercicioFiscal', 'U') IS NULL
BEGIN
  CREATE TABLE fiscal.EjercicioFiscal
  (
    Rfc varchar(50) NOT NULL,
    Ejercicio int NOT NULL,
    PtuPagada money NOT NULL CONSTRAINT DF_fiscal_Ejercicio_Ptu DEFAULT (0),
    PerdidasFiscalesPorAplicar money NOT NULL CONSTRAINT DF_fiscal_Ejercicio_Perd DEFAULT (0),
    DeduccionInmediata money NOT NULL CONSTRAINT DF_fiscal_Ejercicio_DedInm DEFAULT (0),
    ProporcionIva decimal(9,4) NOT NULL CONSTRAINT DF_fiscal_Ejercicio_PropIva DEFAULT (1.0000),
    ActualizadoEn datetime2(0) NOT NULL CONSTRAINT DF_fiscal_Ejercicio_Act DEFAULT SYSUTCDATETIME(),
    ActualizadoPor nvarchar(256) NULL,
    CONSTRAINT PK_fiscal_EjercicioFiscal PRIMARY KEY (Rfc, Ejercicio),
    CONSTRAINT CK_fiscal_Ejercicio_Anio CHECK (Ejercicio BETWEEN 2000 AND 2100),
    CONSTRAINT CK_fiscal_Ejercicio_PropIva CHECK (ProporcionIva BETWEEN 0 AND 1)
  );
END;

/*--------------------------------------------------------------------------
  DeclaracionPresentada - lo que efectivamente se dijo al SAT.

  Sin esta tabla, "declarado vs calculado" es imposible, y es justamente la
  comparacion que decide que meses necesitan complementaria. Las columnas
  siguen el orden del formato "Declaracion Provisional o Definitiva de
  Impuestos Federales" version 24.0.0.

  EsParcial marca las filas que salieron de las tablas de periodos anteriores
  que trae cualquier acuse (una sola declaracion de julio reconstruye enero a
  junio, pero solo con ingresos nominales, ISR a cargo y numero de operacion).
  El PDF propio de cada mes despues la completa.
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.DeclaracionPresentada', 'U') IS NULL
BEGIN
  CREATE TABLE fiscal.DeclaracionPresentada
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_fiscal_DeclaracionPresentada PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Ejercicio int NOT NULL,
    Periodo tinyint NOT NULL,
    TipoDeclaracion char(1) NOT NULL CONSTRAINT DF_fiscal_Decl_Tipo DEFAULT ('N'),
    NumeroComplementaria tinyint NOT NULL CONSTRAINT DF_fiscal_Decl_NumComp DEFAULT (0),
    NumeroOperacion varchar(30) NULL,
    FechaPresentacion date NULL,
    Estatus nvarchar(200) NULL,
    EsParcial bit NOT NULL CONSTRAINT DF_fiscal_Decl_Parcial DEFAULT (0),

    -- ISR personas morales
    IngresosNominalesPeriodo money NULL,
    IngresosNominalesAnteriores money NULL,
    TotalIngresosNominales money NULL,
    CoeficienteUtilidad decimal(9,4) NULL,
    UtilidadFiscal money NULL,
    DeduccionInmediata money NULL,
    Ptu money NULL,
    PerdidasFiscales money NULL,
    BaseGravableIsr money NULL,
    ImpuestoCausado money NULL,
    PagosProvisionalesAnteriores money NULL,
    IsrRetenido money NULL,
    IsrACargo money NULL,
    IsrRecargos money NULL,
    IsrTotalAPagar money NULL,

    -- IVA
    ActosGravados16 money NULL,
    IvaTrasladado16 money NULL,
    ActosGravados0 money NULL,
    ActosExentos money NULL,
    ActosNoObjeto money NULL,
    TotalIvaACargo money NULL,
    ActosPagados16 money NULL,
    IvaAcreditable16 money NULL,
    ActosPagados0 money NULL,
    ProporcionIva decimal(9,4) NULL,
    TotalIvaAcreditable money NULL,
    IvaRetenido money NULL,
    IvaSaldoAFavor money NULL,
    IvaACargo money NULL,
    IvaTotalAPagar money NULL,

    -- Trazabilidad de la importacion
    ArchivoNombre nvarchar(400) NULL,
    ArchivoSha256 char(64) NULL,
    ImportadoEn datetime2(0) NOT NULL CONSTRAINT DF_fiscal_Decl_Importado DEFAULT SYSUTCDATETIME(),
    ImportadoPor nvarchar(256) NULL,
    CONSTRAINT CK_fiscal_Decl_Periodo CHECK (Periodo BETWEEN 1 AND 12),
    CONSTRAINT CK_fiscal_Decl_Ejercicio CHECK (Ejercicio BETWEEN 2000 AND 2100),
    CONSTRAINT CK_fiscal_Decl_Tipo CHECK (TipoDeclaracion IN ('N','C'))
  );

  CREATE UNIQUE INDEX UX_fiscal_Decl_Periodo
    ON fiscal.DeclaracionPresentada (Rfc, Ejercicio, Periodo, TipoDeclaracion, NumeroComplementaria);
  CREATE INDEX IX_fiscal_Decl_RfcEjercicio
    ON fiscal.DeclaracionPresentada (Rfc, Ejercicio, Periodo);
  CREATE INDEX IX_fiscal_Decl_Sha
    ON fiscal.DeclaracionPresentada (ArchivoSha256) WHERE ArchivoSha256 IS NOT NULL;
END;

-- El acuse escribe en "Tipo de declaracion" textos como "Complementaria Tipo de
-- complementaria: Modificacion de Obligaciones", de 67 caracteres. La columna
-- nacio de 60 y hay que ampliarla en las bases que ya la tienen.
IF COL_LENGTH(N'fiscal.DeclaracionPresentada', N'Estatus') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'fiscal.DeclaracionPresentada')
                 AND name = 'Estatus' AND max_length < 400)
BEGIN
  ALTER TABLE fiscal.DeclaracionPresentada ALTER COLUMN Estatus nvarchar(200) NULL;
END;

/*--------------------------------------------------------------------------
  DeclaracionCierre - la poliza que salda 118 contra 208.
  Guardar el Transaccion_ID es lo que hace idempotente el boton de generar:
  una segunda pulsacion encuentra la fila y se rechaza en vez de duplicar el
  asiento.
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.DeclaracionCierre', 'U') IS NULL
BEGIN
  CREATE TABLE fiscal.DeclaracionCierre
  (
    Rfc varchar(50) NOT NULL,
    Ejercicio int NOT NULL,
    Periodo tinyint NOT NULL,
    TransaccionIdCierre int NOT NULL,
    IvaTrasladado money NOT NULL,
    IvaAcreditable money NOT NULL,
    NetoAFavor money NOT NULL,
    NetoPorPagar money NOT NULL,
    GeneradoEn datetime2(0) NOT NULL CONSTRAINT DF_fiscal_Cierre_Gen DEFAULT SYSUTCDATETIME(),
    GeneradoPor nvarchar(256) NULL,
    CONSTRAINT PK_fiscal_DeclaracionCierre PRIMARY KEY (Rfc, Ejercicio, Periodo),
    CONSTRAINT CK_fiscal_Cierre_Periodo CHECK (Periodo BETWEEN 1 AND 12)
  );
END;

/*--------------------------------------------------------------------------
  Semillas.
--------------------------------------------------------------------------*/
MERGE fiscal.PerfilFiscal AS destino
USING (VALUES ('OHM191112Q26', 'M', '601', 0.3000, 1, 17)) AS origen
      (Rfc, TipoPersona, RegimenFiscal, TasaIsr, ObligadoIva, DiaVencimiento)
  ON destino.Rfc = origen.Rfc
WHEN NOT MATCHED BY TARGET THEN
  INSERT (Rfc, TipoPersona, RegimenFiscal, TasaIsr, ObligadoIva, DiaVencimiento, ActualizadoPor)
  VALUES (origen.Rfc, origen.TipoPersona, origen.RegimenFiscal, origen.TasaIsr,
          origen.ObligadoIva, origen.DiaVencimiento, N'20260907_fiscal_declaracion_schema');

-- Coeficientes y fechas tomados de la tabla "COEFICIENTE DE UTILIDAD" que el
-- propio acuse imprime (OHM191112Q26.38.2026.pdf, hoja 2). No hay fila de 2026:
-- esa anual se presenta hasta 2027.
MERGE fiscal.CoeficienteUtilidad AS destino
USING (VALUES
        ('OHM191112Q26', 2021, 0.0154, 'DECLARACION ANUAL', '2022-03-23'),
        ('OHM191112Q26', 2022, 0.0000, 'DECLARACION ANUAL', '2023-03-23'),
        ('OHM191112Q26', 2023, 0.0819, 'DECLARACION ANUAL', '2024-04-02'),
        ('OHM191112Q26', 2024, 0.1166, 'DECLARACION ANUAL', '2025-04-13'),
        ('OHM191112Q26', 2025, 0.3726, 'DECLARACION ANUAL', '2026-04-10')
      ) AS origen (Rfc, Ejercicio, Coeficiente, Origen, FechaPresentacion)
  ON destino.Rfc = origen.Rfc AND destino.Ejercicio = origen.Ejercicio
WHEN NOT MATCHED BY TARGET THEN
  INSERT (Rfc, Ejercicio, Coeficiente, Origen, FechaPresentacion, ActualizadoPor)
  VALUES (origen.Rfc, origen.Ejercicio, origen.Coeficiente, origen.Origen,
          CONVERT(date, origen.FechaPresentacion), N'20260907_fiscal_declaracion_schema');

MERGE fiscal.EjercicioFiscal AS destino
USING (VALUES ('OHM191112Q26', 2026)) AS origen (Rfc, Ejercicio)
  ON destino.Rfc = origen.Rfc AND destino.Ejercicio = origen.Ejercicio
WHEN NOT MATCHED BY TARGET THEN
  INSERT (Rfc, Ejercicio, ActualizadoPor)
  VALUES (origen.Rfc, origen.Ejercicio, N'20260907_fiscal_declaracion_schema');

DELETE FROM fiscal.CoeficienteUtilidad
WHERE Rfc = 'OHM191112Q26' AND Ejercicio = 2026 AND Origen = 'DECLARACION ANUAL 2025';

/*--------------------------------------------------------------------------
  Seguridad a nivel de fila.

  Mismo predicado que rh.fn_RfcAccessPredicate: filtra por el RFC que
  SqlConnectionFactory deja en SESSION_CONTEXT('OrionRfc') al abrir la conexion.
  Cuando el contexto viene vacio (mantenimiento, scripts) no filtra nada, para
  no romper este mismo script ni las herramientas administrativas.

  Es defensa en profundidad contra una consulta que olvide el WHERE, no una
  frontera dura: la aplicacion fija el contexto con @read_only=0 y podria
  reescribirlo.

  Para revertir:
    DROP SECURITY POLICY fiscal.RfcSecurityPolicy;
    DROP FUNCTION fiscal.fn_RfcAccessPredicate;
--------------------------------------------------------------------------*/
IF OBJECT_ID('fiscal.fn_RfcAccessPredicate', 'IF') IS NULL
  EXEC
  (
    'CREATE FUNCTION fiscal.fn_RfcAccessPredicate(@Rfc varchar(50))
     RETURNS TABLE
     WITH SCHEMABINDING
     AS
     RETURN SELECT 1 AS IsAllowed
     WHERE SESSION_CONTEXT(N''OrionRfc'') IS NULL
        OR @Rfc = CONVERT(varchar(50), SESSION_CONTEXT(N''OrionRfc''));'
  );

IF NOT EXISTS (SELECT 1 FROM sys.security_policies
               WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('fiscal'))
  EXEC
  (
    'CREATE SECURITY POLICY fiscal.RfcSecurityPolicy
       ADD FILTER PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.PerfilFiscal,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.PerfilFiscal AFTER INSERT,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.PerfilFiscal AFTER UPDATE,
       ADD FILTER PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.CoeficienteUtilidad,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.CoeficienteUtilidad AFTER INSERT,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.CoeficienteUtilidad AFTER UPDATE,
       ADD FILTER PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.EjercicioFiscal,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.EjercicioFiscal AFTER INSERT,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.EjercicioFiscal AFTER UPDATE,
       ADD FILTER PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionPresentada,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionPresentada AFTER INSERT,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionPresentada AFTER UPDATE,
       ADD FILTER PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionCierre,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionCierre AFTER INSERT,
       ADD BLOCK PREDICATE fiscal.fn_RfcAccessPredicate(Rfc) ON fiscal.DeclaracionCierre AFTER UPDATE
       WITH (STATE = ON);'
  );

/*--------------------------------------------------------------------------
  Validacion final
--------------------------------------------------------------------------*/
IF OBJECT_ID(N'fiscal.PerfilFiscal', N'U') IS NULL
   OR OBJECT_ID(N'fiscal.CoeficienteUtilidad', N'U') IS NULL
   OR OBJECT_ID(N'fiscal.EjercicioFiscal', N'U') IS NULL
   OR OBJECT_ID(N'fiscal.DeclaracionPresentada', N'U') IS NULL
   OR OBJECT_ID(N'fiscal.DeclaracionCierre', N'U') IS NULL
  THROW 51002, 'La validacion del esquema fiscal no fue satisfactoria.', 1;

IF @ApplyChanges = 1
BEGIN
  COMMIT TRANSACTION;
  SELECT N'APLICADO' AS Estado, DB_NAME() AS BaseDatos;
END
ELSE
BEGIN
  ROLLBACK TRANSACTION;
  SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos;
END;
GO

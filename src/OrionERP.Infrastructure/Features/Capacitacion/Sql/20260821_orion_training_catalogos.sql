/*
  Seeds the reviewed, fictional reference catalogs that every OrionERP module
  reads from dropdowns and pickers. The sanitizer erases every table it does
  not preserve, and only the provisioned scenario tables are re-seeded, so
  without this batch a trainee finds an empty "Forma de pago" select, an empty
  chart of accounts, and no restaurant site to practise against.

  Two kinds of rows live here and the difference is deliberate:

    * dbo.Formas_Pago carries the SAT c_FormaPago catalog. Those 22 claves are
      published by an external authority, are identical for every taxpayer, and
      carry no business meaning of their own. The MERGE below restates the
      canonical wording so any preserved clone row is overwritten and no
      production byte survives a full reset.

    * Everything else is synthetic. It describes an organization that does not
      exist, is scoped to the fictional RFC XAXX010101000, and is marked with
      TRN- codes, FICTICIO/FICTICIA names, or DATOS SINTÉTICOS notes so the
      attestation can tell it apart from anything else.

  Run only through Sanitize-OrionTraining.ps1 (after the synthetic cohort and
  the workforce scenarios, before the statistics rebuild and the attestation),
  or standalone through Seed-OrionTrainingCatalogos.ps1 to repair the catalogs
  of an already-attested Training database without a fresh reset.

  Three invariants this batch must never break:

    1. Exactly one set-based INSERT or MERGE per table per run. The attestation
       verifies that every identity counter equals seed + (rowcount - 1) *
       increment, so a second statement against the same table, a partial
       insert, or an insert-then-delete leaves a gap and fails at 51839.
    2. Identity columns are never written explicitly, and no table is ever
       emptied wholesale. Both would defeat the counter check above.
    3. Every MERGE matches on the natural key and inserts or updates only.
       A source-driven delete clause is forbidden: trainees add their own
       catalog rows through /ajustes/catalogos, and it would erase their
       practice work on the next run.

  A unit test asserts that the forbidden constructs named above appear nowhere
  in this file, so keep the prose free of the literal keywords.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51905, 'CATALOG SEED BLOCKED: the active database is not exactly Orion_Training.', 1;

/*
  Two guarded workflows may run this batch: the full reset, which already holds
  the sanitizer session key, and the standalone catalog repair, which sets its
  own. Both compare through ISNULL(TRY_CONVERT(...)) so a missing key is a
  rejection rather than a NULL that silently satisfies the comparison.
*/
IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
   AND ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingCatalogSeedApply')), N'') <> N'20260821-v1'
  THROW 51906, 'CATALOG SEED BLOCKED: neither the guarded reset nor the guarded catalog workflow authorized this batch.', 1;

DECLARE @RequiredTables table
(
  SchemaName sysname NOT NULL,
  TableName sysname NOT NULL,
  PRIMARY KEY (SchemaName, TableName)
);

INSERT @RequiredTables (SchemaName, TableName)
VALUES
  (N'dbo', N'Formas_Pago'),
  (N'dbo', N'CuentasContables'),
  (N'dbo', N'Actividad'),
  (N'dbo', N'Compra'),
  (N'dbo', N'Servicios'),
  (N'dbo', N'Proveedores'),
  (N'dbo', N'ROOM'),
  (N'dbo', N'CfdiPolizaCuentaDefault'),
  (N'dbo', N'PlantillaContable'),
  (N'dbo', N'PlantillaContableLinea'),
  (N'dbo', N'PARAMETROS_CONFIGURACION'),
  (N'dbo', N'OrdenTrabajoCategoria'),
  (N'dbo', N'Extra'),
  (N'dbo', N'ExperienceProvider'),
  (N'dbo', N'Experience'),
  (N'dbo', N'ExperiencePackage'),
  (N'dbo', N'ExperienceAddOn'),
  (N'dbo', N'BusinessPartner'),
  (N'dbo', N'BusinessPartnerRfcScope'),
  (N'dbo', N'BusinessPartnerRole'),
  (N'dbo', N'SatRfcProfile'),
  (N'bancos', N'Cuentas_Banco'),
  (N'logistica', N'UnitOfMeasure'),
  (N'logistica', N'UnitConversion'),
  (N'logistica', N'Allergen'),
  (N'logistica', N'Material'),
  (N'logistica', N'MaterialCategory'),
  (N'logistica', N'Location'),
  (N'rh', N'Holiday'),
  (N'rh', N'WorkSite'),
  (N'restaurante', N'Site'),
  (N'restaurante', N'SiteLocationPriority'),
  (N'restaurante', N'DiningTable'),
  (N'restaurante', N'KitchenStation'),
  (N'restaurante', N'CashRegister'),
  (N'restaurante', N'ExternalProvider'),
  (N'restaurante', N'AccountingConfiguration'),
  (N'restaurante', N'ProductCard'),
  (N'restaurante', N'Product'),
  (N'restaurante', N'ModifierGroup'),
  (N'restaurante', N'ModifierOption'),
  (N'restaurante', N'ProductModifierGroup'),
  (N'restaurante', N'Menu'),
  (N'restaurante', N'MenuSection'),
  (N'restaurante', N'MenuItem'),
  (N'restaurante', N'MenuSchedule');

IF EXISTS
(
  SELECT 1
  FROM @RequiredTables required
  WHERE OBJECT_ID(QUOTENAME(required.SchemaName) + N'.' + QUOTENAME(required.TableName), N'U') IS NULL
)
  THROW 51907, 'CATALOG SEED BLOCKED: a required catalog table is missing.', 1;

/*
  dbo.Formas_Pago, CuentasContables, Actividad, Compra, Servicios, Proveedores
  and PARAMETROS_CONFIGURACION predate the migration folder and have no DDL
  anywhere in this repository. Their shape is asserted here rather than assumed,
  so an upstream column rename stops the seed instead of silently writing the
  wrong thing.
*/
IF COL_LENGTH(N'dbo.Formas_Pago', N'Clave') IS NULL
   OR COL_LENGTH(N'dbo.Formas_Pago', N'Descripcion') IS NULL
   OR COL_LENGTH(N'dbo.CuentasContables', N'RFC') IS NULL
   OR COL_LENGTH(N'dbo.CuentasContables', N'Nivel1') IS NULL
   OR COL_LENGTH(N'dbo.CuentasContables', N'Nivel2') IS NULL
   OR COL_LENGTH(N'dbo.CuentasContables', N'Nivel3') IS NULL
   OR COL_LENGTH(N'dbo.CuentasContables', N'Descripcion') IS NULL
   OR COL_LENGTH(N'dbo.Actividad', N'Descripcion') IS NULL
   OR COL_LENGTH(N'dbo.Compra', N'Descripcion') IS NULL
   OR COL_LENGTH(N'dbo.Servicios', N'Descripcion') IS NULL
   OR COL_LENGTH(N'dbo.Proveedores', N'RazonSocial') IS NULL
   OR COL_LENGTH(N'dbo.ROOM', N'OWNER_ID') IS NULL
   OR COL_LENGTH(N'dbo.CfdiPolizaCuentaDefault', N'CuentaClave') IS NULL
   OR COL_LENGTH(N'dbo.CfdiPolizaCuentaDefault', N'CuentaContableId') IS NULL
   OR COL_LENGTH(N'dbo.PlantillaContable', N'CategoriaID') IS NULL
   OR COL_LENGTH(N'dbo.PlantillaContableLinea', N'CuentaContableID') IS NULL
   OR COL_LENGTH(N'dbo.PARAMETROS_CONFIGURACION', N'PARAMETRO') IS NULL
   OR COL_LENGTH(N'dbo.PARAMETROS_CONFIGURACION', N'VALOR1') IS NULL
   OR COL_LENGTH(N'bancos.Cuentas_Banco', N'Cuenta_Contable_ID') IS NULL
   OR COL_LENGTH(N'logistica.UnitOfMeasure', N'Abbreviation') IS NULL
   OR COL_LENGTH(N'logistica.Material', N'ProductType') IS NULL
   OR COL_LENGTH(N'restaurante.Site', N'SiteCode') IS NULL
   OR COL_LENGTH(N'restaurante.AccountingConfiguration', N'SalesAccount') IS NULL
   OR COL_LENGTH(N'restaurante.Product', N'MaterialId') IS NULL
  THROW 51908, 'CATALOG SEED BLOCKED: a target catalog schema shape was not reviewed.', 1;

/*
  The four legacy lookup tables generate their own keys. Asserting it here is
  what allows each of them to be seeded with a single set-based INSERT, which in
  turn is what keeps their identity counters contiguous for attestation guard
  51839. If one of these ever loses its identity column, stop rather than guess.
*/
IF ISNULL(OBJECTPROPERTY(OBJECT_ID(N'dbo.Actividad'), 'TableHasIdentity'), 0) <> 1
   OR ISNULL(OBJECTPROPERTY(OBJECT_ID(N'dbo.Compra'), 'TableHasIdentity'), 0) <> 1
   OR ISNULL(OBJECTPROPERTY(OBJECT_ID(N'dbo.Servicios'), 'TableHasIdentity'), 0) <> 1
   OR ISNULL(OBJECTPROPERTY(OBJECT_ID(N'dbo.Proveedores'), 'TableHasIdentity'), 0) <> 1
  THROW 51909, 'CATALOG SEED BLOCKED: a legacy lookup table no longer generates its own key.', 1;

/*
  Columns these legacy tables require but no C# query reads. Missing one would
  surface mid-seed as a NULL-insert failure rather than a reviewed refusal.
*/
IF COL_LENGTH(N'dbo.Actividad', N'Fecha_Inicio') IS NULL
   OR COL_LENGTH(N'dbo.Actividad', N'RazonSocial') IS NULL
   OR COL_LENGTH(N'dbo.Actividad', N'Asignacion') IS NULL
   OR COL_LENGTH(N'dbo.Compra', N'Proveedor_ID') IS NULL
   OR COL_LENGTH(N'dbo.Servicios', N'Entidad_Cobro_ID') IS NULL
   OR COL_LENGTH(N'dbo.Proveedores', N'Giro') IS NULL
   OR COL_LENGTH(N'dbo.Proveedores', N'CPostal') IS NULL
  THROW 51910, 'CATALOG SEED BLOCKED: a legacy lookup table lost a required column.', 1;

DECLARE @TrainingRfc varchar(50) = 'XAXX010101000';
DECLARE @SeedActor nvarchar(256) = N'OrionERP Training catalogs v1';

/*
  The synthetic organization and its fictional cohort must already exist. The
  chart of accounts, the arrendador rewrite and the restaurant site all hang off
  rows that provision and scenarios create.
*/
IF NOT EXISTS (SELECT 1 FROM dbo.RazonSocial WHERE Nombre = N'ORION TRAINING · ORGANIZACIÓN FICTICIA')
   OR (SELECT COUNT(*) FROM dbo.Capital_Humano WHERE ID IN (990001, 990002, 990003, 990004)) <> 4
   OR NOT EXISTS (SELECT 1 FROM logistica.Location WHERE Rfc = @TrainingRfc AND LocationCode = 'TRN-ALM-01')
   OR NOT EXISTS (SELECT 1 FROM rh.WorkSite WHERE Rfc = @TrainingRfc)
  THROW 51911, 'CATALOG SEED BLOCKED: the synthetic organization was not provisioned before the catalog seed.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  /* ------------------------------------------------------------------ *
   * 1. dbo.Formas_Pago - SAT c_FormaPago
   *
   * The published catalog, restated in full. WHEN MATCHED overwrites the
   * description so a clave preserved from the clone keeps the repository's
   * wording rather than production's. This list is mirrored by the clamp in
   * 20260817_orion_training_sanitize.sql and by attestation guard 51769; the
   * three must stay identical.
   * ------------------------------------------------------------------ */
  MERGE dbo.Formas_Pago AS target
  USING
  (
    VALUES
      (N'01', N'Efectivo'),
      (N'02', N'Cheque nominativo'),
      (N'03', N'Transferencia electrónica de fondos'),
      (N'04', N'Tarjeta de crédito'),
      (N'05', N'Monedero electrónico'),
      (N'06', N'Dinero electrónico'),
      (N'08', N'Vales de despensa'),
      (N'12', N'Dación en pago'),
      (N'13', N'Pago por subrogación'),
      (N'14', N'Pago por consignación'),
      (N'15', N'Condonación'),
      (N'17', N'Compensación'),
      (N'23', N'Novación'),
      (N'24', N'Confusión'),
      (N'25', N'Remisión de deuda'),
      (N'26', N'Prescripción o caducidad'),
      (N'27', N'A satisfacción del acreedor'),
      (N'28', N'Tarjeta de débito'),
      (N'29', N'Tarjeta de servicios'),
      (N'30', N'Aplicación de anticipos'),
      (N'31', N'Intermediario pagos'),
      (N'99', N'Por definir')
  ) AS source (Clave, Descripcion)
    ON target.Clave = source.Clave
  WHEN MATCHED THEN
    UPDATE SET Descripcion = source.Descripcion
  WHEN NOT MATCHED BY TARGET THEN
    INSERT (Clave, Descripcion)
    VALUES (source.Clave, source.Descripcion);

  /* ------------------------------------------------------------------ *
   * 2. dbo.CuentasContables - synthetic chart of accounts
   *
   * CuentasContablesRepository treats Nivel2 = '00' AND Nivel3 = '00' as a
   * Nivel1 header, Nivel3 = '00' as a Nivel2 header, and anything else as a
   * postable leaf. SearchUnifiedAsync self-joins twice to resolve both parents,
   * so every leaf below has its Nivel1 and Nivel2 header present; without them
   * the account picker renders blank group names.
   *
   * All three tiers go in as one statement so the identity stays contiguous.
   * ------------------------------------------------------------------ */
  INSERT dbo.CuentasContables (RFC, Nivel1, Nivel2, Nivel3, Descripcion)
  SELECT @TrainingRfc, source.Nivel1, source.Nivel2, source.Nivel3, source.Descripcion
  FROM
  (
    VALUES
      ('100', '00', '00', N'ACTIVO CIRCULANTE FICTICIO'),
      ('100', '01', '00', N'Caja y bancos ficticios'),
      ('100', '01', '01', N'Caja chica ficticia'),
      ('100', '01', '02', N'Banco ficticio MXN'),
      ('100', '02', '00', N'Clientes ficticios'),
      ('100', '02', '01', N'Clientes por cobrar ficticios'),
      ('100', '03', '00', N'Impuestos acreditables ficticios'),
      ('100', '03', '01', N'IVA acreditable ficticio'),
      ('100', '03', '02', N'IEPS acreditable ficticio'),
      ('100', '04', '00', N'Inventarios ficticios'),
      ('100', '04', '01', N'Inventario de insumos ficticio'),
      ('200', '00', '00', N'PASIVO FICTICIO'),
      ('200', '01', '00', N'Proveedores ficticios'),
      ('200', '01', '01', N'Proveedores por pagar ficticios'),
      ('200', '02', '00', N'Impuestos trasladados ficticios'),
      ('200', '02', '01', N'IVA trasladado ficticio'),
      ('200', '02', '02', N'IEPS trasladado ficticio'),
      ('200', '03', '00', N'Impuestos retenidos ficticios'),
      ('200', '03', '01', N'Retención de IVA ficticia'),
      ('200', '03', '02', N'Retención de ISR ficticia'),
      ('200', '03', '03', N'Retención de IEPS ficticia'),
      ('200', '04', '00', N'Otras cuentas por pagar ficticias'),
      ('200', '04', '01', N'Propinas por pagar ficticias'),
      ('300', '00', '00', N'CAPITAL FICTICIO'),
      ('300', '01', '00', N'Capital contable ficticio'),
      ('300', '01', '01', N'Capital social ficticio'),
      ('400', '00', '00', N'INGRESOS FICTICIOS'),
      ('400', '01', '00', N'Ventas ficticias'),
      ('400', '01', '01', N'Ingresos por hospedaje ficticio'),
      ('400', '01', '02', N'Ingresos por restaurante ficticio'),
      ('400', '01', '03', N'Ingresos por experiencias ficticio'),
      ('400', '02', '00', N'Deducciones de venta ficticias'),
      ('400', '02', '01', N'Descuentos sobre ventas ficticios'),
      ('500', '00', '00', N'COSTOS FICTICIOS'),
      ('500', '01', '00', N'Costo de ventas ficticio'),
      ('500', '01', '01', N'Costo de ventas de alimentos ficticio'),
      ('500', '01', '02', N'Merma ficticia'),
      ('600', '00', '00', N'GASTOS FICTICIOS'),
      ('600', '01', '00', N'Gastos de operación ficticios'),
      ('600', '01', '01', N'Gastos generales ficticios'),
      ('600', '01', '02', N'Comisiones de plataforma ficticias'),
      ('600', '01', '03', N'Servicios ficticios')
  ) AS source (Nivel1, Nivel2, Nivel3, Descripcion)
  WHERE NOT EXISTS
  (
    SELECT 1
    FROM dbo.CuentasContables existing
    WHERE existing.RFC = @TrainingRfc
      AND existing.Nivel1 = source.Nivel1
      AND existing.Nivel2 = source.Nivel2
      AND existing.Nivel3 = source.Nivel3
  );

  /* ------------------------------------------------------------------ *
   * 3. dbo.Proveedores, and the dbo.ROOM.OWNER_ID rewrite
   *
   * Comes before the transaction lookups because dbo.Compra and dbo.Servicios
   * both carry a non-nullable owner reference.
   *
   * ArrendadoresEstadoCuentaService joins dbo.ROOM.OWNER_ID against
   * dbo.Proveedores.id, but provisioning sets OWNER_ID to 990001, which is a
   * dbo.Capital_Humano identifier. That mismatch leaves /arrendadores empty even
   * when synthetic rooms exist, so the fictional arrendador is created here and
   * the rooms are repointed at it. Attestation guard 51757 inspects ROOM_NAME
   * and NOTES only, so rewriting OWNER_ID does not disturb it.
   *
   * Every column below is NOT NULL without a default in the live schema. These
   * legacy tables predate the migration folder, so the values are spelled out
   * rather than left to the database.
   * ------------------------------------------------------------------ */
  INSERT dbo.Proveedores
    (RazonSocial, Calle, Colonia, Ciudad, Estado, CPostal, Giro, RFC, Email, Tel, Notas)
  SELECT 'ARRENDADOR FICTICIO CAPACITACION', 'CALLE FICTICIA SIN NUMERO', 'CENTRO',
         'CIUDAD DE PRUEBA', 'TLAXCALA', '00000', 'Arrendamiento ficticio',
         @TrainingRfc, 'arrendador@training.orion.local', '0000000000',
         'DATOS SINTÉTICOS'
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Proveedores existing
    WHERE existing.RazonSocial = 'ARRENDADOR FICTICIO CAPACITACION'
  );

  DECLARE @ArrendadorId int =
  (
    SELECT TOP (1) id
    FROM dbo.Proveedores
    WHERE RazonSocial = 'ARRENDADOR FICTICIO CAPACITACION'
    ORDER BY id
  );

  UPDATE dbo.ROOM
  SET OWNER_ID = @ArrendadorId
  WHERE ROOM_NAME LIKE 'TRN-%';

  /* ------------------------------------------------------------------ *
   * 4. dbo.Actividad, dbo.Compra, dbo.Servicios
   *
   * The Proyecto, Compra and Servicio pickers on the transaction editor.
   *
   * All three predate the migration folder and carry several NOT NULL columns
   * with no default that no C# query ever reads, so the insert lists below are
   * wider than the two columns the application selects. The shape preflight
   * above asserts that each still has an identity key, which is what lets these
   * be single set-based statements.
   * ------------------------------------------------------------------ */
  DECLARE @SeedStart datetime = CONVERT(datetime, '20260101');
  DECLARE @SeedEnd datetime = CONVERT(datetime, '20261231');

  INSERT dbo.Actividad
    (Fecha_Inicio, Fecha_Final, Ubicacion, Descripcion, RazonSocial, Departamento,
     Tipo_Proyecto, Cliente, Asignacion, Presupuesto, Estatus, Memo, RFC)
  SELECT @SeedStart, @SeedEnd, 'UBICACIÓN FICTICIA', source.Descripcion,
         N'ORION TRAINING · ORGANIZACIÓN FICTICIA', source.Departamento,
         source.TipoProyecto, N'CLIENTE FICTICIO', 0, 0, N'ACTIVO',
         N'DATOS SINTÉTICOS', @TrainingRfc
  FROM
  (
    VALUES
      (N'PROYECTO FICTICIO · HOSPEDAJE', N'OPERACIÓN', N'HOSPEDAJE'),
      (N'PROYECTO FICTICIO · RESTAURANTE', N'OPERACIÓN', N'RESTAURANTE'),
      (N'PROYECTO FICTICIO · EXPERIENCIAS', N'OPERACIÓN', N'EXPERIENCIAS'),
      (N'PROYECTO FICTICIO · MANTENIMIENTO', N'MANTENIMIENTO', N'MANTENIMIENTO'),
      (N'PROYECTO FICTICIO · ADMINISTRACIÓN', N'ADMINISTRACIÓN', N'ADMINISTRACIÓN')
  ) AS source (Descripcion, Departamento, TipoProyecto)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Actividad existing
    WHERE existing.RFC = @TrainingRfc AND existing.Descripcion = source.Descripcion
  );

  INSERT dbo.Compra (Proveedor_ID, FechaCompra, Descripcion, Status, IVA, Notas, RFC)
  SELECT ISNULL(@ArrendadorId, 0), @SeedStart, source.Descripcion, 'ACTIVA', 1,
         'DATOS SINTÉTICOS', @TrainingRfc
  FROM
  (
    VALUES
      ('COMPRA FICTICIA - INSUMOS DE COCINA'),
      ('COMPRA FICTICIA - AMENIDADES'),
      ('COMPRA FICTICIA - LIMPIEZA'),
      ('COMPRA FICTICIA - PAPELERIA')
  ) AS source (Descripcion)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Compra existing
    WHERE existing.RFC = @TrainingRfc AND existing.Descripcion = source.Descripcion
  );

  /* dbo.Servicios carries a rowversion, which must never appear in an insert. */
  INSERT dbo.Servicios
    (Descripcion, RazonSocial, Entidad_Cobro_ID, Periodicidad, Categoria,
     Comentarios, Status, RFC)
  SELECT source.Descripcion, 'ORION TRAINING - ORGANIZACION FICTICIA',
         ISNULL(@ArrendadorId, 0), 1, source.Categoria,
         'DATOS SINTÉTICOS', N'ACTIVO', @TrainingRfc
  FROM
  (
    VALUES
      ('SERVICIO FICTICIO - MANTENIMIENTO', 'MANTENIMIENTO'),
      ('SERVICIO FICTICIO - ENERGIA ELECTRICA', 'ENERGIA'),
      ('SERVICIO FICTICIO - AGUA POTABLE', 'AGUA'),
      ('SERVICIO FICTICIO - INTERNET', 'TELECOMUNICACIONES'),
      ('SERVICIO FICTICIO - CONTABILIDAD', 'PROFESIONALES')
  ) AS source (Descripcion, Categoria)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Servicios existing
    WHERE existing.RFC = @TrainingRfc AND existing.Descripcion = source.Descripcion
  );

  /* ------------------------------------------------------------------ *
   * 5. bancos.Cuentas_Banco
   *
   * One fictional account bound to the synthetic bank leaf, so the bank
   * reconciliation screens resolve their contra account instead of reporting
   * that Cuenta_Contable_ID does not resolve to CuentasContables.
   * ------------------------------------------------------------------ */
  DECLARE @BancoCuentaId int =
  (
    SELECT id FROM dbo.CuentasContables
    WHERE RFC = @TrainingRfc AND Nivel1 = '100' AND Nivel2 = '01' AND Nivel3 = '02'
  );

  INSERT bancos.Cuentas_Banco
    (Nombre_Banco, Numero_Cuenta, Tipo_Cuenta, Nombre_Titular, CLABE_Cuenta,
     RFC, Activo, Cuenta_Contable_ID, Cuenta_Contable_Egreso, Cuenta_Contable_Ingreso)
  SELECT N'BANCO FICTICIO', N'TRN-0001', N'CHEQUES',
         N'ORION TRAINING · ORGANIZACIÓN FICTICIA', N'000000000000000000',
         @TrainingRfc, 1, @BancoCuentaId, NULL, NULL
  WHERE @BancoCuentaId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM bancos.Cuentas_Banco existing
      WHERE existing.RFC = @TrainingRfc AND existing.Numero_Cuenta = N'TRN-0001'
    );

  /* ------------------------------------------------------------------ *
   * 6. dbo.CfdiPolizaCuentaDefault
   *
   * Every role in CfdiPolizaCuentaDefaultRoles.Required, bound to the synthetic
   * leaves above, so the CFDI-to-poliza panel in /ajustes opens fully configured
   * instead of showing eleven unbound roles.
   * ------------------------------------------------------------------ */
  INSERT dbo.CfdiPolizaCuentaDefault (Rfc, CuentaClave, CuentaContableId, CreadoEn, ActualizadoEn)
  SELECT @TrainingRfc, source.CuentaClave, cuenta.id, SYSUTCDATETIME(), SYSUTCDATETIME()
  FROM
  (
    VALUES
      (N'SUBTOTAL_GASTO',   '600', '01', '01'),
      (N'SUBTOTAL_INGRESO', '400', '01', '01'),
      (N'IVA_TRASLADADO',   '200', '02', '01'),
      (N'IVA_ACREDITABLE',  '100', '03', '01'),
      (N'IEPS_TRASLADADO',  '200', '02', '02'),
      (N'IEPS_ACREDITABLE', '100', '03', '02'),
      (N'RETENCION_IVA',    '200', '03', '01'),
      (N'RETENCION_ISR',    '200', '03', '02'),
      (N'RETENCION_IEPS',   '200', '03', '03'),
      (N'TOTAL_GASTO',      '200', '01', '01'),
      (N'TOTAL_INGRESO',    '100', '02', '01')
  ) AS source (CuentaClave, Nivel1, Nivel2, Nivel3)
  JOIN dbo.CuentasContables cuenta
    ON cuenta.RFC = @TrainingRfc
   AND cuenta.Nivel1 = source.Nivel1
   AND cuenta.Nivel2 = source.Nivel2
   AND cuenta.Nivel3 = source.Nivel3
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.CfdiPolizaCuentaDefault existing
    WHERE existing.Rfc = @TrainingRfc AND existing.CuentaClave = source.CuentaClave
  );

  /* ------------------------------------------------------------------ *
   * 7. dbo.PlantillaContable and dbo.PlantillaContableLinea
   *
   * Two balanced templates over the accounts seeded above, so a trainee can
   * post a complete poliza from the transaction editor on the first try.
   * CategoriaID is a plain int assigned MAX + 1 by AjustesService; there is no
   * category table. Origen is 'Manual' so the templates behave exactly like
   * ones an instructor would author by hand.
   * ------------------------------------------------------------------ */
  DECLARE @PlantillaSeed table
  (
    Nombre nvarchar(200) NOT NULL PRIMARY KEY,
    Descripcion nvarchar(400) NOT NULL,
    TipoPoliza nvarchar(50) NOT NULL,
    Orden int NOT NULL
  );
  INSERT @PlantillaSeed (Nombre, Descripcion, TipoPoliza, Orden)
  VALUES
    (N'TRN-INGRESO-HOSPEDAJE', N'Plantilla ficticia de ingreso por hospedaje con IVA trasladado.', N'INGRESO', 1),
    (N'TRN-EGRESO-GASTO', N'Plantilla ficticia de gasto general con IVA acreditable.', N'EGRESO', 2);

  INSERT dbo.PlantillaContable
    (Nombre, Descripcion, CategoriaID, RFC, TipoPoliza, Contexto, Activa, Origen)
  SELECT
    source.Nombre,
    source.Descripcion,
    ISNULL((SELECT MAX(seed.CategoriaID) FROM dbo.PlantillaContable seed), 0)
      + ROW_NUMBER() OVER (ORDER BY source.Orden),
    @TrainingRfc,
    source.TipoPoliza,
    N'TRANSACCION',
    1,
    N'Manual'
  FROM @PlantillaSeed source
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.PlantillaContable existing
    WHERE existing.RFC = @TrainingRfc AND existing.Nombre = source.Nombre
  );

  INSERT dbo.PlantillaContableLinea
    (PlantillaContableID, Orden, CuentaContableID, Naturaleza, MontoTipo, Factor,
     ConceptoTipo, ConceptoFijo, Activa)
  SELECT plantilla.PlantillaContableID, source.Orden, cuenta.id,
         source.Naturaleza, source.MontoTipo, 1, N'TRANSACCION', NULL, 1
  FROM
  (
    VALUES
      (N'TRN-INGRESO-HOSPEDAJE', 1, '100', '02', '01', N'DEBE',  N'MONTO_TOTAL'),
      (N'TRN-INGRESO-HOSPEDAJE', 2, '400', '01', '01', N'HABER', N'SUBTOTAL_IVA_16'),
      (N'TRN-INGRESO-HOSPEDAJE', 3, '200', '02', '01', N'HABER', N'IVA_16'),
      (N'TRN-EGRESO-GASTO',      1, '600', '01', '01', N'DEBE',  N'SUBTOTAL_IVA_16'),
      (N'TRN-EGRESO-GASTO',      2, '100', '03', '01', N'DEBE',  N'IVA_16'),
      (N'TRN-EGRESO-GASTO',      3, '200', '01', '01', N'HABER', N'MONTO_TOTAL')
  ) AS source (Nombre, Orden, Nivel1, Nivel2, Nivel3, Naturaleza, MontoTipo)
  JOIN dbo.PlantillaContable plantilla
    ON plantilla.RFC = @TrainingRfc AND plantilla.Nombre = source.Nombre
  JOIN dbo.CuentasContables cuenta
    ON cuenta.RFC = @TrainingRfc
   AND cuenta.Nivel1 = source.Nivel1
   AND cuenta.Nivel2 = source.Nivel2
   AND cuenta.Nivel3 = source.Nivel3
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.PlantillaContableLinea existing
    WHERE existing.PlantillaContableID = plantilla.PlantillaContableID
      AND existing.Orden = source.Orden
  );

  /* ------------------------------------------------------------------ *
   * 8. dbo.PARAMETROS_CONFIGURACION
   *
   * The one key AjustesService reads. The literal wording matches
   * SaveGeneralSettingsAsync so a later save through /ajustes is a no-op
   * rather than a rewrite.
   * ------------------------------------------------------------------ */
  INSERT dbo.PARAMETROS_CONFIGURACION (PARAMETRO, VALOR1, VALOR2, VALOR3, VALOR4, VALOR5)
  SELECT N'CxcrApNotificationDays', N'15',
         N'Dias de anticipacion para notificar CxCR en dashboard', NULL, NULL, NULL
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.PARAMETROS_CONFIGURACION existing
    WHERE existing.PARAMETRO = N'CxcrApNotificationDays'
  );

  /* ------------------------------------------------------------------ *
   * 9. dbo.OrdenTrabajoCategoria
   *
   * The same four categories 20260425_ordenes_trabajo_v1.sql seeds in every
   * other environment, restated verbatim rather than reinvented so Training
   * and production agree.
   * ------------------------------------------------------------------ */
  MERGE dbo.OrdenTrabajoCategoria AS target
  USING
  (
    VALUES
      ('LIMPIEZA', N'Limpieza', 1),
      ('MANTENIMIENTO', N'Mantenimiento', 2),
      ('CHECKLIST', N'Checklist', 3),
      ('SERVICIO', N'Servicio', 4)
  ) AS source (Codigo, Nombre, Orden)
    ON target.Codigo = source.Codigo
  WHEN MATCHED THEN
    UPDATE SET Nombre = source.Nombre, Orden = source.Orden, Activa = 1
  WHEN NOT MATCHED BY TARGET THEN
    INSERT (Codigo, Nombre, Orden)
    VALUES (source.Codigo, source.Nombre, source.Orden);

  /* ------------------------------------------------------------------ *
   * 10. dbo.Extra
   *
   * Reservation extras. Not RFC-scoped in this schema, so the TRAINING prefix
   * is what marks them as synthetic.
   * ------------------------------------------------------------------ */
  INSERT dbo.Extra ([Name], [Description], Price, IsActive, CreatedAtUtc, UpdatedAtUtc)
  SELECT source.[Name], source.[Description], source.Price, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
  FROM
  (
    VALUES
      (N'TRAINING · Cama adicional', N'Extra ficticio para capacitación.', CONVERT(decimal(18, 2), 350)),
      (N'TRAINING · Desayuno incluido', N'Extra ficticio para capacitación.', CONVERT(decimal(18, 2), 220)),
      (N'TRAINING · Late checkout', N'Extra ficticio para capacitación.', CONVERT(decimal(18, 2), 400)),
      (N'TRAINING · Decoración especial', N'Extra ficticio para capacitación.', CONVERT(decimal(18, 2), 500))
  ) AS source ([Name], [Description], Price)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Extra existing WHERE existing.[Name] = source.[Name]
  );

  /* ------------------------------------------------------------------ *
   * 11. Experiencias: ExperienceProvider, Experience, ExperiencePackage,
   *     ExperienceAddOn
   *
   * These are the repository's own catalog values from
   * 20260601_reservation_experiences.sql, restated rather than reinvented so a
   * trainee practises against the same experience catalog that exists in every
   * other environment. They are product catalog rows, not customer data.
   * ------------------------------------------------------------------ */
  INSERT dbo.ExperienceProvider (Code, [Name], [Description], IsActive)
  SELECT N'avistamiento-las-4e', N'Avistamiento las 4E',
         N'Proveedor subcontratado para el avistamiento de luciernagas.', 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.ExperienceProvider existing
    WHERE existing.Code = N'avistamiento-las-4e'
  );

  DECLARE @ExperienceProviderId int =
  (
    SELECT ExperienceProviderID FROM dbo.ExperienceProvider
    WHERE Code = N'avistamiento-las-4e'
  );

  INSERT dbo.Experience
    (ExperienceProviderID, Code, [Name], [Description], Category,
     SeasonStart, SeasonEnd, MinimumParticipants, MaximumParticipants, IsPublic, IsActive)
  SELECT @ExperienceProviderId, N'luciernagas-calpulalpan',
         N'Avistamiento de Luciernagas en Calpulalpan',
         N'Avistamiento de luciernagas en Calpulalpan con transporte incluido en el precio de la experiencia.',
         N'Turismo', '20260615', '20260815', 1, NULL, 1, 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.Experience existing
    WHERE existing.Code = N'luciernagas-calpulalpan'
  );

  DECLARE @ExperienceId int =
  (
    SELECT ExperienceID FROM dbo.Experience WHERE Code = N'luciernagas-calpulalpan'
  );

  INSERT dbo.ExperiencePackage
    (ExperienceID, Code, [Name], ProviderPackageName, [Description], Includes,
     UnitPrice, TaxMode, DisplayOrder, IsPublic, IsActive)
  SELECT @ExperienceId, source.Code, source.[Name], source.ProviderPackageName,
         source.[Description], source.Includes, source.UnitPrice,
         N'TaxableExclusive', source.DisplayOrder, 1, 1
  FROM
  (
    VALUES
      (N'esencial', N'Experiencia Esencial', N'Paquete Esencial',
       N'Recorrido base de luciernagas.',
       N'Estacionamiento; baños; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas.',
       CONVERT(decimal(18, 2), 800), 10),
      (N'clasico', N'Experiencia Clasica', N'Paquete Clasico',
       N'Experiencia de luciernagas con atole y pan.',
       N'Estacionamiento; baños; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas; degustacion de atole y pan.',
       CONVERT(decimal(18, 2), 900), 20),
      (N'gastronomico', N'Experiencia Gastronomica', N'Paquete Gastronomico',
       N'Experiencia de luciernagas con atole, pan y comida regional.',
       N'Estacionamiento; baños; recorrido de formas y sonidos del bosque; platica de moneda antigua; platica del perro de agua; transporte; guia acreditado SECTUR; avistamiento de luciernagas; degustacion de atole y pan; comida tradicional.',
       CONVERT(decimal(18, 2), 1200), 30)
  ) AS source (Code, [Name], ProviderPackageName, [Description], Includes, UnitPrice, DisplayOrder)
  WHERE @ExperienceId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM dbo.ExperiencePackage existing
      WHERE existing.ExperienceID = @ExperienceId AND existing.Code = source.Code
    );

  INSERT dbo.ExperienceAddOn
    (ExperienceID, Code, [Name], [Description], UnitPrice, AppliesPerParticipant,
     TaxMode, DisplayOrder, IsPublic, IsActive)
  SELECT @ExperienceId, N'tecoaque', N'Tecoaque',
         N'Visita libre opcional a Zona Arqueologica Tecoaque.',
         CONVERT(decimal(18, 2), 300), 1, N'TaxableExclusive', 10, 1, 1
  WHERE @ExperienceId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM dbo.ExperienceAddOn existing
      WHERE existing.ExperienceID = @ExperienceId AND existing.Code = N'tecoaque'
    );

  /* ------------------------------------------------------------------ *
   * 12. logistica: UnitOfMeasure, UnitConversion, Allergen, MaterialCategory,
   *     Material
   *
   * Provisioning creates two units, two categories, two locations and three
   * housekeeping materials. A restaurant menu needs food-shaped materials,
   * because restaurante.Product has FK_RestaurantProduct_Material_Rfc and
   * UX_RestaurantProduct_Material (one product per material), so the existing
   * towel/amenity/cleaner rows cannot double as menu items.
   *
   * The TRAINING and TRN- prefixes are load-bearing: attestation guard 51758
   * asserts that no UnitOfMeasure, MaterialCategory or Material row is missing
   * them, so only the pinned counts move, never the predicates.
   * ------------------------------------------------------------------ */
  INSERT logistica.UnitOfMeasure (UnitName, Abbreviation, Description, IsActive)
  SELECT source.UnitName, source.Abbreviation, N'Unidad ficticia para capacitación', 1
  FROM
  (
    VALUES
      ('TRAINING GRAMO', 'GR'),
      ('TRAINING KILOGRAMO', 'KG'),
      ('TRAINING MILILITRO', 'ML'),
      ('TRAINING LITRO', 'LT')
  ) AS source (UnitName, Abbreviation)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM logistica.UnitOfMeasure existing
    WHERE existing.Abbreviation = source.Abbreviation
  );

  /*
    The mass and volume factors from 20260713_restaurant_inventory_bom.sql,
    narrowed to the abbreviations Training now has. The join on Abbreviation is
    what keeps this correct if the unit rows ever change identity.
  */
  INSERT logistica.UnitConversion (FromUnitId, ToUnitId, Dimension, Factor)
  SELECT fromUnit.Id, toUnit.Id, source.Dimension, source.Factor
  FROM
  (
    VALUES
      ('GR', 'KG', 'Mass', CONVERT(decimal(24, 10), 0.001)),
      ('KG', 'GR', 'Mass', CONVERT(decimal(24, 10), 1000)),
      ('ML', 'LT', 'Volume', CONVERT(decimal(24, 10), 0.001)),
      ('LT', 'ML', 'Volume', CONVERT(decimal(24, 10), 1000))
  ) AS source (FromCode, ToCode, Dimension, Factor)
  JOIN logistica.UnitOfMeasure fromUnit ON fromUnit.Abbreviation = source.FromCode
  JOIN logistica.UnitOfMeasure toUnit ON toUnit.Abbreviation = source.ToCode
  WHERE NOT EXISTS
  (
    SELECT 1 FROM logistica.UnitConversion existing
    WHERE existing.FromUnitId = fromUnit.Id AND existing.ToUnitId = toUnit.Id
  );

  /* The fourteen standard allergens, verbatim from the same BOM migration. */
  INSERT logistica.Allergen (Code, [Name])
  SELECT source.Code, source.[Name]
  FROM
  (
    VALUES
      ('GLUTEN', N'Gluten'), ('CRUSTACEOS', N'Crustaceos'), ('HUEVO', N'Huevo'),
      ('PESCADO', N'Pescado'), ('CACAHUATE', N'Cacahuate'), ('SOYA', N'Soya'),
      ('LECHE', N'Leche'), ('NUECES', N'Nueces de arbol'), ('APIO', N'Apio'),
      ('MOSTAZA', N'Mostaza'), ('AJONJOLI', N'Ajonjoli'), ('SULFITOS', N'Sulfitos'),
      ('ALTRAMUZ', N'Altramuz'), ('MOLUSCOS', N'Moluscos')
  ) AS source (Code, [Name])
  WHERE NOT EXISTS
  (
    SELECT 1 FROM logistica.Allergen existing WHERE existing.Code = source.Code
  );

  INSERT logistica.MaterialCategory (LegacyCategoryId, CategoryName, Description, IsActive, Rfc)
  SELECT NULL, 'TRAINING ALIMENTOS', N'Categoría ficticia', 1, @TrainingRfc
  WHERE NOT EXISTS
  (
    SELECT 1 FROM logistica.MaterialCategory existing
    WHERE existing.Rfc = @TrainingRfc AND existing.CategoryName = 'TRAINING ALIMENTOS'
  );

  DECLARE @PieceUnitId int =
    (SELECT Id FROM logistica.UnitOfMeasure WHERE UnitName = 'TRAINING PIEZA');
  DECLARE @FoodCategoryId int =
  (
    SELECT Id FROM logistica.MaterialCategory
    WHERE Rfc = @TrainingRfc AND CategoryName = 'TRAINING ALIMENTOS'
  );

  /*
    Made-to-order finished goods, so the POS can sell them without a stock
    balance behind them. Deliberately no logistica.StockBalance rows: the
    attestation pins that table at six, and a made-to-order product does not
    need one.
  */
  INSERT logistica.Material
    (MaterialCode, Description, BaseUnitId, PurchaseQuantity, PurchaseUnitId,
     BaseUnitPrice, Brand, IsPerishable, MaterialStatus, CategoryId, MaterialClass,
     IsActive, Rfc, ProductType, FulfillmentMode, TrackLots)
  SELECT source.MaterialCode, source.Description, @PieceUnitId, 1, @PieceUnitId,
         source.BaseUnitPrice, 'TRAINING', 0, 'ACTIVO', @FoodCategoryId, 'Consumable',
         1, @TrainingRfc, 'FinishedGood', 'MadeToOrder', 0
  FROM
  (
    VALUES
      ('TRN-MAT-101', N'Plato fuerte ficticio de capacitación', CONVERT(decimal(18, 2), 180)),
      ('TRN-MAT-102', N'Entrada ficticia de capacitación', CONVERT(decimal(18, 2), 95)),
      ('TRN-MAT-103', N'Bebida ficticia de capacitación', CONVERT(decimal(18, 2), 60))
  ) AS source (MaterialCode, Description, BaseUnitPrice)
  WHERE @PieceUnitId IS NOT NULL
    AND @FoodCategoryId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM logistica.Material existing
      WHERE existing.Rfc = @TrainingRfc AND existing.MaterialCode = source.MaterialCode
    );

  /* ------------------------------------------------------------------ *
   * 13. dbo.BusinessPartner, BusinessPartnerRfcScope, BusinessPartnerRole
   *
   * Two partners so the purchase-order and recurring-payable screens have a
   * vendor to select and the CFDI screens have a customer.
   * ------------------------------------------------------------------ */
  INSERT dbo.BusinessPartner (LegacyProveedorId, PartnerName, Rfc, City, [State], BusinessLine, Notes, IsActive)
  SELECT NULL, source.PartnerName, 'XAXX010101000', N'CIUDAD DE PRUEBA', N'TLAXCALA',
         source.BusinessLine, N'DATOS SINTÉTICOS', 1
  FROM
  (
    VALUES
      (N'PROVEEDOR FICTICIO · INSUMOS', N'Abarrotes y alimentos'),
      (N'CLIENTE FICTICIO · CORPORATIVO', N'Servicios corporativos')
  ) AS source (PartnerName, BusinessLine)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.BusinessPartner existing WHERE existing.PartnerName = source.PartnerName
  );

  INSERT dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId, IsActive, CreatedBy)
  SELECT @TrainingRfc, partner.Id, 1, @SeedActor
  FROM dbo.BusinessPartner partner
  WHERE partner.PartnerName IN (N'PROVEEDOR FICTICIO · INSUMOS', N'CLIENTE FICTICIO · CORPORATIVO')
    AND NOT EXISTS
    (
      SELECT 1 FROM dbo.BusinessPartnerRfcScope existing
      WHERE existing.Rfc = @TrainingRfc AND existing.BusinessPartnerId = partner.Id
    );

  INSERT dbo.BusinessPartnerRole (BusinessPartnerId, RoleCode)
  SELECT partner.Id, source.RoleCode
  FROM
  (
    VALUES
      (N'PROVEEDOR FICTICIO · INSUMOS', 'Vendor'),
      (N'CLIENTE FICTICIO · CORPORATIVO', 'Customer')
  ) AS source (PartnerName, RoleCode)
  JOIN dbo.BusinessPartner partner ON partner.PartnerName = source.PartnerName
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.BusinessPartnerRole existing
    WHERE existing.BusinessPartnerId = partner.Id AND existing.RoleCode = source.RoleCode
  );

  /* ------------------------------------------------------------------ *
   * 14. rh.Holiday
   *
   * Must come after 20260817_orion_training_scenarios.sql, which refuses to run
   * unless the entire rh schema is empty (THROW 51825). Bound to the synthetic
   * work site so the attendance calendar has something to show.
   * ------------------------------------------------------------------ */
  DECLARE @TrainingWorkSiteId int =
    (SELECT TOP (1) Id FROM rh.WorkSite WHERE Rfc = @TrainingRfc ORDER BY Id);

  INSERT rh.Holiday (Rfc, SiteId, HolidayDate, [Name], IsPaid)
  SELECT @TrainingRfc, @TrainingWorkSiteId, source.HolidayDate, source.[Name], 1
  FROM
  (
    VALUES
      (CONVERT(date, '20260101'), N'Año Nuevo ficticio'),
      (CONVERT(date, '20260501'), N'Día del Trabajo ficticio'),
      (CONVERT(date, '20261216'), N'Descanso ficticio de capacitación')
  ) AS source (HolidayDate, [Name])
  WHERE @TrainingWorkSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM rh.Holiday existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @TrainingWorkSiteId
        AND existing.HolidayDate = source.HolidayDate
    );

  /* ------------------------------------------------------------------ *
   * 15. restaurante.*
   *
   * The whole configuration chain in foreign-key order, so /restaurante/pos,
   * /restaurante/configuracion and /restaurante/menus all open against a
   * working fictional site.
   *
   * Only configuration is seeded. Order, OrderLine, Payment, CashShift,
   * CashMovement, QuickPin, SupervisorAuthorization, EventOutbox and
   * DailySequence stay empty and stay outside the attestation allowlist: a
   * trainee produces those by working, and keeping them empty is what makes
   * the fail-closed check meaningful.
   *
   * The AccountingConfiguration columns are free text. RestaurantAccountingService
   * splits them on '.', '-', '/' and '>' into Nivel1/Nivel2/Nivel3, so the
   * hyphenated three-level form below is the shape the poliza builder expects.
   * ------------------------------------------------------------------ */
  INSERT restaurante.Site
    (Rfc, SiteCode, [Name], TimeZoneId, OperationalDayCutoff, CurrencyCode,
     TaxRate, PricesIncludeTax, IsEnabled)
  SELECT @TrainingRfc, 'TRN-REST-01', 'TRAINING · RESTAURANTE FICTICIO',
         'Central Standard Time (Mexico)', '04:00', 'MXN',
         CONVERT(decimal(9, 6), 0.160000), 1, 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.Site existing
    WHERE existing.Rfc = @TrainingRfc AND existing.SiteCode = 'TRN-REST-01'
  );

  DECLARE @RestaurantSiteId int =
  (
    SELECT Id FROM restaurante.Site
    WHERE Rfc = @TrainingRfc AND SiteCode = 'TRN-REST-01'
  );
  DECLARE @WarehouseLocationId int =
  (
    SELECT Id FROM logistica.Location
    WHERE Rfc = @TrainingRfc AND LocationCode = 'TRN-ALM-01'
  );

  INSERT restaurante.SiteLocationPriority (Rfc, SiteId, StationCode, LocationId, Priority)
  SELECT @TrainingRfc, @RestaurantSiteId, 'GENERAL', @WarehouseLocationId, 1
  WHERE @RestaurantSiteId IS NOT NULL
    AND @WarehouseLocationId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.SiteLocationPriority existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @RestaurantSiteId
        AND existing.StationCode = 'GENERAL'
        AND existing.LocationId = @WarehouseLocationId
    );

  INSERT restaurante.DiningTable (Rfc, SiteId, TableCode, [Name], Capacity, IsActive)
  SELECT @TrainingRfc, @RestaurantSiteId, source.TableCode, source.[Name], source.Capacity, 1
  FROM
  (
    VALUES
      ('M-01', 'Mesa ficticia 01', 2),
      ('M-02', 'Mesa ficticia 02', 4),
      ('M-03', 'Mesa ficticia 03', 4),
      ('M-04', 'Mesa ficticia 04', 6)
  ) AS source (TableCode, [Name], Capacity)
  WHERE @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.DiningTable existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @RestaurantSiteId
        AND existing.TableCode = source.TableCode
    );

  INSERT restaurante.KitchenStation (Rfc, SiteId, StationCode, [Name], SortOrder, IsActive)
  SELECT @TrainingRfc, @RestaurantSiteId, source.StationCode, source.[Name], source.SortOrder, 1
  FROM
  (
    VALUES
      ('COCINA', 'Cocina ficticia', 1),
      ('BARRA', 'Barra ficticia', 2)
  ) AS source (StationCode, [Name], SortOrder)
  WHERE @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.KitchenStation existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @RestaurantSiteId
        AND existing.StationCode = source.StationCode
    );

  /* DeviceKeyHash stays NULL: a training register must not carry a credential. */
  INSERT restaurante.CashRegister (Rfc, SiteId, RegisterCode, [Name], DeviceKeyHash, IsActive)
  SELECT @TrainingRfc, @RestaurantSiteId, 'CAJA-01', 'Caja ficticia 01', NULL, 1
  WHERE @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.CashRegister existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @RestaurantSiteId
        AND existing.RegisterCode = 'CAJA-01'
    );

  INSERT restaurante.ExternalProvider (Rfc, SiteId, ProviderCode, [Name], DefaultCommissionRate, IsActive)
  SELECT @TrainingRfc, @RestaurantSiteId, 'TRN-PLATAFORMA', 'Plataforma ficticia de entregas',
         CONVERT(decimal(9, 6), 0.150000), 1
  WHERE @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.ExternalProvider existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.SiteId = @RestaurantSiteId
        AND existing.ProviderCode = 'TRN-PLATAFORMA'
    );

  INSERT restaurante.AccountingConfiguration
    (Rfc, SiteId, CashAccount, CardBankAccount, TransferBankAccount,
     PlatformReceivableAccount, SalesAccount, VatAccount, DiscountAccount,
     TipsPayableAccount, PlatformCommissionAccount, InventoryAccount,
     CostOfSalesAccount, WasteAccount, DailyPolicyEnabled)
  SELECT @TrainingRfc, @RestaurantSiteId,
         '100-01-01', '100-01-02', '100-01-02', '100-02-01',
         '400-01-02', '200-02-01', '400-02-01', '200-04-01',
         '600-01-02', '100-04-01', '500-01-01', '500-01-02', 1
  WHERE @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.AccountingConfiguration existing
      WHERE existing.Rfc = @TrainingRfc AND existing.SiteId = @RestaurantSiteId
    );

  INSERT restaurante.ProductCard (Rfc, CardCode, [Name], [Description], IsActive)
  SELECT @TrainingRfc, source.CardCode, source.[Name], source.[Description], 1
  FROM
  (
    VALUES
      ('TRN-CARD-101', 'Plato fuerte ficticio', 'Ficha ficticia de capacitación.'),
      ('TRN-CARD-102', 'Entrada ficticia', 'Ficha ficticia de capacitación.'),
      ('TRN-CARD-103', 'Bebida ficticia', 'Ficha ficticia de capacitación.')
  ) AS source (CardCode, [Name], [Description])
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.ProductCard existing
    WHERE existing.Rfc = @TrainingRfc AND existing.CardCode = source.CardCode
  );

  INSERT restaurante.Product
    (Rfc, ProductCardId, MaterialId, Sku, VariantName, Price, KitchenStationId,
     PreparationMinutes, IsActive, SoldOutOverride)
  SELECT @TrainingRfc, card.Id, material.Id, source.Sku, source.VariantName,
         source.Price, station.Id, source.PreparationMinutes, 1, 0
  FROM
  (
    VALUES
      ('TRN-CARD-101', 'TRN-MAT-101', 'TRN-SKU-101', 'Porción estándar', CONVERT(decimal(18, 2), 180), 'COCINA', 20),
      ('TRN-CARD-102', 'TRN-MAT-102', 'TRN-SKU-102', 'Porción estándar', CONVERT(decimal(18, 2), 95), 'COCINA', 10),
      ('TRN-CARD-103', 'TRN-MAT-103', 'TRN-SKU-103', 'Vaso estándar', CONVERT(decimal(18, 2), 60), 'BARRA', 5)
  ) AS source (CardCode, MaterialCode, Sku, VariantName, Price, StationCode, PreparationMinutes)
  JOIN restaurante.ProductCard card
    ON card.Rfc = @TrainingRfc AND card.CardCode = source.CardCode
  JOIN logistica.Material material
    ON material.Rfc = @TrainingRfc AND material.MaterialCode = source.MaterialCode
  JOIN restaurante.KitchenStation station
    ON station.Rfc = @TrainingRfc
   AND station.SiteId = @RestaurantSiteId
   AND station.StationCode = source.StationCode
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.Product existing
    WHERE existing.Rfc = @TrainingRfc AND existing.Sku = source.Sku
  );

  INSERT restaurante.ModifierGroup (Rfc, [Name], MinSelections, MaxSelections, SortOrder, IsActive)
  SELECT @TrainingRfc, 'Término ficticio', 0, 1, 1, 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.ModifierGroup existing
    WHERE existing.Rfc = @TrainingRfc AND existing.[Name] = 'Término ficticio'
  );

  DECLARE @ModifierGroupId bigint =
  (
    SELECT Id FROM restaurante.ModifierGroup
    WHERE Rfc = @TrainingRfc AND [Name] = 'Término ficticio'
  );

  INSERT restaurante.ModifierOption (Rfc, ModifierGroupId, [Name], PriceDelta, SortOrder, IsActive)
  SELECT @TrainingRfc, @ModifierGroupId, source.[Name], source.PriceDelta, source.SortOrder, 1
  FROM
  (
    VALUES
      ('Bien cocido ficticio', CONVERT(decimal(18, 2), 0), 1),
      ('Término medio ficticio', CONVERT(decimal(18, 2), 0), 2)
  ) AS source ([Name], PriceDelta, SortOrder)
  WHERE @ModifierGroupId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.ModifierOption existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.ModifierGroupId = @ModifierGroupId
        AND existing.[Name] = source.[Name]
    );

  INSERT restaurante.ProductModifierGroup (Rfc, ProductId, ModifierGroupId, SortOrder)
  SELECT @TrainingRfc, product.Id, @ModifierGroupId, 1
  FROM restaurante.Product product
  WHERE product.Rfc = @TrainingRfc
    AND product.Sku = 'TRN-SKU-101'
    AND @ModifierGroupId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.ProductModifierGroup existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.ProductId = product.Id
        AND existing.ModifierGroupId = @ModifierGroupId
    );

  INSERT restaurante.Menu (Rfc, MenuCode, [Name], IsPublished, IsActive)
  SELECT @TrainingRfc, 'TRN-MENU', 'Menú ficticio de capacitación', 1, 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.Menu existing
    WHERE existing.Rfc = @TrainingRfc AND existing.MenuCode = 'TRN-MENU'
  );

  DECLARE @MenuId bigint =
  (
    SELECT Id FROM restaurante.Menu WHERE Rfc = @TrainingRfc AND MenuCode = 'TRN-MENU'
  );

  INSERT restaurante.MenuSection (Rfc, MenuId, [Name], SortOrder)
  SELECT @TrainingRfc, @MenuId, source.[Name], source.SortOrder
  FROM
  (
    VALUES
      ('Alimentos ficticios', 1),
      ('Bebidas ficticias', 2)
  ) AS source ([Name], SortOrder)
  WHERE @MenuId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.MenuSection existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.MenuId = @MenuId
        AND existing.[Name] = source.[Name]
    );

  INSERT restaurante.MenuItem (Rfc, MenuSectionId, ProductId, SortOrder)
  SELECT @TrainingRfc, section.Id, product.Id, source.SortOrder
  FROM
  (
    VALUES
      ('Alimentos ficticios', 'TRN-SKU-101', 1),
      ('Alimentos ficticios', 'TRN-SKU-102', 2),
      ('Bebidas ficticias', 'TRN-SKU-103', 1)
  ) AS source (SectionName, Sku, SortOrder)
  JOIN restaurante.MenuSection section
    ON section.Rfc = @TrainingRfc AND section.MenuId = @MenuId AND section.[Name] = source.SectionName
  JOIN restaurante.Product product
    ON product.Rfc = @TrainingRfc AND product.Sku = source.Sku
  WHERE @MenuId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.MenuItem existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.MenuSectionId = section.Id
        AND existing.ProductId = product.Id
    );

  INSERT restaurante.MenuSchedule (Rfc, MenuId, SiteId, DayOfWeek, StartsAt, EndsAt)
  SELECT @TrainingRfc, @MenuId, @RestaurantSiteId,
         CONVERT(tinyint, source.DayOfWeek), CONVERT(time(0), '11:00'), CONVERT(time(0), '22:00')
  FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) AS source (DayOfWeek)
  WHERE @MenuId IS NOT NULL
    AND @RestaurantSiteId IS NOT NULL
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.MenuSchedule existing
      WHERE existing.Rfc = @TrainingRfc
        AND existing.MenuId = @MenuId
        AND existing.SiteId = @RestaurantSiteId
        AND existing.DayOfWeek = CONVERT(tinyint, source.DayOfWeek)
    );

  /* ------------------------------------------------------------------ *
   * 16. dbo.SatRfcProfile
   *
   * The fiscal identity behind the RFC picker and the CFDI screens.
   *
   * SATFielCertificate, SATFielKey, SATFielPfx and SATFielPasswordEnc are
   * written as explicit NULLs and then asserted to be NULL. This table is the
   * one place in the database that can hold a real FIEL certificate and its
   * encrypted password, which is why it is never preserved from a clone and why
   * the assertion below is not optional.
   * ------------------------------------------------------------------ */
  INSERT dbo.SatRfcProfile
    (Rfc, RazonSocial, NombreComercial, RegimenCapital, FechaInicioOperaciones,
     EstatusPadron, FechaUltCambioEstatus, EmisionFecha, AddressLine1, AddressLine2,
     Municipio, EntidadFederativa, CodigoPostal, CsfDataJson,
     SATFielCertificate, SATFielKey, SATFielPfx, SATFielPasswordEnc, Email)
  SELECT @TrainingRfc, N'ORION TRAINING · ORGANIZACIÓN FICTICIA',
         N'ORION TRAINING', NULL, NULL,
         N'ACTIVO', NULL, NULL, N'CALLE FICTICIA SIN NÚMERO', NULL,
         N'CIUDAD DE PRUEBA', N'TLAXCALA', N'00000', NULL,
         NULL, NULL, NULL, NULL, N'capacitacion@training.orion.local'
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.SatRfcProfile existing WHERE existing.Rfc = @TrainingRfc
  );

  IF EXISTS
  (
    SELECT 1
    FROM dbo.SatRfcProfile
    WHERE SATFielCertificate IS NOT NULL
       OR SATFielKey IS NOT NULL
       OR SATFielPfx IS NOT NULL
       OR SATFielPasswordEnc IS NOT NULL
  )
    THROW 51931, 'CATALOG SEED FAILED: a SAT FIEL credential is present in the Training fiscal profile.', 1;

  /* ------------------------------------------------------------------ *
   * 17. Manifest self-check
   *
   * Everything the seed claims to have created must exist, and nothing it
   * created may belong to any RFC but the fictional one. A silent partial seed
   * is worse than a failed one: it produces a Training environment that looks
   * populated and breaks halfway through a lesson.
   * ------------------------------------------------------------------ */
  IF (SELECT COUNT(*) FROM dbo.Formas_Pago) <> 22
     OR EXISTS
        (
          SELECT 1 FROM dbo.Formas_Pago
          WHERE Clave COLLATE Latin1_General_100_BIN2 NOT IN
            (N'01', N'02', N'03', N'04', N'05', N'06', N'08', N'12', N'13', N'14',
             N'15', N'17', N'23', N'24', N'25', N'26', N'27', N'28', N'29', N'30',
             N'31', N'99')
        )
    THROW 51930, 'CATALOG SEED FAILED: dbo.Formas_Pago does not match the canonical SAT c_FormaPago manifest.', 1;

  IF NOT EXISTS (SELECT 1 FROM dbo.CuentasContables WHERE RFC = @TrainingRfc AND Nivel2 = '00' AND Nivel3 = '00')
     OR NOT EXISTS (SELECT 1 FROM dbo.CuentasContables WHERE RFC = @TrainingRfc AND Nivel3 <> '00')
     OR NOT EXISTS (SELECT 1 FROM dbo.Actividad WHERE RFC = @TrainingRfc)
     OR NOT EXISTS (SELECT 1 FROM dbo.Compra WHERE RFC = @TrainingRfc)
     OR NOT EXISTS (SELECT 1 FROM dbo.Servicios WHERE RFC = @TrainingRfc)
     OR NOT EXISTS (SELECT 1 FROM dbo.Proveedores)
     OR NOT EXISTS (SELECT 1 FROM bancos.Cuentas_Banco WHERE RFC = @TrainingRfc)
     OR (SELECT COUNT(*) FROM dbo.CfdiPolizaCuentaDefault WHERE Rfc = @TrainingRfc) <> 11
     OR (SELECT COUNT(*) FROM dbo.PlantillaContable WHERE RFC = @TrainingRfc) < 2
     OR NOT EXISTS (SELECT 1 FROM dbo.PlantillaContableLinea)
     OR NOT EXISTS (SELECT 1 FROM dbo.PARAMETROS_CONFIGURACION WHERE PARAMETRO = N'CxcrApNotificationDays')
     OR (SELECT COUNT(*) FROM dbo.OrdenTrabajoCategoria) < 4
     OR NOT EXISTS (SELECT 1 FROM dbo.Extra)
     OR NOT EXISTS (SELECT 1 FROM dbo.ExperiencePackage)
     OR NOT EXISTS (SELECT 1 FROM logistica.Allergen)
     OR NOT EXISTS (SELECT 1 FROM logistica.UnitConversion)
     OR NOT EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole)
     OR NOT EXISTS (SELECT 1 FROM rh.Holiday WHERE Rfc = @TrainingRfc)
     OR NOT EXISTS (SELECT 1 FROM restaurante.MenuItem WHERE Rfc = @TrainingRfc)
     OR NOT EXISTS (SELECT 1 FROM dbo.SatRfcProfile WHERE Rfc = @TrainingRfc)
    THROW 51932, 'CATALOG SEED FAILED: the synthetic catalog manifest is incomplete.', 1;

  /*
    No seeded row may carry a tenant other than the fictional one. This is the
    check that would catch a clone row surviving inside an RFC-scoped catalog.
  */
  IF EXISTS (SELECT 1 FROM dbo.CuentasContables WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.Actividad WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.Compra WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.Servicios WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.CfdiPolizaCuentaDefault WHERE Rfc <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.PlantillaContable WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM bancos.Cuentas_Banco WHERE RFC <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.BusinessPartnerRfcScope WHERE Rfc <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM dbo.SatRfcProfile WHERE Rfc <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM rh.Holiday WHERE Rfc <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc <> @TrainingRfc OR SiteCode NOT LIKE 'TRN-%')
     OR EXISTS (SELECT 1 FROM restaurante.DiningTable WHERE Rfc <> @TrainingRfc)
     OR EXISTS (SELECT 1 FROM restaurante.Menu WHERE Rfc <> @TrainingRfc OR MenuCode NOT LIKE 'TRN-%')
     OR EXISTS (SELECT 1 FROM restaurante.Product WHERE Rfc <> @TrainingRfc OR Sku NOT LIKE 'TRN-%')
    THROW 51933, 'CATALOG SEED FAILED: a seeded catalog row is scoped to a tenant other than the fictional RFC.', 1;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW;
END CATCH;

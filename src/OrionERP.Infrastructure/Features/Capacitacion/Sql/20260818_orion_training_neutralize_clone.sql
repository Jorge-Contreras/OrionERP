/*
  OrionERP.Training.NeutralizeClone.v1

  Removes only the byte-exact unsafe schema artifacts observed in the reviewed
  Orion_Training clone. Every present artifact must match its reviewed manifest
  before any DDL is issued. Missing artifacts are accepted so an interrupted or
  already-completed neutralization can be run again safely.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51880, 'TRAINING NEUTRALIZATION BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51881, 'TRAINING NEUTRALIZATION BLOCKED: a sysadmin maintenance connection is required.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'')
     COLLATE Latin1_General_100_BIN2
     <> N'20260817-v1' COLLATE Latin1_General_100_BIN2
  THROW 51882, 'TRAINING NEUTRALIZATION BLOCKED: the read-only sanitizer session guard is missing.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions sessionInfo
  WHERE sessionInfo.is_user_process = 1
    AND sessionInfo.database_id = DB_ID()
    AND sessionInfo.session_id <> @@SPID
)
  THROW 51904, 'TRAINING NEUTRALIZATION BLOCKED: close every other Orion_Training session before schema cleanup.', 1;

DECLARE @ExpectedSynonyms table
(
  SynonymSchema sysname NOT NULL,
  SynonymName sysname NOT NULL,
  BaseSchema sysname NOT NULL,
  BaseObject sysname NOT NULL,
  BaseObjectName nvarchar(1035) NOT NULL,
  PRIMARY KEY (SynonymSchema, SynonymName)
);

INSERT @ExpectedSynonyms (SynonymSchema, SynonymName, BaseSchema, BaseObject, BaseObjectName)
VALUES
  (N'dbo', N'CatalogoCuentas', N'dbo', N'Categorias', N'[dbo].[Categorias]'),
  (N'dbo', N'Comprobante', N'cfdi', N'Comprobante', N'[cfdi].[Comprobante]'),
  (N'dbo', N'Concepto', N'cfdi', N'Concepto', N'[cfdi].[Concepto]'),
  (N'dbo', N'Conceptos', N'cfdi', N'Conceptos', N'[cfdi].[Conceptos]'),
  (N'dbo', N'Emisor', N'cfdi', N'Emisor', N'[cfdi].[Emisor]'),
  (N'dbo', N'Impuestos', N'cfdi', N'Impuestos', N'[cfdi].[Impuestos]'),
  (N'dbo', N'InformacionGlobal', N'cfdi', N'InformacionGlobal', N'[cfdi].[InformacionGlobal]'),
  (N'dbo', N'Receptor', N'cfdi', N'Receptor', N'[cfdi].[Receptor]'),
  (N'dbo', N'retencion', N'cfdi', N'retencion', N'[cfdi].[retencion]'),
  (N'dbo', N'Retenciones', N'cfdi', N'Retenciones', N'[cfdi].[Retenciones]'),
  (N'dbo', N'TimbreFiscalDigital', N'cfdi', N'TimbreFiscalDigital', N'[cfdi].[TimbreFiscalDigital]'),
  (N'dbo', N'traslado', N'cfdi', N'traslado', N'[cfdi].[traslado]'),
  (N'dbo', N'Traslados', N'cfdi', N'Traslados', N'[cfdi].[Traslados]');

DECLARE @ExpectedModules table
(
  ModuleGroup varchar(16) NOT NULL,
  ModuleSchema sysname NOT NULL,
  ModuleName sysname NOT NULL,
  TypeCode char(2) NOT NULL,
  DefinitionHash varbinary(32) NOT NULL,
  ExpectedExecuteAsPrincipalId int NULL,
  ExpectedSchemaBound bit NOT NULL,
  PRIMARY KEY (ModuleSchema, ModuleName)
);

INSERT @ExpectedModules
  (ModuleGroup, ModuleSchema, ModuleName, TypeCode, DefinitionHash,
   ExpectedExecuteAsPrincipalId, ExpectedSchemaBound)
VALUES
  ('DIAGRAM', N'dbo', N'fn_diagramobjects', 'FN',
   0x5726CE583B4510598A5C21655C2E9859603C2E3B9E9FD1ACC2376A64AC164FD2, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_alterdiagram', 'P',
   0xD89F6643A33AD995760C4898690AE079E5C90F89FBF461259E6769D18FA5D46F, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_creatediagram', 'P',
   0x09D5CC50AC6A965A4BF341702FD1245BDA1C0918F0BA9471729709DD6DAB3B2A, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_dropdiagram', 'P',
   0x4C091BF12C7E19DF10F8BBD960AA3A2BAA387BA34CAB7D081D991F2E42AFEFB5, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_helpdiagramdefinition', 'P',
   0x4EA70BFB2A6B4D09068884B989808A747EDE8F39B300A82EEA1B8B2CD5D95A9C, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_helpdiagrams', 'P',
   0xAA90ACB5710987905D00878C43412675718521A32567C737B486362DF210BE88, 1, 0),
  ('DIAGRAM', N'dbo', N'sp_renamediagram', 'P',
   0x303F2F058FA24B66B4189076AD61281059B89D0E781626E6BB8E1B0FD3E0B593, 1, 0),

  ('DEPENDENCY', N'bancos', N'Procesar_Movimientos_AmericanExpress', 'P',
   0xD9C8F17F16DD1761D7695C53137EDD97F831CBCFF0AA28C970A7EB4A8AB63503, NULL, 0),
  ('DEPENDENCY', N'bancos', N'Procesar_Movimientos_BBVA', 'P',
   0xEA6ECD5DB3E22CD82FBA2D75089E75419C6C54C50BDA7C9E5A5E6A6D9718BF69, NULL, 0),
  ('DEPENDENCY', N'bancos', N'Procesar_Movimientos_SchoolsFirst', 'P',
   0x3AF36677CFECDB0940951D99EFBA1604408894321452D68CCEAE726417DC8FBE, NULL, 0),
  ('DEPENDENCY', N'cfdi', N'PROCESAR_SAT_XML_V2', 'P',
   0xFD9174CDBA937A19D810FB378D24271A8E1CA21A79DF778F3FF3BEA926DD65E6, NULL, 0),
  ('DEPENDENCY', N'dbo', N'BBVA_Parse_ToRows', 'P',
   0x791F619833D79647906D40B7F0C18DEFEF4B96E1E9747515E08A2E8C82E39E0A, NULL, 0),
  ('DEPENDENCY', N'dbo', N'Importar_Movimientos_BBVA', 'P',
   0x38B80FFCB7C7A2BE8B5ED0B220FD406FD85956C1242E2505D3F51AA5E6AE2EB5, NULL, 0),
  ('DEPENDENCY', N'dbo', N'UPDATE_INVENTORY_FROM_REMISION', 'P',
   0xCB84A46AA3F82101055CAB282775330FD8DA0C7EC0BA443BCD1A7A2A70AC552F, NULL, 0);

DECLARE @ExpectedDependencies table
(
  ModuleSchema sysname NOT NULL,
  ModuleName sysname NOT NULL,
  ReferencedServer sysname NULL,
  ReferencedDatabase sysname NULL,
  ReferencedSchema sysname NULL,
  ReferencedEntity sysname NULL,
  ReferencedClass tinyint NOT NULL,
  IsSchemaBoundReference bit NOT NULL,
  IsAmbiguous bit NOT NULL
);

INSERT @ExpectedDependencies
  (ModuleSchema, ModuleName, ReferencedServer, ReferencedDatabase,
   ReferencedSchema, ReferencedEntity, ReferencedClass,
   IsSchemaBoundReference, IsAmbiguous)
VALUES
  (N'bancos', N'Procesar_Movimientos_AmericanExpress', NULL, N'T', N'N', N'value', 1, 0, 1),
  (N'bancos', N'Procesar_Movimientos_BBVA', NULL, N'T', N'C1', N'value', 1, 0, 1),
  (N'bancos', N'Procesar_Movimientos_SchoolsFirst', NULL, N'T', N'N', N'value', 1, 0, 1),
  (N'cfdi', N'PROCESAR_SAT_XML_V2', NULL, N'R', N'X', N'value', 1, 0, 1),
  (N'cfdi', N'PROCESAR_SAT_XML_V2', NULL, N'T', N'X', N'value', 1, 0, 1),
  (N'dbo', N'BBVA_Parse_ToRows', NULL, N'T', N'C1', N'value', 1, 0, 1),
  (N'dbo', N'Importar_Movimientos_BBVA', NULL, N'T', N'C1', N'value', 1, 0, 1),
  (N'dbo', N'UPDATE_INVENTORY_FROM_REMISION', N'Desktop-qga22ta\sqlexpress', N'timbralofacil', N'dbo', N'remision', 1, 0, 0),
  (N'dbo', N'UPDATE_INVENTORY_FROM_REMISION', N'Desktop-qga22ta\sqlexpress', N'timbralofacil', N'dbo', N'remisiondetalle', 1, 0, 0);

DECLARE @ExpectedRlsTargets table
(
  TargetSchema sysname NOT NULL,
  TargetTable sysname NOT NULL,
  PRIMARY KEY (TargetSchema, TargetTable)
);

INSERT @ExpectedRlsTargets (TargetSchema, TargetTable)
VALUES
  (N'dbo', N'BusinessPartnerRfcScope'),
  (N'fidelidad', N'MemberAccount'),
  (N'fidelidad', N'MemberClosureRequest'),
  (N'fidelidad', N'MemberConsent'),
  (N'fidelidad', N'MemberQrToken'),
  (N'fidelidad', N'PointLedger'),
  (N'fidelidad', N'ProgramSettings'),
  (N'logistica', N'BomComponent'),
  (N'logistica', N'BomHeader'),
  (N'logistica', N'BomVersion'),
  (N'logistica', N'InventoryAdjustment'),
  (N'logistica', N'InventoryAdjustmentLine'),
  (N'logistica', N'InventoryReservation'),
  (N'logistica', N'InventoryReservationLine'),
  (N'logistica', N'InventoryTransfer'),
  (N'logistica', N'InventoryTransferLine'),
  (N'logistica', N'Location'),
  (N'logistica', N'LocationMaterialAttachment'),
  (N'logistica', N'LotBalance'),
  (N'logistica', N'Material'),
  (N'logistica', N'MaterialAllergen'),
  (N'logistica', N'MaterialCategory'),
  (N'logistica', N'MaterialLot'),
  (N'logistica', N'MaterialUnitConversion'),
  (N'logistica', N'PhysicalCountAttachment'),
  (N'logistica', N'PhysicalCountLine'),
  (N'logistica', N'PhysicalCountLotLine'),
  (N'logistica', N'PhysicalCountRecountPlan'),
  (N'logistica', N'PhysicalCountRecountPlanLine'),
  (N'logistica', N'PhysicalCountSession'),
  (N'logistica', N'ProductionOrder'),
  (N'logistica', N'PurchaseOrder'),
  (N'logistica', N'PurchaseOrderLine'),
  (N'logistica', N'PurchaseOrderLineAllocation'),
  (N'logistica', N'PurchaseOrderRoomScope'),
  (N'logistica', N'PurchaseReceipt'),
  (N'logistica', N'PurchaseReceiptLine'),
  (N'logistica', N'Recipe'),
  (N'logistica', N'RecipeStep'),
  (N'logistica', N'StockBalance'),
  (N'logistica', N'StockTransaction'),
  (N'logistica', N'VendorProfile'),
  (N'restaurante', N'AccountingConfiguration'),
  (N'restaurante', N'AccountingLink'),
  (N'restaurante', N'AccountingOrderLink'),
  (N'restaurante', N'CashMovement'),
  (N'restaurante', N'CashRegister'),
  (N'restaurante', N'CashShift'),
  (N'restaurante', N'DailySequence'),
  (N'restaurante', N'Delivery'),
  (N'restaurante', N'DiningTable'),
  (N'restaurante', N'EventOutbox'),
  (N'restaurante', N'ExternalProvider'),
  (N'restaurante', N'KitchenStation'),
  (N'restaurante', N'Menu'),
  (N'restaurante', N'MenuItem'),
  (N'restaurante', N'MenuSchedule'),
  (N'restaurante', N'MenuSection'),
  (N'restaurante', N'ModifierGroup'),
  (N'restaurante', N'ModifierIngredientDelta'),
  (N'restaurante', N'ModifierOption'),
  (N'restaurante', N'Order'),
  (N'restaurante', N'OrderEvent'),
  (N'restaurante', N'OrderLine'),
  (N'restaurante', N'OrderLineModifier'),
  (N'restaurante', N'OrderLinePromotion'),
  (N'restaurante', N'OrderPromotion'),
  (N'restaurante', N'Payment'),
  (N'restaurante', N'PaymentRefund'),
  (N'restaurante', N'Product'),
  (N'restaurante', N'ProductCard'),
  (N'restaurante', N'ProductDietaryTag'),
  (N'restaurante', N'ProductModifierGroup'),
  (N'restaurante', N'Promotion'),
  (N'restaurante', N'PromotionCode'),
  (N'restaurante', N'PromotionMaterialCategory'),
  (N'restaurante', N'PromotionProduct'),
  (N'restaurante', N'PromotionRedemption'),
  (N'restaurante', N'PromotionSchedule'),
  (N'restaurante', N'ProviderSettlement'),
  (N'restaurante', N'ProviderSettlementOrder'),
  (N'restaurante', N'PublicSiteSettings'),
  (N'restaurante', N'QuickPin'),
  (N'restaurante', N'QuickPinAttempt'),
  (N'restaurante', N'Site'),
  (N'restaurante', N'SiteLocationPriority'),
  (N'restaurante', N'SupervisorAuthorization');

DECLARE @ReviewedTriggers table
(
  TriggerSchema sysname NOT NULL,
  TriggerName sysname NOT NULL,
  ParentSchema sysname NOT NULL,
  ParentTable sysname NOT NULL,
  PRIMARY KEY (TriggerSchema, TriggerName)
);

INSERT @ReviewedTriggers (TriggerSchema, TriggerName, ParentSchema, ParentTable)
VALUES
  (N'dbo', N'trg_Transacciones_Audit', N'dbo', N'Transacciones'),
  (N'dbo', N'trg_Registro_Contable_Audit', N'dbo', N'Registro_Contable'),
  (N'dbo', N'TR_Transaccion_Comprobante_BlockPago20Direct', N'dbo', N'Transaccion_Comprobante'),
  (N'capacitacion', N'TR_EntornoSeguridad_MaintenanceOnly', N'capacitacion', N'EntornoSeguridad'),
  (N'capacitacion', N'TR_EsquemaVersion_MaintenanceOnly', N'capacitacion', N'EsquemaVersion'),
  (N'capacitacion', N'TR_Finalizacion_AppendOnly', N'capacitacion', N'Finalizacion'),
  (N'capacitacion', N'TR_EventoAuditoria_AppendOnly', N'capacitacion', N'EventoAuditoria'),
  (N'capacitacion', N'TR_CursoVersion_PublicadaInmutable', N'capacitacion', N'CursoVersion'),
  (N'capacitacion', N'TR_FirmaInstructor_AppendOnly', N'capacitacion', N'FirmaInstructor'),
  (N'capacitacion', N'TR_Leccion_VersionPublicadaInmutable', N'capacitacion', N'Leccion'),
  (N'capacitacion', N'TR_BloqueContenido_VersionPublicadaInmutable', N'capacitacion', N'BloqueContenido'),
  (N'capacitacion', N'TR_Recurso_VersionPublicadaInmutable', N'capacitacion', N'Recurso'),
  (N'capacitacion', N'TR_Evaluacion_VersionPublicadaInmutable', N'capacitacion', N'Evaluacion'),
  (N'capacitacion', N'TR_Pregunta_VersionPublicadaInmutable', N'capacitacion', N'Pregunta'),
  (N'capacitacion', N'TR_OpcionPregunta_VersionPublicadaInmutable', N'capacitacion', N'OpcionPregunta'),
  (N'capacitacion', N'TR_Practica_VersionPublicadaInmutable', N'capacitacion', N'Practica'),
  (N'capacitacion', N'TR_PracticaPaso_VersionPublicadaInmutable', N'capacitacion', N'PracticaPaso');

BEGIN TRY
  BEGIN TRANSACTION;

  -- Synonyms are a reviewed, local compatibility layer. Preserve only an exact
  -- subset of the manifest and require every referenced target to be a local
  -- user table. A missing synonym is accepted for recovery.
  IF EXISTS
  (
    SELECT 1
    FROM sys.synonyms synonymInfo
    JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
    LEFT JOIN @ExpectedSynonyms expected
      ON expected.SynonymSchema = synonymSchema.name
     AND expected.SynonymName = synonymInfo.name
    WHERE expected.SynonymName IS NULL
       OR synonymSchema.name COLLATE Latin1_General_100_BIN2
            <> expected.SynonymSchema COLLATE Latin1_General_100_BIN2
       OR synonymInfo.name COLLATE Latin1_General_100_BIN2
            <> expected.SynonymName COLLATE Latin1_General_100_BIN2
       OR synonymInfo.base_object_name COLLATE Latin1_General_100_BIN2
            <> expected.BaseObjectName COLLATE Latin1_General_100_BIN2
       OR PARSENAME(synonymInfo.base_object_name, 4) IS NOT NULL
       OR PARSENAME(synonymInfo.base_object_name, 3) IS NOT NULL
       OR PARSENAME(synonymInfo.base_object_name, 2) COLLATE Latin1_General_100_BIN2
            <> expected.BaseSchema COLLATE Latin1_General_100_BIN2
       OR PARSENAME(synonymInfo.base_object_name, 1) COLLATE Latin1_General_100_BIN2
            <> expected.BaseObject COLLATE Latin1_General_100_BIN2
       OR
          (
            NOT
            (
              expected.SynonymSchema COLLATE Latin1_General_100_BIN2 = N'dbo'
              AND expected.SynonymName COLLATE Latin1_General_100_BIN2 = N'CatalogoCuentas'
            )
            AND OBJECT_ID(
                  QUOTENAME(expected.BaseSchema) + N'.' + QUOTENAME(expected.BaseObject),
                  N'U') IS NULL
          )
  )
    THROW 51883, 'TRAINING NEUTRALIZATION BLOCKED: the synonym manifest is not the exact reviewed local subset.', 1;

  DECLARE @CfdiParserObjectId int = OBJECT_ID(N'cfdi.PROCESAR_SAT_XML_V2');
  DECLARE @CfdiParserIsLegacy bit = 0;
  DECLARE @CfdiParserIsTraining bit = 0;

  IF @CfdiParserObjectId IS NOT NULL
  BEGIN
    IF EXISTS
    (
      SELECT 1
      FROM sys.objects objectInfo
      JOIN sys.schemas moduleSchema ON moduleSchema.schema_id = objectInfo.schema_id
      JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
      WHERE objectInfo.object_id = @CfdiParserObjectId
        AND moduleSchema.name COLLATE Latin1_General_100_BIN2 = N'cfdi'
        AND objectInfo.name COLLATE Latin1_General_100_BIN2 = N'PROCESAR_SAT_XML_V2'
        AND objectInfo.type = 'P'
        AND moduleInfo.definition IS NOT NULL
        AND moduleInfo.execute_as_principal_id IS NULL
        AND moduleInfo.is_schema_bound = 0
        AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
              = 0xFD9174CDBA937A19D810FB378D24271A8E1CA21A79DF778F3FF3BEA926DD65E6
    )
      SET @CfdiParserIsLegacy = 1;

    IF EXISTS
    (
      SELECT 1
      FROM sys.objects objectInfo
      JOIN sys.schemas moduleSchema ON moduleSchema.schema_id = objectInfo.schema_id
      JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
      WHERE objectInfo.object_id = @CfdiParserObjectId
        AND moduleSchema.name COLLATE Latin1_General_100_BIN2 = N'cfdi'
        AND objectInfo.name COLLATE Latin1_General_100_BIN2 = N'PROCESAR_SAT_XML_V2'
        AND objectInfo.type = 'P'
        AND moduleInfo.definition IS NOT NULL
        AND moduleInfo.definition LIKE
              N'%OrionERP.Training.CfdiFixtureParser.v1:6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B%'
        AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
        -- Hash of the definition as SQL Server STORES it, which is not the same
        -- text as the installer file: the body ships inside a dynamic-SQL literal
        -- (so escaped '' quotes collapse to ') and CREATE OR ALTER is recorded as
        -- CREATE followed by blanks. A hash taken over the raw file text can never
        -- match, which left this recovery branch unreachable and blocked every
        -- re-run once the Training parser had been installed.
              = 0x0078FB57089DE8378CA77BFF968AEF21A7D338D5C086FB61F8262998E16925A9
        AND moduleInfo.execute_as_principal_id IS NULL
        AND moduleInfo.is_schema_bound = 0
        AND (SELECT COUNT(*) FROM sys.parameters WHERE object_id = objectInfo.object_id) = 2
        AND EXISTS
            (
              SELECT 1
              FROM sys.parameters parameterInfo
              WHERE parameterInfo.object_id = objectInfo.object_id
                AND parameterInfo.parameter_id = 1
                AND parameterInfo.name COLLATE Latin1_General_100_BIN2 = N'@TransaccionID'
                AND parameterInfo.system_type_id = TYPE_ID(N'int')
                AND parameterInfo.user_type_id = TYPE_ID(N'int')
                AND parameterInfo.max_length = 4
                AND parameterInfo.precision = 10
                AND parameterInfo.scale = 0
                AND parameterInfo.is_output = 0
                AND parameterInfo.has_default_value = 0
                AND parameterInfo.default_value IS NULL
            )
        AND EXISTS
            (
              SELECT 1
              FROM sys.parameters parameterInfo
              WHERE parameterInfo.object_id = objectInfo.object_id
                AND parameterInfo.parameter_id = 2
                AND parameterInfo.name COLLATE Latin1_General_100_BIN2 = N'@AttachmentID'
                AND parameterInfo.system_type_id = TYPE_ID(N'int')
                AND parameterInfo.user_type_id = TYPE_ID(N'int')
                AND parameterInfo.max_length = 4
                AND parameterInfo.precision = 10
                AND parameterInfo.scale = 0
                AND parameterInfo.is_output = 0
                AND parameterInfo.has_default_value = 0
                AND parameterInfo.default_value IS NULL
            )
        AND NOT EXISTS
            (
              SELECT 1
              FROM sys.sql_expression_dependencies dependencyInfo
              WHERE dependencyInfo.referencing_id = objectInfo.object_id
                AND
                (
                  dependencyInfo.referenced_server_name IS NOT NULL
                  OR dependencyInfo.referenced_database_name IS NOT NULL
                )
            )
    )
      SET @CfdiParserIsTraining = 1;

    IF @CfdiParserIsLegacy = 0 AND @CfdiParserIsTraining = 0
      THROW 51903, 'TRAINING NEUTRALIZATION BLOCKED: cfdi.PROCESAR_SAT_XML_V2 is neither the reviewed legacy module nor the exact Training parser.', 1;
  END;

  -- Every present diagram or dependency-bearing module must be byte-for-byte
  -- the reviewed clone definition, with the exact module type and context.
  IF EXISTS
  (
    SELECT 1
    FROM @ExpectedModules expected
    JOIN sys.schemas moduleSchema ON moduleSchema.name = expected.ModuleSchema
    JOIN sys.objects objectInfo
      ON objectInfo.schema_id = moduleSchema.schema_id
     AND objectInfo.name = expected.ModuleName
    LEFT JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
    WHERE
      NOT
      (
        expected.ModuleSchema COLLATE Latin1_General_100_BIN2 = N'cfdi'
        AND expected.ModuleName COLLATE Latin1_General_100_BIN2 = N'PROCESAR_SAT_XML_V2'
        AND @CfdiParserIsTraining = 1
      )
      AND
      (
        moduleSchema.name COLLATE Latin1_General_100_BIN2
              <> expected.ModuleSchema COLLATE Latin1_General_100_BIN2
        OR objectInfo.name COLLATE Latin1_General_100_BIN2
              <> expected.ModuleName COLLATE Latin1_General_100_BIN2
        OR objectInfo.type COLLATE Latin1_General_100_BIN2
             <> expected.TypeCode COLLATE Latin1_General_100_BIN2
        OR moduleInfo.object_id IS NULL
        OR moduleInfo.definition IS NULL
        OR HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
              <> expected.DefinitionHash
        OR ISNULL(moduleInfo.execute_as_principal_id, -1)
              <> ISNULL(expected.ExpectedExecuteAsPrincipalId, -1)
        OR moduleInfo.is_schema_bound <> expected.ExpectedSchemaBound
      )
  )
    THROW 51884, 'TRAINING NEUTRALIZATION BLOCKED: a reviewed module name, type, execution context, or definition hash drifted.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.sql_modules moduleInfo
    JOIN sys.objects objectInfo ON objectInfo.object_id = moduleInfo.object_id
    JOIN sys.schemas moduleSchema ON moduleSchema.schema_id = objectInfo.schema_id
    LEFT JOIN @ExpectedModules expected
      ON expected.ModuleGroup = 'DIAGRAM'
     AND expected.ModuleSchema COLLATE Latin1_General_100_BIN2
           = moduleSchema.name COLLATE Latin1_General_100_BIN2
     AND expected.ModuleName COLLATE Latin1_General_100_BIN2
           = objectInfo.name COLLATE Latin1_General_100_BIN2
     AND expected.TypeCode COLLATE Latin1_General_100_BIN2
           = objectInfo.type COLLATE Latin1_General_100_BIN2
    WHERE moduleInfo.execute_as_principal_id IS NOT NULL
      AND expected.ModuleName IS NULL
  )
    THROW 51885, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed EXECUTE AS module exists.', 1;

  DECLARE @ActualDependencyCount int =
  (
    SELECT COUNT(*)
    FROM sys.sql_expression_dependencies dependencyInfo
    WHERE dependencyInfo.referenced_server_name IS NOT NULL
       OR dependencyInfo.referenced_database_name IS NOT NULL
  );
  DECLARE @ExpectedDependencyCount int =
  (
    SELECT COUNT(*)
    FROM @ExpectedDependencies expectedDependency
    WHERE EXISTS
    (
      SELECT 1
      FROM @ExpectedModules expectedModule
      JOIN sys.schemas moduleSchema
        ON moduleSchema.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleSchema COLLATE Latin1_General_100_BIN2
      JOIN sys.objects objectInfo
        ON objectInfo.schema_id = moduleSchema.schema_id
       AND objectInfo.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleName COLLATE Latin1_General_100_BIN2
      JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
      WHERE moduleSchema.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleSchema COLLATE Latin1_General_100_BIN2
        AND objectInfo.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleName COLLATE Latin1_General_100_BIN2
        AND objectInfo.type = 'P'
        AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
              = expectedModule.DefinitionHash
    )
  );

  IF @ActualDependencyCount <> @ExpectedDependencyCount
    THROW 51886, 'TRAINING NEUTRALIZATION BLOCKED: the cross-database dependency row count drifted.', 1;

  IF EXISTS
  (
    SELECT
      expectedDependency.ModuleSchema COLLATE Latin1_General_100_BIN2,
      expectedDependency.ModuleName COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedServer COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedDatabase COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedSchema COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedEntity COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedClass,
      expectedDependency.IsSchemaBoundReference,
      expectedDependency.IsAmbiguous
    FROM @ExpectedDependencies expectedDependency
    WHERE EXISTS
    (
      SELECT 1
      FROM @ExpectedModules expectedModule
      JOIN sys.schemas moduleSchema
        ON moduleSchema.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleSchema COLLATE Latin1_General_100_BIN2
      JOIN sys.objects objectInfo
        ON objectInfo.schema_id = moduleSchema.schema_id
       AND objectInfo.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleName COLLATE Latin1_General_100_BIN2
      JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
      WHERE moduleSchema.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleSchema COLLATE Latin1_General_100_BIN2
        AND objectInfo.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleName COLLATE Latin1_General_100_BIN2
        AND objectInfo.type = 'P'
        AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
              = expectedModule.DefinitionHash
    )
    EXCEPT
    SELECT
      moduleSchema.name COLLATE Latin1_General_100_BIN2,
      objectInfo.name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_server_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_database_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_schema_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_entity_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_class,
      dependencyInfo.is_schema_bound_reference,
      dependencyInfo.is_ambiguous
    FROM sys.sql_expression_dependencies dependencyInfo
    JOIN sys.objects objectInfo ON objectInfo.object_id = dependencyInfo.referencing_id
    JOIN sys.schemas moduleSchema ON moduleSchema.schema_id = objectInfo.schema_id
    WHERE dependencyInfo.referenced_server_name IS NOT NULL
       OR dependencyInfo.referenced_database_name IS NOT NULL
  )
    THROW 51887, 'TRAINING NEUTRALIZATION BLOCKED: an expected dependency row is missing or changed.', 1;

  IF EXISTS
  (
    SELECT
      moduleSchema.name COLLATE Latin1_General_100_BIN2,
      objectInfo.name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_server_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_database_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_schema_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_entity_name COLLATE Latin1_General_100_BIN2,
      dependencyInfo.referenced_class,
      dependencyInfo.is_schema_bound_reference,
      dependencyInfo.is_ambiguous
    FROM sys.sql_expression_dependencies dependencyInfo
    JOIN sys.objects objectInfo ON objectInfo.object_id = dependencyInfo.referencing_id
    JOIN sys.schemas moduleSchema ON moduleSchema.schema_id = objectInfo.schema_id
    WHERE dependencyInfo.referenced_server_name IS NOT NULL
       OR dependencyInfo.referenced_database_name IS NOT NULL
    EXCEPT
    SELECT
      expectedDependency.ModuleSchema COLLATE Latin1_General_100_BIN2,
      expectedDependency.ModuleName COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedServer COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedDatabase COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedSchema COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedEntity COLLATE Latin1_General_100_BIN2,
      expectedDependency.ReferencedClass,
      expectedDependency.IsSchemaBoundReference,
      expectedDependency.IsAmbiguous
    FROM @ExpectedDependencies expectedDependency
    WHERE EXISTS
    (
      SELECT 1
      FROM @ExpectedModules expectedModule
      JOIN sys.schemas moduleSchema
        ON moduleSchema.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleSchema COLLATE Latin1_General_100_BIN2
      JOIN sys.objects objectInfo
        ON objectInfo.schema_id = moduleSchema.schema_id
       AND objectInfo.name COLLATE Latin1_General_100_BIN2
             = expectedModule.ModuleName COLLATE Latin1_General_100_BIN2
      JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = objectInfo.object_id
      WHERE moduleSchema.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleSchema COLLATE Latin1_General_100_BIN2
        AND objectInfo.name COLLATE Latin1_General_100_BIN2
              = expectedDependency.ModuleName COLLATE Latin1_General_100_BIN2
        AND objectInfo.type = 'P'
        AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
              = expectedModule.DefinitionHash
    )
  )
    THROW 51888, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed cross-database dependency exists.', 1;

  -- Permit no database DDL trigger. DML triggers must be either in the reviewed
  -- Training list or the one exact legacy trigger removed by this batch.
  IF EXISTS
  (
    SELECT 1
    FROM sys.triggers triggerInfo
    WHERE triggerInfo.parent_class = 0
      AND triggerInfo.is_ms_shipped = 0
  )
    THROW 51889, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed database DDL trigger exists.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.triggers triggerInfo
    JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id
    JOIN sys.objects parentInfo ON parentInfo.object_id = triggerInfo.parent_id
    JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id
    JOIN sys.schemas parentSchema ON parentSchema.schema_id = parentInfo.schema_id
    LEFT JOIN @ReviewedTriggers reviewed
      ON reviewed.TriggerSchema COLLATE Latin1_General_100_BIN2
           = triggerSchema.name COLLATE Latin1_General_100_BIN2
     AND reviewed.TriggerName COLLATE Latin1_General_100_BIN2
           = triggerInfo.name COLLATE Latin1_General_100_BIN2
     AND reviewed.ParentSchema COLLATE Latin1_General_100_BIN2
           = parentSchema.name COLLATE Latin1_General_100_BIN2
     AND reviewed.ParentTable COLLATE Latin1_General_100_BIN2
           = parentInfo.name COLLATE Latin1_General_100_BIN2
    WHERE triggerInfo.parent_class = 1
      AND triggerInfo.is_ms_shipped = 0
      AND reviewed.TriggerName IS NULL
      AND NOT
      (
        triggerSchema.name COLLATE Latin1_General_100_BIN2 = N'dbo'
        AND triggerInfo.name COLLATE Latin1_General_100_BIN2 = N'tr_TriggerName'
        AND parentSchema.name COLLATE Latin1_General_100_BIN2 = N'dbo'
        AND parentInfo.name COLLATE Latin1_General_100_BIN2 = N'MY_TRIGGERS'
      )
  )
    THROW 51890, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed DML trigger exists.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.triggers triggerInfo
    JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id
    JOIN sys.objects parentInfo ON parentInfo.object_id = triggerInfo.parent_id
    JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id
    JOIN sys.schemas parentSchema ON parentSchema.schema_id = parentInfo.schema_id
    LEFT JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = triggerInfo.object_id
    WHERE triggerSchema.name = N'dbo'
      AND triggerInfo.name = N'tr_TriggerName'
      AND
      (
        triggerSchema.name COLLATE Latin1_General_100_BIN2 <> N'dbo'
        OR triggerInfo.name COLLATE Latin1_General_100_BIN2 <> N'tr_TriggerName'
        OR parentSchema.name COLLATE Latin1_General_100_BIN2 <> N'dbo'
        OR parentInfo.name COLLATE Latin1_General_100_BIN2 <> N'MY_TRIGGERS'
        OR triggerObject.type <> 'TR'
        OR triggerInfo.parent_class <> 1
        OR triggerInfo.is_instead_of_trigger <> 0
        OR triggerInfo.is_not_for_replication <> 0
        OR moduleInfo.object_id IS NULL
        OR moduleInfo.definition IS NULL
        OR moduleInfo.execute_as_principal_id IS NOT NULL
        OR HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
             <> 0x7F8EEA45D2446B144BB477A244E6B266C261FB5718DC2766ED0B452B7AC717D9
      )
  )
    THROW 51891, 'TRAINING NEUTRALIZATION BLOCKED: the legacy DML trigger definition or parent drifted.', 1;

  DECLARE @RlsFunctionId int = OBJECT_ID(N'logistica.fn_RfcAccessPredicate');
  IF @RlsFunctionId IS NOT NULL
     AND NOT EXISTS
     (
       SELECT 1
       FROM sys.objects functionInfo
       JOIN sys.schemas functionSchema ON functionSchema.schema_id = functionInfo.schema_id
       JOIN sys.sql_modules moduleInfo ON moduleInfo.object_id = functionInfo.object_id
       WHERE functionInfo.object_id = @RlsFunctionId
         AND functionSchema.name COLLATE Latin1_General_100_BIN2 = N'logistica'
         AND functionInfo.name COLLATE Latin1_General_100_BIN2 = N'fn_RfcAccessPredicate'
         AND functionInfo.type = 'IF'
         AND moduleInfo.is_schema_bound = 1
         AND moduleInfo.execute_as_principal_id IS NULL
         AND moduleInfo.definition IS NOT NULL
         AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), moduleInfo.definition))
               = 0x4BC1AA2DC6BB596FDA7D828646F2A60C389FBAE15E3952D8AA8665EFAE6EE36D
     )
    THROW 51892, 'TRAINING NEUTRALIZATION BLOCKED: the RLS predicate function type or definition hash drifted.', 1;

  DECLARE @RlsPolicyId int =
  (
    SELECT policyInfo.object_id
    FROM sys.security_policies policyInfo
    JOIN sys.schemas policySchema ON policySchema.schema_id = policyInfo.schema_id
    WHERE policySchema.name COLLATE Latin1_General_100_BIN2 = N'logistica'
      AND policyInfo.name COLLATE Latin1_General_100_BIN2 = N'RfcSecurityPolicy'
  );

  IF EXISTS
  (
    SELECT 1
    FROM sys.security_policies policyInfo
    JOIN sys.schemas policySchema ON policySchema.schema_id = policyInfo.schema_id
    WHERE policySchema.name COLLATE Latin1_General_100_BIN2 <> N'logistica'
       OR policyInfo.name COLLATE Latin1_General_100_BIN2 <> N'RfcSecurityPolicy'
  )
    THROW 51893, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed row-level security policy exists.', 1;

  IF @RlsPolicyId IS NOT NULL
  BEGIN
    IF @RlsFunctionId IS NULL
      THROW 51894, 'TRAINING NEUTRALIZATION BLOCKED: the reviewed RLS policy has no reviewed predicate function.', 1;

    IF NOT EXISTS
    (
      SELECT 1
      FROM sys.security_policies policyInfo
      WHERE policyInfo.object_id = @RlsPolicyId
        AND policyInfo.is_enabled = 1
        AND policyInfo.is_schema_bound = 1
    )
      THROW 51895, 'TRAINING NEUTRALIZATION BLOCKED: the reviewed RLS policy state drifted.', 1;

    IF (SELECT COUNT(*) FROM @ExpectedRlsTargets) <> 87
       OR (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id = @RlsPolicyId) <> 261
       OR (SELECT COUNT(DISTINCT target_object_id) FROM sys.security_predicates WHERE object_id = @RlsPolicyId) <> 87
      THROW 51896, 'TRAINING NEUTRALIZATION BLOCKED: the RLS target or predicate count drifted.', 1;

    IF EXISTS
    (
      SELECT 1
      FROM @ExpectedRlsTargets expectedTarget
      LEFT JOIN sys.schemas targetSchema
        ON targetSchema.name COLLATE Latin1_General_100_BIN2
             = expectedTarget.TargetSchema COLLATE Latin1_General_100_BIN2
      LEFT JOIN sys.tables targetTable
        ON targetTable.schema_id = targetSchema.schema_id
       AND targetTable.name COLLATE Latin1_General_100_BIN2
             = expectedTarget.TargetTable COLLATE Latin1_General_100_BIN2
      WHERE targetTable.object_id IS NULL
         OR
         (
           SELECT COUNT(*)
           FROM sys.security_predicates predicateInfo
           WHERE predicateInfo.object_id = @RlsPolicyId
             AND predicateInfo.target_object_id = targetTable.object_id
         ) <> 3
         OR
         (
           SELECT COUNT(*)
           FROM sys.security_predicates predicateInfo
           WHERE predicateInfo.object_id = @RlsPolicyId
             AND predicateInfo.target_object_id = targetTable.object_id
             AND predicateInfo.predicate_definition COLLATE Latin1_General_100_BIN2
                   = N'([logistica].[fn_RfcAccessPredicate]([Rfc]))'
                   COLLATE Latin1_General_100_BIN2
             AND predicateInfo.predicate_type = 0
             AND predicateInfo.operation IS NULL
         ) <> 1
         OR
         (
           SELECT COUNT(*)
           FROM sys.security_predicates predicateInfo
           WHERE predicateInfo.object_id = @RlsPolicyId
             AND predicateInfo.target_object_id = targetTable.object_id
             AND predicateInfo.predicate_definition COLLATE Latin1_General_100_BIN2
                   = N'([logistica].[fn_RfcAccessPredicate]([Rfc]))'
                   COLLATE Latin1_General_100_BIN2
             AND predicateInfo.predicate_type = 1
             AND predicateInfo.operation = 1
         ) <> 1
         OR
         (
           SELECT COUNT(*)
           FROM sys.security_predicates predicateInfo
           WHERE predicateInfo.object_id = @RlsPolicyId
             AND predicateInfo.target_object_id = targetTable.object_id
             AND predicateInfo.predicate_definition COLLATE Latin1_General_100_BIN2
                   = N'([logistica].[fn_RfcAccessPredicate]([Rfc]))'
                   COLLATE Latin1_General_100_BIN2
             AND predicateInfo.predicate_type = 1
             AND predicateInfo.operation = 2
         ) <> 1
    )
      THROW 51897, 'TRAINING NEUTRALIZATION BLOCKED: an expected RLS target or predicate triple drifted.', 1;

    IF EXISTS
    (
      SELECT 1
      FROM sys.security_predicates predicateInfo
      JOIN sys.tables targetTable ON targetTable.object_id = predicateInfo.target_object_id
      JOIN sys.schemas targetSchema ON targetSchema.schema_id = targetTable.schema_id
      LEFT JOIN @ExpectedRlsTargets expectedTarget
        ON expectedTarget.TargetSchema COLLATE Latin1_General_100_BIN2
             = targetSchema.name COLLATE Latin1_General_100_BIN2
       AND expectedTarget.TargetTable COLLATE Latin1_General_100_BIN2
             = targetTable.name COLLATE Latin1_General_100_BIN2
      WHERE predicateInfo.object_id = @RlsPolicyId
        AND expectedTarget.TargetTable IS NULL
    )
      THROW 51898, 'TRAINING NEUTRALIZATION BLOCKED: the RLS policy contains an unreviewed target.', 1;
  END;

  -- Drop only after every present unsafe artifact has passed the complete
  -- manifest. DDL is transactional in SQL Server, so any failure rolls back all
  -- drops performed by this batch.
  IF @RlsPolicyId IS NOT NULL
    DROP SECURITY POLICY [logistica].[RfcSecurityPolicy];

  IF OBJECT_ID(N'logistica.fn_RfcAccessPredicate', N'IF') IS NOT NULL
    DROP FUNCTION [logistica].[fn_RfcAccessPredicate];

  IF OBJECT_ID(N'dbo.sp_alterdiagram', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_alterdiagram];
  IF OBJECT_ID(N'dbo.sp_creatediagram', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_creatediagram];
  IF OBJECT_ID(N'dbo.sp_dropdiagram', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_dropdiagram];
  IF OBJECT_ID(N'dbo.sp_helpdiagramdefinition', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_helpdiagramdefinition];
  IF OBJECT_ID(N'dbo.sp_helpdiagrams', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_helpdiagrams];
  IF OBJECT_ID(N'dbo.sp_renamediagram', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_renamediagram];
  IF OBJECT_ID(N'dbo.fn_diagramobjects', N'FN') IS NOT NULL
    DROP FUNCTION [dbo].[fn_diagramobjects];

  IF OBJECT_ID(N'bancos.Procesar_Movimientos_AmericanExpress', N'P') IS NOT NULL
    DROP PROCEDURE [bancos].[Procesar_Movimientos_AmericanExpress];
  IF OBJECT_ID(N'bancos.Procesar_Movimientos_BBVA', N'P') IS NOT NULL
    DROP PROCEDURE [bancos].[Procesar_Movimientos_BBVA];
  IF OBJECT_ID(N'bancos.Procesar_Movimientos_SchoolsFirst', N'P') IS NOT NULL
    DROP PROCEDURE [bancos].[Procesar_Movimientos_SchoolsFirst];
  IF @CfdiParserIsLegacy = 1
    DROP PROCEDURE [cfdi].[PROCESAR_SAT_XML_V2];
  IF OBJECT_ID(N'dbo.Importar_Movimientos_BBVA', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[Importar_Movimientos_BBVA];
  IF OBJECT_ID(N'dbo.BBVA_Parse_ToRows', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[BBVA_Parse_ToRows];
  IF OBJECT_ID(N'dbo.UPDATE_INVENTORY_FROM_REMISION', N'P') IS NOT NULL
    DROP PROCEDURE [dbo].[UPDATE_INVENTORY_FROM_REMISION];

  IF OBJECT_ID(N'dbo.tr_TriggerName', N'TR') IS NOT NULL
    DROP TRIGGER [dbo].[tr_TriggerName];

  -- This exact legacy synonym points to a table that does not exist and has no
  -- reviewed callers. The twelve working local CFDI synonyms remain intact.
  IF EXISTS
  (
    SELECT 1
    FROM sys.synonyms synonymInfo
    JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
    WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = N'dbo'
      AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = N'CatalogoCuentas'
  )
    DROP SYNONYM [dbo].[CatalogoCuentas];

  IF EXISTS (SELECT 1 FROM sys.security_policies)
     OR EXISTS (SELECT 1 FROM sys.security_predicates)
    THROW 51899, 'TRAINING NEUTRALIZATION BLOCKED: row-level security survived the guarded drop.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.sql_modules
    WHERE execute_as_principal_id IS NOT NULL
  )
    THROW 51900, 'TRAINING NEUTRALIZATION BLOCKED: an EXECUTE AS module survived the guarded drop.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.sql_expression_dependencies
    WHERE referenced_server_name IS NOT NULL
       OR referenced_database_name IS NOT NULL
  )
    THROW 51901, 'TRAINING NEUTRALIZATION BLOCKED: a cross-database dependency survived the guarded drop.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.triggers triggerInfo
    JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id
    JOIN sys.objects parentInfo ON parentInfo.object_id = triggerInfo.parent_id
    JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id
    JOIN sys.schemas parentSchema ON parentSchema.schema_id = parentInfo.schema_id
    LEFT JOIN @ReviewedTriggers reviewed
      ON reviewed.TriggerSchema COLLATE Latin1_General_100_BIN2
           = triggerSchema.name COLLATE Latin1_General_100_BIN2
     AND reviewed.TriggerName COLLATE Latin1_General_100_BIN2
           = triggerInfo.name COLLATE Latin1_General_100_BIN2
     AND reviewed.ParentSchema COLLATE Latin1_General_100_BIN2
           = parentSchema.name COLLATE Latin1_General_100_BIN2
     AND reviewed.ParentTable COLLATE Latin1_General_100_BIN2
           = parentInfo.name COLLATE Latin1_General_100_BIN2
    WHERE triggerInfo.parent_class = 1
      AND triggerInfo.is_ms_shipped = 0
      AND reviewed.TriggerName IS NULL
  )
    THROW 51902, 'TRAINING NEUTRALIZATION BLOCKED: an unreviewed DML trigger survived the guarded drop.', 1;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW;
END CATCH;

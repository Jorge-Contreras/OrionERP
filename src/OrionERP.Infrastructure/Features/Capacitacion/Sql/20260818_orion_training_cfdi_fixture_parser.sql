/*
  OrionERP.Training.CfdiFixtureParser.v1

  Installs the deliberately narrow CFDI parser used only by Orion_Training.
  It accepts one byte-exact, visibly fictional fixture and never stamps, calls
  an external service, or resolves an object outside the active catalog.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51850, 'TRAINING CFDI INSTALL BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51851, 'TRAINING CFDI INSTALL BLOCKED: the guarded sanitizer session did not authorize this batch.', 1;

IF OBJECT_ID(N'dbo.TRANSACTION_ATTACHMENT', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transaccion_Comprobante', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Comprobante', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.TimbreFiscalDigital', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Emisor', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Receptor', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.InformacionGlobal', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Conceptos', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Concepto', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Impuestos', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Traslados', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Traslado', N'U') IS NULL
  THROW 51852, 'TRAINING CFDI INSTALL BLOCKED: a required local table is missing.', 1;

IF COL_LENGTH(N'dbo.TRANSACTION_ATTACHMENT', N'Attachment') IS NULL
   OR COL_LENGTH(N'dbo.TRANSACTION_ATTACHMENT', N'TranID') IS NULL
   OR COL_LENGTH(N'dbo.Transacciones', N'RFC') IS NULL
   OR COL_LENGTH(N'cfdi.Comprobante', N'XML_Attachment_ID') IS NULL
   OR COL_LENGTH(N'cfdi.Comprobante', N'Estatus') IS NULL
   OR COL_LENGTH(N'cfdi.TimbreFiscalDigital', N'UUID') IS NULL
   OR COL_LENGTH(N'cfdi.Concepto', N'Conceptos_Id') IS NULL
   OR COL_LENGTH(N'cfdi.Impuestos', N'Concepto_Id') IS NULL
   OR COL_LENGTH(N'cfdi.Impuestos', N'Comprobante_Id') IS NULL
   OR COL_LENGTH(N'dbo.Transaccion_Comprobante', N'Monto') IS NULL
  THROW 51853, 'TRAINING CFDI INSTALL BLOCKED: the reviewed local schema shape is missing.', 1;
DECLARE @TrainingParserDefinition nvarchar(max) = N'CREATE OR ALTER PROCEDURE [cfdi].[PROCESAR_SAT_XML_V2]
  @TransaccionID int = NULL,
  @AttachmentID int
AS
BEGIN
  /* OrionERP.Training.CfdiFixtureParser.v1:6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B */
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N''Orion_Training'' COLLATE Latin1_General_100_BIN2
    THROW 51860, ''CFDI DE CAPACITACIÓN BLOQUEADO: esta rutina sólo existe en Orion_Training.'', 1;

  DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
  IF @OwnTransaction = 1
    BEGIN TRANSACTION;
  ELSE
    SAVE TRANSACTION TrainingCfdiFixture;

  BEGIN TRY
    DECLARE @Attachment varbinary(max);
    DECLARE @AttachmentTranId int;
    DECLARE @AttachmentFound bit = 0;

    SELECT
      @Attachment = attachmentInfo.Attachment,
      @AttachmentTranId = attachmentInfo.TranID,
      @AttachmentFound = 1
    FROM dbo.TRANSACTION_ATTACHMENT attachmentInfo WITH (UPDLOCK, HOLDLOCK)
    WHERE attachmentInfo.ID = @AttachmentID;

    IF @AttachmentFound = 0 OR @Attachment IS NULL
      THROW 51861, ''CFDI DE CAPACITACIÓN RECHAZADO: no existe el adjunto XML indicado.'', 1;

    IF (@TransaccionID IS NULL AND @AttachmentTranId IS NOT NULL)
       OR (@TransaccionID IS NOT NULL AND ISNULL(@AttachmentTranId, -1) <> @TransaccionID)
      THROW 51862, ''CFDI DE CAPACITACIÓN RECHAZADO: la transacción no coincide con el adjunto.'', 1;

    IF @TransaccionID IS NOT NULL
       AND NOT EXISTS
       (
         SELECT 1
         FROM dbo.Transacciones transactionInfo WITH (UPDLOCK, HOLDLOCK)
         WHERE transactionInfo.ID = @TransaccionID
           AND transactionInfo.RFC COLLATE Latin1_General_100_BIN2 =
               ''XAXX010101000'' COLLATE Latin1_General_100_BIN2
       )
      THROW 51863, ''CFDI DE CAPACITACIÓN RECHAZADO: la transacción no es sintética.'', 1;

    DECLARE @ExpectedFixtureHash varbinary(32) =
      0x6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B;

    IF DATALENGTH(@Attachment) <> 2934
       OR HASHBYTES(''SHA2_256'', @Attachment) <> @ExpectedFixtureHash
      THROW 51864, ''CFDI DE CAPACITACIÓN RECHAZADO: sólo se admite el XML ficticio publicado por Orion_Training.'', 1;

    DECLARE @TrainingXml xml = TRY_CONVERT(xml, @Attachment, 0);
    IF @TrainingXml IS NULL
      THROW 51865, ''CFDI DE CAPACITACIÓN RECHAZADO: el adjunto no es XML válido.'', 1;

    DECLARE @RootCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante)'', ''int'');
    DECLARE @RootChildCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/*)'', ''int'');
    DECLARE @EmisorCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Emisor)'', ''int'');
    DECLARE @ReceptorCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Receptor)'', ''int'');
    DECLARE @ConceptoCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto)'', ''int'');
    DECLARE @ConceptoTrasladoCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado)'', ''int'');
    DECLARE @RootTrasladoCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado)'', ''int'');
    DECLARE @TimbreCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; count(/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital)'', ''int'');
    DECLARE @ComplementChildCount int = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; count(/cfdi:Comprobante/cfdi:Complemento/*)'', ''int'');

    IF @RootCount <> 1 OR @RootChildCount <> 5
       OR @EmisorCount <> 1 OR @ReceptorCount <> 1 OR @ConceptoCount <> 1
       OR @ConceptoTrasladoCount <> 1 OR @RootTrasladoCount <> 1
       OR @TimbreCount <> 1 OR @ComplementChildCount <> 1
      THROW 51866, ''CFDI DE CAPACITACIÓN RECHAZADO: la estructura ficticia no coincide.'', 1;

    DECLARE @Ficticio varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace training="urn:orionerp:training-only"; (/cfdi:Comprobante/@training:Ficticio)[1]'', ''varchar(5)'');
    DECLARE @NoValidoFiscal varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace training="urn:orionerp:training-only"; (/cfdi:Comprobante/@training:NoValidoFiscal)[1]'', ''varchar(5)'');
    DECLARE @Version varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Version)[1]'', ''varchar(5)'');
    DECLARE @Serie varchar(25) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Serie)[1]'', ''varchar(25)'');
    DECLARE @Folio varchar(40) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Folio)[1]'', ''varchar(40)'');
    DECLARE @Fecha datetime = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Fecha)[1]'', ''datetime'');
    DECLARE @Sello varchar(100) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Sello)[1]'', ''varchar(100)'');
    DECLARE @FormaPago varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@FormaPago)[1]'', ''varchar(3)'');
    DECLARE @NoCertificado varchar(30) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@NoCertificado)[1]'', ''varchar(30)'');
    DECLARE @Certificado varchar(100) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Certificado)[1]'', ''varchar(100)'');
    DECLARE @Condiciones varchar(1000) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@CondicionesDePago)[1]'', ''varchar(1000)'');
    DECLARE @SubTotal decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@SubTotal)[1]'', ''decimal(18,6)'');
    DECLARE @Descuento decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Descuento)[1]'', ''decimal(18,6)'');
    DECLARE @Moneda varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Moneda)[1]'', ''varchar(3)'');
    DECLARE @TipoCambio decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@TipoCambio)[1]'', ''decimal(18,6)'');
    DECLARE @Total decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Total)[1]'', ''decimal(18,6)'');
    DECLARE @TipoDeComprobante varchar(1) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@TipoDeComprobante)[1]'', ''varchar(1)'');
    DECLARE @Exportacion varchar(2) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Exportacion)[1]'', ''varchar(2)'');
    DECLARE @MetodoPago varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@MetodoPago)[1]'', ''varchar(3)'');
    DECLARE @LugarExpedicion varchar(10) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@LugarExpedicion)[1]'', ''varchar(10)'');
    DECLARE @Confirmacion varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/@Confirmacion)[1]'', ''varchar(5)'');

    DECLARE @EmisorRfc varchar(13) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Emisor/@Rfc)[1]'', ''varchar(13)'');
    DECLARE @EmisorNombre varchar(300) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Emisor/@Nombre)[1]'', ''varchar(300)'');
    DECLARE @EmisorRegimen varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Emisor/@RegimenFiscal)[1]'', ''varchar(3)'');
    DECLARE @EmisorFacAtr varchar(20) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Emisor/@FacAtrAdquirente)[1]'', ''varchar(20)'');
    DECLARE @ReceptorRfc varchar(13) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@Rfc)[1]'', ''varchar(13)'');
    DECLARE @ReceptorNombre varchar(300) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@Nombre)[1]'', ''varchar(300)'');
    DECLARE @ReceptorDomicilio varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@DomicilioFiscalReceptor)[1]'', ''varchar(5)'');
    DECLARE @ReceptorResidencia varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@ResidenciaFiscalReceptor)[1]'', ''varchar(3)'');
    DECLARE @ReceptorRegistro varchar(40) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@NumRegIdTrib)[1]'', ''varchar(40)'');
    DECLARE @ReceptorRegimen varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@RegimenFiscalReceptor)[1]'', ''varchar(3)'');
    DECLARE @ReceptorUso varchar(4) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Receptor/@UsoCFDI)[1]'', ''varchar(4)'');

    DECLARE @Uuid varchar(50) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@UUID)[1]'', ''varchar(50)'');
    DECLARE @TimbreFicticio varchar(5) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; declare namespace training="urn:orionerp:training-only"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@training:Ficticio)[1]'', ''varchar(5)'');
    DECLARE @TimbreVersion varchar(10) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@Version)[1]'', ''varchar(10)'');
    DECLARE @FechaTimbrado datetime = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@FechaTimbrado)[1]'', ''datetime'');
    DECLARE @RfcProvCertif varchar(20) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@RfcProvCertif)[1]'', ''varchar(20)'');
    DECLARE @Leyenda varchar(150) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@Leyenda)[1]'', ''varchar(150)'');
    DECLARE @SelloCfd varchar(100) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@SelloCFD)[1]'', ''varchar(100)'');
    DECLARE @NoCertificadoSat varchar(30) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@NoCertificadoSAT)[1]'', ''varchar(30)'');
    DECLARE @SelloSat varchar(100) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; declare namespace tfd="http://www.sat.gob.mx/TimbreFiscalDigital"; (/cfdi:Comprobante/cfdi:Complemento/tfd:TimbreFiscalDigital/@SelloSAT)[1]'', ''varchar(100)'');

    IF ISNULL(@Ficticio, '''') <> ''true'' OR ISNULL(@NoValidoFiscal, '''') <> ''true''
       OR ISNULL(@TimbreFicticio, '''') <> ''true'' OR ISNULL(@Version, '''') <> ''4.0''
       OR ISNULL(@Serie, '''') <> ''TRN'' OR ISNULL(@Folio, '''') <> ''NO-TIMBRABLE-001''
       OR ISNULL(@Sello, '''') <> ''NO_VALIDO_ENTRENAMIENTO''
       OR ISNULL(@Certificado, '''') <> ''NO_VALIDO_ENTRENAMIENTO''
       OR ISNULL(@NoCertificado, '''') <> ''00000000000000000000''
       OR ISNULL(@Uuid, '''') <> ''00000000-0000-4000-8000-000000000001''
       OR ISNULL(@SelloCfd, '''') <> ''NO_VALIDO_ENTRENAMIENTO''
       OR ISNULL(@SelloSat, '''') <> ''NO_VALIDO_ENTRENAMIENTO''
       OR ISNULL(@NoCertificadoSat, '''') <> ''00000000000000000000''
       OR ISNULL(@EmisorRfc, '''') <> ''XAXX010101000''
       OR ISNULL(@ReceptorRfc, '''') <> ''XAXX010101000''
       OR ISNULL(@RfcProvCertif, '''') <> ''XAXX010101000''
       OR ISNULL(@TipoDeComprobante, '''') <> ''I''
       OR ISNULL(@FormaPago, '''') <> ''99'' OR ISNULL(@MetodoPago, '''') <> ''PPD''
       OR ISNULL(@Moneda, '''') <> ''MXN'' OR ISNULL(@Exportacion, '''') <> ''01''
       OR ISNULL(@LugarExpedicion, '''') <> ''00000'' OR ISNULL(@Confirmacion, '''') <> ''TRN''
       OR ISNULL(@SubTotal, -1) <> 1000 OR ISNULL(@Descuento, -1) <> 0
       OR ISNULL(@TipoCambio, -1) <> 1 OR ISNULL(@Total, -1) <> 1160
       OR @Fecha <> CONVERT(datetime, ''2026-01-15T12:00:00'', 126)
       OR @FechaTimbrado <> CONVERT(datetime, ''2026-01-15T12:00:01'', 126)
      THROW 51867, ''CFDI DE CAPACITACIÓN RECHAZADO: los marcadores ficticios no coinciden.'', 1;

    DECLARE @ConceptoClave varchar(20) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@ClaveProdServ)[1]'', ''varchar(20)'');
    DECLARE @ConceptoIdentificacion varchar(100) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@NoIdentificacion)[1]'', ''varchar(100)'');
    DECLARE @ConceptoCantidad decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@Cantidad)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoClaveUnidad varchar(20) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@ClaveUnidad)[1]'', ''varchar(20)'');
    DECLARE @ConceptoUnidad varchar(20) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@Unidad)[1]'', ''varchar(20)'');
    DECLARE @ConceptoDescripcion varchar(1000) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@Descripcion)[1]'', ''varchar(1000)'');
    DECLARE @ConceptoValor decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@ValorUnitario)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoImporte decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@Importe)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoDescuento decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@Descuento)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoObjeto varchar(2) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/@ObjetoImp)[1]'', ''varchar(2)'');
    DECLARE @ConceptoBase decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Base)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoImpuesto varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Impuesto)[1]'', ''varchar(3)'');
    DECLARE @ConceptoFactor varchar(10) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@TipoFactor)[1]'', ''varchar(10)'');
    DECLARE @ConceptoTasa decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@TasaOCuota)[1]'', ''decimal(18,6)'');
    DECLARE @ConceptoIva decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Importe)[1]'', ''decimal(18,6)'');
    DECLARE @RootBase decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Base)[1]'', ''decimal(18,6)'');
    DECLARE @RootImpuesto varchar(3) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Impuesto)[1]'', ''varchar(3)'');
    DECLARE @RootFactor varchar(10) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@TipoFactor)[1]'', ''varchar(10)'');
    DECLARE @RootTasa decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@TasaOCuota)[1]'', ''decimal(18,6)'');
    DECLARE @RootIva decimal(18,6) = @TrainingXml.value(
      ''declare namespace cfdi="http://www.sat.gob.mx/cfd/4"; (/cfdi:Comprobante/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado/@Importe)[1]'', ''decimal(18,6)'');

    IF ISNULL(@ConceptoClave, '''') <> ''01010101''
       OR ISNULL(@ConceptoIdentificacion, '''') <> ''TRN-SERVICIO-001''
       OR ISNULL(@ConceptoCantidad, -1) <> 1
       OR ISNULL(@ConceptoClaveUnidad, '''') <> ''E48''
       OR ISNULL(@ConceptoUnidad, '''') <> ''SERVICIO FICTICIO''
       OR ISNULL(@ConceptoDescripcion, '''') <> ''EJERCICIO CONTABLE SIN EFECTOS FISCALES''
       OR ISNULL(@ConceptoValor, -1) <> 1000 OR ISNULL(@ConceptoImporte, -1) <> 1000
       OR ISNULL(@ConceptoDescuento, -1) <> 0 OR ISNULL(@ConceptoObjeto, '''') <> ''02''
       OR ISNULL(@ConceptoBase, -1) <> 1000 OR ISNULL(@RootBase, -1) <> 1000
       OR ISNULL(@ConceptoImpuesto, '''') <> ''002'' OR ISNULL(@RootImpuesto, '''') <> ''002''
       OR ISNULL(@ConceptoFactor, '''') <> ''Tasa'' OR ISNULL(@RootFactor, '''') <> ''Tasa''
       OR ISNULL(@ConceptoTasa, -1) <> 0.16 OR ISNULL(@RootTasa, -1) <> 0.16
       OR ISNULL(@ConceptoIva, -1) <> 160 OR ISNULL(@RootIva, -1) <> 160
      THROW 51868, ''CFDI DE CAPACITACIÓN RECHAZADO: el ejercicio 1000/160/1160 no coincide.'', 1;

    DECLARE @ComprobanteId int;
    SELECT @ComprobanteId = stampInfo.Comprobante_Id
    FROM cfdi.TimbreFiscalDigital stampInfo WITH (UPDLOCK, HOLDLOCK)
    WHERE stampInfo.UUID = @Uuid;

    IF @ComprobanteId IS NULL
       AND EXISTS
       (
         SELECT 1 FROM cfdi.Comprobante headerInfo WITH (UPDLOCK, HOLDLOCK)
         WHERE headerInfo.Serie = ''TRN'' AND headerInfo.Folio = ''NO-TIMBRABLE-001''
       )
      THROW 51869, ''CFDI DE CAPACITACIÓN RECHAZADO: existe un encabezado ficticio sin su UUID conocido.'', 1;

    IF @ComprobanteId IS NOT NULL
       AND EXISTS
       (
         SELECT 1 FROM dbo.Transaccion_Comprobante linkInfo
         WHERE linkInfo.Comprobante_ID = @ComprobanteId
           AND (@TransaccionID IS NULL OR linkInfo.Transaccion_ID <> @TransaccionID)
       )
      THROW 51870, ''CFDI DE CAPACITACIÓN RECHAZADO: el XML ficticio ya está ligado a otra transacción.'', 1;

    IF @ComprobanteId IS NULL
    BEGIN
      INSERT cfdi.Comprobante
      (
        Version, Serie, Folio, Fecha, Sello, FormaPago, NoCertificado, Certificado,
        CondicionesDePago, SubTotal, Descuento, Moneda, TipoCambio, Total,
        TipoDeComprobante, Exportacion, MetodoPago, LugarExpedicion, Confirmacion,
        Tipo_Comprobante, Incluir_En_Declaracion, Factor_Declaracion, Estatus,
        XML_Attachment_ID
      )
      VALUES
      (
        @Version, @Serie, @Folio, @Fecha, @Sello, @FormaPago, @NoCertificado, @Certificado,
        @Condiciones, @SubTotal, @Descuento, @Moneda, @TipoCambio, @Total,
        @TipoDeComprobante, @Exportacion, @MetodoPago, @LugarExpedicion, @Confirmacion,
        ''INGRESO'', 1, 1, ''TRAINING_NO_VALIDO'', @AttachmentID
      );
      SET @ComprobanteId = CONVERT(int, SCOPE_IDENTITY());
    END
    ELSE
    BEGIN
      IF NOT EXISTS
      (
        SELECT 1 FROM cfdi.Comprobante headerInfo
        WHERE headerInfo.Comprobante_Id = @ComprobanteId
          AND headerInfo.Estatus = ''TRAINING_NO_VALIDO''
          AND headerInfo.Serie = ''TRN''
          AND headerInfo.Folio = ''NO-TIMBRABLE-001''
      )
        THROW 51871, ''CFDI DE CAPACITACIÓN RECHAZADO: el UUID conocido no pertenece al ejercicio ficticio.'', 1;

      DELETE transferInfo
      FROM cfdi.Traslado transferInfo
      JOIN cfdi.Traslados groupInfo ON groupInfo.Traslados_Id = transferInfo.Traslados_Id
      JOIN cfdi.Impuestos taxInfo ON taxInfo.Impuestos_Id = groupInfo.Impuestos_Id
      LEFT JOIN cfdi.Concepto conceptInfo ON conceptInfo.Concepto_Id = taxInfo.Concepto_Id
      LEFT JOIN cfdi.Conceptos conceptsInfo ON conceptsInfo.Conceptos_Id = conceptInfo.Conceptos_Id
      WHERE taxInfo.Comprobante_Id = @ComprobanteId
         OR conceptsInfo.Comprobante_Id = @ComprobanteId;

      DELETE groupInfo
      FROM cfdi.Traslados groupInfo
      JOIN cfdi.Impuestos taxInfo ON taxInfo.Impuestos_Id = groupInfo.Impuestos_Id
      LEFT JOIN cfdi.Concepto conceptInfo ON conceptInfo.Concepto_Id = taxInfo.Concepto_Id
      LEFT JOIN cfdi.Conceptos conceptsInfo ON conceptsInfo.Conceptos_Id = conceptInfo.Conceptos_Id
      WHERE taxInfo.Comprobante_Id = @ComprobanteId
         OR conceptsInfo.Comprobante_Id = @ComprobanteId;

      DELETE taxInfo
      FROM cfdi.Impuestos taxInfo
      LEFT JOIN cfdi.Concepto conceptInfo ON conceptInfo.Concepto_Id = taxInfo.Concepto_Id
      LEFT JOIN cfdi.Conceptos conceptsInfo ON conceptsInfo.Conceptos_Id = conceptInfo.Conceptos_Id
      WHERE taxInfo.Comprobante_Id = @ComprobanteId
         OR conceptsInfo.Comprobante_Id = @ComprobanteId;

      DELETE conceptInfo
      FROM cfdi.Concepto conceptInfo
      JOIN cfdi.Conceptos conceptsInfo ON conceptsInfo.Conceptos_Id = conceptInfo.Conceptos_Id
      WHERE conceptsInfo.Comprobante_Id = @ComprobanteId;

      DELETE FROM cfdi.Conceptos WHERE Comprobante_Id = @ComprobanteId;
      DELETE FROM cfdi.InformacionGlobal WHERE Comprobante_ID = @ComprobanteId;
      DELETE FROM cfdi.TimbreFiscalDigital WHERE Comprobante_Id = @ComprobanteId;
      DELETE FROM cfdi.Emisor WHERE Comprobante_Id = @ComprobanteId;
      DELETE FROM cfdi.Receptor WHERE Comprobante_Id = @ComprobanteId;

      UPDATE cfdi.Comprobante
      SET Version = @Version, Serie = @Serie, Folio = @Folio, Fecha = @Fecha,
          Sello = @Sello, FormaPago = @FormaPago, NoCertificado = @NoCertificado,
          Certificado = @Certificado, CondicionesDePago = @Condiciones,
          SubTotal = @SubTotal, Descuento = @Descuento, Moneda = @Moneda,
          TipoCambio = @TipoCambio, Total = @Total,
          TipoDeComprobante = @TipoDeComprobante, Exportacion = @Exportacion,
          MetodoPago = @MetodoPago, LugarExpedicion = @LugarExpedicion,
          Confirmacion = @Confirmacion, Tipo_Comprobante = ''INGRESO'',
          Incluir_En_Declaracion = 1, Factor_Declaracion = 1,
          Estatus = ''TRAINING_NO_VALIDO'', FechaCancelacion = NULL,
          XML_Attachment_ID = @AttachmentID
      WHERE Comprobante_Id = @ComprobanteId;
    END;

    INSERT cfdi.TimbreFiscalDigital
      (Version, UUID, FechaTimbrado, RfcProvCertif, Leyenda, SelloCFD,
       NoCertificadoSAT, SelloSAT, Comprobante_Id)
    VALUES
      (@TimbreVersion, @Uuid, @FechaTimbrado, @RfcProvCertif, @Leyenda, @SelloCfd,
       @NoCertificadoSat, @SelloSat, @ComprobanteId);

    INSERT cfdi.Emisor (Rfc, Nombre, RegimenFiscal, FacAtrAdquirente, Comprobante_Id)
    VALUES (@EmisorRfc, @EmisorNombre, @EmisorRegimen, @EmisorFacAtr, @ComprobanteId);

    INSERT cfdi.Receptor
      (Rfc, Nombre, DomicilioFiscalReceptor, ResidenciaFiscal, NumRegIdTrib,
       RegimenFiscalReceptor, UsoCFDI, Comprobante_Id)
    VALUES
      (@ReceptorRfc, @ReceptorNombre, @ReceptorDomicilio, @ReceptorResidencia,
       @ReceptorRegistro, @ReceptorRegimen, @ReceptorUso, @ComprobanteId);

    INSERT cfdi.InformacionGlobal (Comprobante_ID, PERIODICIDAD, MESES, ANIO)
    VALUES (@ComprobanteId, ''01'', ''01'', ''2026'');

    INSERT cfdi.Conceptos (Comprobante_Id) VALUES (@ComprobanteId);
    DECLARE @ConceptosId int = CONVERT(int, SCOPE_IDENTITY());

    INSERT cfdi.Concepto
      (ClaveProdServ, NoIdentificacion, Cantidad, ClaveUnidad, Unidad, Descripcion,
       ValorUnitario, Importe, Descuento, ObjetoImp, Conceptos_Id, Linea, Deducible)
    VALUES
      (@ConceptoClave, @ConceptoIdentificacion, @ConceptoCantidad,
       @ConceptoClaveUnidad, @ConceptoUnidad, @ConceptoDescripcion,
       @ConceptoValor, @ConceptoImporte, @ConceptoDescuento, @ConceptoObjeto,
       @ConceptosId, 1, ''NO'');
    DECLARE @ConceptoId int = CONVERT(int, SCOPE_IDENTITY());

    INSERT cfdi.Impuestos (Concepto_Id, Comprobante_Id) VALUES (@ConceptoId, NULL);
    DECLARE @ConceptoImpuestosId int = CONVERT(int, SCOPE_IDENTITY());
    INSERT cfdi.Traslados (Impuestos_Id) VALUES (@ConceptoImpuestosId);
    DECLARE @ConceptoTrasladosId int = CONVERT(int, SCOPE_IDENTITY());
    INSERT cfdi.Traslado (Base, Impuesto, TipoFactor, TasaOCuota, Importe, Traslados_Id)
    VALUES (@ConceptoBase, @ConceptoImpuesto, @ConceptoFactor, @ConceptoTasa, @ConceptoIva, @ConceptoTrasladosId);

    INSERT cfdi.Impuestos (Concepto_Id, Comprobante_Id) VALUES (NULL, @ComprobanteId);
    DECLARE @RootImpuestosId int = CONVERT(int, SCOPE_IDENTITY());
    INSERT cfdi.Traslados (Impuestos_Id) VALUES (@RootImpuestosId);
    DECLARE @RootTrasladosId int = CONVERT(int, SCOPE_IDENTITY());
    INSERT cfdi.Traslado (Base, Impuesto, TipoFactor, TasaOCuota, Importe, Traslados_Id)
    VALUES (@RootBase, @RootImpuesto, @RootFactor, @RootTasa, @RootIva, @RootTrasladosId);

    IF @TransaccionID IS NOT NULL
    BEGIN
      IF EXISTS
      (
        SELECT 1 FROM dbo.Transaccion_Comprobante
        WHERE Transaccion_ID = @TransaccionID AND Comprobante_ID = @ComprobanteId
      )
        UPDATE dbo.Transaccion_Comprobante
        SET Monto = CONVERT(money, @Total)
        WHERE Transaccion_ID = @TransaccionID AND Comprobante_ID = @ComprobanteId;
      ELSE
        INSERT dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
        VALUES (@TransaccionID, @ComprobanteId, CONVERT(money, @Total));
    END;

    IF @OwnTransaction = 1
      COMMIT TRANSACTION;

    SELECT @ComprobanteId AS Comprobante_ID;
  END TRY
  BEGIN CATCH
    IF @OwnTransaction = 1 AND XACT_STATE() <> 0
      ROLLBACK TRANSACTION;
    ELSE IF @OwnTransaction = 0 AND XACT_STATE() = 1
      ROLLBACK TRANSACTION TrainingCfdiFixture;
    THROW;
  END CATCH;
END;';
EXEC sys.sp_executesql @TrainingParserDefinition;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51854, 'TRAINING CFDI ATTESTATION BLOCKED: the active database changed.', 1;

DECLARE @ParserObjectId int = OBJECT_ID(N'cfdi.PROCESAR_SAT_XML_V2', N'P');
IF @ParserObjectId IS NULL
   OR EXISTS
      (SELECT 1 FROM sys.sql_modules
       WHERE object_id = @ParserObjectId
         AND (execute_as_principal_id IS NOT NULL
              OR definition NOT LIKE N'%OrionERP.Training.CfdiFixtureParser.v1:6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B%'))
   OR EXISTS
      (SELECT 1 FROM sys.sql_expression_dependencies
       WHERE referencing_id = @ParserObjectId
         AND (referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL))
  THROW 51855, 'TRAINING CFDI ATTESTATION BLOCKED: the local fictional parser contract does not match.', 1;

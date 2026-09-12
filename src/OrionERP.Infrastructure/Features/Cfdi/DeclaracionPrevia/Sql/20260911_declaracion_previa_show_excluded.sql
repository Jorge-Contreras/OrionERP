/*
  Declaracion Previa - separar CFDI vigentes y cancelados.

  La X de Incluir_En_Declaracion es una decision reversible del usuario. El
  procedimiento base devuelve tanto check como X para que la pagina permita
  cambiar esa decision. Los CFDI cancelados se excluyen de todas las secciones
  normales y se devuelven exclusivamente en CANCELADAS / OMITIDAS.

  El deploy fiscal compila este archivo con NOEXEC en simulacro y aplica los
  objetos al confirmar.
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'cfdi.CK_Comprobante_Cancelado_No_Declaracion', N'C') IS NULL
BEGIN
  ALTER TABLE cfdi.Comprobante WITH CHECK
    ADD CONSTRAINT CK_Comprobante_Cancelado_No_Declaracion CHECK
    (
      (
        ISNULL(LTRIM(RTRIM(Estatus)), '') NOT IN ('Cancelado', 'Cancelada')
        AND FechaCancelacion IS NULL
      )
      OR ISNULL(Incluir_En_Declaracion, 1) = 0
    );
END;
GO

CREATE OR ALTER PROCEDURE [cfdi].[Declaracion_CFDI_Base]
  @Year NVARCHAR(4) = NULL,
  @Month NVARCHAR(2) = NULL,
  @RFC NVARCHAR(20)
AS
BEGIN
  SET NOCOUNT ON;

  SELECT
    cd.Comprobante_Id,
    CASE WHEN ISNULL(cd.Incluir_En_Declaracion, 1) = 1 THEN NCHAR(10004) ELSE 'X' END AS D,
    cd.Fecha,
    cd.MESES AS MES_GLOBAL,
    cd.ANIO AS ANIO_GLOBAL,
    cd.EMISOR,
    cd.RECEPTOR,
    cd.RFC_EMISOR,
    cd.RFC_RECEPTOR,
    cd.SubTotal,
    cd.Descuento,
    cd.SubTotal_Desc,
    cd.Actos_16,
    cd.Actos_0,
    cd.IVA,
    cd.IEPS,
    cd.IVA_RETENIDO,
    cd.ISR_RETENIDO,
    cd.IEPS_RETENIDO,
    cd.Total,
    cd.FOLIO_FISCAL,
    cd.FormaPago,
    cd.TipoDeComprobante,
    cd.MetodoPago,
    cd.UsoCFDI,
    cd.FechaCancelacion,
    cd.Estatus,
    cd.fechastransacciones,
    cd.Poliza,
    cd.SumaPolizas,
    cd.XML_Attachment_ID,
    CAST(CASE WHEN cd.RFC_EMISOR = @RFC THEN 1 ELSE 0 END AS bit) AS EsEmitida,
    CAST(CASE WHEN cd.RFC_RECEPTOR = @RFC THEN 1 ELSE 0 END AS bit) AS EsRecibida
  FROM cfdi.COMPROBANTE_DETALLE AS cd
  WHERE (@Year IS NULL OR cd.ANIO = @Year)
    AND (@Month IS NULL OR TRY_CONVERT(int, cd.MESES) = @Month)
    AND (@RFC IS NULL OR cd.RFC_EMISOR = @RFC OR cd.RFC_RECEPTOR = @RFC)
    AND cd.TipoDeComprobante IN ('I', 'N', 'E')
    AND ISNULL(LTRIM(RTRIM(cd.Estatus)), '') NOT IN ('Cancelado', 'Cancelada')
    AND cd.FechaCancelacion IS NULL
  ORDER BY cd.Fecha ASC;
END;
GO

CREATE OR ALTER PROCEDURE [cfdi].[Declaracion_Canceladas_Omitidas]
  @Year NVARCHAR(4) = NULL,
  @Month NVARCHAR(2) = NULL,
  @RFC NVARCHAR(20)
AS
BEGIN
  SET NOCOUNT ON;

  SELECT
    cd.Comprobante_Id,
    CASE WHEN ISNULL(cd.Incluir_En_Declaracion, 1) = 1 THEN NCHAR(10004) ELSE 'X' END AS D,
    cd.Fecha,
    cd.MESES AS MES_GLOBAL,
    cd.ANIO AS ANIO_GLOBAL,
    cd.EMISOR,
    cd.RECEPTOR,
    cd.RFC_EMISOR,
    cd.RFC_RECEPTOR,
    cd.SubTotal,
    cd.Descuento,
    cd.SubTotal_Desc,
    cd.Actos_16,
    cd.Actos_0,
    cd.IVA,
    cd.IEPS,
    cd.IVA_RETENIDO,
    cd.ISR_RETENIDO,
    cd.IEPS_RETENIDO,
    cd.Total,
    cd.FOLIO_FISCAL,
    cd.FormaPago,
    cd.TipoDeComprobante,
    cd.MetodoPago,
    cd.UsoCFDI,
    cd.FechaCancelacion,
    cd.Estatus,
    cd.fechastransacciones,
    cd.Poliza,
    cd.SumaPolizas,
    cd.XML_Attachment_ID,
    CAST(CASE WHEN cd.RFC_EMISOR = @RFC THEN 1 ELSE 0 END AS bit) AS EsEmitida,
    CAST(CASE WHEN cd.RFC_RECEPTOR = @RFC THEN 1 ELSE 0 END AS bit) AS EsRecibida
  FROM cfdi.COMPROBANTE_DETALLE AS cd
  WHERE (@Year IS NULL OR cd.ANIO = @Year)
    AND (@Month IS NULL OR TRY_CONVERT(int, cd.MESES) = @Month)
    AND (@RFC IS NULL OR cd.RFC_EMISOR = @RFC OR cd.RFC_RECEPTOR = @RFC)
    AND
    (
      ISNULL(LTRIM(RTRIM(cd.Estatus)), '') IN ('Cancelado', 'Cancelada')
      OR cd.FechaCancelacion IS NOT NULL
    )
  ORDER BY cd.Fecha ASC;
END;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF OBJECT_ID('dbo.OrdenTrabajoEvidencia', 'U') IS NOT NULL
  AND COL_LENGTH('dbo.OrdenTrabajoEvidencia', 'CaptureSource') IS NULL
BEGIN
  ALTER TABLE dbo.OrdenTrabajoEvidencia
    ADD CaptureSource varchar(20) NOT NULL
      CONSTRAINT DF_OrdenTrabajoEvidencia_CaptureSource DEFAULT ('UNKNOWN') WITH VALUES;
END;

COMMIT TRANSACTION;
GO

BEGIN TRANSACTION;

IF OBJECT_ID('dbo.OrdenTrabajoEvidencia', 'U') IS NOT NULL
  AND OBJECT_ID('dbo.CK_OrdenTrabajoEvidencia_CaptureSource', 'C') IS NULL
BEGIN
  ALTER TABLE dbo.OrdenTrabajoEvidencia
    ADD CONSTRAINT CK_OrdenTrabajoEvidencia_CaptureSource
      CHECK (CaptureSource IN ('CAMERA','FILE','UNKNOWN'));
END;

COMMIT TRANSACTION;
GO

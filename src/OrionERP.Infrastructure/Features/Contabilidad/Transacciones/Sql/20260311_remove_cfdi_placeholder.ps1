[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$ConnectionString = $env:ORION_DB_CONNECTION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
  throw "Provide -ConnectionString or set ORION_DB_CONNECTION."
}

Add-Type -AssemblyName System.Data

function New-DbConnection {
  $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
  $connection.Open()
  return $connection
}

function Invoke-DbNonQuery {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$Sql
  )

  $command = $Connection.CreateCommand()
  $command.CommandTimeout = 0
  $command.CommandText = $Sql
  [void]$command.ExecuteNonQuery()
}

function Invoke-DbScalar {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$Sql,
    [Parameter(Mandatory = $false)]
    [hashtable]$Parameters = @{}
  )

  $command = $Connection.CreateCommand()
  $command.CommandTimeout = 0
  $command.CommandText = $Sql

  foreach ($entry in $Parameters.GetEnumerator()) {
    $parameter = $command.Parameters.Add("@$($entry.Key)", [System.Data.SqlDbType]::NVarChar, -1)
    $parameter.Value = [string]$entry.Value
  }

  return $command.ExecuteScalar()
}

function Get-ObjectDefinition {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$ObjectName
  )

  $definition = Invoke-DbScalar `
    -Connection $Connection `
    -Sql "SELECT OBJECT_DEFINITION(OBJECT_ID(@ObjectName));" `
    -Parameters @{ ObjectName = $ObjectName }

  if ([string]::IsNullOrWhiteSpace([string]$definition)) {
    throw "Could not load definition for $ObjectName."
  }

  return [string]$definition
}

function Set-ObjectDefinition {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$Definition
  )

  Invoke-DbNonQuery -Connection $Connection -Sql $Definition
}

function Convert-ToCreateOrAlter {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Definition
  )

  return ($Definition -replace "(?im)^\s*CREATE\s+PROCEDURE\b", "CREATE OR ALTER PROCEDURE")
}

function Replace-Regex {
  param(
    [Parameter(Mandatory = $true)]
    [string]$InputText,
    [Parameter(Mandatory = $true)]
    [string]$Pattern,
    [Parameter(Mandatory = $true)]
    [string]$Replacement,
    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  if (-not [regex]::IsMatch($InputText, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
    throw "Failed to find replacement target for $Label."
  }

  return [regex]::Replace(
    $InputText,
    $Pattern,
    $Replacement,
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

function Assert-NoPlaceholder {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$ObjectName
  )

  $definition = Get-ObjectDefinition -Connection $Connection -ObjectName $ObjectName
  if ($definition -match "5505|Placeholder|placeholder") {
    throw "Placeholder references still exist in $ObjectName."
  }
}

$schemaSql = @"
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.TRANSACTION_ATTACHMENT')
      AND name = N'TranID'
      AND is_nullable = 0
)
BEGIN
    ALTER TABLE dbo.TRANSACTION_ATTACHMENT
    ALTER COLUMN TranID INT NULL;
END;
"@

$ligarProc = @"
CREATE OR ALTER PROCEDURE [contabilidad].[Ligar_CFDI_Poliza]
    @TransaccionId            INT,
    @ComprobanteId            BIGINT,
    @Monto                    DECIMAL(18, 6),
    @UseDoctoRelacionadoTable BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        IF @UseDoctoRelacionadoTable = 1
        BEGIN
            INSERT INTO dbo.Transaccion_DoctoRelacionado
                (Transaccion_ID, DoctoRelacionado_Id, Monto)
            VALUES
                (@TransaccionId, @ComprobanteId, @Monto);
        END
        ELSE
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM dbo.Transaccion_Comprobante
                WHERE Transaccion_ID = @TransaccionId
                  AND Comprobante_ID = @ComprobanteId
            )
            BEGIN
                UPDATE dbo.Transaccion_Comprobante
                SET Monto = @Monto
                WHERE Transaccion_ID = @TransaccionId
                  AND Comprobante_ID = @ComprobanteId;
            END
            ELSE
            BEGIN
                INSERT INTO dbo.Transaccion_Comprobante
                    (Transaccion_ID, Comprobante_ID, Monto)
                VALUES
                    (@TransaccionId, @ComprobanteId, @Monto);
            END

            DECLARE @XmlAttachmentId INT;

            SELECT @XmlAttachmentId = c.XML_Attachment_ID
            FROM cfdi.Comprobante AS c
            WHERE c.Comprobante_Id = @ComprobanteId;

            IF @XmlAttachmentId IS NOT NULL
            BEGIN
                UPDATE dbo.TRANSACTION_ATTACHMENT
                SET TranID = @TransaccionId
                WHERE ID = @XmlAttachmentId;
            END
        END

        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK;

        THROW;
    END CATCH
END
"@

$dataMigrationSql = @"
SET XACT_ABORT ON;

BEGIN TRAN;

;WITH PlaceholderComprobantes AS
(
    SELECT
        tc.Comprobante_ID,
        c.XML_Attachment_ID
    FROM dbo.Transaccion_Comprobante tc
    JOIN cfdi.Comprobante c
      ON c.Comprobante_ID = tc.Comprobante_ID
    WHERE tc.Transaccion_ID = 5505
      AND c.XML_Attachment_ID IS NOT NULL
),
AttachmentTargets AS
(
    SELECT
        pc.XML_Attachment_ID,
        target.TargetTransaccionId
    FROM PlaceholderComprobantes pc
    OUTER APPLY
    (
        SELECT TOP (1)
            tc2.Transaccion_ID AS TargetTransaccionId
        FROM dbo.Transaccion_Comprobante tc2
        JOIN dbo.Transacciones t
          ON t.ID = tc2.Transaccion_ID
        WHERE tc2.Comprobante_ID = pc.Comprobante_ID
          AND tc2.Transaccion_ID <> 5505
        ORDER BY t.Fecha, t.ID
    ) target
)
UPDATE ta
SET ta.TranID = at.TargetTransaccionId
FROM dbo.TRANSACTION_ATTACHMENT ta
JOIN AttachmentTargets at
  ON at.XML_Attachment_ID = ta.ID
WHERE ta.TranID = 5505;

UPDATE dbo.TRANSACTION_ATTACHMENT
SET TranID = NULL
WHERE TranID = 5505;

DELETE FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = 5505;

COMMIT TRAN;
"@

$connection = New-DbConnection

try {
  Write-Host "Altering dbo.TRANSACTION_ATTACHMENT.TranID to nullable if needed..."
  Invoke-DbNonQuery -Connection $connection -Sql $schemaSql

  Write-Host "Updating contabilidad.Ligar_CFDI_Poliza..."
  Invoke-DbNonQuery -Connection $connection -Sql $ligarProc

  Write-Host "Patching cfdi.PROCESAR_SAT_XML_V2..."
  $procesar = Get-ObjectDefinition -Connection $connection -ObjectName "cfdi.PROCESAR_SAT_XML_V2"
  $procesar = Convert-ToCreateOrAlter -Definition $procesar
  $procesar = Replace-Regex `
    -InputText $procesar `
    -Pattern "(?ms)CREATE OR ALTER PROCEDURE\s+\[cfdi\]\.\[PROCESAR_SAT_XML_V2\]\s+@TransaccionID\s+INT,\s+@AttachmentID\s+INT" `
    -Replacement @"
CREATE OR ALTER PROCEDURE [cfdi].[PROCESAR_SAT_XML_V2]
    @TransaccionID INT = NULL,
    @AttachmentID  INT
"@ `
    -Label "cfdi.PROCESAR_SAT_XML_V2 signature"
  $procesar = Replace-Regex `
    -InputText $procesar `
    -Pattern "(?ms)DECLARE\s+@Tipo_Comprobante\s+VARCHAR\(10\)\s*=\s*\(\s*SELECT\s+CASE\s+WHEN\s+monto\s*>\s*0\s+THEN\s+'INGRESO'\s+ELSE\s+'GASTO'\s+END\s+FROM\s+dbo\.Transacciones\s+WHERE\s+ID\s*=\s*@TransaccionID\s*\);\s*IF\s+@Tipo_Comprobante\s+IS\s+NULL\s*SET\s+@Tipo_Comprobante\s*=\s*'INGRESO';" `
    -Replacement @"
        DECLARE @Tipo_Comprobante VARCHAR(10) =
        (
            SELECT
                CASE WHEN monto > 0 THEN 'INGRESO' ELSE 'GASTO' END
            FROM dbo.Transacciones
            WHERE ID = @TransaccionID
        );

        IF @Tipo_Comprobante IS NULL
            SET @Tipo_Comprobante =
            (
                SELECT TOP (1) Tipo_Comprobante
                FROM cfdi.Comprobante
                WHERE Comprobante_Id = @ComprobanteID
            );

        IF @Tipo_Comprobante IS NULL
            SET @Tipo_Comprobante = 'INGRESO';
"@ `
    -Label "cfdi.PROCESAR_SAT_XML_V2 tipo_comprobante"
  $procesar = Replace-Regex `
    -InputText $procesar `
    -Pattern "(?ms)IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+dbo\.transaccion_comprobante\s+WHERE\s+comprobante_id\s*=\s*@ComprobanteID\s+AND\s+transaccion_id\s*=\s*@TransaccionID\s*\)\s*BEGIN\s*INSERT\s+INTO\s+dbo\.transaccion_comprobante\s*\(comprobante_id,\s*transaccion_id,\s*Monto\)\s*VALUES\s*\(@ComprobanteID,\s*@TransaccionID,\s*@Total\);\s*END" `
    -Replacement @"
        IF @TransaccionID IS NOT NULL
           AND NOT EXISTS
        (
            SELECT 1
            FROM dbo.transaccion_comprobante
            WHERE comprobante_id = @ComprobanteID
              AND transaccion_id = @TransaccionID
        )
        BEGIN
            INSERT INTO dbo.transaccion_comprobante (comprobante_id, transaccion_id, Monto)
            VALUES (@ComprobanteID, @TransaccionID, @Total);
        END
"@ `
    -Label "cfdi.PROCESAR_SAT_XML_V2 insert link"
  Set-ObjectDefinition -Connection $connection -Definition $procesar
  Assert-NoPlaceholder -Connection $connection -ObjectName "cfdi.PROCESAR_SAT_XML_V2"

  Write-Host "Patching contabilidad.Generar_Poliza_Desde_Comprobante..."
  $generar = Get-ObjectDefinition -Connection $connection -ObjectName "contabilidad.Generar_Poliza_Desde_Comprobante"
  $generar = Convert-ToCreateOrAlter -Definition $generar
  $generar = $generar.Replace("@TienePoliza5505", "@TieneLigaPrevia")
  $generar = Replace-Regex `
    -InputText $generar `
    -Pattern "(?ms)--\s*2\.1\).*?--\s*2\.2\)" `
    -Replacement @"
        -- 2.1) Validar ligas existentes en Transaccion_Comprobante
        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
        )
        BEGIN
            RAISERROR('Ya existe una Transacción ligada a este Comprobante (Transaccion_Comprobante).',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        -- 2.2) Validar relación Total vs SubTotal_Desc/Impuestos/Retenciones
"@ `
    -Label "contabilidad.Generar_Poliza_Desde_Comprobante validation"
  $generar = Replace-Regex `
    -InputText $generar `
    -Pattern "(?ms)--\s*9\).*?--\s*10\)" `
    -Replacement @"
        -- 9) Ligar póliza con el Comprobante (Transaccion_Comprobante)
        INSERT INTO dbo.Transaccion_Comprobante
            (Transaccion_ID, Comprobante_ID, Monto)
        VALUES
            (@TransaccionID, @Comprobante_Id, @TotalCFDI);

        -- 10) Actualizar TRANSACTION_ATTACHMENT.TranID usando XML_Attachment_ID
"@ `
    -Label "contabilidad.Generar_Poliza_Desde_Comprobante link block"
  Set-ObjectDefinition -Connection $connection -Definition $generar
  Assert-NoPlaceholder -Connection $connection -ObjectName "contabilidad.Generar_Poliza_Desde_Comprobante"

  Write-Host "Patching contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion..."
  $regenerar = Get-ObjectDefinition -Connection $connection -ObjectName "contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion"
  $regenerar = Convert-ToCreateOrAlter -Definition $regenerar
  $regenerar = $regenerar.Replace("@TienePoliza5505", "@TieneLigaPrevia")
  $regenerar = Replace-Regex `
    -InputText $regenerar `
    -Pattern "(?ms)--\s*2\.1\).*?--\s*2\.2\)" `
    -Replacement @"
        -- 2.1) Validar ligas existentes en Transaccion_Comprobante
        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID <> @Transaccion_ID
        )
        BEGIN
            RAISERROR('Ya existe una Transacción distinta de la actual ligada a este Comprobante (Transaccion_Comprobante).',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        -- 2.2) Validar relación Total vs SubTotal_Desc/Impuestos/Retenciones
"@ `
    -Label "contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion validation"
  $regenerar = Replace-Regex `
    -InputText $regenerar `
    -Pattern "(?ms)--\s*9\).*?--\s*10\)" `
    -Replacement @"
        -- 9) Ligar póliza con el Comprobante (Transaccion_Comprobante)
        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID = @Transaccion_ID
        )
        BEGIN
            UPDATE dbo.Transaccion_Comprobante
            SET Monto = @TotalCFDI
            WHERE Comprobante_ID = @Comprobante_Id
              AND Transaccion_ID = @Transaccion_ID;
        END
        ELSE
        BEGIN
            INSERT INTO dbo.Transaccion_Comprobante
                (Transaccion_ID, Comprobante_ID, Monto)
            VALUES
                (@Transaccion_ID, @Comprobante_Id, @TotalCFDI);
        END;

        -- 10) Actualizar TRANSACTION_ATTACHMENT.TranID usando XML_Attachment_ID
"@ `
    -Label "contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion link block"
  Set-ObjectDefinition -Connection $connection -Definition $regenerar
  Assert-NoPlaceholder -Connection $connection -ObjectName "contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion"

  Write-Host "Migrating placeholder data out of Transaccion_Comprobante and TRANSACTION_ATTACHMENT..."
  Invoke-DbNonQuery -Connection $connection -Sql $dataMigrationSql

  $placeholderLinkCount = [int](Invoke-DbScalar -Connection $connection -Sql "SELECT COUNT(*) FROM dbo.Transaccion_Comprobante WHERE Transaccion_ID = 5505;")
  $placeholderAttachmentCount = [int](Invoke-DbScalar -Connection $connection -Sql "SELECT COUNT(*) FROM dbo.TRANSACTION_ATTACHMENT WHERE TranID = 5505;")

  Write-Host "Placeholder link rows remaining: $placeholderLinkCount"
  Write-Host "Placeholder attachment rows remaining: $placeholderAttachmentCount"

  if ($placeholderLinkCount -ne 0 -or $placeholderAttachmentCount -ne 0) {
    throw "Placeholder data still remains after migration."
  }

  Write-Host "CFDI placeholder migration completed successfully."
}
finally {
  $connection.Dispose()
}

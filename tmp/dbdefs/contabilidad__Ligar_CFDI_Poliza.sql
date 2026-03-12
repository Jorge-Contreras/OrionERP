
CREATE   PROCEDURE [contabilidad].[Ligar_CFDI_Poliza]
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

        -----------------------------------------------------------------------
        -- 1) Link logic (table selection)
        -----------------------------------------------------------------------
        IF @UseDoctoRelacionadoTable = 1
        BEGIN
            -- If you also want "upsert" behavior here, tell me; for now keep insert-only.
            INSERT INTO dbo.Transaccion_DoctoRelacionado
                (Transaccion_ID, DoctoRelacionado_Id, Monto)
            VALUES
                (@TransaccionId, @ComprobanteId, @Monto);
        END
        ELSE
        BEGIN
            -------------------------------------------------------------------
            -- 1A) If exists placeholder row for this Comprobante in Tran 5505,
            --     update Transaccion_ID -> @TransaccionId AND always update Monto.
            --     If not, insert new row.
            -------------------------------------------------------------------
            UPDATE dbo.Transaccion_Comprobante
               SET Transaccion_ID = @TransaccionId,
                   Monto         = @Monto
             WHERE Comprobante_ID = @ComprobanteId
               AND Transaccion_ID = 5505;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.Transaccion_Comprobante
                    (Transaccion_ID, Comprobante_ID, Monto)
                VALUES
                    (@TransaccionId, @ComprobanteId, @Monto);
            END

            -------------------------------------------------------------------
            -- 2) If cfdi.Comprobante has XML_Attachment_ID, re-parent the
            --    attachment row to the new transaction id.
            -------------------------------------------------------------------
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


/*
  Las polizas bancarias automaticas conservan la cuenta contable del banco,
  pero resuelven la contrapartida por RFC desde Ajustes > Cuentas contables CFDI.

  Egreso  -> SUBTOTAL_GASTO
  Ingreso -> SUBTOTAL_INGRESO
*/
CREATE OR ALTER PROCEDURE [dbo].[Crear_Transaccion_Contable_Banco]
(
    @RFC                VARCHAR(50),
    @Fecha              DATETIME2(0),
    @Concepto           NVARCHAR(500),
    @Tipo               CHAR(1),
    @Monto              DECIMAL(19,2),
    @CuentaBancoID      INT,
    @TransaccionID      INT OUTPUT
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        IF @Tipo NOT IN ('I','E')
            THROW 50000, 'El parametro @Tipo debe ser ''I'' (ingreso) o ''E'' (egreso).', 1;

        IF @CuentaBancoID IS NULL
            THROW 50001, 'Debe proporcionar @CuentaBancoID.', 1;

        SET @RFC = LTRIM(RTRIM(@RFC));
        SET @Concepto = LTRIM(RTRIM(@Concepto));

        DECLARE @TipoPoliza VARCHAR(20) =
            CASE @Tipo WHEN 'I' THEN 'INGRESO' ELSE 'EGRESO' END;

        DECLARE @NombreCuentaTrans NVARCHAR(100);
        DECLARE @Cuenta_Banco_ID INT, @Cuenta_Gasto_ID INT, @Cuenta_Ingreso_ID INT;

        SELECT
            @NombreCuentaTrans = LEFT(cb.Nombre_Banco, 100),
            @Cuenta_Banco_ID = cb.Cuenta_Contable_ID
        FROM bancos.Cuentas_Banco AS cb
        WHERE cb.Cuenta_Banco_ID = @CuentaBancoID
          AND cb.RFC = @RFC;

        SELECT
            @Cuenta_Gasto_ID = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_GASTO' THEN defaults.CuentaContableId END),
            @Cuenta_Ingreso_ID = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_INGRESO' THEN defaults.CuentaContableId END)
        FROM dbo.CfdiPolizaCuentaDefault AS defaults
        WHERE defaults.Rfc = @RFC
          AND defaults.CuentaClave IN ('SUBTOTAL_GASTO', 'SUBTOTAL_INGRESO');

        IF @Cuenta_Banco_ID IS NULL
            THROW 50010, 'La cuenta contable del banco no esta configurada para esta cuenta bancaria y RFC.', 1;

        IF @Tipo = 'E' AND @Cuenta_Gasto_ID IS NULL
            THROW 50011, 'Configura Subtotal gasto en Ajustes > Cuentas contables CFDI antes de crear polizas automaticas.', 1;

        IF @Tipo = 'I' AND @Cuenta_Ingreso_ID IS NULL
            THROW 50012, 'Configura Subtotal ingreso en Ajustes > Cuentas contables CFDI antes de crear polizas automaticas.', 1;

        DECLARE
            @B_N1 VARCHAR(50), @B_N2 VARCHAR(50), @B_N3 VARCHAR(50), @B_Nombre NVARCHAR(200),
            @G_N1 VARCHAR(50), @G_N2 VARCHAR(50), @G_N3 VARCHAR(50), @G_Nombre NVARCHAR(200),
            @I_N1 VARCHAR(50), @I_N2 VARCHAR(50), @I_N3 VARCHAR(50), @I_Nombre NVARCHAR(200);

        SELECT
            @B_N1 = c.Nivel1,
            @B_N2 = c.Nivel2,
            @B_N3 = c.Nivel3,
            @B_Nombre = LEFT(COALESCE(NULLIF(c.Descripcion,''), c.RFC), 200)
        FROM dbo.CuentasContables AS c
        WHERE c.id = @Cuenta_Banco_ID
          AND c.RFC = @RFC;

        IF @B_N1 IS NULL
            THROW 50013, 'La cuenta contable del banco no pertenece al RFC actual o no existe.', 1;

        IF @Tipo = 'E'
        BEGIN
            SELECT
                @G_N1 = c.Nivel1,
                @G_N2 = c.Nivel2,
                @G_N3 = c.Nivel3,
                @G_Nombre = LEFT(COALESCE(NULLIF(c.Descripcion,''), c.RFC), 200)
            FROM dbo.CuentasContables AS c
            WHERE c.id = @Cuenta_Gasto_ID
              AND c.RFC = @RFC;

            IF @G_N1 IS NULL
                THROW 50014, 'La cuenta Subtotal gasto configurada en Ajustes no pertenece al RFC actual o no existe.', 1;
        END
        ELSE
        BEGIN
            SELECT
                @I_N1 = c.Nivel1,
                @I_N2 = c.Nivel2,
                @I_N3 = c.Nivel3,
                @I_Nombre = LEFT(COALESCE(NULLIF(c.Descripcion,''), c.RFC), 200)
            FROM dbo.CuentasContables AS c
            WHERE c.id = @Cuenta_Ingreso_ID
              AND c.RFC = @RFC;

            IF @I_N1 IS NULL
                THROW 50015, 'La cuenta Subtotal ingreso configurada en Ajustes no pertenece al RFC actual o no existe.', 1;
        END

        BEGIN TRAN;

        INSERT dbo.Transacciones
            (Concepto, Fecha, Monto, Cuenta, Tipo_Poliza, Forma_Pago, RFC, Memo)
        VALUES
            (LEFT(@Concepto, 500), @Fecha, @Monto, @NombreCuentaTrans,
             @TipoPoliza, '03', @RFC, @Concepto);

        SET @TransaccionID = SCOPE_IDENTITY();

        INSERT dbo.Registro_Contable
            (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID)
        VALUES
            (@B_N1, @B_N2, @B_N3, @B_Nombre, LEFT(@Concepto, 200),
             CASE WHEN @Tipo = 'I' THEN @Monto ELSE 0 END,
             CASE WHEN @Tipo = 'E' THEN @Monto ELSE 0 END,
             @TransaccionID);

        IF @Tipo = 'E'
        BEGIN
            INSERT dbo.Registro_Contable
                (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID)
            VALUES
                (@G_N1, @G_N2, @G_N3, @G_Nombre, LEFT(@Concepto, 200), @Monto, 0, @TransaccionID);
        END
        ELSE
        BEGIN
            INSERT dbo.Registro_Contable
                (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID)
            VALUES
                (@I_N1, @I_N2, @I_N3, @I_Nombre, LEFT(@Concepto, 200), 0, @Monto, @TransaccionID);
        END

        COMMIT TRAN;

        SELECT Inserted = 1, TransaccionID = @TransaccionID;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END;

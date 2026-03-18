CREATE OR ALTER PROCEDURE [bancos].[Procesar_Movimientos_XML]
  @ArchivoXML      VARCHAR(MAX),
  @Cuenta_Banco_ID INT,
  @SaldoInicial    DECIMAL(19,2) = NULL
AS
BEGIN
  SET NOCOUNT ON;

  IF @ArchivoXML IS NULL OR LEN(@ArchivoXML) = 0
    THROW 50000, 'El archivo de movimientos esta vacio.', 1;

  DECLARE @Nombre_Banco VARCHAR(100);

  SELECT @Nombre_Banco = Nombre_Banco
  FROM bancos.Cuentas_Banco
  WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID;

  IF @Nombre_Banco IS NULL
    THROW 50001, 'Cuenta_Banco_ID no existe.', 1;

  SET @Nombre_Banco = UPPER(@Nombre_Banco);

  IF @Nombre_Banco LIKE 'SANTANDER%'
  BEGIN
    EXEC bancos.Procesar_Movimientos_SANTANDER
         @ArchivoXML      = @ArchivoXML,
         @Cuenta_Banco_ID = @Cuenta_Banco_ID,
         @SaldoInicial    = @SaldoInicial;
  END
  ELSE IF @Nombre_Banco LIKE 'BBVA%'
  BEGIN
    EXEC bancos.Procesar_Movimientos_BBVA
         @ArchivoTexto    = @ArchivoXML,
         @Cuenta_Banco_ID = @Cuenta_Banco_ID;
  END
  ELSE IF @Nombre_Banco LIKE 'SCHOOLSFIRST%'
       OR @Nombre_Banco LIKE 'SCHOOLS FIRST%'
       OR @Nombre_Banco LIKE 'SCHOOLS%'
  BEGIN
    EXEC bancos.Procesar_Movimientos_SchoolsFirst
         @ArchivoTexto    = @ArchivoXML,
         @Cuenta_Banco_ID = @Cuenta_Banco_ID;
  END
  ELSE IF @Nombre_Banco LIKE '%AMERICAN EXPRESS%'
       OR @Nombre_Banco LIKE '%AMERICANEXPRESS%'
       OR @Nombre_Banco LIKE '%AMEX%'
  BEGIN
    EXEC bancos.Procesar_Movimientos_AmericanExpress
         @ArchivoTexto    = @ArchivoXML,
         @Cuenta_Banco_ID = @Cuenta_Banco_ID;
  END
  ELSE
  BEGIN
    THROW 50010, 'Banco no soportado en Procesar_Movimientos_XML.', 1;
  END
END

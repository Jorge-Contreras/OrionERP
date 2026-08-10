/*
  Corrige la idempotencia de las cargas BBVA superpuestas.

  El saldo mostrado por BBVA puede cambiar entre exportaciones de rangos
  superpuestos. Por eso no forma parte de la identidad estable del movimiento.
  OccurrenceNo conserva la capacidad de distinguir movimientos genuinamente
  repetidos el mismo día con el mismo concepto, importe y dirección.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" -i 20260809_bancos_procesar_movimientos_bbva_stable_uid.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" -i 20260809_bancos_procesar_movimientos_bbva_stable_uid.sql
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51520, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51521, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF OBJECT_ID(N'bancos.Movimientos', N'U') IS NULL
   OR OBJECT_ID(N'bancos.Cuentas_Banco', N'U') IS NULL
  THROW 51522, 'Falta el esquema de movimientos bancarios.', 1;
GO

CREATE OR ALTER PROCEDURE [bancos].[Procesar_Movimientos_BBVA]
  @ArchivoTexto     VARCHAR(MAX),
  @Cuenta_Banco_ID  INT
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @ArchivoTexto IS NULL OR LEN(@ArchivoTexto) = 0
    THROW 50000, 'El archivo está vacío.', 1;

  IF NOT EXISTS (SELECT 1 FROM bancos.Cuentas_Banco WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID)
    THROW 50001, 'Cuenta_Banco_ID no existe.', 1;

  DECLARE @Nombre_Banco  VARCHAR(100),
          @Numero_Cuenta VARCHAR(50),
          @RFC           VARCHAR(50);

  SELECT
    @Nombre_Banco = Nombre_Banco,
    @Numero_Cuenta = Numero_Cuenta,
    @RFC = RFC
  FROM bancos.Cuentas_Banco
  WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID;

  DECLARE @src NVARCHAR(MAX) =
    REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(@ArchivoTexto, ''))),
      CHAR(13) + CHAR(10), CHAR(10)), CHAR(13), CHAR(10)), '&', '&amp;');
  SET @src = REPLACE(REPLACE(@src, '<', '&lt;'), '>', '&gt;');

  DECLARE @xml XML = TRY_CAST(
    '<x><r><c>' +
    REPLACE(REPLACE(@src, CHAR(9), '</c><c>'), CHAR(10), '</c></r><r><c>') +
    '</c></r></x>' AS XML);

  IF @xml IS NULL
    THROW 50002, 'Archivo BBVA inválido (no se pudo convertir a XML).', 1;

  DECLARE @ArchivoHashHex VARCHAR(64) =
    CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @src)), 2);

  BEGIN TRANSACTION;

  DECLARE @S TABLE
  (
    Dia                DATE,
    Concepto           VARCHAR(500),
    Cargo              DECIMAL(19,2),
    Abono              DECIMAL(19,2),
    Saldo              DECIMAL(19,2),
    TipoDerivado       CHAR(1),
    UID                VARCHAR(64),
    Secuencia_Archivo  INT,
    RN_Dia             INT,
    OccurrenceNo       INT,
    Balance_OK         BIT
  );

  ;WITH Raw AS
  (
    SELECT
      RowIndex = ROW_NUMBER() OVER (ORDER BY (SELECT 1)),
      ColDia = T.C1.value('(c[1])[1]', 'nvarchar(50)'),
      ColConcepto = T.C1.value('(c[2])[1]', 'nvarchar(700)'),
      ColCargoTxt = T.C1.value('(c[3])[1]', 'nvarchar(50)'),
      ColAbonoTxt = T.C1.value('(c[4])[1]', 'nvarchar(50)'),
      ColSaldoTxt = T.C1.value('(c[5])[1]', 'nvarchar(50)')
    FROM @xml.nodes('/x/r') AS T(C1)
  ),
  Parsed AS
  (
    SELECT
      RowIndex,
      Dia = TRY_CONVERT(DATE, NULLIF(ColDia, ''), 105),
      Concepto = NULLIF(ColConcepto, ''),
      BankCargo = TRY_CONVERT(MONEY, REPLACE(NULLIF(ColCargoTxt, ''), ',', '')),
      BankAbono = TRY_CONVERT(MONEY, REPLACE(NULLIF(ColAbonoTxt, ''), ',', '')),
      Saldo = TRY_CONVERT(MONEY, REPLACE(NULLIF(ColSaldoTxt, ''), ',', ''))
    FROM Raw
    WHERE TRY_CONVERT(DATE, NULLIF(ColDia, ''), 105) IS NOT NULL
      AND (NULLIF(ColCargoTxt, '') IS NOT NULL OR NULLIF(ColAbonoTxt, '') IS NOT NULL)
  ),
  Final AS
  (
    SELECT
      RowIndex,
      Dia,
      Concepto = LEFT(LTRIM(RTRIM(Concepto)), 500),
      Cargo = CAST(CASE WHEN ISNULL(BankAbono, 0) > 0 THEN BankAbono ELSE 0 END AS DECIMAL(19,2)),
      Abono = CAST(CASE WHEN ISNULL(BankCargo, 0) > 0 THEN BankCargo ELSE 0 END AS DECIMAL(19,2)),
      Saldo = CAST(Saldo AS DECIMAL(19,2)),
      TipoDer = CASE WHEN ISNULL(BankAbono, 0) > 0 THEN 'I' ELSE 'E' END,
      MontoBase = CAST(CASE WHEN ISNULL(BankAbono, 0) > 0 THEN BankAbono ELSE BankCargo END AS DECIMAL(19,2))
    FROM Parsed
    WHERE COALESCE(BankCargo, BankAbono) IS NOT NULL
  ),
  WithOrdinal AS
  (
    SELECT
      RowIndex,
      Dia,
      Concepto,
      Cargo,
      Abono,
      Saldo,
      TipoDer,
      MontoBase,
      OccurrenceNo = ROW_NUMBER() OVER
      (
        PARTITION BY Dia, Concepto, MontoBase, TipoDer
        ORDER BY RowIndex DESC
      )
    FROM Final
  ),
  WithUID AS
  (
    SELECT
      RowIndex,
      Dia,
      Concepto,
      Cargo,
      Abono,
      Saldo,
      TipoDer,
      OccurrenceNo,
      UID = UPPER(CONVERT(VARCHAR(64), HASHBYTES(
        'SHA2_256',
        CONCAT(
          CONVERT(CHAR(10), Dia, 120), '|',
          TRIM(Concepto), '|',
          CONVERT(VARCHAR(32), MontoBase), '|',
          TipoDer, '|',
          OccurrenceNo, '|',
          'BBVA'
        )
      ), 2))
    FROM WithOrdinal
  ),
  Ordered AS
  (
    SELECT
      *,
      ROW_NUMBER() OVER (PARTITION BY Dia ORDER BY RowIndex DESC, UID ASC) AS RN_Dia,
      RowIndex AS Secuencia_Archivo
    FROM WithUID
  ),
  WithBalance AS
  (
    SELECT
      o.Dia,
      o.Concepto,
      o.Cargo,
      o.Abono,
      o.Saldo,
      o.TipoDer,
      o.UID,
      o.Secuencia_Archivo,
      o.RN_Dia,
      o.OccurrenceNo,
      Balance_OK = CASE
        WHEN LAG(o.Saldo) OVER (PARTITION BY o.Dia ORDER BY o.RN_Dia) IS NULL
             OR o.Saldo IS NULL THEN NULL
        WHEN ROUND(
               LAG(o.Saldo) OVER (PARTITION BY o.Dia ORDER BY o.RN_Dia)
               + o.Cargo - o.Abono,
               2
             ) = o.Saldo THEN CAST(1 AS BIT)
        ELSE CAST(0 AS BIT)
      END
    FROM Ordered AS o
  )
  INSERT INTO @S
  (
    Dia,
    Concepto,
    Cargo,
    Abono,
    Saldo,
    TipoDerivado,
    UID,
    Secuencia_Archivo,
    RN_Dia,
    OccurrenceNo,
    Balance_OK
  )
  SELECT
    Dia,
    Concepto,
    Cargo,
    Abono,
    Saldo,
    TipoDer,
    UID,
    Secuencia_Archivo,
    RN_Dia,
    OccurrenceNo,
    Balance_OK
  FROM WithBalance;

  IF EXISTS (SELECT 1 FROM @S GROUP BY UID HAVING COUNT(*) > 1)
    THROW 50003, 'Archivo BBVA contiene movimientos cuya identidad estable no es única.', 1;

  /*
    Migra al UID estable solamente los movimientos naturales que corresponden
    a la misma ocurrencia dentro del día. Saldo se excluye intencionalmente.
  */
  ;WITH ExistingNatural AS
  (
    SELECT
      M.Movimiento_ID,
      M.Dia,
      Concepto = TRIM(ISNULL(M.Concepto, '')),
      Cargo = CAST(ISNULL(M.Cargo, 0) AS DECIMAL(19,2)),
      Abono = CAST(ISNULL(M.Abono, 0) AS DECIMAL(19,2)),
      TipoDerivado = TRIM(ISNULL(M.Tipo, '')),
      OccurrenceNo = ROW_NUMBER() OVER
      (
        PARTITION BY
          M.Cuenta_Banco_ID,
          M.Dia,
          TRIM(ISNULL(M.Concepto, '')),
          CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN M.Cargo ELSE M.Abono END AS DECIMAL(19,2)),
          TRIM(ISNULL(M.Tipo, ''))
        ORDER BY
          CASE WHEN M.Secuencia_Archivo IS NULL THEN 1 ELSE 0 END,
          M.Secuencia_Archivo DESC,
          CASE WHEN M.Secuencia_Diaria IS NULL THEN 1 ELSE 0 END,
          M.Secuencia_Diaria ASC,
          M.Movimiento_ID ASC
      )
    FROM bancos.Movimientos AS M
    WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID
  )
  UPDATE M
  SET M.UID = S.UID
  FROM bancos.Movimientos AS M
  INNER JOIN ExistingNatural AS E
    ON E.Movimiento_ID = M.Movimiento_ID
  INNER JOIN @S AS S
    ON S.Dia = E.Dia
   AND S.Concepto = E.Concepto
   AND ISNULL(S.Cargo, 0) = E.Cargo
   AND ISNULL(S.Abono, 0) = E.Abono
   AND TRIM(ISNULL(S.TipoDerivado, '')) = E.TipoDerivado
   AND S.OccurrenceNo = E.OccurrenceNo
  WHERE ISNULL(M.UID, '') <> ISNULL(S.UID, '')
    AND NOT EXISTS
    (
      SELECT 1
      FROM bancos.Movimientos AS Stable
      WHERE Stable.Cuenta_Banco_ID = @Cuenta_Banco_ID
        AND Stable.UID = S.UID
        AND Stable.Movimiento_ID <> M.Movimiento_ID
    );

  DECLARE @Existing TABLE
  (
    UID      VARCHAR(64) PRIMARY KEY,
    Dia      DATE,
    SecDia   INT,
    SecClave BIGINT
  );

  INSERT INTO @Existing (UID, Dia, SecDia, SecClave)
  SELECT DISTINCT
    M.UID,
    M.Dia,
    M.Secuencia_Diaria,
    M.Secuencia_Clave
  FROM bancos.Movimientos AS M
  INNER JOIN @S AS S
    ON S.UID = M.UID
  WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID;

  DECLARE @existingMatches INT =
  (
    SELECT COUNT(*)
    FROM bancos.Movimientos AS M
    INNER JOIN @S AS S
      ON S.UID = M.UID
    WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID
  );

  DECLARE @historicalBalanceChanges INT =
  (
    SELECT COUNT(*)
    FROM bancos.Movimientos AS M
    INNER JOIN @S AS S
      ON S.UID = M.UID
    WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID
      AND
      (
           M.Saldo <> S.Saldo
        OR (M.Saldo IS NULL AND S.Saldo IS NOT NULL)
        OR (M.Saldo IS NOT NULL AND S.Saldo IS NULL)
      )
  );

  DECLARE @MaxSeq TABLE
  (
    Dia DATE PRIMARY KEY,
    MaxSeq INT
  );

  INSERT INTO @MaxSeq (Dia, MaxSeq)
  SELECT
    Dia,
    ISNULL(MAX(Secuencia_Diaria), 0)
  FROM bancos.Movimientos
  WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID
  GROUP BY Dia;

  DECLARE @New TABLE
  (
    Dia    DATE,
    UID    VARCHAR(64),
    RN_Dia INT,
    NewSeq INT
  );

  INSERT INTO @New (Dia, UID, RN_Dia, NewSeq)
  SELECT
    S.Dia,
    S.UID,
    S.RN_Dia,
    ROW_NUMBER() OVER (PARTITION BY S.Dia ORDER BY S.RN_Dia)
  FROM @S AS S
  LEFT JOIN @Existing AS E
    ON E.UID = S.UID
  WHERE E.UID IS NULL;

  DECLARE @ToMerge TABLE
  (
    Dia                     DATE,
    Concepto                VARCHAR(500),
    Cargo                   DECIMAL(19,2),
    Abono                   DECIMAL(19,2),
    Saldo                   DECIMAL(19,2),
    UID                     VARCHAR(64),
    TipoDerivado            CHAR(1),
    Secuencia_Archivo       INT,
    Balance_OK              BIT,
    Secuencia_Diaria_Assign INT,
    Secuencia_Clave_Assign  BIGINT
  );

  INSERT INTO @ToMerge
  SELECT
    S.Dia,
    S.Concepto,
    S.Cargo,
    S.Abono,
    S.Saldo,
    S.UID,
    S.TipoDerivado,
    S.Secuencia_Archivo,
    S.Balance_OK,
    CASE
      WHEN E.UID IS NOT NULL AND E.SecDia IS NOT NULL THEN E.SecDia
      ELSE ISNULL(MS.MaxSeq, 0) + ISNULL(N.NewSeq, 0)
    END,
    CAST(
      CONVERT(CHAR(8), S.Dia, 112) +
      RIGHT(
        '0000' + CAST(
          CASE
            WHEN E.UID IS NOT NULL AND E.SecDia IS NOT NULL THEN E.SecDia
            ELSE ISNULL(MS.MaxSeq, 0) + ISNULL(N.NewSeq, 0)
          END AS VARCHAR(4)
        ),
        4
      )
      AS BIGINT
    )
  FROM @S AS S
  LEFT JOIN @Existing AS E ON E.UID = S.UID
  LEFT JOIN @MaxSeq AS MS ON MS.Dia = S.Dia
  LEFT JOIN @New AS N ON N.UID = S.UID;

  DECLARE @changes TABLE (Action NVARCHAR(10));

  MERGE bancos.Movimientos WITH (HOLDLOCK) AS T
  USING @ToMerge AS S
    ON T.Cuenta_Banco_ID = @Cuenta_Banco_ID
   AND T.UID = S.UID
  WHEN MATCHED AND
  (
       ISNULL(T.Cargo, 0) <> ISNULL(S.Cargo, 0)
    OR ISNULL(T.Abono, 0) <> ISNULL(S.Abono, 0)
    OR ISNULL(T.Concepto, '') <> ISNULL(S.Concepto, '')
    OR T.Dia <> S.Dia
    OR ISNULL(T.Tipo, '') <> ISNULL(S.TipoDerivado, '')
    OR (T.Secuencia_Diaria IS NULL AND S.Secuencia_Diaria_Assign IS NOT NULL)
    OR (T.Secuencia_Clave IS NULL AND S.Secuencia_Clave_Assign IS NOT NULL)
  )
    THEN UPDATE SET
      T.Dia = S.Dia,
      T.Concepto = S.Concepto,
      T.Cargo = S.Cargo,
      T.Abono = S.Abono,
      T.Tipo = S.TipoDerivado,
      T.Secuencia_Diaria = ISNULL(T.Secuencia_Diaria, S.Secuencia_Diaria_Assign),
      T.Secuencia_Clave = ISNULL(T.Secuencia_Clave, S.Secuencia_Clave_Assign)
  WHEN NOT MATCHED BY TARGET
    THEN INSERT
    (
      Cuenta_Banco_ID,
      Dia,
      Concepto,
      Tipo,
      Cargo,
      Abono,
      Saldo,
      UID,
      RFC,
      Nombre_Banco,
      Numero_Cuenta,
      ArchivoHash,
      Fecha_Carga,
      Secuencia_Archivo,
      Secuencia_Diaria,
      Secuencia_Clave,
      Balance_OK
    )
    VALUES
    (
      @Cuenta_Banco_ID,
      S.Dia,
      S.Concepto,
      S.TipoDerivado,
      S.Cargo,
      S.Abono,
      S.Saldo,
      S.UID,
      @RFC,
      @Nombre_Banco,
      @Numero_Cuenta,
      @ArchivoHashHex,
      SYSUTCDATETIME(),
      S.Secuencia_Archivo,
      S.Secuencia_Diaria_Assign,
      S.Secuencia_Clave_Assign,
      S.Balance_OK
    )
  OUTPUT $action INTO @changes;

  DECLARE @inserted INT = (SELECT COUNT(*) FROM @changes WHERE Action = 'INSERT');
  DECLARE @updated INT = (SELECT COUNT(*) FROM @changes WHERE Action = 'UPDATE');

  COMMIT TRANSACTION;

  SELECT
    Insertados = @inserted,
    Actualizados = @updated,
    Cuenta_Banco_ID = @Cuenta_Banco_ID,
    Nombre_Banco = @Nombre_Banco,
    Numero_Cuenta = @Numero_Cuenta,
    ArchivoHash = @ArchivoHashHex,
    Balance_Warnings = (SELECT COUNT(*) FROM @S WHERE Balance_OK = 0),
    Coincidencias_Existentes = @existingMatches,
    Cambios_Saldo_Historico = @historicalBalanceChanges;
END
GO

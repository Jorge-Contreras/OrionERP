SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE [bancos].[Procesar_Movimientos_SchoolsFirst]
  @ArchivoTexto     VARCHAR(MAX),
  @Cuenta_Banco_ID  INT
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @ArchivoTexto IS NULL OR LEN(LTRIM(RTRIM(@ArchivoTexto))) = 0
    THROW 50000, 'El archivo esta vacio.', 1;

  IF NOT EXISTS (SELECT 1 FROM bancos.Cuentas_Banco WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID)
    THROW 50001, 'Cuenta_Banco_ID no existe.', 1;

  DECLARE @Nombre_Banco  VARCHAR(100),
          @Numero_Cuenta VARCHAR(50),
          @RFC           VARCHAR(50);

  SELECT
    @Nombre_Banco  = Nombre_Banco,
    @Numero_Cuenta = Numero_Cuenta,
    @RFC           = RFC
  FROM bancos.Cuentas_Banco
  WHERE Cuenta_Banco_ID = @Cuenta_Banco_ID;

  /*
    SchoolsFirst CSV esperado:
    Date,Description,Check#,Category,Currency,Amount,Balance

    Reglas:
    - Amount > 0  => ingreso (Cargo)
    - Amount < 0  => egreso  (Abono)
    - Se conserva Balance tal como viene en el CSV
    - Se usa RowIndex DESC para reconstruir el orden cronologico dentro del dia.
    - Si existen filas identicas dentro del mismo dia, se asigna un OccurrenceNo
      deterministico para que el archivo siga siendo idempotente.
  */

  DECLARE @src NVARCHAR(MAX) = CONVERT(NVARCHAR(MAX), ISNULL(@ArchivoTexto, ''));

  IF UNICODE(LEFT(@src, 1)) = 65279
    SET @src = SUBSTRING(@src, 2, LEN(@src));

  SET @src = REPLACE(REPLACE(@src, CHAR(13) + CHAR(10), CHAR(10)), CHAR(13), CHAR(10));
  SET @src = LTRIM(RTRIM(@src));

  IF @src = ''
    THROW 50002, 'El archivo CSV esta vacio despues de normalizar.', 1;

  DECLARE @ArchivoHashHex VARCHAR(64) =
    CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @src)), 2);

  DECLARE @xml XML = TRY_CAST(
    '<x><r>' +
      REPLACE(
        REPLACE(
          REPLACE(
            REPLACE(@src, '&', '&amp;'),
            '<', '&lt;'
          ),
          '>', '&gt;'
        ),
        CHAR(10),
        '</r><r>'
      ) +
    '</r></x>' AS XML);

  IF @xml IS NULL
    THROW 50003, 'Archivo SchoolsFirst invalido (no se pudo convertir a XML por lineas).', 1;

  DECLARE @Lines TABLE (
    RowIndex INT IDENTITY(1,1) PRIMARY KEY,
    LineText NVARCHAR(MAX) NOT NULL
  );

  INSERT INTO @Lines (LineText)
  SELECT LTRIM(RTRIM(T.N.value('.', 'nvarchar(max)')))
  FROM @xml.nodes('/x/r') AS T(N)
  WHERE LTRIM(RTRIM(T.N.value('.', 'nvarchar(max)'))) <> '';

  IF NOT EXISTS (SELECT 1 FROM @Lines)
    THROW 50004, 'No se encontraron lineas utiles en el CSV.', 1;

  DECLARE @ParsedCsv TABLE (
    RowIndex     INT            NOT NULL,
    DateTxt      NVARCHAR(50)   NULL,
    Description  NVARCHAR(500)  NULL,
    CheckNoTxt   NVARCHAR(100)  NULL,
    CategoryTxt  NVARCHAR(200)  NULL,
    CurrencyTxt  NVARCHAR(50)   NULL,
    AmountTxt    NVARCHAR(100)  NULL,
    BalanceTxt   NVARCHAR(100)  NULL
  );

  DECLARE @MaxRow INT = (SELECT MAX(RowIndex) FROM @Lines);
  DECLARE @i INT = 1;

  WHILE @i <= @MaxRow
  BEGIN
    DECLARE @line NVARCHAR(MAX);
    SELECT @line = LineText FROM @Lines WHERE RowIndex = @i;

    IF UPPER(REPLACE(@line, '"', '')) NOT LIKE 'DATE,DESCRIPTION,CHECK#,CATEGORY,CURRENCY,AMOUNT,BALANCE%'
    BEGIN
      DECLARE @work NVARCHAR(MAX) = @line + N',';
      DECLARE @len INT = LEN(@work);
      DECLARE @pos INT = 1;
      DECLARE @inQuotes BIT = 0;
      DECLARE @fieldNo INT = 1;
      DECLARE @field NVARCHAR(MAX) = N'';

      DECLARE @f1 NVARCHAR(MAX) = NULL,
              @f2 NVARCHAR(MAX) = NULL,
              @f3 NVARCHAR(MAX) = NULL,
              @f4 NVARCHAR(MAX) = NULL,
              @f5 NVARCHAR(MAX) = NULL,
              @f6 NVARCHAR(MAX) = NULL,
              @f7 NVARCHAR(MAX) = NULL;

      WHILE @pos <= @len
      BEGIN
        DECLARE @ch  NCHAR(1) = SUBSTRING(@work, @pos, 1);
        DECLARE @nch NCHAR(1) = CASE WHEN @pos < @len THEN SUBSTRING(@work, @pos + 1, 1) ELSE N'' END;

        IF @ch = N'"'
        BEGIN
          IF @inQuotes = 1 AND @nch = N'"'
          BEGIN
            SET @field += N'"';
            SET @pos += 1;
          END
          ELSE
          BEGIN
            SET @inQuotes = CASE WHEN @inQuotes = 1 THEN 0 ELSE 1 END;
          END
        END
        ELSE IF @ch = N',' AND @inQuotes = 0
        BEGIN
          DECLARE @cleanField NVARCHAR(MAX) = LTRIM(RTRIM(@field));

          IF @fieldNo = 1 SET @f1 = @cleanField;
          IF @fieldNo = 2 SET @f2 = @cleanField;
          IF @fieldNo = 3 SET @f3 = @cleanField;
          IF @fieldNo = 4 SET @f4 = @cleanField;
          IF @fieldNo = 5 SET @f5 = @cleanField;
          IF @fieldNo = 6 SET @f6 = @cleanField;
          IF @fieldNo = 7 SET @f7 = @cleanField;

          SET @field = N'';
          SET @fieldNo += 1;
        END
        ELSE
        BEGIN
          SET @field += @ch;
        END

        SET @pos += 1;
      END

      IF TRY_CONVERT(DATE, NULLIF(@f1, ''), 101) IS NOT NULL
      BEGIN
        INSERT INTO @ParsedCsv (
          RowIndex, DateTxt, Description, CheckNoTxt, CategoryTxt, CurrencyTxt, AmountTxt, BalanceTxt
        )
        VALUES (
          @i,
          NULLIF(@f1, ''),
          LEFT(NULLIF(@f2, ''), 500),
          NULLIF(@f3, ''),
          NULLIF(@f4, ''),
          NULLIF(@f5, ''),
          NULLIF(@f6, ''),
          NULLIF(@f7, '')
        );
      END
    END

    SET @i += 1;
  END

  IF NOT EXISTS (SELECT 1 FROM @ParsedCsv)
    THROW 50005, 'No se pudieron interpretar movimientos validos del CSV de SchoolsFirst.', 1;

  BEGIN TRAN;

  DECLARE @S TABLE (
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

  ;WITH Parsed AS (
    SELECT
      RowIndex,
      Dia = TRY_CONVERT(DATE, DateTxt, 101),
      Concepto = LEFT(LTRIM(RTRIM(ISNULL(Description, ''))), 500),
      AmountValue = TRY_CONVERT(DECIMAL(19,5), REPLACE(REPLACE(AmountTxt, ',', ''), '$', '')),
      BalanceValue = TRY_CONVERT(DECIMAL(19,5), REPLACE(REPLACE(BalanceTxt, ',', ''), '$', ''))
    FROM @ParsedCsv
  ),
  Final AS (
    SELECT
      RowIndex,
      Dia,
      Concepto,
      Cargo = CAST(CASE WHEN AmountValue > 0 THEN AmountValue ELSE 0 END AS DECIMAL(19,2)),
      Abono = CAST(CASE WHEN AmountValue < 0 THEN ABS(AmountValue) ELSE 0 END AS DECIMAL(19,2)),
      Saldo = CAST(BalanceValue AS DECIMAL(19,2)),
      TipoDer = CASE WHEN AmountValue > 0 THEN 'I' ELSE 'E' END,
      MontoBase = CAST(ABS(AmountValue) AS DECIMAL(19,2)),
      SaldoBase = CAST(ISNULL(BalanceValue, 0) AS DECIMAL(19,2))
    FROM Parsed
    WHERE Dia IS NOT NULL
      AND AmountValue IS NOT NULL
  ),
  WithOrdinal AS (
    SELECT
      RowIndex,
      Dia,
      Concepto,
      Cargo,
      Abono,
      Saldo,
      TipoDer,
      MontoBase,
      SaldoBase,
      OccurrenceNo = ROW_NUMBER() OVER (
        PARTITION BY Dia, Concepto, MontoBase, TipoDer, SaldoBase
        ORDER BY RowIndex DESC
      )
    FROM Final
  ),
  WithUID AS (
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
          CONVERT(VARCHAR(32), SaldoBase), '|',
          OccurrenceNo, '|',
          'SCHOOLSFIRST'
        )
      ), 2))
    FROM WithOrdinal
  ),
  Ordered AS (
    SELECT
      *,
      ROW_NUMBER() OVER (PARTITION BY Dia ORDER BY RowIndex DESC, UID ASC) AS RN_Dia,
      RowIndex AS Secuencia_Archivo
    FROM WithUID
  ),
  WithBalance AS (
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
      PrevSaldo = LAG(o.Saldo) OVER (PARTITION BY o.Dia ORDER BY o.RN_Dia),
      Balance_OK = CASE
                     WHEN LAG(o.Saldo) OVER (PARTITION BY o.Dia ORDER BY o.RN_Dia) IS NULL
                          OR o.Saldo IS NULL THEN NULL
                     WHEN ROUND(
                            LAG(o.Saldo) OVER (PARTITION BY o.Dia ORDER BY o.RN_Dia)
                            + o.Cargo - o.Abono,
                            2
                          ) = o.Saldo
                       THEN CAST(1 AS BIT)
                     ELSE CAST(0 AS BIT)
                   END
    FROM Ordered o
  )
  INSERT INTO @S (
    Dia, Concepto, Cargo, Abono, Saldo, TipoDerivado, UID, Secuencia_Archivo, RN_Dia, OccurrenceNo, Balance_OK
  )
  SELECT
    Dia,
    Concepto,
    Cargo,
    Abono,
    Saldo,
    CASE WHEN Cargo > 0 THEN 'I' ELSE 'E' END,
    UID,
    Secuencia_Archivo,
    RN_Dia,
    OccurrenceNo,
    Balance_OK
  FROM WithBalance;

  ;WITH ExistingNatural AS (
    SELECT
      M.Movimiento_ID,
      Dia = M.Dia,
      Concepto = TRIM(ISNULL(M.Concepto, '')),
      Cargo = CAST(ISNULL(M.Cargo, 0) AS DECIMAL(19,2)),
      Abono = CAST(ISNULL(M.Abono, 0) AS DECIMAL(19,2)),
      Saldo = CAST(ISNULL(M.Saldo, 0) AS DECIMAL(19,2)),
      TipoDerivado = TRIM(ISNULL(M.Tipo, '')),
      OccurrenceNo = ROW_NUMBER() OVER (
        PARTITION BY
          M.Cuenta_Banco_ID,
          M.Dia,
          TRIM(ISNULL(M.Concepto, '')),
          CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN M.Cargo ELSE M.Abono END AS DECIMAL(19,2)),
          TRIM(ISNULL(M.Tipo, '')),
          CAST(ISNULL(M.Saldo, 0) AS DECIMAL(19,2))
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
  JOIN ExistingNatural AS E
    ON E.Movimiento_ID = M.Movimiento_ID
  JOIN @S AS S
    ON S.Dia = E.Dia
   AND S.Concepto = E.Concepto
   AND ISNULL(S.Cargo, 0) = ISNULL(E.Cargo, 0)
   AND ISNULL(S.Abono, 0) = ISNULL(E.Abono, 0)
   AND ISNULL(S.Saldo, 0) = ISNULL(E.Saldo, 0)
   AND TRIM(ISNULL(S.TipoDerivado, '')) = E.TipoDerivado
   AND S.OccurrenceNo = E.OccurrenceNo
  WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID
    AND ISNULL(M.UID, '') <> ISNULL(S.UID, '');

  DECLARE @Existing TABLE (
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
  JOIN @S AS S
    ON S.UID = M.UID
  WHERE M.Cuenta_Banco_ID = @Cuenta_Banco_ID;

  DECLARE @MaxSeq TABLE (
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

  DECLARE @New TABLE (
    Dia    DATE,
    UID    VARCHAR(64),
    RN_Dia INT,
    NewSeq INT
  );

  INSERT INTO @New (Dia, UID, RN_Dia, NewSeq)
  SELECT
    s.Dia,
    s.UID,
    s.RN_Dia,
    ROW_NUMBER() OVER (PARTITION BY s.Dia ORDER BY s.RN_Dia) AS NewSeq
  FROM @S AS s
  LEFT JOIN @Existing AS e
    ON e.UID = s.UID
  WHERE e.UID IS NULL;

  DECLARE @ToMerge TABLE (
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
    s.Dia,
    s.Concepto,
    s.Cargo,
    s.Abono,
    s.Saldo,
    s.UID,
    s.TipoDerivado,
    s.Secuencia_Archivo,
    s.Balance_OK,
    CASE
      WHEN e.UID IS NOT NULL AND e.SecDia IS NOT NULL THEN e.SecDia
      ELSE ISNULL(ms.MaxSeq, 0) + ISNULL(n.NewSeq, 0)
    END AS Secuencia_Diaria_Assign,
    CAST(
      CONVERT(CHAR(8), s.Dia, 112) +
      RIGHT(
        '0000' + CAST(
          CASE
            WHEN e.UID IS NOT NULL AND e.SecDia IS NOT NULL THEN e.SecDia
            ELSE ISNULL(ms.MaxSeq, 0) + ISNULL(n.NewSeq, 0)
          END AS VARCHAR(4)
        ),
        4
      )
      AS BIGINT
    ) AS Secuencia_Clave_Assign
  FROM @S AS s
  LEFT JOIN @Existing AS e ON e.UID = s.UID
  LEFT JOIN @MaxSeq  AS ms ON ms.Dia = s.Dia
  LEFT JOIN @New     AS n  ON n.UID = s.UID;

  DECLARE @changes TABLE (Action NVARCHAR(10));

  MERGE bancos.Movimientos AS T
  USING @ToMerge AS S
    ON T.Cuenta_Banco_ID = @Cuenta_Banco_ID
   AND T.UID = S.UID
  WHEN MATCHED AND (
         ISNULL(T.Cargo, 0) <> ISNULL(S.Cargo, 0)
      OR ISNULL(T.Abono, 0) <> ISNULL(S.Abono, 0)
      OR ISNULL(T.Saldo, 0) <> ISNULL(S.Saldo, 0)
      OR ISNULL(T.Concepto, '') <> ISNULL(S.Concepto, '')
      OR T.Dia <> S.Dia
      OR ISNULL(T.Balance_OK, 2) <> ISNULL(S.Balance_OK, 2)
      OR ISNULL(T.Tipo, '') <> ISNULL(S.TipoDerivado, '')
      OR (T.Secuencia_Diaria IS NULL AND S.Secuencia_Diaria_Assign IS NOT NULL)
      OR (T.Secuencia_Clave  IS NULL AND S.Secuencia_Clave_Assign  IS NOT NULL)
  )
    THEN UPDATE SET
      T.Dia              = S.Dia,
      T.Concepto         = S.Concepto,
      T.Cargo            = S.Cargo,
      T.Abono            = S.Abono,
      T.Saldo            = S.Saldo,
      T.Tipo             = S.TipoDerivado,
      T.Balance_OK       = S.Balance_OK,
      T.ArchivoHash      = @ArchivoHashHex,
      T.Fecha_Carga      = SYSUTCDATETIME(),
      T.Secuencia_Diaria = ISNULL(T.Secuencia_Diaria, S.Secuencia_Diaria_Assign),
      T.Secuencia_Clave  = ISNULL(T.Secuencia_Clave,  S.Secuencia_Clave_Assign)
  WHEN NOT MATCHED BY TARGET
    THEN INSERT (
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
    VALUES (
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
  DECLARE @updated  INT = (SELECT COUNT(*) FROM @changes WHERE Action = 'UPDATE');

  COMMIT TRAN;

  SELECT
    Insertados       = @inserted,
    Actualizados     = @updated,
    Cuenta_Banco_ID  = @Cuenta_Banco_ID,
    Nombre_Banco     = @Nombre_Banco,
    Numero_Cuenta    = @Numero_Cuenta,
    ArchivoHash      = @ArchivoHashHex,
    Balance_Warnings = (SELECT COUNT(*) FROM @S WHERE Balance_OK = 0);
END
GO

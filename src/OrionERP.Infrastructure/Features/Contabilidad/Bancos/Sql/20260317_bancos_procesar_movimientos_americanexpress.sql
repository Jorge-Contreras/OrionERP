CREATE OR ALTER PROCEDURE [bancos].[Procesar_Movimientos_AmericanExpress]
  @ArchivoTexto    VARCHAR(MAX),
  @Cuenta_Banco_ID INT
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
    American Express CSV esperado:
    Date,Description,Amount

    Reglas:
    - Amount > 0  => cargo a la tarjeta => egreso (Abono)
    - Amount < 0  => pago, reembolso o credito => ingreso (Cargo)
    - No hay saldo en el archivo, por lo que Saldo y Balance_OK quedan NULL
    - El archivo normalmente viene del mas nuevo al mas antiguo, asi que
      usamos RowIndex DESC para reconstruir el orden cronologico dentro del dia
    - Para distinguir cargos repetidos el mismo dia, el UID incluye un
      consecutivo por ocurrencia dentro de la llave natural
  */

  DECLARE @src NVARCHAR(MAX) = CONVERT(NVARCHAR(MAX), ISNULL(@ArchivoTexto, ''));

  IF LEN(@src) > 0 AND UNICODE(LEFT(@src, 1)) = 65279
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
    THROW 50003, 'Archivo American Express invalido (no se pudo convertir a XML por lineas).', 1;

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
    RowIndex     INT           NOT NULL,
    DateTxt      NVARCHAR(50)  NULL,
    Description  NVARCHAR(500) NULL,
    AmountTxt    NVARCHAR(100) NULL
  );

  DECLARE @MaxRow INT = (SELECT MAX(RowIndex) FROM @Lines);
  DECLARE @i INT = 1;
  DECLARE @Fields TABLE (
    FieldNo INT NOT NULL,
    FieldText NVARCHAR(MAX) NULL
  );

  WHILE @i <= @MaxRow
  BEGIN
    DECLARE @line NVARCHAR(MAX);
    SELECT @line = LineText FROM @Lines WHERE RowIndex = @i;

    IF UPPER(REPLACE(@line, '"', '')) NOT LIKE 'DATE,DESCRIPTION,AMOUNT%'
    BEGIN
      DECLARE @work NVARCHAR(MAX) = @line + N',';
      DECLARE @len INT = LEN(@work);
      DECLARE @pos INT = 1;
      DECLARE @inQuotes BIT = 0;
      DECLARE @field NVARCHAR(MAX) = N'';
      DECLARE @fieldNo INT = 1;

      DELETE FROM @Fields;

      WHILE @pos <= @len
      BEGIN
        DECLARE @ch NCHAR(1) = SUBSTRING(@work, @pos, 1);
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
          INSERT INTO @Fields (FieldNo, FieldText)
          VALUES (@fieldNo, LTRIM(RTRIM(@field)));

          SET @field = N'';
          SET @fieldNo += 1;
        END
        ELSE
        BEGIN
          SET @field += @ch;
        END

        SET @pos += 1;
      END

      DECLARE @FieldCount INT = (SELECT COUNT(*) FROM @Fields);
      DECLARE @LastFieldNo INT = (SELECT MAX(FieldNo) FROM @Fields);

      IF @FieldCount >= 3
      BEGIN
        DECLARE @f1 NVARCHAR(MAX);
        DECLARE @f2 NVARCHAR(MAX);
        DECLARE @f3 NVARCHAR(MAX);

        SELECT @f1 = NULLIF(FieldText, N'')
        FROM @Fields
        WHERE FieldNo = 1;

        SELECT @f3 = NULLIF(FieldText, N'')
        FROM @Fields
        WHERE FieldNo = @LastFieldNo;

        SELECT @f2 = NULLIF(
          STUFF((
            SELECT N',' + F.FieldText
            FROM @Fields AS F
            WHERE F.FieldNo BETWEEN 2 AND @LastFieldNo - 1
            ORDER BY F.FieldNo
            FOR XML PATH(''), TYPE
          ).value('.', 'nvarchar(max)'), 1, 1, N''),
          N''
        );

        IF TRY_CONVERT(DATE, @f1, 101) IS NOT NULL
           AND TRY_CONVERT(
                 DECIMAL(19,5),
                 REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@f3, ',', ''), '$', ''), '(', '-'), ')', ''), CHAR(9), ''), ' ', '')
               ) IS NOT NULL
        BEGIN
          INSERT INTO @ParsedCsv (RowIndex, DateTxt, Description, AmountTxt)
          VALUES (
            @i,
            @f1,
            LEFT(ISNULL(@f2, N''), 500),
            @f3
          );
        END
      END
    END

    SET @i += 1;
  END

  IF NOT EXISTS (SELECT 1 FROM @ParsedCsv)
    THROW 50005, 'No se pudieron interpretar movimientos validos del CSV de American Express.', 1;

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
    Balance_OK         BIT
  );

  ;WITH Parsed AS (
    SELECT
      RowIndex,
      Dia = TRY_CONVERT(DATE, DateTxt, 101),
      Concepto = LEFT(LTRIM(RTRIM(ISNULL(Description, ''))), 500),
      AmountValue = TRY_CONVERT(
        DECIMAL(19,5),
        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(AmountTxt, ',', ''), '$', ''), '(', '-'), ')', ''), CHAR(9), ''), ' ', '')
      )
    FROM @ParsedCsv
  ),
  Final AS (
    SELECT
      RowIndex,
      Dia,
      Concepto,
      Cargo = CAST(CASE WHEN AmountValue < 0 THEN ABS(AmountValue) ELSE 0 END AS DECIMAL(19,2)),
      Abono = CAST(CASE WHEN AmountValue > 0 THEN AmountValue ELSE 0 END AS DECIMAL(19,2)),
      Saldo = CAST(NULL AS DECIMAL(19,2)),
      TipoDer = CASE WHEN AmountValue < 0 THEN 'I' ELSE 'E' END,
      MontoBase = CAST(ABS(AmountValue) AS DECIMAL(19,2))
    FROM Parsed
    WHERE Dia IS NOT NULL
      AND AmountValue IS NOT NULL
      AND AmountValue <> 0
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
      OccurrenceNo = ROW_NUMBER() OVER (
        PARTITION BY Dia, Concepto, MontoBase, TipoDer
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
      UID = UPPER(CONVERT(VARCHAR(64), HASHBYTES(
        'SHA2_256',
        CONCAT(
          CONVERT(CHAR(10), Dia, 120), '|',
          TRIM(Concepto), '|',
          CONVERT(VARCHAR(32), MontoBase), '|',
          TipoDer, '|',
          OccurrenceNo, '|',
          'AMERICANEXPRESS'
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
  )
  INSERT INTO @S (
    Dia, Concepto, Cargo, Abono, Saldo, TipoDerivado, UID, Secuencia_Archivo, RN_Dia, Balance_OK
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
    NULL AS Balance_OK
  FROM Ordered;

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
      ) AS BIGINT
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
    Balance_Warnings = 0;
END

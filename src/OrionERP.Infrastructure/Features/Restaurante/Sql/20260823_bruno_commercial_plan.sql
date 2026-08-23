/*
  Plan comercial de Bruno's: correcciones de configuración y carga de promociones.

  Autorizado por Dirección General el 23 de agosto de 2026 (puntos B1-B6 y B8 del
  informe "Membresía, promociones y plan comercial").

  Contenido:
    1. Corrige restaurante.Site.OperationalDayCutoff de 22:00 a 04:00 (valor por
       defecto del sistema). Sin esta corrección toda la venta diurna queda
       fechada un día antes y los reportes por fecha operativa salen corridos.
    2. Limita el código TUCUMPLE a un uso por socio y limpia los valores muertos
       de PercentOff/BuyQuantity/PayQuantity de esa promoción.
    3. Retira el 2x1 de chilaquiles (promociones 1 y 4) y archiva el borrador
       vencido de chicken fingers (promoción 2).
    4. Carga las cinco promociones aprobadas con horarios, alcance y condiciones
       públicas completas.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="0" -i 20260823_bruno_commercial_plan.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="1" -i 20260823_bruno_commercial_plan.sql

  ApplyChanges=0 ejecuta y valida todo dentro de una transacción que se revierte.
  ApplyChanges=1 confirma. Producción requiere respaldo previo.
  -f 65001 es obligatorio para conservar los literales Unicode del archivo UTF-8.

  El script es idempotente: puede volver a ejecutarse sin duplicar promociones.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @LockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51300, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51301, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51302, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51303, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:CommercialPlan:20260823',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51304, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  DECLARE @SiteId int =
    (SELECT Id FROM restaurante.Site WHERE Rfc=@BrunoRfc AND SiteCode='BRUNOS-01');
  IF @SiteId IS NULL
    THROW 51305, 'No existe la sede BRUNOS-01 para el RFC de Bruno.', 1;

  DECLARE @Author nvarchar(256) = N'20260823_bruno_commercial_plan';

  /* ------------------------------------------------------------------
     1. Corte del día operativo: 22:00 -> 04:00
     ------------------------------------------------------------------ */
  UPDATE restaurante.Site
  SET OperationalDayCutoff = '04:00'
  WHERE Rfc=@BrunoRfc AND Id=@SiteId AND OperationalDayCutoff <> '04:00';

  /* ------------------------------------------------------------------
     2. Control del beneficio de cumpleaños
     ------------------------------------------------------------------ */
  UPDATE restaurante.PromotionCode
  SET PerMemberLimit = 1
  WHERE Rfc=@BrunoRfc AND Code='TUCUMPLE' AND (PerMemberLimit IS NULL OR PerMemberLimit <> 1);

  UPDATE promotion
  SET PercentOff = 0, BuyQuantity = 0, PayQuantity = 0,
      UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @Author
  FROM restaurante.Promotion promotion
  WHERE promotion.Rfc=@BrunoRfc
    AND promotion.RuleType='FixedAmountOff'
    AND (promotion.PercentOff <> 0 OR promotion.BuyQuantity <> 0 OR promotion.PayQuantity <> 0);

  /* ------------------------------------------------------------------
     3. Retiro del 2x1 de chilaquiles y archivo del borrador vencido
     ------------------------------------------------------------------ */
  UPDATE restaurante.Promotion
  SET [Status]='Paused', UpdatedAt=SYSUTCDATETIME(), UpdatedBy=@Author
  WHERE Rfc=@BrunoRfc
    AND [Name] IN (N'Chilaquiles 2x1 · martes y miércoles', N'Chilaquiles 2x1 con Codigo')
    AND [Status] <> 'Paused';

  UPDATE restaurante.Promotion
  SET [Status]='Expired', UpdatedAt=SYSUTCDATETIME(), UpdatedBy=@Author
  WHERE Rfc=@BrunoRfc AND [Name]=N'Chicken fingers 3x2' AND [Status] <> 'Expired';

  /* ------------------------------------------------------------------
     4. Carga de las cinco promociones aprobadas
     ------------------------------------------------------------------ */
  DECLARE @PromotionId bigint;

  /* ---- P1 · Almuerzo Club Bruno's ---- */
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Almuerzo Club Bruno''s')
  BEGIN
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
       ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
       IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
       BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy)
    VALUES
      (@BrunoRfc,@SiteId,N'Almuerzo Club Bruno''s',
       N'Como socio de Club Bruno recibe $25 de descuento en tu cuenta de $160 o más, de martes a domingo de 8:00 a 11:00.',
       N'Vigencia del 24 de agosto de 2026 al 24 de febrero de 2027. Aplica exclusivamente a socios de Club Bruno con cuenta activa y correo verificado, identificados antes de cobrar. Válido de martes a domingo de 8:00 a 11:00 horas. Requiere consumo mínimo de $160.00 en mercancía participante; no participan productos capturados como concepto libre en caja, propinas ni cargos por servicio. Descuento de $25.00 por cuenta. No acumulable con otras promociones sobre las mismas unidades, no canjeable por efectivo y sin cambio ni devolución. Sujeta a disponibilidad. Precios con IVA incluido. Bruno''s Garden & Snacks S.A. de C.V. puede modificar o dar por terminada esta promoción avisando en brunosgarden.com.',
       'Active','FixedAmountOff',50,
       '2026-08-24T00:00:00','2027-02-24T00:00:00',1,1,1,0,
       0,1,0,0,0,25.00,
       0,0,160.00,NULL,@Author,@Author);
    SET @PromotionId = SCOPE_IDENTITY();
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    VALUES (@BrunoRfc,@PromotionId,2,'08:00','11:00'),
           (@BrunoRfc,@PromotionId,3,'08:00','11:00'),
           (@BrunoRfc,@PromotionId,4,'08:00','11:00'),
           (@BrunoRfc,@PromotionId,5,'08:00','11:00'),
           (@BrunoRfc,@PromotionId,6,'08:00','11:00'),
           (@BrunoRfc,@PromotionId,0,'08:00','11:00');
  END;

  /* ---- P2 · Miércoles de Producto Estrella ---- */
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Miércoles de Producto Estrella')
  BEGIN
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
       ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
       IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
       BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy)
    VALUES
      (@BrunoRfc,@SiteId,N'Miércoles de Producto Estrella',
       N'Todos los miércoles, 15% de descuento en alimentos con consumo mínimo de $150.',
       N'Vigencia del 26 de agosto al 26 de noviembre de 2026. Válido únicamente los miércoles de 12:00 a 21:00 horas. Aplica 15% de descuento sobre los alimentos participantes de la sección Comida y Desayuno. No aplica en bebidas, cervezas, cigarros ni en productos capturados como concepto libre en caja. Requiere consumo mínimo de $150.00 en productos participantes. No acumulable con otras promociones sobre las mismas unidades. No canjeable por efectivo. Sujeta a disponibilidad de los productos participantes. Precios con IVA incluido. Consulta la lista vigente de productos participantes en el restaurante o en brunosgarden.com.',
       'Active','PercentOff',40,
       '2026-08-26T00:00:00','2026-11-26T00:00:00',1,1,0,0,
       0,1,0,0,15.0000,0,
       0,0,150.00,NULL,@Author,@Author);
    SET @PromotionId = SCOPE_IDENTITY();
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    VALUES (@BrunoRfc,@PromotionId,3,'12:00','21:00');
    INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId)
    SELECT @BrunoRfc,@PromotionId,value
    FROM (VALUES (1),(3),(4),(5),(6),(26),(27),(30),(43),(46),(52)) AS ids(value);
  END;

  /* ---- P3 · Bienvenida Club Bruno's ---- */
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Bienvenida Club Bruno''s')
  BEGIN
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
       ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
       IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
       BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy)
    VALUES
      (@BrunoRfc,@SiteId,N'Bienvenida Club Bruno''s',
       N'Regístrate en Club Bruno y recibe $50 de descuento en tu primera cuenta de $200 o más.',
       N'Vigencia del 24 de agosto de 2026 al 24 de agosto de 2027, o hasta agotar 300 canjes, lo que ocurra primero. Beneficio de bienvenida limitado a un solo uso por socio, válido una única vez durante la vida de la membresía. Requiere cuenta de Club Bruno activa con correo verificado, identificación del socio antes de cobrar y presentación del código BIENVENIDA en caja. Consumo mínimo de $200.00 en mercancía participante; no participan productos capturados como concepto libre en caja, propinas ni cargos por servicio. Válido de martes a sábado de 8:00 a 22:00 horas y domingo de 8:00 a 13:00 horas. No acumulable con otras promociones sobre las mismas unidades, no canjeable por efectivo y no transferible. Sujeta a disponibilidad. Precios con IVA incluido.',
       'Active','FixedAmountOff',90,
       '2026-08-24T00:00:00','2027-08-24T00:00:00',1,1,1,1,
       0,1,0,0,0,50.00,
       0,0,200.00,300,@Author,@Author);
    SET @PromotionId = SCOPE_IDENTITY();
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    VALUES (@BrunoRfc,@PromotionId,2,'08:00','22:00'),
           (@BrunoRfc,@PromotionId,3,'08:00','22:00'),
           (@BrunoRfc,@PromotionId,4,'08:00','22:00'),
           (@BrunoRfc,@PromotionId,5,'08:00','22:00'),
           (@BrunoRfc,@PromotionId,6,'08:00','22:00'),
           (@BrunoRfc,@PromotionId,0,'08:00','13:00');
    INSERT restaurante.PromotionCode(Rfc,PromotionId,Code,GlobalLimit,PerMemberLimit,IsActive)
    VALUES (@BrunoRfc,@PromotionId,'BIENVENIDA',300,1,1);
  END;

  /* ---- P4 · Jueves y Viernes de Parrilla ---- */
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Jueves y Viernes de Parrilla')
  BEGIN
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
       ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
       IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
       BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy)
    VALUES
      (@BrunoRfc,@SiteId,N'Jueves y Viernes de Parrilla',
       N'Dos hamburguesas a elegir por $159, jueves y viernes de 17:00 a 22:00.',
       N'Vigencia del 27 de agosto al 27 de diciembre de 2026. Válido jueves y viernes de 17:00 a 22:00 horas. El precio de $159.00 aplica por cada dos hamburguesas participantes: Hamburguesa de Sirlón Bruno''s, Hamburguesa de Arrachera y Chicken Finger Burger. Si se ordena un número impar de hamburguesas participantes, la unidad restante se cobra a precio de lista. El paquete se arma automáticamente con las unidades de mayor precio. No incluye bebidas, guarniciones ni complementos. No acumulable con otras promociones sobre las mismas unidades. No canjeable por efectivo. Sujeta a disponibilidad. Precios con IVA incluido.',
       'Active','FixedBundlePrice',60,
       '2026-08-27T00:00:00','2026-12-27T00:00:00',1,1,0,0,
       0,1,2,0,0,0,
       159.00,0,0,NULL,@Author,@Author);
    SET @PromotionId = SCOPE_IDENTITY();
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    VALUES (@BrunoRfc,@PromotionId,4,'17:00','22:00'),
           (@BrunoRfc,@PromotionId,5,'17:00','22:00');
    INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId)
    SELECT @BrunoRfc,@PromotionId,value
    FROM (VALUES (3),(4),(6)) AS ids(value);
  END;

  /* ---- P5 · Merienda en el Jardín ---- */
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Merienda en el Jardín')
  BEGIN
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
       ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
       IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
       BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy)
    VALUES
      (@BrunoRfc,@SiteId,N'Merienda en el Jardín',
       N'De 15:00 a 17:00, 20% de descuento llevando dos o más postres, cafés o bebidas participantes.',
       N'Vigencia del 25 de agosto al 25 de noviembre de 2026. Válido de martes a domingo de 15:00 a 17:00 horas. Aplica 20% de descuento al ordenar dos o más unidades de los productos participantes: Pan de Elote con Helado, Waffle con Helado, Helado Bruno''s, Buñuelos, Malteada, Café Americano, Chamoyada y Mangoyada. No aplica en alimentos, cervezas ni bebidas con alcohol. No acumulable con otras promociones sobre las mismas unidades. No canjeable por efectivo. Sujeta a disponibilidad. Precios con IVA incluido.',
       'Active','PercentOff',30,
       '2026-08-25T00:00:00','2026-11-25T00:00:00',1,1,0,0,
       0,1,0,0,20.0000,0,
       0,2,0,NULL,@Author,@Author);
    SET @PromotionId = SCOPE_IDENTITY();
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    VALUES (@BrunoRfc,@PromotionId,2,'15:00','17:00'),
           (@BrunoRfc,@PromotionId,3,'15:00','17:00'),
           (@BrunoRfc,@PromotionId,4,'15:00','17:00'),
           (@BrunoRfc,@PromotionId,5,'15:00','17:00'),
           (@BrunoRfc,@PromotionId,6,'15:00','17:00'),
           (@BrunoRfc,@PromotionId,0,'15:00','17:00');
    INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId)
    SELECT @BrunoRfc,@PromotionId,value
    FROM (VALUES (15),(16),(20),(22),(34),(58),(59),(60)) AS ids(value);
  END;

  /* ------------------------------------------------------------------
     Validaciones
     ------------------------------------------------------------------ */
  IF EXISTS(SELECT 1 FROM restaurante.Site WHERE Rfc=@BrunoRfc AND Id=@SiteId AND OperationalDayCutoff <> '04:00')
    THROW 51306, 'El corte del día operativo no quedó en 04:00.', 1;
  IF EXISTS(SELECT 1 FROM restaurante.PromotionCode WHERE Rfc=@BrunoRfc AND Code='TUCUMPLE' AND PerMemberLimit <> 1)
    THROW 51307, 'El código TUCUMPLE no quedó limitado a un uso por socio.', 1;
  IF EXISTS(SELECT 1 FROM restaurante.Promotion
            WHERE Rfc=@BrunoRfc AND [Name] LIKE N'Chilaquiles 2x1%' AND [Status] IN('Active','Scheduled'))
    THROW 51308, 'El 2x1 de chilaquiles sigue activo.', 1;
  IF (SELECT COUNT(*) FROM restaurante.Promotion
      WHERE Rfc=@BrunoRfc AND [Status]='Active'
        AND [Name] IN (N'Almuerzo Club Bruno''s',N'Miércoles de Producto Estrella',
                       N'Bienvenida Club Bruno''s',N'Jueves y Viernes de Parrilla',
                       N'Merienda en el Jardín')) <> 5
    THROW 51309, 'No quedaron activas las cinco promociones aprobadas.', 1;
  IF NOT EXISTS(SELECT 1 FROM restaurante.PromotionCode
                WHERE Rfc=@BrunoRfc AND Code='BIENVENIDA' AND PerMemberLimit=1 AND GlobalLimit=300 AND IsActive=1)
    THROW 51310, 'El código BIENVENIDA no quedó configurado correctamente.', 1;
  IF EXISTS(SELECT 1 FROM restaurante.Promotion promotion
            WHERE promotion.Rfc=@BrunoRfc AND promotion.[Status] IN('Active','Scheduled')
              AND (LEN(LTRIM(RTRIM(promotion.PublicTerms))) < 80))
    THROW 51311, 'Existe una promoción publicable con condiciones incompletas.', 1;

  /* Resumen */
  SELECT DB_NAME() AS DatabaseName, @ApplyChanges AS ApplyChanges,
         (SELECT OperationalDayCutoff FROM restaurante.Site WHERE Rfc=@BrunoRfc AND Id=@SiteId) AS Cutoff,
         (SELECT COUNT(*) FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Status]='Active') AS Activas,
         (SELECT COUNT(*) FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Status]='Paused') AS Pausadas,
         (SELECT COUNT(*) FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Status]='Expired') AS Archivadas;

  SELECT promotion.Id, promotion.[Name], promotion.[Status], promotion.RuleType, promotion.Priority,
         promotion.MemberOnly, promotion.CodeRequired, promotion.WebEnabled,
         (SELECT COUNT(*) FROM restaurante.PromotionSchedule s
          WHERE s.Rfc=promotion.Rfc AND s.PromotionId=promotion.Id) AS Horarios,
         (SELECT COUNT(*) FROM restaurante.PromotionProduct p
          WHERE p.Rfc=promotion.Rfc AND p.PromotionId=promotion.Id) AS Productos
  FROM restaurante.Promotion promotion
  WHERE promotion.Rfc=@BrunoRfc
  ORDER BY promotion.[Status], promotion.Priority DESC, promotion.Id;

  IF @ApplyChanges=1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

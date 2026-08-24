/*
  Curso de caja de Bruno's: guion de membresía, venta sugerida y promociones.

  Redacta y publica el curso BRUNO-CAJA-MEMBRESIA en el catálogo de
  capacitación, acotado al RFC de Bruno's. Enseña la secuencia exacta que
  debe seguir quien atiende el punto de venta para que cada cuenta quede
  vinculada a un socio, para que cada ticket lleve una sugerencia concreta y
  para que las promociones se apliquen como el servidor las calcula.

  Origen del contenido:
    * Guion de caja del plan comercial de septiembre de 2026
      (Bruno's/marketing/2026-09-plan-comercial/03-punto-de-venta/guion-caja.md),
      autorizado por Dirección General el 23 de agosto de 2026.
    * Promociones vigentes cargadas por 20260823_bruno_commercial_plan.sql.
    * Política de puntos aprobada en 20260823_bruno_points_redemption.sql:
      1 punto por cada $10 de mercancía elegible, 1 punto = $1 MXN,
      canje desde 100 puntos y vigencia de 12 meses.

  El curso se redacta en una versión BORRADOR y se publica al final, porque los
  disparadores de contenido publicado hacen inmutables lecciones, bloques,
  recursos, evaluación y práctica en cuanto la versión queda publicada.

  Antes de escribir nada, el lote comprueba que la política de fidelidad y el
  código BIENVENIDA sigan configurados como el curso los enseña. Si alguien
  cambia la política y no actualiza el texto, este lote falla en lugar de
  publicar un curso que enseñe cifras falsas.

  Uso:
    sqlcmd -S <servidor> -d grupocarpio -E -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="0" -i 20260824_bruno_curso_caja_membresia.sql
    sqlcmd -S <servidor> -d grupocarpio -E -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="1" -i 20260824_bruno_curso_caja_membresia.sql

  Es idempotente: volver a ejecutarlo no duplica el curso ni toca la versión ya
  publicada.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @BrunoRfc nvarchar(50) = N'BRUNOS260707L26';
DECLARE @Autor nvarchar(256) = N'OrionERP.Capacitacion.Bruno.Caja.v1';
DECLARE @Clave nvarchar(64) = N'BRUNO-CAJA-MEMBRESIA';
DECLARE @LockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51700, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio. El curso trae datos comerciales reales y no se siembra en Orion_Training.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51701, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51702, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51703, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;
IF OBJECT_ID(N'capacitacion.Curso', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.CursoVersion', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.BloqueContenido', N'U') IS NULL
  THROW 51704, 'Instale primero 20260817_capacitacion_v1.sql: falta el esquema de capacitación.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:CursoCaja:20260824',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51705, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  /* ------------------------------------------------------------------
     0. El curso solo se publica si la realidad que enseña sigue vigente
     ------------------------------------------------------------------ */
  IF OBJECT_ID(N'fidelidad.ProgramSettings', N'U') IS NOT NULL
     AND COL_LENGTH('fidelidad.ProgramSettings', 'PointValueMxn') IS NOT NULL
  BEGIN
    DECLARE @PoliticaOk int = 0;
    EXEC sys.sp_executesql
      N'SELECT @OkOut = COUNT(*) FROM fidelidad.ProgramSettings
        WHERE Rfc = @Rfc AND PesosPerPoint = 10 AND PointValueMxn = 1.00
          AND MinimumRedeemPoints = 100 AND PointsValidityMonths = 12;',
      N'@Rfc nvarchar(50), @OkOut int OUTPUT',
      @Rfc = @BrunoRfc, @OkOut = @PoliticaOk OUTPUT;

    IF @PoliticaOk = 0
      THROW 51706, 'La política de puntos ya no es 1 punto por cada $10, $1 por punto, canje desde 100 y vigencia de 12 meses. Actualice el texto del curso antes de publicarlo.', 1;
  END;

  IF OBJECT_ID(N'restaurante.PromotionCode', N'U') IS NOT NULL
     AND NOT EXISTS
     (
       SELECT 1 FROM restaurante.PromotionCode
       WHERE Rfc = @BrunoRfc AND Code = 'BIENVENIDA' AND PerMemberLimit = 1 AND IsActive = 1
     )
    THROW 51707, 'El código BIENVENIDA no está activo con límite de un uso por socio. El curso enseña esa regla; revísela antes de publicar.', 1;

  IF OBJECT_ID(N'restaurante.PromotionCode', N'U') IS NOT NULL
     AND EXISTS
     (
       SELECT 1 FROM restaurante.PromotionCode
       WHERE Rfc = @BrunoRfc AND Code = 'TUCUMPLE' AND PerMemberLimit <> 1
     )
    THROW 51720, 'El código TUCUMPLE ya no está limitado a un uso por socio. El curso enseña esa regla; revísela antes de publicar.', 1;

  /* ------------------------------------------------------------------
     1. Curso y versión en borrador
     ------------------------------------------------------------------ */
  INSERT INTO capacitacion.Curso (Rfc, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, CreadoPor)
  SELECT @BrunoRfc, @Clave, N'Restaurante',
         N'Caja Bruno''s: guion de membresía y venta sugerida',
         N'La secuencia completa de una cuenta en el punto de venta: identificar al socio antes de cobrar, invitar a quien no lo es, sugerir una línea concreta según la hora, dejar que el servidor aplique las promociones y cerrar el turno sin perder información. Es el curso de quien opera la caja.',
         90, @Autor
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Curso WHERE Rfc = @BrunoRfc AND Clave = @Clave
  );

  DECLARE @CursoId int =
  (
    SELECT CursoId FROM capacitacion.Curso WHERE Rfc = @BrunoRfc AND Clave = @Clave
  );

  IF @CursoId IS NULL
    THROW 51708, 'No fue posible resolver el curso BRUNO-CAJA-MEMBRESIA.', 1;

  INSERT INTO capacitacion.CursoVersion
    (CursoId, NumeroVersion, Estado, Objetivos, Prerequisitos, CalificacionMinima, PublicadaEn, PublicadaPor, CreadaPor)
  SELECT @CursoId, 1, N'BORRADOR',
         N'Al terminar podrás: decir el guion de identificación de memoria y aplicarlo en toda cuenta antes de cobrar; vincular a un socio o invitarlo a registrarse sin frenar la fila; ofrecer una sugerencia concreta por ticket según la franja horaria; explicar qué promoción aplica el sistema solo y cuáles exigen algo de ti; explicar cómo se ganan, cuánto valen y cuándo caducan los puntos de Club Bruno; y cerrar el turno dejando registrada la información que el negocio necesita para decidir dónde invertir.',
         N'Haber trabajado al menos un turno en el punto de venta de Bruno''s. No requiere cursos previos.',
         80, NULL, NULL, @Autor
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.CursoVersion WHERE CursoId = @CursoId AND NumeroVersion = 1
  );

  DECLARE @VersionId int =
  (
    SELECT CursoVersionId FROM capacitacion.CursoVersion
    WHERE CursoId = @CursoId AND NumeroVersion = 1
  );

  IF @VersionId IS NULL
    THROW 51709, 'No fue posible resolver la versión 1 del curso de caja.', 1;

  /* ------------------------------------------------------------------
     2. Lecciones
     ------------------------------------------------------------------ */
  DECLARE @Lecciones TABLE
  (
    Orden int NOT NULL PRIMARY KEY,
    Clave nvarchar(64) NOT NULL UNIQUE,
    Titulo nvarchar(160) NOT NULL,
    Objetivo nvarchar(1000) NOT NULL,
    Duracion int NOT NULL
  );

  INSERT INTO @Lecciones (Orden, Clave, Titulo, Objetivo, Duracion)
  VALUES
    (1, N'PORQUE', N'Por qué existe este guion',
     N'Reconocer cuánto dinero se pierde hoy en el mostrador y entender por qué la membresía se pide antes de cobrar y nunca después.', 15),
    (2, N'IDENTIFICAR', N'Identificar al socio: el guion de tres segundos',
     N'Decir el guion de memoria, vincular al socio en el punto de venta, invitar una sola vez a quien no lo es y responder las objeciones frecuentes sin discutir.', 20),
    (3, N'SUGERIR', N'Venta sugerida: una recomendación concreta por ticket',
     N'Sustituir el "¿algo más?" por una sugerencia específica según la franja horaria, dicha una sola vez y en afirmativa.', 15),
    (4, N'PROMOS', N'Promociones, puntos y canje',
     N'Distinguir lo que el servidor aplica solo de lo que exige acción del cajero, y explicar al cliente cómo se ganan, cuánto valen y cuándo caducan sus puntos.', 20),
    (5, N'CERRAR', N'Errores que cuestan dinero y cierre de turno',
     N'Evitar los cuatro errores que rompen el programa, registrar el origen del cliente nuevo y cerrar el turno con la lista de verificación completa.', 20);

  INSERT INTO capacitacion.Leccion (CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida)
  SELECT @VersionId, source.Orden, source.Clave, source.Titulo, source.Objetivo, source.Duracion, 1
  FROM @Lecciones source
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Leccion target
    WHERE target.CursoVersionId = @VersionId AND target.Clave = source.Clave
  );

  /* ------------------------------------------------------------------
     3. Bloques de contenido
     ------------------------------------------------------------------ */
  DECLARE @Bloques TABLE
  (
    LeccionClave nvarchar(64) NOT NULL,
    Orden int NOT NULL,
    Tipo nvarchar(24) NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Contenido nvarchar(max) NOT NULL,
    ConfiguracionJson nvarchar(max) NULL,
    PRIMARY KEY (LeccionClave, Orden)
  );

  INSERT INTO @Bloques (LeccionClave, Orden, Tipo, Titulo, Contenido, ConfiguracionJson)
  VALUES
    /* ---- Leccion 1 - Por que existe este guion ---- */
    (N'PORQUE', 1, N'OBJETIVOS', N'Objetivos: lo que vas a saber hacer al terminar',
     N'Al terminar dirás el guion de identificación de memoria, sabrás vincular o invitar a un cliente en menos de diez segundos, ofrecerás una sugerencia concreta por cuenta y explicarás sin dudar qué promoción aplica el sistema y cuál depende de ti.',
     N'{"icon":"target","flowStep":"Preparar"}'),

    (N'PORQUE', 2, N'TEORIA', N'El diagnóstico: cuatro números del mostrador',
     N'Estas cifras son del corte del plan comercial del 23 de agosto de 2026. Ninguna se corrige en redes sociales: las cuatro se corrigen en la caja, cuenta por cuenta.',
     N'{"callout":"info","flowStep":"Explicar","items":["Solo 6 de cada 100 órdenes se vinculan a un socio. Las otras 94 no generan puntos ni permiten volver a contactar a ese cliente.","El ticket promedio del socio es 35% más bajo que el del cliente no registrado, porque hoy se identifica a los clientes de consumo pequeño y a los demás no.","Apenas el 44% de las órdenes lleva bebida.","Apenas el 1.2% de las órdenes lleva postre."],"notasInstructor":"Pregunte al grupo cuántas cuentas atendieron ayer y cuántas vincularon. El contraste entre su respuesta y el 6% abre la conversación mejor que cualquier explicación."}'),

    (N'PORQUE', 3, N'TEORIA', N'Qué cambia cuando el guion se aplica',
     N'Una cuenta vinculada deja tres cosas que una cuenta anónima no deja: puntos que traen de vuelta al cliente, un contacto al que se le puede escribir con permiso y un dato de consumo que permite saber qué se vende y a quién. Una línea sugerida sube el ticket sin gastar un peso en publicidad.',
     N'{"flowStep":"Explicar","diagram":["Identificar al socio","Sugerir una línea","Cobrar","Puntos acreditados","El cliente regresa"]}'),

    (N'PORQUE', 4, N'ALERTA', N'Regla número uno: la membresía se pide ANTES de cobrar',
     N'Siempre, a todos, sin excepción. Si el socio se identifica después de cerrar la cuenta, esa compra ya no genera puntos y no hay forma de acreditarlos después: el sistema no permite acreditación retroactiva y no se debe pedir que se haga por otra vía. Un ajuste manual de puntos deja rastro, requiere autorización y no es un atajo disponible en caja.',
     N'{"severity":"critical","flowStep":"Explicar","notasInstructor":"Insista en que la regla no admite juicio del cajero. En el momento en que se decide a quién sí preguntar y a quién no, el programa deja de servir como dato."}'),

    /* ---- Leccion 2 - Identificar al socio ---- */
    (N'IDENTIFICAR', 1, N'PASOS', N'El guion de tres segundos',
     N'Se dice al cerrar la cuenta, antes de cobrar, mirando al cliente. Una frase, siempre la misma.',
     N'{"flowStep":"Demostrar","items":["1. Di la frase completa: ¿Eres socio de Club Bruno? Muéstrame tu QR y te sumo los puntos.","2. Si dice que sí: pide el QR desde Mi cuenta en su teléfono, o búscalo por correo o teléfono.","3. Si dice que no: invítalo una sola vez, señalando la tarjeta de mesa.","4. Vincula antes de cobrar y confirma en voz alta el nombre que aparece en pantalla."],"notasInstructor":"Haga que cada persona repita la frase en voz alta tres veces hasta que salga sin titubeo. Una frase memorizada se dice en la prisa; una frase improvisada no."}'),

    (N'IDENTIFICAR', 2, N'TEORIA', N'Si dice que sí: vincular sin frenar la fila',
     N'En el punto de venta, en la sección Club Bruno, escribe o escanea el identificador y presiona Vincular. Sirven cuatro datos: el QR, el número de socio, el correo o el teléfono de diez dígitos. Cuando aparezca el nombre del socio y su saldo de puntos, la cuenta ya quedó vinculada. Si no aparece nadie, la cuenta no está verificada o el dato es de otra persona: no adivines, pide el correo con el que se registró.',
     N'{"callout":"info","flowStep":"Demostrar"}'),

    (N'IDENTIFICAR', 3, N'TEORIA', N'Si dice que no: la invitación de una sola vez',
     N'Dilo así: es gratis y toma un minuto, escanea este código y en tu próxima visita ya acumulas; y si te registras ahora, con el código BIENVENIDA te descuento $50 en esta cuenta si pasa de $200. Señala la tarjeta de mesa y guarda silencio. No insistas más de una vez: una invitación clara es suficiente, dos son presión y se recuerdan mal.',
     N'{"callout":"info","flowStep":"Demostrar","notasInstructor":"El silencio después de la invitación es parte del guion. Practíquelo: la mayoría de los cajeros llena ese silencio hablando, y con eso convierte una invitación en insistencia."}'),

    (N'IDENTIFICAR', 4, N'PASOS', N'Objeciones frecuentes y qué responder',
     N'Responde corto, sin discutir, y sigue cobrando. Ninguna objeción se responde dos veces.',
     N'{"flowStep":"Practicar","items":["No traigo el teléfono: sin problema, te busco por tu teléfono o tu correo.","No quiero dar mi correo: es solo para verificar la cuenta y avisarte de tus puntos; no mandamos publicidad si no la autorizas.","Otro día lo hago: claro, el código está en la tarjeta de la mesa por si lo quieres hacer mientras esperas. Y ahí termina.","¿Cuánto me dan?: un punto por cada $10 de consumo, y cada punto vale un peso de descuento.","Soy socio pero no encuentro mi QR: lo busco con tu teléfono. No pidas que instale nada ni lo resuelvas con un descuento.","Ya pagué, ¿me los pueden sumar?: esa cuenta ya no puede acreditar puntos, pero la siguiente sí; te dejo vinculado desde ahora."],"notasInstructor":"Reparta las objeciones y trabájelas en pares: uno es cliente, otro es cajero, y se intercambian. Corrija solo dos cosas: la longitud de la respuesta y que no se prometa nada que el sistema no haga."}'),

    (N'IDENTIFICAR', 5, N'DEMOSTRACION', N'Demostrar: vincular a un socio en el punto de venta',
     N'El instructor mostrará la secuencia completa en pantalla, desde la cuenta abierta hasta el momento previo al cobro, sin enviar la orden.',
     N'{"flowStep":"Demostrar","demoSteps":["Capturar los productos desde la carta, nunca como concepto libre","Decir el guion y pedir el QR","Escribir el identificador en la sección Club Bruno y presionar Vincular","Confirmar en voz alta el nombre del socio y su saldo","Capturar el código promocional cuando aplique y presionar Aplicar","Revisar el desglose de descuentos antes de cobrar"],"notasInstructor":"Muestre a propósito una vinculación fallida, con un teléfono inexistente, para que vean el mensaje del sistema y sepan que no significa que el cliente esté mintiendo."}'),

    (N'IDENTIFICAR', 6, N'ALERTA', N'Nunca cobres primero y preguntes después',
     N'Cobrar y luego preguntar por la membresía es el error más caro del mostrador porque no tiene arreglo: la venta ya ocurrió, los puntos no se generaron y el cliente se queda con la impresión de que el programa no sirve. Si te pasa, vincúlalo de todas formas para la próxima visita y dilo tal cual: esta cuenta ya no acumula, la siguiente sí.',
     N'{"severity":"critical","flowStep":"Evaluar"}'),

    /* ---- Leccion 3 - Venta sugerida ---- */
    (N'SUGERIR', 1, N'TEORIA', N'Preguntar si desea algo más no es una venta sugerida',
     N'La pregunta abierta pide una decisión que el cliente ya tomó y casi siempre recibe un no. Una sugerencia concreta pide una decisión nueva y sencilla: sí o no a algo específico. La diferencia entre las dos frases es margen puro, porque una línea adicional no cuesta publicidad y no cuesta descuento.',
     N'{"callout":"info","flowStep":"Explicar"}'),

    (N'SUGERIR', 2, N'PASOS', N'Una sugerencia por franja horaria',
     N'Elige la de la hora en que estás. Es una por cuenta, no una lista.',
     N'{"flowStep":"Practicar","items":["8:00 a 11:00: ¿Le agrego un café americano?","12:00 a 15:00: ¿Quiere una bebida con su platillo? Las limonadas están recién hechas.","15:00 a 17:00: hoy el postre y el café llevan 20% si pide dos. ¿Le antojo un pan de elote con helado?","17:00 a 22:00: si van a pedir dos hamburguesas, hoy salen en $159 las dos."],"notasInstructor":"Que cada persona diga en voz alta la sugerencia de la franja en la que trabaja normalmente. Corrija la entonación: se dice en afirmativa, no como pregunta dudosa."}'),

    (N'SUGERIR', 3, N'TEORIA', N'Cómo se dice para que funcione',
     N'Tres reglas. Primera: sé específico y nombra el producto. Segunda: dilo en afirmativa, no como si esperaras un no. Tercera: dilo una sola vez y acepta la respuesta. Las sugerencias de las 15:00 a 17:00 y de las 17:00 a 22:00 tienen una ventaja extra: describen una promoción real, así que el cliente recibe un beneficio verdadero y el sistema lo aplica solo.',
     N'{"callout":"info","flowStep":"Explicar"}'),

    (N'SUGERIR', 4, N'ALERTA', N'Sugerir no es insistir',
     N'Una sugerencia por cuenta. Si el cliente dice que no, se cobra y se cierra. Repetir la oferta, ofrecer tres cosas seguidas o insistir después de un no convierte una recomendación en presión, y eso sí se recuerda y sí cuesta clientes.',
     N'{"severity":"warning","flowStep":"Evaluar"}'),
    /* ---- Leccion 4 - Promociones, puntos y canje ---- */
    (N'PROMOS', 1, N'TEORIA', N'Las promociones las calcula el servidor, no la caja',
     N'Cada promoción tiene día, horario, productos participantes y consumo mínimo cargados en el sistema. Al cobrar, el servidor evalúa la cuenta con la hora de la sede y aplica el descuento que corresponda. El cajero no decide si aplica ni cuánto: solo se asegura de capturar los productos desde la carta, de identificar al socio y de escribir el código cuando la promoción lo pide. Si el cliente cree que le tocaba una promoción y no salió, se revisa el horario y las condiciones; no se fuerza el descuento.',
     N'{"callout":"info","flowStep":"Explicar"}'),

    (N'PROMOS', 2, N'PASOS', N'Las promociones vigentes y qué te toca hacer',
     N'Aprende la columna de la derecha: es lo único que depende de ti.',
     N'{"flowStep":"Explicar","items":["Almuerzo Club, $25 desde $160, martes a domingo de 8:00 a 11:00. Tú: identificar al socio. El sistema aplica solo.","Miércoles de Producto Estrella, 15% en alimentos participantes desde $150, miércoles de 12:00 a 21:00. Tú: nada. El sistema aplica solo.","Bienvenida Club, $50 desde $200, martes a sábado de 8:00 a 22:00 y domingo de 8:00 a 13:00. Tú: identificar al socio y capturar el código BIENVENIDA. Una sola vez por socio en toda su vida.","Jueves y Viernes de Parrilla, dos hamburguesas participantes en $159, jueves y viernes de 17:00 a 22:00. Tú: nada. El sistema arma el paquete con las de mayor precio.","Merienda en el Jardín, 20% desde dos unidades participantes, martes a domingo de 15:00 a 17:00. Tú: nada. El sistema aplica solo.","Tu cumpleaños, código TUCUMPLE, todo el año y una sola vez por socio. Tú: verificar identificación oficial con fecha de nacimiento antes de capturar el código, y confirmar el beneficio vigente con gerencia antes de prometerlo."],"notasInstructor":"Pregunte cuáles exigen acción del cajero. La respuesta correcta son tres: Almuerzo Club por la identificación, Bienvenida Club por identificación más código, y Tu cumpleaños por la verificación de la identificación."}'),

    (N'PROMOS', 3, N'TEORIA', N'Puntos de Club Bruno: cuánto se gana, cuánto vale y cuándo caduca',
     N'La política aprobada por Dirección es sencilla y conviene decirla igual siempre: se gana un punto por cada $10 de mercancía elegible ya con descuentos aplicados; cada punto vale $1 de descuento; el canje empieza en 100 puntos; y los puntos tienen una vigencia de 12 meses, de modo que se consumen siempre los más antiguos primero. La propina no forma parte de ese importe y no genera puntos. Si el cliente pregunta cuántos puntos tiene, el saldo aparece junto a su nombre al vincularlo.',
     N'{"callout":"info","flowStep":"Explicar","items":["Se gana: 1 punto por cada $10 de mercancía elegible.","Vale: 1 punto = $1 de descuento.","Se canjea: desde 100 puntos, es decir desde $100 de descuento.","Caduca: a los 12 meses, y primero se gastan los puntos más antiguos."]}'),

    (N'PROMOS', 4, N'DEMOSTRACION', N'Demostrar: canje de puntos y vale',
     N'El canje no se hace desde el punto de venta. Se genera en la pantalla de promociones y membresía, que emite un vale con folio y el importe exacto que la caja debe aplicar como descuento en la cuenta, citando ese folio como motivo. El instructor mostrará el recorrido completo.',
     N'{"flowStep":"Demostrar","demoSteps":["Buscar al socio y revisar su saldo canjeable","Capturar los puntos a canjear, nunca por debajo del mínimo","Generar el vale y anotar el folio","Aplicar en la cuenta el importe del vale como descuento, citando el folio como motivo","Verificar el saldo restante del socio"],"notasInstructor":"Deje claro que el canje lo autoriza quien tiene acceso a esa pantalla y que el folio es lo que hace auditable el descuento. Un descuento sin folio es un descuento sin respaldo."}'),

    (N'PROMOS', 5, N'ALERTA', N'No apliques descuentos manuales para emparejar una promoción',
     N'Si una promoción no salió, no la imites con un descuento manual. Se duplica el beneficio cuando el sistema sí la aplicó, se descuadra el reporte de la promoción y se pierde la posibilidad de saber si la campaña funcionó. El descuento manual existe para casos autorizados, siempre con motivo escrito y, cuando corresponda, con supervisor y PIN.',
     N'{"severity":"critical","flowStep":"Evaluar"}'),

    /* ---- Leccion 5 - Errores y cierre ---- */
    (N'CERRAR', 1, N'PASOS', N'Los cuatro errores que cuestan dinero',
     N'Ninguno de los cuatro es un descuido menor: los cuatro destruyen información que después no se puede reconstruir.',
     N'{"flowStep":"Explicar","items":["Cobrar y luego preguntar por la membresía. Esa compra se perdió para el programa y no hay acreditación retroactiva.","Capturar productos como concepto libre. La línea no califica para ninguna promoción, no descuenta inventario y no tiene receta ni costo, así que descuadra el margen. Si un producto no está en la carta, avisa a gerencia para darlo de alta.","Aplicar descuentos manuales encima de una promoción. Duplica el descuento y descuadra el reporte.","No preguntar el origen del cliente nuevo. Sin ese dato no se sabe qué canal funciona y se sigue gastando a ciegas."],"notasInstructor":"Pida un ejemplo real de cada error ocurrido en el último mes. Es más eficaz que la lista."}'),

    (N'CERRAR', 2, N'TEORIA', N'El concepto libre: por qué rompe la cuenta',
     N'Una partida capturada a mano no está ligada a ningún producto de la carta. Por eso nunca entra en una promoción, no consume inventario, no tiene costo teórico y no aparece en el reporte de venta por producto. Sí suma al importe sobre el que se calculan los puntos, pero el daño en promociones, inventario y margen es mayor que ese beneficio. La semana previa al plan comercial se cobraron como concepto libre productos que sí existen en el menú, entre ellos la hamburguesa de arrachera y la jarra de agua de frutas. Úsalo solo para cargos que de verdad no existan en la carta, y avisa a gerencia el mismo día.',
     N'{"callout":"info","flowStep":"Explicar"}'),

    (N'CERRAR', 3, N'PASOS', N'Pregunta obligatoria para cliente nuevo',
     N'A todo cliente nuevo: ¿cómo se enteró de Bruno? Se registra una sola respuesta principal, la que el cliente diga primero, escrita en el campo Notas de la orden con el prefijo ORIGEN y en mayúsculas, por ejemplo ORIGEN: FACEBOOK. Ese formato es lo que permite contarlas después.',
     N'{"flowStep":"Practicar","items":["ORIGEN: FACEBOOK","ORIGEN: INSTAGRAM","ORIGEN: WHATSAPP","ORIGEN: GOOGLE","ORIGEN: VOLANTE","ORIGEN: RECOMENDACION","ORIGEN: PASO POR EL LUGAR","ORIGEN: YA ERA CLIENTE","ORIGEN: OTRO"],"notasInstructor":"Insista en una sola respuesta. Si el cliente dice dos canales, se anota el que mencionó primero. Dos respuestas en una nota vuelven el dato imposible de contar."}'),

    (N'CERRAR', 4, N'PASOS', N'Cierre de turno: lista de verificación',
     N'Antes de cerrar, revisa las cuatro. Lo que no se verifica aquí ya no se corrige mañana.',
     N'{"flowStep":"Cerrar","items":["Todas las órdenes con socio quedaron vinculadas.","No hay órdenes canceladas con estado de pago Pagado sin explicación.","Las respuestas de origen del cliente quedaron registradas en las notas.","Las tarjetas de mesa siguen en las mesas y legibles."],"notasInstructor":"Haga el cierre acompañado la primera semana. La lista se aprende haciéndola, no leyéndola."}'),

    (N'CERRAR', 5, N'PRACTICA', N'Practicar: el simulacro completo en el punto de venta',
     N'Con el instructor haciendo de cliente, atiende una cuenta completa desde la captura hasta el momento previo al cobro. La orden no se envía ni se cobra en ningún momento: construir la cuenta, vincular al socio y pedir la cotización de promociones son consultas que no escriben nada.',
     N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Observe tres cosas: que el guion salga completo y sin titubeo, que la vinculación ocurra antes de cualquier intento de cobro, y que la cuenta quede vacía al final. Lo demás es afinación."}'),

    (N'CERRAR', 6, N'EVALUACION', N'Evaluar: guion, secuencia y promociones',
     N'Responde la evaluación. Las preguntas marcadas como críticas son las que cuestan dinero cuando se contestan mal: se deben responder correctamente para acreditar.',
     N'{"required":true,"flowStep":"Evaluar","checklist":["Guion memorizado","Membresía antes de cobrar","Una sugerencia por cuenta","Sin descuentos manuales sobre promociones"]}'),

    (N'CERRAR', 7, N'RESUMEN', N'Cierre: una sola frase, dicha siempre',
     N'Todo este curso cabe en una frase que se dice antes de cobrar, en toda cuenta, sin excepción: ¿Eres socio de Club Bruno? Muéstrame tu QR y te sumo los puntos. Lo demás son consecuencias de haberla dicho a tiempo: los puntos se acreditan, la promoción del socio se aplica sola, el ticket sube con una sugerencia concreta y el negocio por fin sabe de dónde viene su gente.',
     N'{"highlight":true,"flowStep":"Cerrar"}');

  INSERT INTO capacitacion.BloqueContenido (LeccionId, Orden, Tipo, Titulo, Contenido, ConfiguracionJson, Requerido)
  SELECT lesson.LeccionId, source.Orden, source.Tipo, source.Titulo, source.Contenido, source.ConfiguracionJson, 1
  FROM @Bloques source
  JOIN capacitacion.Leccion lesson
    ON lesson.CursoVersionId = @VersionId AND lesson.Clave = source.LeccionClave
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.BloqueContenido target
    WHERE target.LeccionId = lesson.LeccionId AND target.Orden = source.Orden
  );

  /* ------------------------------------------------------------------
     4. Recursos: la pantalla real de cada bloque
     ------------------------------------------------------------------ */
  DECLARE @Recursos TABLE
  (
    LeccionClave nvarchar(64) NOT NULL,
    BloqueOrden int NOT NULL,
    Orden int NOT NULL,
    Tipo nvarchar(30) NOT NULL,
    Titulo nvarchar(160) NOT NULL,
    Ruta nvarchar(500) NOT NULL,
    TextoAlternativo nvarchar(500) NULL,
    PRIMARY KEY (LeccionClave, BloqueOrden, Orden)
  );

  INSERT INTO @Recursos (LeccionClave, BloqueOrden, Orden, Tipo, Titulo, Ruta, TextoAlternativo)
  VALUES
    (N'PORQUE', 2, 1, N'ENLACE', N'Abrir reportes de restaurante', N'/restaurante/reportes',
     N'Reporte de venta donde se leen la vinculación de socios y la composición del ticket.'),
    (N'IDENTIFICAR', 5, 1, N'ENLACE', N'Abrir el punto de venta', N'/restaurante/pos',
     N'Pantalla de captura, vinculación de socio y código promocional.'),
    (N'PROMOS', 4, 1, N'ENLACE', N'Abrir promociones y membresía', N'/restaurante/promociones',
     N'Pantalla donde se consulta la política del programa y se genera el vale de canje.'),
    (N'CERRAR', 4, 1, N'ENLACE', N'Abrir turnos de caja', N'/restaurante/turnos',
     N'Pantalla de apertura, conteo y corte del turno.');

  INSERT INTO capacitacion.Recurso (BloqueId, Orden, Tipo, Titulo, Ruta, TextoAlternativo, VersionAplicacion)
  SELECT blockInfo.BloqueId, source.Orden, source.Tipo, source.Titulo, source.Ruta, source.TextoAlternativo, N'bruno-caja-v1'
  FROM @Recursos source
  JOIN capacitacion.Leccion lesson
    ON lesson.CursoVersionId = @VersionId AND lesson.Clave = source.LeccionClave
  JOIN capacitacion.BloqueContenido blockInfo
    ON blockInfo.LeccionId = lesson.LeccionId AND blockInfo.Orden = source.BloqueOrden
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Recurso target
    WHERE target.BloqueId = blockInfo.BloqueId AND target.Orden = source.Orden
  );

  /* ------------------------------------------------------------------
     5. Evaluación, preguntas y opciones
     ------------------------------------------------------------------ */
  DECLARE @EvaluacionTitulo nvarchar(160) = N'Validación del guion de caja';

  INSERT INTO capacitacion.Evaluacion (CursoVersionId, Titulo, Instrucciones, CalificacionMinima, Requerida)
  SELECT @VersionId, @EvaluacionTitulo,
         N'Elige la mejor respuesta. Responde pensando en lo que harías en el mostrador con fila, no en lo que suena mejor. Las preguntas críticas deben responderse correctamente para acreditar.',
         80, 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Evaluacion
    WHERE CursoVersionId = @VersionId AND Titulo = @EvaluacionTitulo
  );

  DECLARE @EvaluacionId int =
  (
    SELECT EvaluacionId FROM capacitacion.Evaluacion
    WHERE CursoVersionId = @VersionId AND Titulo = @EvaluacionTitulo
  );

  IF @EvaluacionId IS NULL
    THROW 51710, 'No fue posible resolver la evaluación del curso de caja.', 1;

  DECLARE @Preguntas TABLE
  (
    Orden int NOT NULL PRIMARY KEY,
    Texto nvarchar(1000) NOT NULL,
    Explicacion nvarchar(1000) NULL,
    Critica bit NOT NULL,
    Correcta nvarchar(1000) NOT NULL,
    Incorrecta1 nvarchar(1000) NOT NULL,
    Incorrecta2 nvarchar(1000) NOT NULL
  );

  INSERT INTO @Preguntas (Orden, Texto, Explicacion, Critica, Correcta, Incorrecta1, Incorrecta2)
  VALUES
    (1, N'¿En qué momento se pregunta por la membresía?',
     N'La regla no admite excepciones ni criterio del cajero: siempre antes de cobrar y a todos.', 1,
     N'Al cerrar la cuenta, antes de cobrar, en toda cuenta y a todo cliente.',
     N'Solo cuando el cliente parece ser cliente frecuente.',
     N'Después de cobrar, para no hacer más lento el cobro.'),

    (2, N'La cuenta ya se cobró y entonces el cliente dice que es socio. ¿Qué procede?',
     N'No existe acreditación retroactiva y no debe pedirse por otra vía.', 1,
     N'Explicarle que esa cuenta ya no acumula, vincularlo de todas formas y dejarlo listo para la siguiente visita.',
     N'Pedir a gerencia que le acredite los puntos de esa compra.',
     N'Aplicar un descuento equivalente a los puntos que hubiera ganado.'),

    (3, N'¿Cuál es la frase del guion de identificación?',
     N'Es una sola frase y siempre la misma: se dice de memoria, no se improvisa.', 0,
     N'¿Eres socio de Club Bruno? Muéstrame tu QR y te sumo los puntos.',
     N'¿Quieres registrarte en nuestro programa de lealtad para acumular beneficios?',
     N'¿Tienes alguna tarjeta o cupón que quieras usar hoy?'),

    (4, N'El cliente dice que no es socio. ¿Cuántas veces se le invita a registrarse?',
     N'Una invitación clara es suficiente; dos son presión y se recuerdan mal.', 0,
     N'Una sola vez, señalando la tarjeta de mesa, y después se cobra.',
     N'Las veces necesarias hasta que acepte o pida la cuenta.',
     N'Ninguna, porque el registro es responsabilidad del cliente.'),

    (5, N'El cliente reclama que no le salió la promoción del miércoles. ¿Qué haces?',
     N'Las promociones se calculan en el servidor con la hora de la sede; el cajero verifica condiciones, no fuerza resultados.', 1,
     N'Revisar horario, productos participantes y consumo mínimo, y explicar por qué no aplicó.',
     N'Aplicar un descuento manual del 15% para dejar la cuenta como debería haber quedado.',
     N'Cancelar la orden y volver a capturarla hasta que la promoción salga.'),

    (6, N'¿Cuáles promociones exigen que el cajero capture un código?',
     N'Las demás las aplica el servidor sin intervención del cajero.', 0,
     N'Bienvenida Club con el código BIENVENIDA, y Tu cumpleaños con el código TUCUMPLE.',
     N'Todas: sin código no se aplica ninguna promoción.',
     N'Ninguna: el sistema siempre aplica todo automáticamente.'),

    (7, N'¿Cuántas veces puede usar un socio el código BIENVENIDA?',
     N'Es un beneficio de bienvenida limitado a un uso por socio durante toda la vida de la membresía.', 0,
     N'Una sola vez en toda la vida de su membresía.',
     N'Una vez al mes mientras la promoción siga vigente.',
     N'Las veces que quiera, siempre que la cuenta pase de $200.'),

    (8, N'¿Cuántos puntos se ganan y cuánto vale cada punto?',
     N'Es la política aprobada por Dirección: se dice igual siempre para no crear expectativas falsas.', 0,
     N'Un punto por cada $10 de mercancía elegible, y cada punto vale $1 de descuento.',
     N'Un punto por cada peso consumido, y cada punto vale diez centavos.',
     N'Diez puntos por cada visita, sin importar el consumo.'),

    (9, N'¿Desde cuántos puntos se puede canjear y cuánto duran los puntos?',
     N'Canje mínimo de 100 puntos y vigencia de 12 meses, gastando siempre primero los más antiguos.', 0,
     N'Desde 100 puntos, y los puntos vencen a los 12 meses.',
     N'Desde el primer punto, y nunca vencen.',
     N'Desde 500 puntos, y vencen al cierre de cada año.'),

    (10, N'Un producto que sí existe en la carta se captura como concepto libre. ¿Qué se pierde?',
     N'La partida a mano no está ligada a ningún producto, así que queda fuera de promociones, de inventario y del costo.', 1,
     N'No califica para ninguna promoción, no descuenta inventario y no tiene costo, así que descuadra el margen.',
     N'Solo se pierde el nombre bonito en el ticket; el efecto es cosmético.',
     N'No se pierde nada mientras el precio cobrado sea el correcto.'),

    (11, N'Un cliente nuevo dice que llegó por Facebook y que además vio un volante. ¿Qué registras?',
     N'Se registra una sola respuesta principal, la que el cliente mencionó primero; dos respuestas vuelven el dato imposible de contar.', 0,
     N'Una sola respuesta, la que dijo primero, en Notas de la orden con el formato ORIGEN: FACEBOOK.',
     N'Las dos respuestas separadas por una diagonal, para no perder información.',
     N'Ninguna, porque el cliente no eligió un solo canal.'),

    (12, N'Son las 17:30 y el cliente ya pidió. En lugar de preguntar si desea algo más, ¿qué dices?',
     N'La sugerencia es concreta, corresponde a la franja horaria y describe una promoción real.', 0,
     N'Si van a pedir dos hamburguesas, hoy salen en $159 las dos.',
     N'¿Le gustaría ordenar algo más de nuestra carta?',
     N'Tenemos postres, bebidas, café y cervezas por si se le antoja algo.'),

    (13, N'¿Dónde se genera el canje de puntos de un socio?',
     N'El canje se genera fuera del punto de venta y produce un vale con folio; ese folio es el respaldo del descuento.', 0,
     N'En la pantalla de promociones y membresía, que emite un vale con folio para aplicar en la cuenta.',
     N'Directamente en el punto de venta, escribiendo los puntos en el campo de descuento.',
     N'En ningún lado: los puntos se descuentan solos al cobrar.');

  INSERT INTO capacitacion.Pregunta (EvaluacionId, Orden, Texto, Explicacion, Critica)
  SELECT @EvaluacionId, source.Orden, source.Texto, source.Explicacion, source.Critica
  FROM @Preguntas source
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Pregunta target
    WHERE target.EvaluacionId = @EvaluacionId AND target.Orden = source.Orden
  );

  INSERT INTO capacitacion.OpcionPregunta (PreguntaId, Orden, Texto, EsCorrecta)
  SELECT question.PreguntaId, optionInfo.Orden, optionInfo.Texto, optionInfo.EsCorrecta
  FROM @Preguntas source
  JOIN capacitacion.Pregunta question
    ON question.EvaluacionId = @EvaluacionId AND question.Orden = source.Orden
  CROSS APPLY
  (
    VALUES (1, source.Correcta, CONVERT(bit, 1)),
           (2, source.Incorrecta1, CONVERT(bit, 0)),
           (3, source.Incorrecta2, CONVERT(bit, 0))
  ) optionInfo(Orden, Texto, EsCorrecta)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.OpcionPregunta target
    WHERE target.PreguntaId = question.PreguntaId AND target.Orden = optionInfo.Orden
  );

  /* ------------------------------------------------------------------
     6. Práctica guiada y sus pasos evaluables
     ------------------------------------------------------------------ */
  DECLARE @PracticaTitulo nvarchar(160) = N'Simulacro de caja: una cuenta completa sin cobrar';

  INSERT INTO capacitacion.Practica (CursoVersionId, Titulo, Instrucciones, RutaSandbox, Requerida)
  SELECT @VersionId, @PracticaTitulo,
         N'El instructor hace de cliente. Tú atiendes una cuenta completa en el punto de venta y la llevas hasta el momento previo al cobro. Construir la cuenta, vincular al socio y pedir la cotización de promociones no escribe nada: son consultas. Lo único que sí cobra y sí acredita puntos es enviar la orden, y en esta práctica la orden no se envía nunca. Usa una cuenta de socio de prueba que el instructor te indique, o realiza la vinculación como demostración sin confirmar el cobro. Al terminar, vacía la cuenta.',
         N'/restaurante/pos', 1
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.Practica
    WHERE CursoVersionId = @VersionId AND Titulo = @PracticaTitulo
  );

  DECLARE @PracticaId int =
  (
    SELECT PracticaId FROM capacitacion.Practica
    WHERE CursoVersionId = @VersionId AND Titulo = @PracticaTitulo
  );

  IF @PracticaId IS NULL
    THROW 51711, 'No fue posible resolver la práctica del curso de caja.', 1;

  DECLARE @Pasos TABLE
  (
    Orden int NOT NULL PRIMARY KEY,
    Descripcion nvarchar(1000) NOT NULL,
    Critico bit NOT NULL
  );

  INSERT INTO @Pasos (Orden, Descripcion, Critico)
  VALUES
    (1, N'Captura al menos dos productos tomándolos de la carta. Ninguna partida se captura como concepto libre.', 1),
    (2, N'Di el guion de identificación completo, palabra por palabra, antes de cualquier intento de cobro.', 1),
    (3, N'Vincula al socio de prueba por QR, teléfono o correo y confirma en voz alta el nombre y el saldo que aparecen.', 1),
    (4, N'Ofrece una sola sugerencia, la que corresponde a la franja horaria en que estás haciendo la práctica.', 0),
    (5, N'Responde en una frase las dos objeciones que plantee el instructor, sin repetir la invitación.', 0),
    (6, N'Captura el código promocional cuando el caso lo pida, presiona Aplicar y lee en voz alta el desglose de descuentos.', 0),
    (7, N'Registra el origen del cliente en Notas de la orden con el formato ORIGEN seguido del canal en mayúsculas.', 0),
    (8, N'Termina el simulacro sin enviar ni cobrar la orden, y vacía la cuenta antes de ceder el lugar.', 1);

  INSERT INTO capacitacion.PracticaPaso (PracticaId, Orden, Descripcion, Critico)
  SELECT @PracticaId, source.Orden, source.Descripcion, source.Critico
  FROM @Pasos source
  WHERE NOT EXISTS
  (
    SELECT 1 FROM capacitacion.PracticaPaso target
    WHERE target.PracticaId = @PracticaId AND target.Orden = source.Orden
  );

  /* ------------------------------------------------------------------
     7. Publicación de la versión redactada
     ------------------------------------------------------------------ */
  UPDATE capacitacion.CursoVersion
  SET Estado = N'PUBLICADA',
      PublicadaEn = SYSUTCDATETIME(),
      PublicadaPor = @Autor
  WHERE CursoVersionId = @VersionId
    AND Estado = N'BORRADOR'
    AND PublicadaEn IS NULL;

  /* ------------------------------------------------------------------
     8. Comprobación del curso resultante
     ------------------------------------------------------------------ */
  IF NOT EXISTS
  (
    SELECT 1 FROM capacitacion.CursoVersion
    WHERE CursoVersionId = @VersionId AND Estado = N'PUBLICADA' AND PublicadaEn IS NOT NULL
  )
    THROW 51712, 'La versión del curso de caja no quedó publicada.', 1;

  IF (SELECT COUNT(*) FROM capacitacion.Leccion WHERE CursoVersionId = @VersionId)
       <> (SELECT COUNT(*) FROM @Lecciones)
    THROW 51713, 'El curso de caja no quedó con todas sus lecciones.', 1;

  IF
  (
    SELECT COUNT(*)
    FROM capacitacion.BloqueContenido blockInfo
    JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId
    WHERE lesson.CursoVersionId = @VersionId
  ) <> (SELECT COUNT(*) FROM @Bloques)
    THROW 51714, 'El curso de caja no quedó con todos sus bloques de contenido.', 1;

  IF
  (
    SELECT COUNT(*)
    FROM capacitacion.Recurso resource
    JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.BloqueId = resource.BloqueId
    JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId
    WHERE lesson.CursoVersionId = @VersionId
  ) <> (SELECT COUNT(*) FROM @Recursos)
    THROW 51715, 'El curso de caja no quedó con todos sus recursos.', 1;

  IF (SELECT COUNT(*) FROM capacitacion.Pregunta WHERE EvaluacionId = @EvaluacionId)
       <> (SELECT COUNT(*) FROM @Preguntas)
    THROW 51716, 'La evaluación del curso de caja no quedó con todas sus preguntas.', 1;

  IF
  (
    SELECT COUNT(*)
    FROM capacitacion.OpcionPregunta optionInfo
    JOIN capacitacion.Pregunta question ON question.PreguntaId = optionInfo.PreguntaId
    WHERE question.EvaluacionId = @EvaluacionId
  ) <> (SELECT COUNT(*) FROM @Preguntas) * 3
    THROW 51717, 'Alguna pregunta del curso de caja no quedó con sus tres opciones.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM capacitacion.Pregunta question
    LEFT JOIN capacitacion.OpcionPregunta optionInfo
      ON optionInfo.PreguntaId = question.PreguntaId AND optionInfo.EsCorrecta = 1
    WHERE question.EvaluacionId = @EvaluacionId
    GROUP BY question.PreguntaId
    HAVING COUNT(optionInfo.OpcionId) <> 1
  )
    THROW 51718, 'Cada pregunta debe tener exactamente una opción correcta.', 1;

  IF (SELECT COUNT(*) FROM capacitacion.PracticaPaso WHERE PracticaId = @PracticaId)
       <> (SELECT COUNT(*) FROM @Pasos)
    THROW 51719, 'La práctica del curso de caja no quedó con todos sus pasos.', 1;

  /* ------------------------------------------------------------------
     Resumen
     ------------------------------------------------------------------ */
  SELECT DB_NAME() AS DatabaseName, @ApplyChanges AS ApplyChanges,
         curso.Rfc, curso.Clave, curso.Nombre, curso.DuracionMinutos,
         versionInfo.NumeroVersion, versionInfo.Estado, versionInfo.CalificacionMinima,
         (SELECT COUNT(*) FROM capacitacion.Leccion WHERE CursoVersionId = @VersionId) AS Lecciones,
         (SELECT COUNT(*) FROM capacitacion.BloqueContenido blockInfo
          JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId
          WHERE lesson.CursoVersionId = @VersionId) AS Bloques,
         (SELECT COUNT(*) FROM capacitacion.Pregunta WHERE EvaluacionId = @EvaluacionId) AS Preguntas,
         (SELECT COUNT(*) FROM capacitacion.Pregunta WHERE EvaluacionId = @EvaluacionId AND Critica = 1) AS PreguntasCriticas,
         (SELECT COUNT(*) FROM capacitacion.PracticaPaso WHERE PracticaId = @PracticaId) AS PasosPractica
  FROM capacitacion.Curso curso
  JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = @VersionId
  WHERE curso.CursoId = @CursoId;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

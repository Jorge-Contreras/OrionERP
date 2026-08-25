-- Catálogo completo de capacitación OrionERP (currículo v2).
--
-- Amplía el catálogo español revisado de 20260817_capacitacion_v1.sql con un
-- curso por cada módulo y pantalla del menú de OrionERP, más la ruta de
-- aprendizaje que ordena el recorrido completo. Es idempotente: cada bloque
-- inserta únicamente lo que falta y publica la versión 1 de cada curso nuevo
-- al final, después de haber redactado todo su contenido.
--
-- UTF-8 usage example:
-- sqlcmd -S <servidor> -d Orion_Training -E -v ExpectedDatabase="Orion_Training" -f 65001 -i 20260819_capacitacion_curriculum_v2.sql
-- Override ExpectedDatabase with -v for Orion_Sandbox or grupocarpio when intentionally deploying there.
:ON ERROR EXIT
:setvar ExpectedDatabase "Orion_Training"

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
IF @ExpectedDatabase NOT IN (N'Orion_Training', N'Orion_Sandbox', N'grupocarpio')
  THROW 51630, 'ExpectedDatabase debe ser Orion_Training, Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51631, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF OBJECT_ID(N'capacitacion.Curso', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.RutaAprendizaje', N'U') IS NULL
  THROW 51632, 'Instale primero 20260817_capacitacion_v1.sql: falta el esquema de capacitación.', 1;

IF NOT EXISTS (SELECT 1 FROM capacitacion.Curso WHERE Rfc = N'*' AND Clave = N'ORION-FUNDAMENTOS')
  THROW 51633, 'El currículo v2 requiere el catálogo semilla v1 previamente instalado.', 1;

BEGIN TRANSACTION;

DECLARE @CurriculumActor nvarchar(256) = N'OrionERP.Capacitacion.Curriculum.v2';

/* ---------------------------------------------------------------------------
   1. Cursos y versiones en borrador
   ------------------------------------------------------------------------ */

DECLARE @Cursos TABLE
(
  Clave nvarchar(64) NOT NULL PRIMARY KEY,
  Categoria nvarchar(80) NOT NULL,
  Nombre nvarchar(160) NOT NULL,
  Descripcion nvarchar(1000) NOT NULL,
  Duracion int NOT NULL,
  Objetivos nvarchar(2000) NOT NULL,
  Prerequisitos nvarchar(1000) NULL
);

INSERT INTO @Cursos (Clave, Categoria, Nombre, Descripcion, Duracion, Objetivos, Prerequisitos)
VALUES
  (N'CAPACITACION-MODULO', N'Capacitación', N'Módulo de Capacitación: sesiones, firma y acuse', N'Recorrido por el catálogo, las asignaciones, la sesión guiada, el avance por bloque, la evaluación, la práctica, la firma del instructor y el acuse del colaborador.', 90, N'Conducir y acreditar una sesión de capacitación ficticia, registrar avance y explicar qué evidencia queda guardada y por qué es inmutable.', N'Haber completado Fundamentos de OrionERP.'),
  (N'RESERVAS-CALENDARIO', N'Reservaciones', N'Calendario de ocupación, tarifas y recibos', N'Disponibilidad por habitación y fecha, bloqueos, precio por día, movimientos entre suites y emisión del recibo del huésped.', 90, N'Operar el calendario sin sobrevender, justificar un bloqueo o un cambio de tarifa y emitir el recibo correcto con respaldo.', N'Haber completado Reservaciones de principio a fin.'),
  (N'ARRENDADORES-ESTADO', N'Reservaciones', N'Arrendadores: estado de cuenta y liquidación', N'Propiedades en administración, ingresos por reservación, deducciones, comisiones y estado de cuenta del arrendador.', 80, N'Explicar el estado de cuenta de una propiedad ficticia y comprobar que cada concepto tenga respaldo contable y bancario.', N'Haber completado Reservaciones de principio a fin.'),
  (N'OT-OPERACION', N'Órdenes de trabajo', N'Órdenes de trabajo: ejecución, evidencia y plantillas', N'Tablero de órdenes, asignación por responsable, ejecución con evidencia de limpieza y mantenimiento, y plantillas versionadas por suite.', 100, N'Ejecutar y cerrar una orden ficticia con evidencia suficiente y explicar cómo la plantilla define la ruta crítica y los pasos obligatorios.', N'Haber completado Fundamentos de OrionERP.'),
  (N'CFDI-SAT-OPERACION', N'Fiscal y contabilidad', N'Operación fiscal: alta de RFC, descarga masiva y carga de XML', N'Abastecimiento de comprobantes: alta de emisores y receptores, descarga masiva desde el SAT, carga manual de XML y lectura del resumen fiscal.', 90, N'Abastecer comprobantes de forma trazable, interpretar el estado de cada descarga y evitar cargas duplicadas o con datos reales.', N'Haber completado Fundamentos de OrionERP.'),
  (N'CFDI-DECLARACION-PREVIA', N'Fiscal y contabilidad', N'Declaración previa y amarre de comprobantes', N'Revisión previa por periodo, clasificación de comprobantes, amarre de CFDI y complementos de pago con su póliza y registros contables del módulo fiscal.', 100, N'Clasificar comprobantes en declaración previa, ligar un CFDI o un complemento a su póliza y sostener la trazabilidad del amarre.', N'Haber completado Operación fiscal y Del CFDI a la contabilidad.'),
  (N'CONTA-POLIZAS', N'Finanzas', N'Pólizas y registros contables', N'Captura, revisión y cierre de pólizas, auxiliares contables, ligas entre transacciones y control del periodo.', 110, N'Elaborar y revisar una póliza balanceada, ubicar su registro contable y explicar cada liga sin duplicar efectos.', N'Haber completado Del CFDI a la contabilidad.'),
  (N'BANCOS-CONCILIACION', N'Finanzas', N'Bancos, movimientos y conciliación', N'Cuentas bancarias, movimientos, saldos, ligas de movimiento a póliza o transacción y prevención de duplicados en tesorería.', 100, N'Conciliar un movimiento bancario con su efecto contable autorizado sin crear un segundo registro y conservar la evidencia del enlace.', N'Haber completado Pólizas y registros contables.'),
  (N'CXP-RECURRENTES', N'Finanzas', N'Cuentas por pagar y cobrar recurrentes', N'Servicios, impuestos y seguros recurrentes: calendario de vencimientos, provisión, seguimiento y cierre del compromiso.', 80, N'Programar y dar seguimiento a un compromiso recurrente ficticio y explicar su efecto en el flujo de efectivo y en la contabilidad.', N'Haber completado Bancos, movimientos y conciliación.'),
  (N'REPORTES-FINANCIEROS', N'Analítica', N'Reportes financieros y salud del negocio', N'Hoja de trabajo, balanza de comprobación, estado de pérdidas y ganancias y tablero de salud financiera con ocupación, margen y flujo.', 90, N'Leer los cuatro reportes financieros, explicar el origen de cada cifra y detectar una inconsistencia antes de reportarla.', N'Haber completado Pólizas y registros contables.'),
  (N'LOGISTICA-COMPRAS', N'Logística', N'Proveedores, compras y recepciones', N'Alta y mantenimiento de proveedores, órdenes de compra, recepciones totales o parciales y control de referencias.', 100, N'Levantar una compra ficticia, recibirla sin duplicar y explicar su efecto en existencias, costo promedio y cuentas por pagar.', N'Haber completado Logística: materiales, compras e inventario.'),
  (N'LOGISTICA-INVENTARIO', N'Logística', N'Ubicaciones, existencias y conteos', N'Almacenes y ubicaciones, existencia por ubicación, mínimos y máximos, conteos cíclicos y validación de diferencias.', 90, N'Ejecutar un conteo ficticio, documentar la diferencia con evidencia y explicar quién autoriza el ajuste y por qué.', N'Haber completado Proveedores, compras y recepciones.'),
  (N'REST-POS-SERVICIO', N'Restaurante', N'Restaurante: POS, órdenes y entregas', N'Captura táctil por mesa o folio, modificadores, cobro, envío a cocina, despacho, entrega y pantalla pública de órdenes.', 100, N'Levantar y cobrar una orden ficticia, seguirla hasta la entrega y corregir un error sin borrar la evidencia del folio.', N'Haber completado Fundamentos de OrionERP.'),
  (N'REST-COCINA-PRODUCCION', N'Restaurante', N'Cocina KDS, recetas y producción', N'Comandas por partida y tiempos en la pantalla de cocina, recetas y BOM con subrecetas y rendimiento, y producción por lotes con merma.', 110, N'Operar la pantalla de cocina, leer una receta con rendimiento y registrar una producción ficticia con merma justificada.', N'Haber completado Restaurante: POS, órdenes y entregas.'),
  (N'REST-INVENTARIO-TURNOS', N'Restaurante', N'Inventario de restaurante y turnos de caja', N'Traspasos atómicos, ajustes y merma con evidencia; apertura de turno, conteo ciego, diferencias, aprobación y corte.', 100, N'Cerrar un turno ficticio con conteo ciego y explicar el efecto de un traspaso o de un ajuste sobre existencias y resultados.', N'Haber completado Cocina KDS, recetas y producción.'),
  (N'REST-CATALOGO-CONFIG', N'Restaurante', N'Menús, productos y configuración operativa', N'Menús por horario, secciones, modificadores, sedes, productos, variantes y precios, además de mesas, estaciones, almacenes y cuentas.', 100, N'Publicar un cambio de menú ficticio y explicar cómo la configuración operativa afecta al punto de venta, a cocina y al inventario.', N'Haber completado Restaurante: POS, órdenes y entregas.'),
  (N'REST-COMERCIAL', N'Restaurante', N'Promociones, membresía, reportes y sitio público', N'Reglas de promoción y códigos, membresía y puntos del Club Bruno, reportes de venta y margen, y contenido del sitio público.', 100, N'Configurar una promoción ficticia, leer su desempeño en reportes y preparar contenido del sitio público con control de cambios.', N'Haber completado Menús, productos y configuración operativa.'),
  (N'RH-ASISTENCIA', N'Capital Humano', N'Control de asistencia, kiosco y equipo', N'Calendario de asistencia por periodo, anomalías, auditoría de eventos, kiosco de registro y colas de aprobación del supervisor.', 100, N'Revisar la asistencia de un periodo ficticio, resolver una anomalía por el flujo autorizado y usar el kiosco sin exponer datos de terceros.', N'Haber completado Capital Humano: autoservicio del colaborador.'),
  (N'RH-CONFIG-TIEMPO', N'Capital Humano', N'Configuración de tiempo: sitios, horarios y kioscos', N'Sitios y geocercas, plantillas de horario, políticas de asistencia, responsables por equipo y kioscos habilitados.', 90, N'Explicar cómo la configuración de tiempo determina anomalías y saldos, y proponer un cambio sin alterar periodos ya cerrados.', N'Haber completado Control de asistencia, kiosco y equipo.'),
  (N'RH-AUSENCIAS', N'Capital Humano', N'Ausencias: políticas, saldos y solicitudes', N'Tipos de ausencia, políticas de devengo, saldos por colaborador, solicitudes, aprobaciones y ajustes auditados.', 90, N'Tramitar y resolver una ausencia ficticia respetando saldo, evidencia y bitácora de ajustes.', N'Haber completado Capital Humano: autoservicio del colaborador.'),
  (N'RH-PRENOMINA', N'Capital Humano', N'Pre-nómina: validación, bloqueo y exportación', N'Unidades de tiempo del periodo, incidencias, validación, aprobación, bloqueo y exportación de la información hacia nómina.', 100, N'Cerrar un periodo ficticio de pre-nómina con incidencias justificadas y explicar el efecto del bloqueo sobre correcciones posteriores.', N'Haber completado Ausencias y Control de asistencia.'),
  (N'RH-EXPEDIENTES', N'Capital Humano', N'Expediente del colaborador', N'Alta y mantenimiento de colaboradores, datos laborales, sede, contrato, archivos y fotografía con control de privacidad.', 80, N'Mantener un expediente ficticio completo y explicar qué dato es sensible, quién puede consultarlo y cómo se documenta un cambio.', N'Haber completado Fundamentos de OrionERP.'),
  (N'AJUSTES-PLANTILLAS', N'Administración', N'Ajustes: plantillas contables y configuración', N'Plantillas de póliza, parámetros por RFC y configuración compartida que gobierna el comportamiento de varios módulos.', 80, N'Modificar una plantilla ficticia entendiendo su alcance, verificar el efecto y revertir el ajuste dejando evidencia.', N'Haber completado Pólizas y registros contables.'),
  (N'ADMIN-SEGURIDAD', N'Administración', N'Portal de seguridad: usuarios, roles y RFC', N'Altas de usuario, asignación de roles, alcance por RFC, vínculo con el colaborador, bloqueos y revisión periódica de accesos.', 90, N'Otorgar el acceso mínimo necesario a un usuario ficticio y explicar el efecto de cada rol y del RFC activo sobre lo que se puede ver.', N'Haber completado Fundamentos de OrionERP.');

INSERT INTO capacitacion.Curso (Rfc, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, CreadoPor)
SELECT N'*', source.Clave, source.Categoria, source.Nombre, source.Descripcion, source.Duracion, @CurriculumActor
FROM @Cursos source
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Curso target WHERE target.Rfc = N'*' AND target.Clave = source.Clave
);

INSERT INTO capacitacion.CursoVersion
  (CursoId, NumeroVersion, Estado, Objetivos, Prerequisitos, CalificacionMinima, PublicadaEn, PublicadaPor, CreadaPor)
SELECT curso.CursoId, 1, N'BORRADOR', source.Objetivos, source.Prerequisitos, 80, NULL, NULL, @CurriculumActor
FROM @Cursos source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.Clave
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.CursoVersion versionInfo
  WHERE versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
);

/* ---------------------------------------------------------------------------
   2. Lecciones: cada curso recorre Preparar, Operar y Cerrar
   ------------------------------------------------------------------------ */

DECLARE @Lecciones TABLE
(
  CursoClave nvarchar(64) NOT NULL,
  Orden int NOT NULL,
  Clave nvarchar(64) NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Objetivo nvarchar(1000) NOT NULL,
  Duracion int NOT NULL,
  PRIMARY KEY (CursoClave, Orden)
);

INSERT INTO @Lecciones (CursoClave, Orden, Clave, Titulo, Objetivo, Duracion)
VALUES
  (N'CAPACITACION-MODULO', 1, N'PREPARAR', N'Preparar: catálogo, asignaciones y roles', N'Distinguir catálogo, asignación y sesión, y reconocer qué puede hacer un colaborador, un instructor y un auditor.', 25),
  (N'CAPACITACION-MODULO', 2, N'OPERAR', N'Operar: sesión guiada, avance y práctica', N'Programar una sesión, avanzar bloque por bloque, registrar evaluación y práctica y seguir el progreso del grupo.', 40),
  (N'CAPACITACION-MODULO', 3, N'CERRAR', N'Cerrar: firma, acuse y evidencia inmutable', N'Firmar como instructor, acusar como colaborador y explicar qué evidencia queda registrada de forma permanente.', 25),

  (N'RESERVAS-CALENDARIO', 1, N'PREPARAR', N'Preparar: disponibilidad, RFC y periodo', N'Confirmar entorno, RFC y periodo, y leer el calendario antes de mover una sola fecha.', 20),
  (N'RESERVAS-CALENDARIO', 2, N'OPERAR', N'Operar: bloqueos, tarifas y cambios de habitación', N'Aplicar un bloqueo o una tarifa por día y mover una reservación ficticia sin generar traslapes.', 45),
  (N'RESERVAS-CALENDARIO', 3, N'CERRAR', N'Cerrar: recibo, evidencia y comunicación', N'Emitir el recibo correcto, verificar el calendario resultante y documentar la excepción cuando exista.', 25),

  (N'ARRENDADORES-ESTADO', 1, N'PREPARAR', N'Preparar: propiedad, propietario y periodo', N'Identificar la propiedad ficticia, su propietario y el periodo del estado de cuenta.', 20),
  (N'ARRENDADORES-ESTADO', 2, N'OPERAR', N'Operar: ingresos, deducciones y comisiones', N'Recorrer los conceptos del estado de cuenta y comprobar que cada uno provenga de una reservación o gasto registrado.', 35),
  (N'ARRENDADORES-ESTADO', 3, N'CERRAR', N'Cerrar: liquidación, respaldo y aclaraciones', N'Explicar la liquidación al propietario y preparar la respuesta a una aclaración con respaldo verificable.', 25),

  (N'OT-OPERACION', 1, N'PREPARAR', N'Preparar: plantilla, alcance y responsable', N'Reconocer la plantilla que origina la orden, su alcance por suite y el responsable asignado.', 25),
  (N'OT-OPERACION', 2, N'OPERAR', N'Operar: ejecución, pasos y evidencia', N'Ejecutar los pasos de una orden ficticia, capturar evidencia y registrar un hallazgo sin salirse del flujo.', 45),
  (N'OT-OPERACION', 3, N'CERRAR', N'Cerrar: validación, reapertura y plantillas', N'Cerrar la orden con validación, explicar cuándo procede reabrirla y proponer un cambio de plantilla versionado.', 30),

  (N'CFDI-SAT-OPERACION', 1, N'PREPARAR', N'Preparar: RFC, credenciales y alcance', N'Confirmar el RFC activo, el alta de emisores y receptores y el alcance de la descarga solicitada.', 20),
  (N'CFDI-SAT-OPERACION', 2, N'OPERAR', N'Operar: descarga masiva y carga de XML', N'Solicitar una descarga, interpretar su estado y cargar un XML local sin repetir comprobantes ya existentes.', 45),
  (N'CFDI-SAT-OPERACION', 3, N'CERRAR', N'Cerrar: resumen fiscal y control de faltantes', N'Leer el resumen, detectar comprobantes faltantes o duplicados y documentar el seguimiento.', 25),

  (N'CFDI-DECLARACION-PREVIA', 1, N'PREPARAR', N'Preparar: periodo, filtros y clasificación', N'Filtrar el periodo correcto y reconocer el estado de cada comprobante antes de tocar nada.', 25),
  (N'CFDI-DECLARACION-PREVIA', 2, N'OPERAR', N'Operar: amarre de CFDI y complementos', N'Ligar un comprobante y un complemento de pago a su póliza y revisar los registros contables resultantes.', 45),
  (N'CFDI-DECLARACION-PREVIA', 3, N'CERRAR', N'Cerrar: trazabilidad y excepciones', N'Comprobar la trazabilidad completa del amarre y escalar un comprobante que no debe contabilizarse.', 30),

  (N'CONTA-POLIZAS', 1, N'PREPARAR', N'Preparar: periodo, cuentas y plantilla', N'Confirmar periodo abierto, cuentas contables aplicables y la plantilla que corresponde al caso.', 25),
  (N'CONTA-POLIZAS', 2, N'OPERAR', N'Operar: captura, balance y ligas', N'Capturar una póliza balanceada, revisar su auxiliar y ligarla con la transacción o el comprobante que la origina.', 50),
  (N'CONTA-POLIZAS', 3, N'CERRAR', N'Cerrar: revisión, corrección y cierre', N'Revisar la póliza como tercero, corregir por el flujo autorizado y explicar el efecto del cierre de periodo.', 35),

  (N'BANCOS-CONCILIACION', 1, N'PREPARAR', N'Preparar: cuenta, saldo y corte', N'Ubicar la cuenta bancaria ficticia, su saldo y el corte que se pretende conciliar.', 20),
  (N'BANCOS-CONCILIACION', 2, N'OPERAR', N'Operar: movimientos, ligas y diferencias', N'Ligar un movimiento con su efecto contable existente y clasificar las diferencias encontradas.', 45),
  (N'BANCOS-CONCILIACION', 3, N'CERRAR', N'Cerrar: conciliación explicada y evidencia', N'Explicar la conciliación resultante, conservar la evidencia y evitar el doble registro del mismo flujo.', 35),

  (N'CXP-RECURRENTES', 1, N'PREPARAR', N'Preparar: compromiso, periodicidad y responsable', N'Identificar el servicio recurrente ficticio, su periodicidad, su monto estimado y quién lo autoriza.', 20),
  (N'CXP-RECURRENTES', 2, N'OPERAR', N'Operar: calendario, provisión y seguimiento', N'Programar el compromiso, revisar el calendario de vencimientos y dar seguimiento a un pago simulado.', 35),
  (N'CXP-RECURRENTES', 3, N'CERRAR', N'Cerrar: variaciones, cancelación y evidencia', N'Documentar una variación de monto, explicar cómo se suspende el compromiso y conservar la evidencia.', 25),

  (N'REPORTES-FINANCIEROS', 1, N'PREPARAR', N'Preparar: periodo, RFC y alcance del reporte', N'Elegir periodo y RFC correctos y anticipar qué pregunta responde cada reporte.', 20),
  (N'REPORTES-FINANCIEROS', 2, N'OPERAR', N'Operar: hoja de trabajo, balanza y resultados', N'Recorrer hoja de trabajo, balanza y estado de resultados, y rastrear una cifra hasta su póliza de origen.', 45),
  (N'REPORTES-FINANCIEROS', 3, N'CERRAR', N'Cerrar: salud financiera y comunicación', N'Leer el tablero de salud, explicar un indicador a un tercero y reportar una inconsistencia con evidencia.', 25),

  (N'LOGISTICA-COMPRAS', 1, N'PREPARAR', N'Preparar: proveedor, material y necesidad', N'Validar el proveedor ficticio, el material TRN y la necesidad que justifica la compra.', 20),
  (N'LOGISTICA-COMPRAS', 2, N'OPERAR', N'Operar: orden de compra y recepción', N'Levantar una orden de compra, recibirla parcial o totalmente y revisar el efecto en existencias y costo.', 50),
  (N'LOGISTICA-COMPRAS', 3, N'CERRAR', N'Cerrar: referencias, diferencias y cuentas por pagar', N'Cerrar la compra con referencias verificables y explicar cómo llega a cuentas por pagar sin duplicarse.', 30),

  (N'LOGISTICA-INVENTARIO', 1, N'PREPARAR', N'Preparar: ubicación, material y corte', N'Reconocer almacenes y ubicaciones ficticias, su existencia declarada y el corte del conteo.', 20),
  (N'LOGISTICA-INVENTARIO', 2, N'OPERAR', N'Operar: conteo, recuento y diferencias', N'Registrar un conteo ficticio, recontar lo que no cuadre y clasificar la diferencia encontrada.', 40),
  (N'LOGISTICA-INVENTARIO', 3, N'CERRAR', N'Cerrar: autorización del ajuste y evidencia', N'Preparar el ajuste para autorización, conservar evidencia y explicar el efecto en el costo.', 30),

  (N'REST-POS-SERVICIO', 1, N'PREPARAR', N'Preparar: sede, caja y menú vigente', N'Confirmar sede, turno abierto y menú vigente antes de capturar la primera orden.', 20),
  (N'REST-POS-SERVICIO', 2, N'OPERAR', N'Operar: captura, modificadores y cobro', N'Capturar una orden ficticia con modificadores, cobrarla y enviarla a cocina con la información completa.', 50),
  (N'REST-POS-SERVICIO', 3, N'CERRAR', N'Cerrar: entrega, correcciones y pantalla pública', N'Entregar por folio, corregir un error dejando rastro y explicar qué muestra la pantalla pública.', 30),

  (N'REST-COCINA-PRODUCCION', 1, N'PREPARAR', N'Preparar: partidas, tiempos y receta', N'Reconocer las partidas de cocina, los tiempos objetivo y la receta que sustenta cada producto.', 25),
  (N'REST-COCINA-PRODUCCION', 2, N'OPERAR', N'Operar: comandas, subrecetas y lotes', N'Atender comandas en la pantalla de cocina y registrar una producción por lotes con rendimiento real.', 50),
  (N'REST-COCINA-PRODUCCION', 3, N'CERRAR', N'Cerrar: merma, rendimiento y costo', N'Justificar la merma, comparar rendimiento esperado contra real y explicar el efecto en costo.', 35),

  (N'REST-INVENTARIO-TURNOS', 1, N'PREPARAR', N'Preparar: almacén, fondo de caja y apertura', N'Confirmar almacén, fondo inicial y apertura del turno ficticio antes de cualquier movimiento.', 20),
  (N'REST-INVENTARIO-TURNOS', 2, N'OPERAR', N'Operar: traspasos, ajustes y conteo ciego', N'Registrar un traspaso o merma con evidencia y ejecutar el conteo ciego del turno.', 45),
  (N'REST-INVENTARIO-TURNOS', 3, N'CERRAR', N'Cerrar: diferencias, aprobación y corte', N'Explicar la diferencia de caja, enviarla a aprobación y cerrar el turno sin editar registros previos.', 35),

  (N'REST-CATALOGO-CONFIG', 1, N'PREPARAR', N'Preparar: sede, catálogo y vigencia', N'Ubicar la sede ficticia, el catálogo de productos y la vigencia del menú que se va a modificar.', 20),
  (N'REST-CATALOGO-CONFIG', 2, N'OPERAR', N'Operar: menús, variantes, precios y estaciones', N'Modificar un menú ficticio con sus secciones, modificadores, precios y estación de preparación.', 50),
  (N'REST-CATALOGO-CONFIG', 3, N'CERRAR', N'Cerrar: publicación, impacto y reversión', N'Publicar el cambio, verificar su impacto en el punto de venta y explicar cómo revertirlo.', 30),

  (N'REST-COMERCIAL', 1, N'PREPARAR', N'Preparar: objetivo comercial y reglas', N'Definir el objetivo de la promoción ficticia y las reglas que la limitan en tiempo, producto y canal.', 20),
  (N'REST-COMERCIAL', 2, N'OPERAR', N'Operar: promociones, códigos y membresía', N'Configurar la promoción y su código, revisar puntos de membresía y comprobar su aplicación en una venta ficticia.', 45),
  (N'REST-COMERCIAL', 3, N'CERRAR', N'Cerrar: desempeño, liquidaciones y sitio público', N'Leer el desempeño en reportes y preparar el contenido del sitio público con control de cambios.', 35),

  (N'RH-ASISTENCIA', 1, N'PREPARAR', N'Preparar: periodo, equipo y privacidad', N'Elegir el periodo y el equipo correctos y recordar el límite de privacidad de la información de terceros.', 25),
  (N'RH-ASISTENCIA', 2, N'OPERAR', N'Operar: eventos, anomalías y kiosco', N'Revisar eventos y anomalías del periodo ficticio y registrar una entrada desde el kiosco de forma segura.', 45),
  (N'RH-ASISTENCIA', 3, N'CERRAR', N'Cerrar: aprobación, auditoría y preparación del periodo', N'Resolver la cola de aprobación, revisar la auditoría del evento y dejar el periodo listo para pre-nómina.', 30),

  (N'RH-CONFIG-TIEMPO', 1, N'PREPARAR', N'Preparar: sitio, horario y política vigente', N'Reconocer el sitio ficticio, su horario y la política de asistencia que ya está aplicándose.', 20),
  (N'RH-CONFIG-TIEMPO', 2, N'OPERAR', N'Operar: geocercas, plantillas y responsables', N'Revisar una geocerca, una plantilla de horario y la asignación de responsables sin romper lo vigente.', 40),
  (N'RH-CONFIG-TIEMPO', 3, N'CERRAR', N'Cerrar: vigencias, kioscos y comunicación', N'Aplicar una vigencia futura, habilitar un kiosco ficticio y comunicar el cambio a quien lo opera.', 30),

  (N'RH-AUSENCIAS', 1, N'PREPARAR', N'Preparar: tipo de ausencia, política y saldo', N'Identificar el tipo de ausencia ficticio, su política de devengo y el saldo disponible.', 20),
  (N'RH-AUSENCIAS', 2, N'OPERAR', N'Operar: solicitud, evidencia y aprobación', N'Capturar una solicitud con evidencia ficticia y recorrer su aprobación o rechazo con motivo.', 40),
  (N'RH-AUSENCIAS', 3, N'CERRAR', N'Cerrar: saldos, ajustes auditados y seguimiento', N'Verificar el saldo resultante, registrar un ajuste auditado y dar seguimiento hasta el cierre.', 30),

  (N'RH-PRENOMINA', 1, N'PREPARAR', N'Preparar: periodo, grupo de pago e incidencias', N'Confirmar el periodo, el grupo de pago ficticio y las incidencias pendientes antes de validar.', 25),
  (N'RH-PRENOMINA', 2, N'OPERAR', N'Operar: validación, unidades de tiempo y aprobación', N'Revisar unidades de tiempo, justificar incidencias y aprobar el periodo ficticio con evidencia.', 45),
  (N'RH-PRENOMINA', 3, N'CERRAR', N'Cerrar: bloqueo, exportación y correcciones', N'Bloquear el periodo, exportar el resultado y explicar cómo se corrige algo después del bloqueo.', 30),

  (N'RH-EXPEDIENTES', 1, N'PREPARAR', N'Preparar: identidad, alcance y privacidad', N'Confirmar la identidad ficticia del colaborador y qué parte del expediente corresponde a cada rol.', 20),
  (N'RH-EXPEDIENTES', 2, N'OPERAR', N'Operar: datos laborales, archivos y fotografía', N'Actualizar datos laborales y adjuntar un archivo ficticio con nombre, tipo y vigencia correctos.', 35),
  (N'RH-EXPEDIENTES', 3, N'CERRAR', N'Cerrar: bajas, resguardo y trazabilidad', N'Explicar el efecto de una baja ficticia, el resguardo de documentos y la trazabilidad del cambio.', 25),

  (N'AJUSTES-PLANTILLAS', 1, N'PREPARAR', N'Preparar: alcance del ajuste y RFC afectado', N'Identificar qué módulos consumen la plantilla o el parámetro que se pretende modificar.', 20),
  (N'AJUSTES-PLANTILLAS', 2, N'OPERAR', N'Operar: plantilla contable y parámetros', N'Modificar una plantilla ficticia, probar su efecto en una propuesta y comparar el resultado esperado.', 35),
  (N'AJUSTES-PLANTILLAS', 3, N'CERRAR', N'Cerrar: verificación, reversión y aviso', N'Verificar el efecto, revertir el ajuste cuando corresponda y avisar a las áreas impactadas.', 25),

  (N'ADMIN-SEGURIDAD', 1, N'PREPARAR', N'Preparar: identidad, RFC y mínimo privilegio', N'Reconocer la relación entre usuario, colaborador y RFC, y el principio de mínimo privilegio.', 20),
  (N'ADMIN-SEGURIDAD', 2, N'OPERAR', N'Operar: altas, roles y alcance por RFC', N'Dar de alta un usuario ficticio, asignar el rol mínimo y comprobar qué puede ver con ese alcance.', 40),
  (N'ADMIN-SEGURIDAD', 3, N'CERRAR', N'Cerrar: bloqueos, revisión de accesos y evidencia', N'Bloquear o retirar un acceso ficticio, documentar el motivo y programar la revisión periódica.', 30);

INSERT INTO capacitacion.Leccion (CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida)
SELECT versionInfo.CursoVersionId, source.Orden, source.Clave, source.Titulo, source.Objetivo, source.Duracion, 1
FROM @Lecciones source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Leccion target
  WHERE target.CursoVersionId = versionInfo.CursoVersionId AND target.Clave = source.Clave
);

/* ---------------------------------------------------------------------------
   3. Bloques de contenido: objetivos, teoría, demostración, alerta,
      práctica, evaluación y cierre para cada curso
   ------------------------------------------------------------------------ */

DECLARE @Bloques TABLE
(
  CursoClave nvarchar(64) NOT NULL,
  LeccionClave nvarchar(64) NOT NULL,
  Orden int NOT NULL,
  Tipo nvarchar(24) NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Contenido nvarchar(max) NOT NULL,
  ConfiguracionJson nvarchar(max) NULL,
  PRIMARY KEY (CursoClave, LeccionClave, Orden)
);

INSERT INTO @Bloques (CursoClave, LeccionClave, Orden, Tipo, Titulo, Contenido, ConfiguracionJson)
VALUES
  (N'CAPACITACION-MODULO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: qué acredita este módulo', N'Al finalizar podrás explicar la diferencia entre catálogo, asignación y sesión, conducir una sesión guiada y describir la evidencia que queda registrada al acreditar un curso.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'CAPACITACION-MODULO', N'PREPARAR', 2, N'TEORIA', N'Explicar: catálogo, asignación, sesión y evidencia', N'El catálogo publica versiones inmutables de cada curso. La asignación entrega una versión a una persona con instructor y fecha límite. La sesión conduce a un grupo bloque por bloque. La evidencia final combina evaluación, práctica, firma del instructor y acuse del colaborador.', N'{"callout":"info","flowStep":"Explicar","diagram":["Catálogo","Asignación","Sesión","Evaluación","Práctica","Firma","Acuse"]}'),
  (N'CAPACITACION-MODULO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: conducir una sesión guiada', N'El instructor mostrará cómo crear la sesión, compartir el código de acceso, avanzar entre bloques, registrar el avance de cada participante y volver a un bloque sin perder lo ya capturado.', N'{"flowStep":"Demostrar","demoSteps":["Crear sesión","Compartir código","Avanzar bloque","Registrar avance","Revisar participantes"],"notasInstructor":"Entregue el control a la persona en formación después del primer bloque; la confusión habitual es creer que el avance de la sesión completa automáticamente la asignación individual."}'),
  (N'CAPACITACION-MODULO', N'OPERAR', 2, N'ALERTA', N'Alerta: la evidencia de acreditación no se corrige después', N'La firma del instructor, la finalización y la bitácora de eventos son inmutables. Antes de firmar confirma identidad, calificación y resultado práctico: una firma equivocada solo puede documentarse, nunca borrarse.', N'{"severity":"critical","notasInstructor":"Pregunte qué haría si firmara a la persona equivocada. La respuesta correcta es documentar y escalar, no intentar editar la evidencia."}'),
  (N'CAPACITACION-MODULO', N'OPERAR', 3, N'PRACTICA', N'Practicar: sesión, avance y resultados', N'Con las identidades sintéticas del entorno, crea una sesión ficticia, avanza al menos dos bloques, registra una evaluación y una práctica, y revisa el porcentaje de avance resultante.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Observe que confirme el curso y la versión antes de crear la sesión. No permita usar identidades que no sean las sintéticas de capacitación."}'),
  (N'CAPACITACION-MODULO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: roles, evidencia y acreditación', N'Responde la evaluación y demuestra que sabes cuándo firmar, qué acredita cada paso y qué información queda registrada de forma permanente.', N'{"required":true,"flowStep":"Evaluar","checklist":["Curso y versión correctos","Avance registrado","Práctica evaluada","Firma y acuse"]}'),
  (N'CAPACITACION-MODULO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: acreditar es demostrar, no asistir', N'Un curso se acredita cuando la evaluación aprueba, la práctica se valida, el instructor firma y la persona acusa de recibido. Asistir a la sesión no sustituye ninguno de esos pasos.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RESERVAS-CALENDARIO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: leer el calendario antes de moverlo', N'Al finalizar podrás interpretar disponibilidad, bloqueos y tarifas por día, y decidir si un cambio es seguro antes de aplicarlo.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RESERVAS-CALENDARIO', N'PREPARAR', 2, N'TEORIA', N'Explicar: una fecha, una habitación, una verdad', N'El calendario expresa la ocupación real por habitación y día. Un bloqueo retira inventario, una tarifa cambia el precio del día y un cambio de habitación mueve la ocupación completa. Cada movimiento afecta cobro, limpieza y reportes.', N'{"callout":"info","flowStep":"Explicar","diagram":["Disponibilidad","Bloqueo","Tarifa","Reservación","Recibo"]}'),
  (N'RESERVAS-CALENDARIO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: bloqueo, tarifa y cambio de suite', N'El instructor mostrará cómo bloquear una fecha ficticia, ajustar el precio de un día, mover una reservación entre suites y comprobar que no se produjo un traslape.', N'{"flowStep":"Demostrar","demoSteps":["Confirmar periodo","Bloquear fecha","Ajustar tarifa","Mover reservación","Verificar traslapes"],"notasInstructor":"Muestre primero el estado del calendario antes y después del cambio. El error frecuente es mover la reservación sin revisar la habitación destino."}'),
  (N'RESERVAS-CALENDARIO', N'OPERAR', 2, N'ALERTA', N'Alerta: nunca resuelvas un traslape borrando la otra reservación', N'Si dos reservaciones coinciden, documenta el conflicto y escálalo. Eliminar o sobrescribir la reservación existente destruye evidencia de cobro y deja al huésped sin habitación.', N'{"severity":"critical","notasInstructor":"Plantee un traslape y pida el plan de acción. Debe verificar ambas reservaciones y su cobro antes de proponer cualquier movimiento."}'),
  (N'RESERVAS-CALENDARIO', N'OPERAR', 3, N'PRACTICA', N'Practicar: calendario de las suites TRN', N'Con las habitaciones ficticias TRN aplica un bloqueo, ajusta una tarifa del día y verifica el resultado en la lista de reservaciones. No modifiques fechas fuera del escenario asignado.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe que confirme RFC y periodo, y que verifique el calendario después de cada cambio en lugar de asumir el resultado."}'),
  (N'RESERVAS-CALENDARIO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: disponibilidad, tarifas y recibos', N'Responde la evaluación y demuestra que sabes bloquear, tarifar y emitir un recibo sin generar traslapes ni cobros duplicados.', N'{"required":true,"flowStep":"Evaluar","checklist":["Sin traslapes","Tarifa justificada","Recibo correcto","Excepción documentada"]}'),
  (N'RESERVAS-CALENDARIO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el calendario es un compromiso con el huésped', N'Antes de cerrar confirma que la ocupación refleja lo cobrado, que el bloqueo tiene motivo y que el recibo coincide con la estancia. Una fecha mal movida se convierte en un huésped sin habitación.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'ARRENDADORES-ESTADO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: entender qué se le rinde al propietario', N'Al finalizar podrás explicar cada concepto del estado de cuenta de una propiedad y comprobar de dónde proviene.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'ARRENDADORES-ESTADO', N'PREPARAR', 2, N'TEORIA', N'Explicar: del ingreso operativo a la liquidación', N'El estado de cuenta reúne los ingresos de las reservaciones de la propiedad, resta gastos, comisiones y retenciones, y presenta el importe a liquidar. Cada línea debe poder rastrearse hasta una reservación, un gasto o un movimiento bancario.', N'{"callout":"info","flowStep":"Explicar","diagram":["Reservación","Ingreso","Deducción","Comisión","Liquidación"]}'),
  (N'ARRENDADORES-ESTADO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: recorrer el estado de cuenta', N'El instructor mostrará cómo filtrar por propiedad y periodo, abrir el detalle de un concepto y comprobar su origen en la reservación o el gasto correspondiente.', N'{"flowStep":"Demostrar","demoSteps":["Filtrar propiedad","Elegir periodo","Abrir detalle","Rastrear origen","Comparar totales"],"notasInstructor":"Pida que anticipe el total antes de mostrarlo. La confusión habitual es sumar ingresos brutos sin considerar deducciones."}'),
  (N'ARRENDADORES-ESTADO', N'OPERAR', 2, N'ALERTA', N'Alerta: no ajustes el estado de cuenta a mano', N'Si una cifra no cuadra, la corrección ocurre en su origen: la reservación, el gasto o la póliza. Ajustar la presentación deja al propietario con un documento que la contabilidad no puede sostener.', N'{"severity":"critical","notasInstructor":"Presente una diferencia y pida el punto de corrección correcto. Debe ir al origen, nunca al resumen."}'),
  (N'ARRENDADORES-ESTADO', N'OPERAR', 3, N'PRACTICA', N'Practicar: rastrear tres conceptos', N'Toma tres conceptos del estado de cuenta ficticio y rastrea cada uno hasta su reservación o gasto de origen. Documenta cualquier concepto que no puedas explicar.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la capacidad de rastreo, no la velocidad. Un concepto sin origen identificado es un hallazgo válido."}'),
  (N'ARRENDADORES-ESTADO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: respaldo, deducciones y aclaraciones', N'Responde la evaluación y demuestra que sabes sostener cada cifra del estado de cuenta frente a una aclaración del propietario.', N'{"required":true,"flowStep":"Evaluar","checklist":["Ingresos rastreados","Deducciones justificadas","Comisión aplicada","Liquidación conciliada"]}'),
  (N'ARRENDADORES-ESTADO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: rendir cuentas es poder explicar cada línea', N'Antes de enviar el estado de cuenta confirma que cada concepto tiene respaldo, que la liquidación coincide con el movimiento bancario y que las aclaraciones quedan documentadas.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'OT-OPERACION', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: la orden nace de una plantilla', N'Al finalizar podrás ejecutar una orden de trabajo con evidencia y explicar cómo la plantilla determina pasos, tiempos y ruta crítica.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'OT-OPERACION', N'PREPARAR', 2, N'TEORIA', N'Explicar: plantilla, orden, pasos y evidencia', N'La plantilla define los pasos obligatorios de limpieza o mantenimiento por tipo de suite. La orden aplica esa plantilla a una fecha y un responsable. La evidencia demuestra que cada paso crítico ocurrió realmente.', N'{"callout":"info","flowStep":"Explicar","diagram":["Plantilla","Orden","Asignación","Ejecución","Evidencia","Cierre"]}'),
  (N'OT-OPERACION', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: del tablero al cierre', N'El instructor mostrará cómo leer el tablero, abrir una orden, avanzar sus pasos, capturar evidencia de un paso crítico y registrar un hallazgo que requiere otra orden.', N'{"flowStep":"Demostrar","demoSteps":["Leer tablero","Abrir orden","Avanzar pasos","Capturar evidencia","Registrar hallazgo"],"notasInstructor":"Muestre un paso crítico sin evidencia y pregunte si la orden puede cerrarse. La respuesta es no."}'),
  (N'OT-OPERACION', N'OPERAR', 2, N'ALERTA', N'Alerta: no cierres una orden con pasos críticos sin evidencia', N'Cerrar sin evidencia convierte la orden en una afirmación sin respaldo. Si un paso no pudo ejecutarse, documenta el motivo y escálalo en lugar de marcarlo como realizado.', N'{"severity":"critical","notasInstructor":"Insista en que marcar un paso no ejecutado es una falta grave; el flujo correcto es documentar el impedimento."}'),
  (N'OT-OPERACION', N'OPERAR', 3, N'PRACTICA', N'Practicar: ejecutar una orden ficticia', N'Con una suite TRN ejecuta una orden ficticia de principio a fin, captura evidencia en al menos un paso y registra un hallazgo con su descripción.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la calidad de la evidencia y la descripción del hallazgo, no la rapidez del cierre."}'),
  (N'OT-OPERACION', N'CERRAR', 1, N'EVALUACION', N'Evaluar: ruta crítica, evidencia y reapertura', N'Responde la evaluación y demuestra que sabes cuándo cerrar, cuándo reabrir y cómo proponer un cambio de plantilla sin afectar órdenes en curso.', N'{"required":true,"flowStep":"Evaluar","checklist":["Pasos completos","Evidencia suficiente","Hallazgo registrado","Plantilla versionada"]}'),
  (N'OT-OPERACION', N'CERRAR', 2, N'RESUMEN', N'Cerrar: la orden cerrada debe poder auditarse', N'Antes de cerrar confirma pasos, evidencia, tiempos y hallazgos. Una orden cerrada es el documento que sostiene que la habitación quedó lista.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'CFDI-SAT-OPERACION', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: abastecer comprobantes sin contaminar', N'Al finalizar podrás dar de alta un RFC, solicitar una descarga y cargar un XML local comprobando que no dupliques comprobantes.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'CFDI-SAT-OPERACION', N'PREPARAR', 2, N'TEORIA', N'Explicar: origen, estado y unicidad del comprobante', N'Un comprobante puede llegar por descarga masiva o por carga manual. En ambos casos el UUID define su unicidad. El alta de RFC determina a qué contribuyente pertenece y el resumen muestra qué falta por conciliar.', N'{"callout":"info","flowStep":"Explicar","diagram":["Alta de RFC","Descarga SAT","Carga de XML","Resumen","Declaración previa"]}'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: solicitar, revisar y cargar', N'El instructor mostrará cómo registrar un RFC ficticio, cómo se solicita una descarga y qué significa cada estado, y cómo cargar el XML local de capacitación revisando el resultado del procesamiento.', N'{"flowStep":"Demostrar","demoSteps":["Registrar RFC","Solicitar descarga","Leer estado","Cargar XML local","Verificar resultado"],"notasInstructor":"En Training la descarga del SAT está simulada. Explique los estados sin intentar una solicitud real y aclare que el XML del curso no es timbrable."}'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 2, N'ALERTA', N'Alerta: nunca cargues XML reales al entorno de capacitación', N'Los XML de producción contienen RFC, domicilios e importes reales. En capacitación se usa exclusivamente el archivo local marcado como no timbrable, con sellos deliberadamente inválidos.', N'{"severity":"critical","notasInstructor":"Pida identificar los marcadores NO_VALIDO_ENTRENAMIENTO y los RFC genéricos antes de cualquier carga."}'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 3, N'PRACTICA', N'Practicar: alta, carga y resumen', N'Registra un RFC ficticio, carga el XML local de capacitación, confirma el resultado del procesamiento y localiza el comprobante en el resumen fiscal.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe que verifique la unicidad por UUID antes de repetir una carga cuyo resultado fue incierto."}'),
  (N'CFDI-SAT-OPERACION', N'CERRAR', 1, N'EVALUACION', N'Evaluar: origen, duplicados y faltantes', N'Responde la evaluación y demuestra que sabes reconocer un comprobante duplicado, uno faltante y uno que no pertenece al RFC activo.', N'{"required":true,"flowStep":"Evaluar","checklist":["RFC correcto","UUID único","Estado leído","Faltante documentado"]}'),
  (N'CFDI-SAT-OPERACION', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el abasto fiscal se mide por lo que falta', N'Antes de cerrar confirma qué comprobantes llegaron, cuáles faltan y cuáles quedaron rechazados. Un periodo sin errores visibles puede seguir incompleto.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'CFDI-DECLARACION-PREVIA', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: revisar antes de contabilizar', N'Al finalizar podrás clasificar los comprobantes del periodo y decidir cuáles están listos para amarrarse a una póliza.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'CFDI-DECLARACION-PREVIA', N'PREPARAR', 2, N'TEORIA', N'Explicar: declaración previa como filtro de calidad', N'La declaración previa muestra los comprobantes del periodo con su estado fiscal y contable. Sirve para detectar duplicados, comprobantes ajenos, cancelados y complementos de pago pendientes antes de generar efectos contables.', N'{"callout":"info","flowStep":"Explicar","diagram":["Comprobante","Clasificación","Póliza","Registro contable","Trazabilidad"]}'),
  (N'CFDI-DECLARACION-PREVIA', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: amarrar un comprobante a su póliza', N'El instructor mostrará cómo filtrar el periodo, abrir un comprobante ficticio, revisar su detalle y explicar el amarre con la póliza y con el complemento de pago relacionado.', N'{"flowStep":"Demostrar","demoSteps":["Filtrar periodo","Abrir comprobante","Revisar detalle","Explicar amarre","Verificar registro contable"],"notasInstructor":"El escenario limpio no provisiona catálogo contable, así que trabaje la explicación del amarre sin guardar pólizas."}'),
  (N'CFDI-DECLARACION-PREVIA', N'OPERAR', 2, N'ALERTA', N'Alerta: un comprobante cancelado no se contabiliza igual', N'Antes de amarrar revisa vigencia, cancelación y comprobantes relacionados. Contabilizar un cancelado o duplicar el amarre de un complemento produce efectos que después hay que revertir.', N'{"severity":"critical","notasInstructor":"Use un caso con complemento de pago y pregunte qué documento genera el efecto. La confusión habitual es contabilizar dos veces la misma operación."}'),
  (N'CFDI-DECLARACION-PREVIA', N'OPERAR', 3, N'PRACTICA', N'Practicar: clasificar y explicar el amarre', N'Sobre el comprobante ficticio del entorno, clasifícalo, describe con qué póliza debería amarrarse y explica la trazabilidad que quedaría, sin guardar movimientos contables.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el razonamiento de clasificación y trazabilidad; no solicite escrituras contables en el escenario inicial limpio."}'),
  (N'CFDI-DECLARACION-PREVIA', N'CERRAR', 1, N'EVALUACION', N'Evaluar: clasificación, amarre y excepciones', N'Responde la evaluación y demuestra que sabes qué comprobantes se amarran, cuáles se detienen y cómo se escala una excepción.', N'{"required":true,"flowStep":"Evaluar","checklist":["Periodo correcto","Vigencia revisada","Amarre explicado","Excepción escalada"]}'),
  (N'CFDI-DECLARACION-PREVIA', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el amarre es una promesa de trazabilidad', N'Antes de cerrar confirma que desde la póliza puedes llegar al comprobante y de regreso. Si esa ruta se rompe, la contabilidad deja de ser auditable.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'CONTA-POLIZAS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: una póliza cuenta una historia', N'Al finalizar podrás capturar y revisar una póliza balanceada, ubicar su auxiliar y explicar cada liga que la sostiene.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'CONTA-POLIZAS', N'PREPARAR', 2, N'TEORIA', N'Explicar: partida doble, periodo y respaldo', N'Toda póliza requiere fecha dentro de un periodo abierto, cuentas con naturaleza correcta, cargos iguales a abonos y un respaldo identificable. El registro contable auxiliar permite revisar el efecto por cuenta y por documento.', N'{"callout":"info","flowStep":"Explicar","diagram":["Documento","Póliza","Cargos y abonos","Registro contable","Reportes"]}'),
  (N'CONTA-POLIZAS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: capturar, balancear y ligar', N'El instructor mostrará cómo elegir la plantilla, capturar los movimientos, comprobar el balance, ligar la póliza con la transacción que la origina y revisar el auxiliar resultante.', N'{"flowStep":"Demostrar","demoSteps":["Elegir plantilla","Capturar movimientos","Comprobar balance","Ligar documento","Revisar auxiliar"],"notasInstructor":"Explique el porqué de cada cuenta y pida anticipar el siguiente movimiento antes de mostrarlo."}'),
  (N'CONTA-POLIZAS', N'OPERAR', 2, N'ALERTA', N'Alerta: no corrijas una póliza creando otra igual', N'Duplicar el asiento para compensar un error deja dos efectos y ningún rastro claro. La corrección se hace por el flujo autorizado, con motivo y referencia al asiento original.', N'{"severity":"critical","notasInstructor":"Plantee un cargo capturado dos veces y pida el plan de corrección. Debe identificar el asiento original antes de proponer nada."}'),
  (N'CONTA-POLIZAS', N'OPERAR', 3, N'PRACTICA', N'Practicar: propuesta de póliza balanceada', N'Redacta una propuesta de póliza balanceada para el caso ficticio, indica cuentas, importes y respaldo, y explica su liga con la transacción. No guardes efectos contables fuera del escenario disponible.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe clasificación, balance y respaldo. El escenario inicial limpio no provisiona catálogo contable, así que trabaje con propuestas explicadas."}'),
  (N'CONTA-POLIZAS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: balance, periodo y corrección', N'Responde la evaluación y demuestra que sabes cuándo una póliza está lista, cuándo debe corregirse y qué cambia al cerrar el periodo.', N'{"required":true,"flowStep":"Evaluar","checklist":["Periodo abierto","Cargos igual a abonos","Respaldo identificado","Liga verificable"]}'),
  (N'CONTA-POLIZAS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: revisar como si fueras el auditor', N'Antes de cerrar lee tu póliza como alguien externo: cuenta, importe, fecha, documento y motivo deben entenderse sin explicación verbal.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'BANCOS-CONCILIACION', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: conciliar sin duplicar', N'Al finalizar podrás enlazar un movimiento bancario con su efecto contable existente y clasificar correctamente las diferencias.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'BANCOS-CONCILIACION', N'PREPARAR', 2, N'TEORIA', N'Explicar: banco, póliza y transacción', N'El movimiento bancario es evidencia de que el dinero se movió. La póliza es su efecto contable. Conciliar significa enlazar ambos y explicar la diferencia cuando alguno falta, no volver a registrar la operación.', N'{"callout":"info","flowStep":"Explicar","diagram":["Movimiento bancario","Transacción","Póliza","Comprobante","Saldo conciliado"]}'),
  (N'BANCOS-CONCILIACION', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: ligar un movimiento', N'El instructor mostrará cómo abrir la cuenta ficticia, localizar un movimiento, buscar su efecto contable, aplicar la liga y comprobar el saldo resultante.', N'{"flowStep":"Demostrar","demoSteps":["Abrir cuenta","Ubicar movimiento","Buscar efecto contable","Aplicar liga","Comprobar saldo"],"notasInstructor":"Muestre un movimiento sin contraparte y pregunte qué hacer. La respuesta correcta es investigar y documentar, no crear un asiento improvisado."}'),
  (N'BANCOS-CONCILIACION', N'OPERAR', 2, N'ALERTA', N'Alerta: un movimiento sin contraparte no se inventa', N'Si el banco muestra un cargo que la contabilidad no tiene, documenta la diferencia y escálala. Registrar un asiento improvisado para cuadrar el saldo oculta el problema real.', N'{"severity":"critical","notasInstructor":"Insista en la diferencia entre conciliar y registrar. Un saldo cuadrado con asientos inventados es peor que un saldo abierto documentado."}'),
  (N'BANCOS-CONCILIACION', N'OPERAR', 3, N'PRACTICA', N'Practicar: clasificar tres diferencias', N'Toma tres movimientos ficticios y clasifica cada uno como conciliado, pendiente de contabilizar o pendiente de aclaración, explicando la evidencia que usarías.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la clasificación y la evidencia propuesta. No permita crear movimientos bancarios nuevos en el escenario inicial limpio."}'),
  (N'BANCOS-CONCILIACION', N'CERRAR', 1, N'EVALUACION', N'Evaluar: enlaces, diferencias y duplicados', N'Responde la evaluación y demuestra que sabes reconocer un doble registro y sostener una conciliación ante una revisión.', N'{"required":true,"flowStep":"Evaluar","checklist":["Movimiento identificado","Efecto contable localizado","Sin duplicados","Diferencia documentada"]}'),
  (N'BANCOS-CONCILIACION', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el saldo conciliado se explica, no se fuerza', N'Antes de cerrar confirma que cada enlace apunta a un efecto existente y que las diferencias abiertas tienen responsable y fecha de seguimiento.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'CXP-RECURRENTES', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: lo recurrente también se controla', N'Al finalizar podrás programar un compromiso recurrente ficticio y explicar su efecto en el flujo de efectivo.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'CXP-RECURRENTES', N'PREPARAR', 2, N'TEORIA', N'Explicar: periodicidad, provisión y vencimiento', N'Servicios, impuestos y seguros se repiten con una periodicidad conocida. Programarlos permite anticipar vencimientos, provisionar el gasto y detectar cuando un cargo llega distinto de lo esperado.', N'{"callout":"info","flowStep":"Explicar","diagram":["Compromiso","Calendario","Provisión","Pago","Conciliación"]}'),
  (N'CXP-RECURRENTES', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: programar y dar seguimiento', N'El instructor mostrará cómo registrar un compromiso ficticio, revisar el calendario de vencimientos, marcar el seguimiento de un pago simulado y detectar una variación de importe.', N'{"flowStep":"Demostrar","demoSteps":["Registrar compromiso","Definir periodicidad","Revisar vencimientos","Seguir el pago","Detectar variación"],"notasInstructor":"Pregunte qué debe ocurrir si el importe cambia. La respuesta correcta incluye documentar la variación antes de pagar."}'),
  (N'CXP-RECURRENTES', N'OPERAR', 2, N'ALERTA', N'Alerta: un recurrente no autoriza un pago automático', N'La programación anticipa el vencimiento, no aprueba el desembolso. Cada pago requiere su comprobante, su autorización y su conciliación bancaria.', N'{"severity":"critical","notasInstructor":"Aclare que el calendario es una herramienta de previsión; el control de autorización sigue siendo obligatorio."}'),
  (N'CXP-RECURRENTES', N'OPERAR', 3, N'PRACTICA', N'Practicar: compromiso ficticio y calendario', N'Programa un compromiso recurrente ficticio, revisa cómo aparece en el calendario de vencimientos y describe el seguimiento que harías durante tres periodos.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la previsión y el control, no la captura. Verifique que no proponga pagos sin comprobante."}'),
  (N'CXP-RECURRENTES', N'CERRAR', 1, N'EVALUACION', N'Evaluar: previsión, autorización y variaciones', N'Responde la evaluación y demuestra que sabes anticipar vencimientos y reaccionar ante una variación de importe.', N'{"required":true,"flowStep":"Evaluar","checklist":["Periodicidad correcta","Vencimiento visible","Autorización vigente","Variación documentada"]}'),
  (N'CXP-RECURRENTES', N'CERRAR', 2, N'RESUMEN', N'Cerrar: previsión con control, no automatismo ciego', N'Antes de cerrar confirma que cada compromiso tiene responsable, periodicidad y evidencia de autorización, y que las variaciones quedaron explicadas.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REPORTES-FINANCIEROS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: cada reporte responde una pregunta', N'Al finalizar podrás elegir el reporte correcto, leerlo con criterio y rastrear una cifra hasta su origen contable.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REPORTES-FINANCIEROS', N'PREPARAR', 2, N'TEORIA', N'Explicar: de la póliza al indicador', N'La balanza resume saldos por cuenta, la hoja de trabajo prepara ajustes, el estado de resultados muestra rentabilidad y el tablero de salud combina ingresos, flujo, margen, ocupación y conciliación. Todos parten de las mismas pólizas.', N'{"callout":"info","flowStep":"Explicar","diagram":["Póliza","Balanza","Hoja de trabajo","Resultados","Salud financiera"]}'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: rastrear una cifra', N'El instructor mostrará cómo fijar periodo y RFC, leer la balanza, abrir el auxiliar de una cuenta y llegar hasta la póliza que originó el saldo.', N'{"flowStep":"Demostrar","demoSteps":["Fijar periodo","Leer balanza","Abrir auxiliar","Llegar a la póliza","Explicar la cifra"],"notasInstructor":"Pida que anticipe si el saldo debe ser deudor o acreedor antes de abrirlo."}'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 2, N'ALERTA', N'Alerta: un reporte no se corrige en el reporte', N'Si una cifra está mal, el error está en la póliza, el periodo o la cuenta. Exportar y editar fuera del sistema produce documentos que nadie puede sostener después.', N'{"severity":"critical","notasInstructor":"Rechace cualquier propuesta de corregir en hoja de cálculo. La corrección ocurre en el origen contable."}'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 3, N'PRACTICA', N'Practicar: leer los cuatro reportes', N'Recorre hoja de trabajo, balanza, estado de resultados y salud financiera del periodo ficticio, y explica en una frase qué pregunta responde cada uno.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la interpretación. Con el escenario limpio los reportes pueden verse vacíos: eso también debe saber explicarlo."}'),
  (N'REPORTES-FINANCIEROS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: interpretación y trazabilidad', N'Responde la evaluación y demuestra que sabes qué reporte usar, cómo rastrear una cifra y cómo reportar una inconsistencia.', N'{"required":true,"flowStep":"Evaluar","checklist":["Periodo correcto","Cifra rastreada","Indicador explicado","Inconsistencia reportada"]}'),
  (N'REPORTES-FINANCIEROS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: reportar es explicar, no solo mostrar', N'Antes de compartir un reporte confirma periodo, RFC y origen de las cifras clave, y prepara la explicación de cualquier variación relevante.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'LOGISTICA-COMPRAS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: comprar con referencia y necesidad', N'Al finalizar podrás levantar una compra ficticia, recibirla sin duplicar y explicar su efecto en existencias y costo.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'LOGISTICA-COMPRAS', N'PREPARAR', 2, N'TEORIA', N'Explicar: proveedor, orden, recepción y costo', N'La orden de compra declara qué se pedirá, a quién y a qué precio. La recepción confirma qué llegó realmente y actualiza existencia y costo. La diferencia entre ambas es la que hay que explicar, no ocultar.', N'{"callout":"info","flowStep":"Explicar","diagram":["Proveedor","Orden de compra","Recepción","Existencia","Cuentas por pagar"]}'),
  (N'LOGISTICA-COMPRAS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: de la orden a la recepción', N'El instructor mostrará cómo revisar el proveedor ficticio, levantar una orden con material TRN, registrar una recepción parcial y comprobar el efecto en la existencia.', N'{"flowStep":"Demostrar","demoSteps":["Revisar proveedor","Levantar orden","Confirmar unidad y precio","Registrar recepción","Verificar existencia"],"notasInstructor":"Muestre una recepción parcial y pregunte qué queda pendiente. La confusión habitual es cerrar la orden completa al recibir una parte."}'),
  (N'LOGISTICA-COMPRAS', N'OPERAR', 2, N'ALERTA', N'Alerta: nunca repitas una recepción sin buscarla primero', N'Si la respuesta del sistema fue incierta, busca la referencia y su estado antes de reintentar. Una recepción duplicada infla existencias, costo y cuentas por pagar al mismo tiempo.', N'{"severity":"critical","notasInstructor":"Simule una respuesta lenta y pida el plan. Debe buscar y verificar antes de reintentar."}'),
  (N'LOGISTICA-COMPRAS', N'OPERAR', 3, N'PRACTICA', N'Practicar: compra ficticia con material TRN', N'Levanta una orden de compra ficticia con material TRN, describe cómo la recibirías parcialmente y explica el efecto en existencia, costo promedio y cuentas por pagar.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el control de referencias y unidades. No permita usar códigos que no empiecen con TRN."}'),
  (N'LOGISTICA-COMPRAS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: referencias, unidades y duplicados', N'Responde la evaluación y demuestra que sabes evitar recepciones duplicadas y explicar el efecto de una compra en el costo.', N'{"required":true,"flowStep":"Evaluar","checklist":["Proveedor correcto","Unidad correcta","Referencia única","Pendiente documentado"]}'),
  (N'LOGISTICA-COMPRAS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: la compra termina cuando se explica', N'Antes de cerrar confirma cantidades recibidas, pendientes, precio y referencia. Lo que no se documente hoy se convierte en una diferencia de inventario mañana.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'LOGISTICA-INVENTARIO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: contar para saber, no para cuadrar', N'Al finalizar podrás ejecutar un conteo ficticio, documentar la diferencia y explicar quién autoriza el ajuste.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'LOGISTICA-INVENTARIO', N'PREPARAR', 2, N'TEORIA', N'Explicar: ubicación, existencia y corte', N'La existencia siempre pertenece a una ubicación y a un momento. Un conteo sin corte definido produce diferencias falsas. Los mínimos y máximos indican riesgo de desabasto, no obligan a ajustar.', N'{"callout":"info","flowStep":"Explicar","diagram":["Almacén","Ubicación","Existencia","Conteo","Diferencia","Ajuste"]}'),
  (N'LOGISTICA-INVENTARIO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: conteo y diferencia', N'El instructor mostrará cómo abrir una ubicación ficticia, revisar la existencia declarada, registrar el conteo, identificar la diferencia y preparar el recuento.', N'{"flowStep":"Demostrar","demoSteps":["Definir corte","Abrir ubicación","Registrar conteo","Identificar diferencia","Preparar recuento"],"notasInstructor":"Insista en el corte. Contar mientras hay movimientos genera diferencias que no existen."}'),
  (N'LOGISTICA-INVENTARIO', N'OPERAR', 2, N'ALERTA', N'Alerta: el ajuste nunca es el primer paso', N'Ante una diferencia, primero recuenta, después busca movimientos sin registrar y solo al final propones un ajuste con autorización. Ajustar de inmediato borra la pista del problema.', N'{"severity":"critical","notasInstructor":"Rechace el ajuste directo. Pida el orden correcto: recontar, investigar, documentar y escalar."}'),
  (N'LOGISTICA-INVENTARIO', N'OPERAR', 3, N'PRACTICA', N'Practicar: conteo de una ubicación TRN', N'Ejecuta un conteo ficticio en una ubicación TRN, documenta la diferencia encontrada y describe la investigación previa a cualquier ajuste.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la disciplina del proceso. Una diferencia bien documentada vale más que un inventario cuadrado sin explicación."}'),
  (N'LOGISTICA-INVENTARIO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: corte, diferencias y autorización', N'Responde la evaluación y demuestra que sabes conducir un conteo y sostener la diferencia encontrada.', N'{"required":true,"flowStep":"Evaluar","checklist":["Corte definido","Conteo registrado","Diferencia investigada","Ajuste autorizado"]}'),
  (N'LOGISTICA-INVENTARIO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: la existencia confiable se gana con evidencia', N'Antes de cerrar confirma ubicación, cantidad contada, diferencia, causa probable y autorización pendiente. El inventario confiable es el que puede explicarse.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REST-POS-SERVICIO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: vender rápido sin perder control', N'Al finalizar podrás capturar y cobrar una orden ficticia, seguirla hasta la entrega y corregir un error sin borrar evidencia.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REST-POS-SERVICIO', N'PREPARAR', 2, N'TEORIA', N'Explicar: folio, comanda y entrega', N'Cada orden nace con un folio, viaja a cocina como comanda y termina con la entrega. El cobro puede ocurrir antes o después según la sede, pero el folio es el hilo que conecta venta, preparación e inventario.', N'{"callout":"info","flowStep":"Explicar","diagram":["Orden","Modificadores","Cobro","Cocina","Entrega"]}'),
  (N'REST-POS-SERVICIO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: capturar, cobrar y despachar', N'El instructor mostrará cómo abrir el turno, capturar productos con modificadores, aplicar el cobro, enviar a cocina y seguir el folio hasta la entrega.', N'{"flowStep":"Demostrar","demoSteps":["Confirmar turno","Capturar productos","Aplicar modificadores","Cobrar","Seguir el folio"],"notasInstructor":"Muestre un modificador que cambia el precio y otro que cambia la preparación; el equipo suele confundirlos."}'),
  (N'REST-POS-SERVICIO', N'OPERAR', 2, N'ALERTA', N'Alerta: una orden equivocada se corrige, no se borra', N'Cancelar y volver a capturar sin motivo rompe la trazabilidad del turno y de la merma. Usa el flujo de corrección con motivo para que el corte de caja siga siendo explicable.', N'{"severity":"critical","notasInstructor":"Pregunte qué pasa con el producto ya preparado cuando se cancela una orden. Debe considerar la merma."}'),
  (N'REST-POS-SERVICIO', N'OPERAR', 3, N'PRACTICA', N'Practicar: una orden completa de principio a fin', N'Captura una orden ficticia con al menos dos productos y un modificador, cóbrala, síguela en la pantalla de órdenes y regístrala como entregada.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe que verifique el folio en cada etapa en lugar de asumir que la orden avanzó."}'),
  (N'REST-POS-SERVICIO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: folio, cobro y corrección', N'Responde la evaluación y demuestra que sabes seguir un folio, corregir con motivo y explicar qué muestra la pantalla pública.', N'{"required":true,"flowStep":"Evaluar","checklist":["Folio seguido","Cobro correcto","Corrección con motivo","Entrega registrada"]}'),
  (N'REST-POS-SERVICIO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el folio es la memoria del servicio', N'Antes de cerrar confirma que cada folio tiene estado, cobro y entrega coherentes. La pantalla pública nunca debe mostrar datos personales del cliente.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REST-COCINA-PRODUCCION', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: cocinar con receta y tiempo', N'Al finalizar podrás operar la pantalla de cocina, leer una receta con rendimiento y registrar una producción con merma justificada.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REST-COCINA-PRODUCCION', N'PREPARAR', 2, N'TEORIA', N'Explicar: comanda, receta, lote y merma', N'La comanda ordena qué preparar y cuándo. La receta define ingredientes y rendimiento esperado. La producción por lotes consume insumos reales y genera producto terminado; la diferencia entre rendimiento esperado y real es merma que debe explicarse.', N'{"callout":"info","flowStep":"Explicar","diagram":["Comanda","Receta","Subreceta","Lote","Rendimiento","Merma"]}'),
  (N'REST-COCINA-PRODUCCION', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: partidas, tiempos y lote', N'El instructor mostrará cómo leer la pantalla por partida, marcar un producto listo, abrir la receta que lo sustenta y registrar una producción por lotes con su rendimiento.', N'{"flowStep":"Demostrar","demoSteps":["Leer partidas","Marcar listo","Abrir receta","Registrar lote","Comparar rendimiento"],"notasInstructor":"Muestre una subreceta y pregunte cómo afecta al costo del producto final."}'),
  (N'REST-COCINA-PRODUCCION', N'OPERAR', 2, N'ALERTA', N'Alerta: la merma sin explicación se convierte en pérdida invisible', N'Registrar producción sin declarar merma real deja el inventario descuadrado y el costo subestimado. Declara la merma con motivo, aunque sea incómodo.', N'{"severity":"critical","notasInstructor":"Insista en que la merma declarada es información valiosa; ocultarla traslada el problema al conteo físico."}'),
  (N'REST-COCINA-PRODUCCION', N'OPERAR', 3, N'PRACTICA', N'Practicar: producción ficticia por lote', N'Registra una producción ficticia por lote, compara el rendimiento esperado con el real y explica la merma resultante y su efecto en el costo.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la explicación de la diferencia. Un lote perfecto sin merma suele indicar que no se midió."}'),
  (N'REST-COCINA-PRODUCCION', N'CERRAR', 1, N'EVALUACION', N'Evaluar: rendimiento, merma y costo', N'Responde la evaluación y demuestra que sabes leer una receta, registrar un lote y explicar la merma.', N'{"required":true,"flowStep":"Evaluar","checklist":["Comanda atendida","Receta leída","Lote registrado","Merma justificada"]}'),
  (N'REST-COCINA-PRODUCCION', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el costo real se construye en cocina', N'Antes de cerrar confirma insumos consumidos, producto obtenido y merma declarada. Ese registro es el que sostiene el margen del menú.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REST-INVENTARIO-TURNOS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: caja e inventario se cierran juntos', N'Al finalizar podrás cerrar un turno ficticio con conteo ciego y explicar el efecto de traspasos y ajustes en existencias.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REST-INVENTARIO-TURNOS', N'PREPARAR', 2, N'TEORIA', N'Explicar: traspaso, ajuste, merma y corte', N'Un traspaso mueve existencia entre almacenes sin cambiar el total. Un ajuste sí cambia el total y requiere motivo. El conteo ciego evita que la expectativa contamine el conteo, y el corte compara lo esperado contra lo contado.', N'{"callout":"info","flowStep":"Explicar","diagram":["Apertura","Traspaso","Ajuste","Conteo ciego","Diferencia","Corte"]}'),
  (N'REST-INVENTARIO-TURNOS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: del traspaso al corte', N'El instructor mostrará cómo abrir el turno, registrar un traspaso con evidencia, declarar una merma, ejecutar el conteo ciego y leer la diferencia resultante.', N'{"flowStep":"Demostrar","demoSteps":["Abrir turno","Registrar traspaso","Declarar merma","Conteo ciego","Leer diferencia"],"notasInstructor":"Pregunte por qué el conteo es ciego. La respuesta correcta menciona evitar el sesgo de confirmación."}'),
  (N'REST-INVENTARIO-TURNOS', N'OPERAR', 2, N'ALERTA', N'Alerta: no ajustes la caja para que cuadre el conteo', N'Una diferencia de caja se declara, se explica y se envía a aprobación. Modificar el conteo o el fondo para eliminar la diferencia convierte un error operativo en un problema de confianza.', N'{"severity":"critical","notasInstructor":"Deje claro que una diferencia declarada es aceptable; una diferencia ocultada no lo es."}'),
  (N'REST-INVENTARIO-TURNOS', N'OPERAR', 3, N'PRACTICA', N'Practicar: turno ficticio con conteo ciego', N'Abre un turno ficticio, registra un traspaso o una merma con evidencia, ejecuta el conteo ciego y prepara la diferencia para aprobación.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el orden del proceso y la calidad de la evidencia, no que el turno cierre en ceros."}'),
  (N'REST-INVENTARIO-TURNOS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: conteo ciego, diferencias y aprobación', N'Responde la evaluación y demuestra que sabes cerrar un turno con diferencias explicadas y evidencia suficiente.', N'{"required":true,"flowStep":"Evaluar","checklist":["Apertura registrada","Movimientos con evidencia","Conteo ciego","Diferencia aprobada"]}'),
  (N'REST-INVENTARIO-TURNOS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el corte honesto protege a quien lo hace', N'Antes de cerrar confirma fondo, ventas, movimientos y conteo. Una diferencia documentada protege al operador; una diferencia escondida lo expone.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REST-CATALOGO-CONFIG', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: el catálogo gobierna la operación', N'Al finalizar podrás publicar un cambio de menú ficticio y anticipar su efecto en punto de venta, cocina e inventario.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REST-CATALOGO-CONFIG', N'PREPARAR', 2, N'TEORIA', N'Explicar: producto, variante, menú y estación', N'El producto define qué se vende, la variante su presentación y precio, el menú cuándo y dónde aparece, y la estación quién lo prepara. Un cambio en cualquiera de esos niveles cambia la operación del turno siguiente.', N'{"callout":"info","flowStep":"Explicar","diagram":["Producto","Variante","Precio","Menú","Estación","Almacén"]}'),
  (N'REST-CATALOGO-CONFIG', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: modificar y publicar un menú', N'El instructor mostrará cómo revisar la sede, editar una sección del menú ficticio, ajustar un modificador y un precio, y confirmar la estación de preparación antes de publicar.', N'{"flowStep":"Demostrar","demoSteps":["Revisar sede","Editar sección","Ajustar modificador","Confirmar precio","Verificar estación"],"notasInstructor":"Muestre el efecto en el punto de venta después de publicar; el equipo suele olvidar revisar la vigencia por horario."}'),
  (N'REST-CATALOGO-CONFIG', N'OPERAR', 2, N'ALERTA', N'Alerta: un precio mal publicado se cobra de inmediato', N'La publicación afecta ventas en curso. Revisa vigencia, sede y horario antes de confirmar, y ten claro cómo revertir el cambio si el precio sale equivocado.', N'{"severity":"critical","notasInstructor":"Pida el plan de reversión antes de publicar. Debe conocerlo previamente, no improvisarlo."}'),
  (N'REST-CATALOGO-CONFIG', N'OPERAR', 3, N'PRACTICA', N'Practicar: cambio de menú ficticio', N'Modifica una sección del menú ficticio, confirma su vigencia y estación, describe cómo verificarías el resultado en el punto de venta y cómo lo revertirías.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la verificación posterior y el plan de reversión, no solo la edición."}'),
  (N'REST-CATALOGO-CONFIG', N'CERRAR', 1, N'EVALUACION', N'Evaluar: vigencia, impacto y reversión', N'Responde la evaluación y demuestra que sabes publicar un cambio de catálogo controlando su alcance y su reversión.', N'{"required":true,"flowStep":"Evaluar","checklist":["Sede correcta","Vigencia definida","Impacto verificado","Reversión conocida"]}'),
  (N'REST-CATALOGO-CONFIG', N'CERRAR', 2, N'RESUMEN', N'Cerrar: configurar es decidir por adelantado', N'Antes de cerrar confirma qué cambió, desde cuándo aplica, a qué sede afecta y quién fue avisado. La configuración silenciosa se descubre en el peor momento.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'REST-COMERCIAL', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: promover con reglas claras', N'Al finalizar podrás configurar una promoción ficticia, leer su desempeño y preparar contenido del sitio público con control.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'REST-COMERCIAL', N'PREPARAR', 2, N'TEORIA', N'Explicar: promoción, código, membresía y margen', N'Una promoción cambia el precio bajo reglas de tiempo, producto y canal. La membresía acumula puntos y crea derechos futuros. El reporte muestra si la promoción movió venta o solo regaló margen.', N'{"callout":"info","flowStep":"Explicar","diagram":["Regla","Código","Venta","Puntos","Reporte","Sitio público"]}'),
  (N'REST-COMERCIAL', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: configurar y medir', N'El instructor mostrará cómo crear una promoción ficticia con su vigencia, generar el código, aplicarla en una venta simulada, revisar los puntos de membresía y leer el reporte de desempeño.', N'{"flowStep":"Demostrar","demoSteps":["Definir regla","Generar código","Aplicar en venta","Revisar puntos","Leer desempeño"],"notasInstructor":"Pregunte cómo distinguir venta incremental de venta que igual habría ocurrido."}'),
  (N'REST-COMERCIAL', N'OPERAR', 2, N'ALERTA', N'Alerta: el contenido público se revisa antes de publicarse', N'Textos, precios, fotos y datos de contacto del sitio público llegan a clientes reales. En capacitación se preparan y se revisan, pero la publicación real requiere autorización del responsable.', N'{"severity":"critical","notasInstructor":"Deje explícito que el entorno de capacitación nunca publica hacia el sitio real."}'),
  (N'REST-COMERCIAL', N'OPERAR', 3, N'PRACTICA', N'Practicar: promoción ficticia y su lectura', N'Configura una promoción ficticia con vigencia y regla, describe cómo se aplicaría en una venta y qué indicador usarías para saber si funcionó.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la relación entre regla, límite y medición. Una promoción sin límite ni indicador es un riesgo."}'),
  (N'REST-COMERCIAL', N'CERRAR', 1, N'EVALUACION', N'Evaluar: reglas, puntos y desempeño', N'Responde la evaluación y demuestra que sabes acotar una promoción, revisar su efecto y controlar la publicación de contenido.', N'{"required":true,"flowStep":"Evaluar","checklist":["Regla acotada","Vigencia definida","Puntos revisados","Publicación autorizada"]}'),
  (N'REST-COMERCIAL', N'CERRAR', 2, N'RESUMEN', N'Cerrar: promover sin medir es regalar margen', N'Antes de cerrar confirma vigencia, límite, canal y el indicador con el que medirás el resultado. Lo que no se mide no se puede repetir con criterio.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RH-ASISTENCIA', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: asistencia con privacidad', N'Al finalizar podrás revisar un periodo ficticio, resolver una anomalía por el flujo correcto y usar el kiosco sin exponer datos.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RH-ASISTENCIA', N'PREPARAR', 2, N'TEORIA', N'Explicar: evento, anomalía y aprobación', N'Cada registro de entrada o salida es un evento con hora y origen. Una anomalía es un evento faltante, fuera de horario o fuera de sitio. La corrección ocurre por solicitud aprobada, nunca editando el evento original.', N'{"callout":"privacy","flowStep":"Explicar","diagram":["Evento","Anomalía","Solicitud","Aprobación","Periodo"]}'),
  (N'RH-ASISTENCIA', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: revisar el periodo y el kiosco', N'El instructor mostrará cómo filtrar el periodo y el equipo, identificar una anomalía ficticia, revisar su auditoría y registrar una entrada desde el kiosco de capacitación.', N'{"flowStep":"Demostrar","demoSteps":["Filtrar periodo","Identificar anomalía","Revisar auditoría","Registrar en kiosco","Enviar a aprobación"],"notasInstructor":"Verifique que no consulte información de personas fuera del equipo ficticio asignado."}'),
  (N'RH-ASISTENCIA', N'OPERAR', 2, N'ALERTA', N'Alerta: la asistencia de otra persona no se edita', N'Un supervisor aprueba o rechaza solicitudes, no reescribe eventos. Editar el registro de alguien más destruye la evidencia que protege tanto al colaborador como a la empresa.', N'{"severity":"critical","notasInstructor":"Refuerce el límite entre aprobar y modificar; es el error conceptual más común del módulo."}'),
  (N'RH-ASISTENCIA', N'OPERAR', 3, N'PRACTICA', N'Practicar: anomalía ficticia del periodo', N'Localiza una anomalía en el periodo ficticio, explica qué solicitud corresponde, quién la aprueba y qué evidencia se requiere. Usa solo las identidades sintéticas del entorno.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe privacidad y elección del trámite. No otorgue permisos administrativos para completar el ejercicio."}'),
  (N'RH-ASISTENCIA', N'CERRAR', 1, N'EVALUACION', N'Evaluar: anomalías, aprobación y privacidad', N'Responde la evaluación y demuestra que sabes resolver una anomalía por el flujo autorizado y proteger la información del equipo.', N'{"required":true,"flowStep":"Evaluar","checklist":["Periodo correcto","Anomalía identificada","Solicitud adecuada","Privacidad respetada"]}'),
  (N'RH-ASISTENCIA', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el periodo listo es el que ya no tiene sorpresas', N'Antes de cerrar confirma que las anomalías tienen solicitud o justificación y que el equipo sabe qué se envió a aprobación. Ese periodo es el insumo de la pre-nómina.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RH-CONFIG-TIEMPO', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: la configuración explica las anomalías', N'Al finalizar podrás relacionar sitios, horarios y políticas con las anomalías que produce el sistema, y proponer un cambio seguro.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RH-CONFIG-TIEMPO', N'PREPARAR', 2, N'TEORIA', N'Explicar: sitio, horario, política y vigencia', N'El sitio define dónde se puede registrar, el horario qué se espera, la política qué tolerancia aplica y la vigencia desde cuándo. Cambiar cualquiera de ellos afecta la lectura de los periodos posteriores, no los ya cerrados.', N'{"callout":"info","flowStep":"Explicar","diagram":["Sitio","Geocerca","Horario","Política","Vigencia","Kiosco"]}'),
  (N'RH-CONFIG-TIEMPO', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: revisar la configuración vigente', N'El instructor mostrará cómo revisar el sitio ficticio y su geocerca, abrir una plantilla de horario, leer la política de asistencia y ubicar a los responsables por equipo.', N'{"flowStep":"Demostrar","demoSteps":["Revisar sitio","Leer geocerca","Abrir horario","Leer política","Ubicar responsables"],"notasInstructor":"Relacione cada parámetro con una anomalía concreta del curso de asistencia."}'),
  (N'RH-CONFIG-TIEMPO', N'OPERAR', 2, N'ALERTA', N'Alerta: un cambio con vigencia pasada reescribe la historia', N'Aplica siempre la vigencia hacia adelante. Modificar horarios o políticas con efecto retroactivo altera periodos ya revisados y contradice lo que las personas ya firmaron.', N'{"severity":"critical","notasInstructor":"Pida identificar la fecha de vigencia antes de guardar cualquier cambio propuesto."}'),
  (N'RH-CONFIG-TIEMPO', N'OPERAR', 3, N'PRACTICA', N'Practicar: proponer un cambio con vigencia', N'Describe un cambio de horario o tolerancia para el sitio ficticio, define su fecha de vigencia, anticipa qué anomalías dejarán de aparecer y a quién hay que avisar.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el análisis de impacto y la comunicación, no la edición del parámetro."}'),
  (N'RH-CONFIG-TIEMPO', N'CERRAR', 1, N'EVALUACION', N'Evaluar: vigencia, impacto y comunicación', N'Responde la evaluación y demuestra que sabes cambiar la configuración de tiempo sin alterar periodos cerrados.', N'{"required":true,"flowStep":"Evaluar","checklist":["Sitio correcto","Vigencia futura","Impacto anticipado","Aviso enviado"]}'),
  (N'RH-CONFIG-TIEMPO', N'CERRAR', 2, N'RESUMEN', N'Cerrar: configurar el tiempo es un acuerdo con la gente', N'Antes de cerrar confirma qué cambió, desde cuándo, a quién afecta y quién fue informado. Un cambio silencioso se traduce en descuentos inesperados.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RH-AUSENCIAS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: la ausencia tiene política y saldo', N'Al finalizar podrás tramitar y resolver una ausencia ficticia respetando saldo, evidencia y bitácora.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RH-AUSENCIAS', N'PREPARAR', 2, N'TEORIA', N'Explicar: tipo, devengo, saldo y solicitud', N'Cada tipo de ausencia tiene una política que define cómo se devenga y qué evidencia requiere. El saldo es el resultado del devengo menos lo utilizado. La solicitud consume saldo solo cuando queda aprobada.', N'{"callout":"info","flowStep":"Explicar","diagram":["Tipo","Política","Devengo","Saldo","Solicitud","Aprobación"]}'),
  (N'RH-AUSENCIAS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: solicitar, aprobar y ajustar', N'El instructor mostrará cómo revisar el saldo ficticio, capturar una solicitud con evidencia, aprobarla o rechazarla con motivo y registrar un ajuste auditado cuando corresponde.', N'{"flowStep":"Demostrar","demoSteps":["Revisar saldo","Capturar solicitud","Adjuntar evidencia","Resolver con motivo","Registrar ajuste"],"notasInstructor":"Muestre un rechazo con motivo claro; el equipo suele rechazar sin explicación y eso genera reclamos."}'),
  (N'RH-AUSENCIAS', N'OPERAR', 2, N'ALERTA', N'Alerta: el saldo no se corrige a mano', N'Si el saldo parece equivocado, revisa el devengo y las solicitudes aplicadas antes de proponer un ajuste. Todo ajuste queda auditado con motivo y responsable.', N'{"severity":"critical","notasInstructor":"Pida investigar el devengo antes de cualquier ajuste. El atajo de editar el saldo destruye la trazabilidad."}'),
  (N'RH-AUSENCIAS', N'OPERAR', 3, N'PRACTICA', N'Practicar: solicitud ficticia completa', N'Captura una solicitud de ausencia ficticia con evidencia, revisa el saldo antes y después, y explica cómo se resolvería un rechazo y una reprogramación.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe respeto al saldo, calidad de la evidencia y claridad del motivo."}'),
  (N'RH-AUSENCIAS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: saldos, evidencia y ajustes', N'Responde la evaluación y demuestra que sabes tramitar una ausencia y sostener el saldo resultante.', N'{"required":true,"flowStep":"Evaluar","checklist":["Tipo correcto","Saldo verificado","Evidencia adjunta","Ajuste auditado"]}'),
  (N'RH-AUSENCIAS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el saldo es un derecho, trátalo como tal', N'Antes de cerrar confirma que la solicitud tiene resolución, motivo y saldo actualizado. Una ausencia mal registrada se convierte en un conflicto laboral.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RH-PRENOMINA', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: cerrar un periodo con evidencia', N'Al finalizar podrás validar, aprobar y bloquear un periodo ficticio de pre-nómina y explicar cómo se corrige después.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RH-PRENOMINA', N'PREPARAR', 2, N'TEORIA', N'Explicar: unidades de tiempo, incidencias y bloqueo', N'La pre-nómina convierte eventos de asistencia y ausencias en unidades de tiempo por colaborador. Las incidencias son las diferencias que requieren justificación. El bloqueo congela el periodo para que la exportación sea reproducible.', N'{"callout":"info","flowStep":"Explicar","diagram":["Asistencia","Ausencias","Unidades de tiempo","Incidencias","Bloqueo","Exportación"]}'),
  (N'RH-PRENOMINA', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: validar y aprobar el periodo', N'El instructor mostrará cómo revisar las unidades de tiempo del periodo ficticio, justificar una incidencia, aprobar el grupo de pago y preparar la exportación.', N'{"flowStep":"Demostrar","demoSteps":["Revisar unidades","Justificar incidencia","Aprobar grupo","Bloquear periodo","Preparar exportación"],"notasInstructor":"Pregunte qué ocurre con una corrección que llega después del bloqueo; debe resolverse en el periodo siguiente con referencia."}'),
  (N'RH-PRENOMINA', N'OPERAR', 2, N'ALERTA', N'Alerta: no bloquees con incidencias sin justificar', N'El bloqueo convierte el periodo en la base del pago. Bloquear con incidencias abiertas traslada el error a la nómina y obliga a correcciones que afectan a personas reales.', N'{"severity":"critical","notasInstructor":"Verifique que revise el listado de incidencias abiertas antes de proponer el bloqueo."}'),
  (N'RH-PRENOMINA', N'OPERAR', 3, N'PRACTICA', N'Practicar: cierre ficticio del periodo', N'Revisa el periodo ficticio, justifica una incidencia, describe la aprobación y explica qué harías con una corrección que llega después del bloqueo.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el criterio de cierre y el manejo de la corrección tardía."}'),
  (N'RH-PRENOMINA', N'CERRAR', 1, N'EVALUACION', N'Evaluar: validación, bloqueo y exportación', N'Responde la evaluación y demuestra que sabes cerrar un periodo con evidencia y manejar una corrección posterior.', N'{"required":true,"flowStep":"Evaluar","checklist":["Incidencias justificadas","Aprobación registrada","Bloqueo aplicado","Exportación reproducible"]}'),
  (N'RH-PRENOMINA', N'CERRAR', 2, N'RESUMEN', N'Cerrar: la pre-nómina se paga en confianza', N'Antes de cerrar confirma que cada incidencia tiene motivo, que la aprobación quedó registrada y que la exportación puede reproducirse igual mañana.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'RH-EXPEDIENTES', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: el expediente es información sensible', N'Al finalizar podrás mantener un expediente ficticio completo y explicar quién puede consultar cada dato.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'RH-EXPEDIENTES', N'PREPARAR', 2, N'TEORIA', N'Explicar: identidad, datos laborales y documentos', N'El expediente reúne identidad, puesto, sede, contrato, documentos y fotografía. Una parte alimenta la operación diaria y otra es estrictamente confidencial. El acceso depende del rol, no de la curiosidad.', N'{"callout":"info","flowStep":"Explicar","diagram":["Identidad","Datos laborales","Documentos","Fotografía","Acceso por rol"]}'),
  (N'RH-EXPEDIENTES', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: actualizar un expediente', N'El instructor mostrará cómo abrir un colaborador ficticio, actualizar un dato laboral, adjuntar un documento con tipo y vigencia y revisar el historial del cambio.', N'{"flowStep":"Demostrar","demoSteps":["Abrir colaborador","Actualizar dato laboral","Adjuntar documento","Revisar vigencia","Consultar historial"],"notasInstructor":"Muestre la diferencia entre un dato operativo y uno confidencial antes de cualquier captura."}'),
  (N'RH-EXPEDIENTES', N'OPERAR', 2, N'ALERTA', N'Alerta: nunca subas documentos personales reales', N'En capacitación solo se usan archivos ficticios. Un documento real en un entorno de práctica es una fuga de datos personales, aunque el entorno se reinicie después.', N'{"severity":"critical","notasInstructor":"Revise el archivo antes de permitir la carga; debe ser claramente ficticio."}'),
  (N'RH-EXPEDIENTES', N'OPERAR', 3, N'PRACTICA', N'Practicar: expediente ficticio completo', N'Actualiza los datos laborales del colaborador ficticio, adjunta un documento de prueba con tipo y vigencia, y explica qué parte del expediente no debe compartirse.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el criterio de privacidad y la calidad de la clasificación documental."}'),
  (N'RH-EXPEDIENTES', N'CERRAR', 1, N'EVALUACION', N'Evaluar: privacidad, vigencia y trazabilidad', N'Responde la evaluación y demuestra que sabes mantener un expediente y proteger la información sensible.', N'{"required":true,"flowStep":"Evaluar","checklist":["Identidad correcta","Dato clasificado","Documento vigente","Cambio trazable"]}'),
  (N'RH-EXPEDIENTES', N'CERRAR', 2, N'RESUMEN', N'Cerrar: cuidar el expediente es cuidar a la persona', N'Antes de cerrar confirma que cada documento tiene tipo y vigencia, que el cambio quedó registrado y que ningún dato sensible salió del sistema.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'AJUSTES-PLANTILLAS', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: un ajuste pequeño con alcance grande', N'Al finalizar podrás modificar una plantilla ficticia entendiendo qué módulos la consumen y cómo revertir el cambio.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'AJUSTES-PLANTILLAS', N'PREPARAR', 2, N'TEORIA', N'Explicar: plantilla, parámetro y alcance por RFC', N'Las plantillas contables determinan cómo se proponen las pólizas de un flujo completo. Los parámetros por RFC ajustan comportamiento por contribuyente. Un cambio aquí se refleja en todos los documentos posteriores.', N'{"callout":"info","flowStep":"Explicar","diagram":["Ajuste","Plantilla","Módulo consumidor","Documento","Efecto contable"]}'),
  (N'AJUSTES-PLANTILLAS', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: cambiar y verificar', N'El instructor mostrará cómo localizar la plantilla ficticia, revisar qué módulos la usan, aplicar un cambio acotado y comprobar el efecto en una propuesta antes de darlo por bueno.', N'{"flowStep":"Demostrar","demoSteps":["Localizar plantilla","Identificar consumidores","Aplicar cambio acotado","Probar propuesta","Comparar resultado"],"notasInstructor":"Insista en identificar consumidores antes de tocar nada; es el paso que más se omite."}'),
  (N'AJUSTES-PLANTILLAS', N'OPERAR', 2, N'ALERTA', N'Alerta: no cambies una plantilla para resolver un caso', N'Si un documento requiere un tratamiento distinto, la excepción se resuelve en el documento. Cambiar la plantilla afecta a todos los casos futuros y suele descubrirse semanas después.', N'{"severity":"critical","notasInstructor":"Diferencie excepción puntual de cambio de política; es el criterio central del curso."}'),
  (N'AJUSTES-PLANTILLAS', N'OPERAR', 3, N'PRACTICA', N'Practicar: cambio acotado y reversión', N'Describe un cambio acotado a una plantilla ficticia, identifica los módulos que lo consumen, explica cómo verificarías el efecto y cómo lo revertirías.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe el análisis de alcance y el plan de reversión documentado."}'),
  (N'AJUSTES-PLANTILLAS', N'CERRAR', 1, N'EVALUACION', N'Evaluar: alcance, verificación y reversión', N'Responde la evaluación y demuestra que sabes distinguir una excepción de un cambio de configuración.', N'{"required":true,"flowStep":"Evaluar","checklist":["Consumidores identificados","Cambio acotado","Efecto verificado","Reversión posible"]}'),
  (N'AJUSTES-PLANTILLAS', N'CERRAR', 2, N'RESUMEN', N'Cerrar: configurar es legislar para el futuro', N'Antes de cerrar confirma qué cambió, qué módulos afecta, cómo se verificó y quién lo autorizó. Un ajuste sin registro es una causa raíz invisible.', N'{"highlight":true,"flowStep":"Cerrar"}'),

  (N'ADMIN-SEGURIDAD', N'PREPARAR', 1, N'OBJETIVOS', N'Preparar: mínimo privilegio como criterio', N'Al finalizar podrás otorgar el acceso mínimo necesario a un usuario ficticio y explicar el efecto de cada rol y del RFC activo.', N'{"icon":"target","flowStep":"Preparar"}'),
  (N'ADMIN-SEGURIDAD', N'PREPARAR', 2, N'TEORIA', N'Explicar: usuario, colaborador, rol y RFC', N'El usuario autentica, el colaborador identifica a la persona en la operación, el rol habilita funciones y el RFC delimita qué información se ve. Los cuatro deben coincidir para que la trazabilidad tenga sentido.', N'{"callout":"info","flowStep":"Explicar","diagram":["Usuario","Colaborador","Rol","RFC","Alcance visible"]}'),
  (N'ADMIN-SEGURIDAD', N'OPERAR', 1, N'DEMOSTRACION', N'Demostrar: alta con acceso mínimo', N'El instructor mostrará cómo dar de alta un usuario ficticio, vincularlo con su colaborador, asignar el rol mínimo, fijar el RFC y comprobar qué puede ver con ese alcance.', N'{"flowStep":"Demostrar","demoSteps":["Crear usuario ficticio","Vincular colaborador","Asignar rol mínimo","Fijar RFC","Comprobar alcance"],"notasInstructor":"Muestre el resultado de un rol de más y uno de menos; el criterio se entiende mejor comparando."}'),
  (N'ADMIN-SEGURIDAD', N'OPERAR', 2, N'ALERTA', N'Alerta: no compartas usuarios ni otorgues roles temporales sin registro', N'Un usuario compartido borra la trazabilidad de todas las acciones del sistema. Si alguien necesita un permiso extraordinario, se otorga con registro, alcance y fecha de retiro.', N'{"severity":"critical","notasInstructor":"Refuerce que un permiso temporal sin fecha de retiro es permanente en la práctica."}'),
  (N'ADMIN-SEGURIDAD', N'OPERAR', 3, N'PRACTICA', N'Practicar: acceso mínimo para un caso ficticio', N'Define el acceso mínimo para un puesto ficticio, justifica cada rol propuesto, indica el RFC aplicable y describe cómo revisarías ese acceso dentro de tres meses.', N'{"sandbox":true,"flowStep":"Practicar","notasInstructor":"Evalúe la justificación de cada rol. Un acceso solicitado por comodidad no es un acceso justificado."}'),
  (N'ADMIN-SEGURIDAD', N'CERRAR', 1, N'EVALUACION', N'Evaluar: roles, alcance y revisión de accesos', N'Responde la evaluación y demuestra que sabes otorgar, revisar y retirar accesos conservando la trazabilidad.', N'{"required":true,"flowStep":"Evaluar","checklist":["Rol justificado","RFC correcto","Usuario individual","Revisión programada"]}'),
  (N'ADMIN-SEGURIDAD', N'CERRAR', 2, N'RESUMEN', N'Cerrar: el acceso se otorga, se revisa y se retira', N'Antes de cerrar confirma que cada usuario tiene dueño, rol justificado, RFC correcto y fecha de revisión. El acceso que nadie revisa es el que termina siendo aprovechado.', N'{"highlight":true,"flowStep":"Cerrar"}');

INSERT INTO capacitacion.BloqueContenido (LeccionId, Orden, Tipo, Titulo, Contenido, ConfiguracionJson, Requerido)
SELECT lesson.LeccionId, source.Orden, source.Tipo, source.Titulo, source.Contenido, source.ConfiguracionJson, 1
FROM @Bloques source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
JOIN capacitacion.Leccion lesson
  ON lesson.CursoVersionId = versionInfo.CursoVersionId AND lesson.Clave = source.LeccionClave
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.BloqueContenido target
  WHERE target.LeccionId = lesson.LeccionId AND target.Orden = source.Orden
);

/* ---------------------------------------------------------------------------
   4. Recursos: enlaces locales a la pantalla real de cada práctica
   ------------------------------------------------------------------------ */

DECLARE @Recursos TABLE
(
  CursoClave nvarchar(64) NOT NULL,
  LeccionClave nvarchar(64) NOT NULL,
  BloqueOrden int NOT NULL,
  Orden int NOT NULL,
  Tipo nvarchar(30) NOT NULL,
  Titulo nvarchar(160) NOT NULL,
  Ruta nvarchar(500) NOT NULL,
  TextoAlternativo nvarchar(500) NULL,
  PRIMARY KEY (CursoClave, LeccionClave, BloqueOrden, Orden)
);

INSERT INTO @Recursos (CursoClave, LeccionClave, BloqueOrden, Orden, Tipo, Titulo, Ruta, TextoAlternativo)
VALUES
  (N'CAPACITACION-MODULO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir el centro de capacitación', N'/capacitacion', N'Acceso local al tablero de capacitación del entorno de práctica.'),
  (N'CAPACITACION-MODULO', N'OPERAR', 3, 2, N'ENLACE', N'Abrir mi capacitación', N'/capacitacion/mi-plan', N'Acceso local al plan personal de cursos asignados.'),
  (N'CAPACITACION-MODULO', N'OPERAR', 3, 3, N'ENLACE', N'Abrir el catálogo de cursos', N'/capacitacion/catalogo', N'Acceso local al catálogo de cursos publicados.'),
  (N'CAPACITACION-MODULO', N'OPERAR', 3, 4, N'ENLACE', N'Abrir la administración de capacitación', N'/capacitacion/admin', N'Acceso local a la asignación y seguimiento de cursos.'),
  (N'CAPACITACION-MODULO', N'OPERAR', 3, 5, N'ENLACE', N'Crear una sesión guiada', N'/capacitacion/sesiones/nueva', N'Acceso local a la creación de sesiones guiadas de capacitación.'),

  (N'RESERVAS-CALENDARIO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir el calendario de reservaciones', N'/reservaciones/calendario', N'Acceso local al calendario de ocupación del entorno de práctica.'),
  (N'RESERVAS-CALENDARIO', N'OPERAR', 3, 2, N'ENLACE', N'Abrir la lista de reservaciones', N'/reservaciones/lista', N'Acceso local a la lista de reservaciones ficticias.'),

  (N'ARRENDADORES-ESTADO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir arrendadores', N'/arrendadores', N'Acceso local al estado de cuenta por propiedad del entorno de práctica.'),

  (N'OT-OPERACION', N'OPERAR', 3, 1, N'ENLACE', N'Abrir órdenes de trabajo', N'/ordenes-trabajo', N'Acceso local al tablero de órdenes de trabajo ficticias.'),
  (N'OT-OPERACION', N'OPERAR', 3, 2, N'ENLACE', N'Abrir plantillas de órdenes de trabajo', N'/ordenes-trabajo/plantillas', N'Acceso local a las plantillas versionadas de órdenes de trabajo.'),

  (N'CFDI-SAT-OPERACION', N'OPERAR', 3, 1, N'ENLACE', N'Abrir el registro de RFC', N'/cfdi/register', N'Acceso local al alta de emisores y receptores ficticios.'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 3, 2, N'ENLACE', N'Abrir descarga masiva SAT', N'/cfdi/descarga-masiva', N'Acceso local a la descarga masiva, simulada en el entorno de capacitación.'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 3, 3, N'ENLACE', N'Abrir la carga de XML', N'/cfdi/cargar-xml-sat', N'Acceso local a la carga del XML ficticio no timbrable.'),
  (N'CFDI-SAT-OPERACION', N'OPERAR', 3, 4, N'ARCHIVO', N'Abrir XML ficticio no timbrable', N'/training/fixtures/cfdi-ficticio-no-timbrable.xml', N'Archivo XML local exclusivo de capacitación, con RFC genéricos y sellos deliberadamente inválidos.'),

  (N'CFDI-DECLARACION-PREVIA', N'OPERAR', 3, 1, N'ENLACE', N'Abrir declaración previa', N'/cfdi/declaracion-previa', N'Acceso local a la revisión previa de comprobantes ficticios.'),
  (N'CFDI-DECLARACION-PREVIA', N'OPERAR', 3, 2, N'ENLACE', N'Abrir registros contables del módulo fiscal', N'/cfdi/registros-contables', N'Acceso local a los registros contables asociados a comprobantes.'),

  (N'CONTA-POLIZAS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir pólizas', N'/contabilidad/transacciones/list', N'Acceso local a la lista de pólizas del entorno de práctica.'),
  (N'CONTA-POLIZAS', N'OPERAR', 3, 2, N'ENLACE', N'Abrir registros contables', N'/contabilidad/registros-contables', N'Acceso local a los auxiliares y registros contables.'),

  (N'BANCOS-CONCILIACION', N'OPERAR', 3, 1, N'ENLACE', N'Abrir bancos', N'/contabilidad/bancos', N'Acceso local a cuentas y movimientos bancarios ficticios.'),

  (N'CXP-RECURRENTES', N'OPERAR', 3, 1, N'ENLACE', N'Abrir compromisos recurrentes', N'/cuentas-por-pagar/recurrentes', N'Acceso local al calendario de compromisos recurrentes ficticios.'),

  (N'REPORTES-FINANCIEROS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir hoja de trabajo', N'/ReportesFinancieros/HojaTrabajo', N'Acceso local a la hoja de trabajo del periodo.'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 3, 2, N'ENLACE', N'Abrir balanza de comprobación', N'/ReportesFinancieros/BalanzaComprobacion', N'Acceso local a la balanza de comprobación del periodo.'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 3, 3, N'ENLACE', N'Abrir estado de pérdidas y ganancias', N'/ReportesFinancieros/EstadoPerdidasGanancias', N'Acceso local al estado de resultados del periodo.'),
  (N'REPORTES-FINANCIEROS', N'OPERAR', 3, 4, N'ENLACE', N'Abrir salud financiera', N'/ReportesFinancieros/SaludEmpresa', N'Acceso local al tablero de salud financiera.'),

  (N'LOGISTICA-COMPRAS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir proveedores', N'/logistica/proveedores', N'Acceso local al catálogo de proveedores ficticios.'),
  (N'LOGISTICA-COMPRAS', N'OPERAR', 3, 2, N'ENLACE', N'Abrir compras', N'/logistica/compras', N'Acceso local a órdenes de compra y recepciones del entorno de práctica.'),

  (N'LOGISTICA-INVENTARIO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir ubicaciones e inventario', N'/logistica/ubicaciones', N'Acceso local a almacenes, ubicaciones y existencias ficticias.'),
  (N'LOGISTICA-INVENTARIO', N'OPERAR', 3, 2, N'ENLACE', N'Abrir conteos físicos', N'/logistica/conteos', N'Acceso local a los conteos físicos del entorno de práctica.'),

  (N'REST-POS-SERVICIO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir el punto de venta', N'/restaurante/pos', N'Acceso local al punto de venta táctil del entorno de práctica.'),
  (N'REST-POS-SERVICIO', N'OPERAR', 3, 2, N'ENLACE', N'Abrir órdenes y entregas', N'/restaurante/ordenes', N'Acceso local al despacho y entrega por folio.'),
  (N'REST-POS-SERVICIO', N'OPERAR', 3, 3, N'ENLACE', N'Abrir la pantalla pública de órdenes', N'/restaurante/pantalla', N'Acceso local a la pantalla pública de folios en preparación y listos.'),

  (N'REST-COCINA-PRODUCCION', N'OPERAR', 3, 1, N'ENLACE', N'Abrir la pantalla de cocina', N'/restaurante/cocina', N'Acceso local a las comandas por partida del entorno de práctica.'),
  (N'REST-COCINA-PRODUCCION', N'OPERAR', 3, 2, N'ENLACE', N'Abrir recetas y BOM', N'/restaurante/recetas', N'Acceso local a recetas, subrecetas y rendimientos ficticios.'),
  (N'REST-COCINA-PRODUCCION', N'OPERAR', 3, 3, N'ENLACE', N'Abrir producción por lotes', N'/restaurante/produccion', N'Acceso local a la producción por lotes con rendimiento y merma.'),

  (N'REST-INVENTARIO-TURNOS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir movimientos de inventario', N'/restaurante/inventario', N'Acceso local a traspasos, ajustes y merma del restaurante ficticio.'),
  (N'REST-INVENTARIO-TURNOS', N'OPERAR', 3, 2, N'ENLACE', N'Abrir turnos de caja', N'/restaurante/turnos', N'Acceso local a apertura, conteo ciego y corte de caja.'),

  (N'REST-CATALOGO-CONFIG', N'OPERAR', 3, 1, N'ENLACE', N'Abrir menús y modificadores', N'/restaurante/menus', N'Acceso local a menús, secciones y modificadores ficticios.'),
  (N'REST-CATALOGO-CONFIG', N'OPERAR', 3, 2, N'ENLACE', N'Abrir administración de restaurante', N'/restaurante/admin', N'Acceso local a sedes, productos, variantes y precios ficticios.'),
  (N'REST-CATALOGO-CONFIG', N'OPERAR', 3, 3, N'ENLACE', N'Abrir configuración operativa', N'/restaurante/configuracion', N'Acceso local a mesas, estaciones, almacenes y cuentas.'),

  (N'REST-COMERCIAL', N'OPERAR', 3, 1, N'ENLACE', N'Abrir promociones y membresía', N'/restaurante/promociones', N'Acceso local a reglas, códigos y puntos de membresía ficticios.'),
  (N'REST-COMERCIAL', N'OPERAR', 3, 2, N'ENLACE', N'Abrir reportes de restaurante', N'/restaurante/reportes', N'Acceso local a reportes de venta, margen y liquidaciones.'),
  (N'REST-COMERCIAL', N'OPERAR', 3, 3, N'ENLACE', N'Abrir el sitio público', N'/restaurante/sitio-brunos', N'Acceso local a la configuración de contenido del sitio público.'),

  (N'RH-ASISTENCIA', N'OPERAR', 3, 1, N'ENLACE', N'Abrir control de asistencia', N'/capital-humano/asistencia', N'Acceso local al calendario de asistencia del equipo ficticio.'),
  (N'RH-ASISTENCIA', N'OPERAR', 3, 2, N'ENLACE', N'Abrir mi equipo', N'/mi-equipo', N'Acceso local a la cola de aprobación del supervisor.'),
  (N'RH-ASISTENCIA', N'OPERAR', 3, 3, N'ENLACE', N'Abrir el kiosco de asistencia', N'/asistencia/kiosco', N'Acceso local al kiosco de registro de asistencia del entorno de práctica.'),

  (N'RH-CONFIG-TIEMPO', N'OPERAR', 3, 1, N'ENLACE', N'Abrir configuración de tiempo', N'/capital-humano/configuracion-tiempo', N'Acceso local a sitios, horarios, políticas y kioscos ficticios.'),

  (N'RH-AUSENCIAS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir ausencias', N'/capital-humano/ausencias', N'Acceso local a políticas, saldos y solicitudes de ausencia ficticias.'),

  (N'RH-PRENOMINA', N'OPERAR', 3, 1, N'ENLACE', N'Abrir pre-nómina', N'/capital-humano/pre-nomina', N'Acceso local a la validación y bloqueo del periodo ficticio.'),

  (N'RH-EXPEDIENTES', N'OPERAR', 3, 1, N'ENLACE', N'Abrir Capital Humano', N'/capital-humano', N'Acceso local al expediente de colaboradores ficticios.'),
  (N'RH-EXPEDIENTES', N'OPERAR', 3, 2, N'ENLACE', N'Abrir mi trabajo', N'/mi-trabajo', N'Acceso local al autoservicio personal para contrastar alcances.'),

  (N'AJUSTES-PLANTILLAS', N'OPERAR', 3, 1, N'ENLACE', N'Abrir ajustes', N'/ajustes', N'Acceso local a plantillas contables y configuración del entorno de práctica.'),

  (N'ADMIN-SEGURIDAD', N'OPERAR', 3, 1, N'ENLACE', N'Abrir el portal de seguridad', N'/admin/seguridad', N'Acceso local a usuarios, roles y permisos del entorno de práctica.');
INSERT INTO @Recursos VALUES
  (N'ADMIN-SEGURIDAD', N'OPERAR', 4, 1, N'ENLACE', N'Abrir el directorio de empresas', N'/admin/empresas', N'Administra branding, disponibilidad y revisiones de acceso por empresa.');

INSERT INTO capacitacion.Recurso (BloqueId, Orden, Tipo, Titulo, Ruta, TextoAlternativo, VersionAplicacion)
SELECT blockInfo.BloqueId, source.Orden, source.Tipo, source.Titulo, source.Ruta, source.TextoAlternativo, N'v2'
FROM @Recursos source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
JOIN capacitacion.Leccion lesson
  ON lesson.CursoVersionId = versionInfo.CursoVersionId AND lesson.Clave = source.LeccionClave
JOIN capacitacion.BloqueContenido blockInfo
  ON blockInfo.LeccionId = lesson.LeccionId AND blockInfo.Orden = source.BloqueOrden
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Recurso target
  WHERE target.BloqueId = blockInfo.BloqueId AND target.Orden = source.Orden
);

/* ---------------------------------------------------------------------------
   5. Evaluaciones, preguntas y opciones
   ------------------------------------------------------------------------ */

DECLARE @Evaluaciones TABLE
(
  CursoClave nvarchar(64) NOT NULL PRIMARY KEY,
  Titulo nvarchar(160) NOT NULL,
  Instrucciones nvarchar(1000) NOT NULL
);

INSERT INTO @Evaluaciones (CursoClave, Titulo, Instrucciones)
VALUES
  (N'CAPACITACION-MODULO', N'Validación del módulo de Capacitación', N'Elige la mejor respuesta. Las preguntas críticas deben responderse correctamente.'),
  (N'RESERVAS-CALENDARIO', N'Validación de calendario, tarifas y recibos', N'Responde con base en la ocupación real, la prevención de traslapes y el respaldo del cobro.'),
  (N'ARRENDADORES-ESTADO', N'Validación de estado de cuenta de arrendadores', N'Responde con base en el rastreo de cada concepto hasta su documento de origen.'),
  (N'OT-OPERACION', N'Validación de órdenes de trabajo', N'Responde con base en la ruta crítica, la evidencia y el control de plantillas.'),
  (N'CFDI-SAT-OPERACION', N'Validación de abasto fiscal', N'Responde con base en el origen del comprobante, su unicidad y el control de faltantes.'),
  (N'CFDI-DECLARACION-PREVIA', N'Validación de declaración previa y amarre', N'Responde con base en la clasificación del comprobante y la trazabilidad del amarre.'),
  (N'CONTA-POLIZAS', N'Validación de pólizas y registros contables', N'Responde con base en la partida doble, el periodo y el respaldo documental.'),
  (N'BANCOS-CONCILIACION', N'Validación de bancos y conciliación', N'Responde con base en la diferencia entre conciliar y registrar.'),
  (N'CXP-RECURRENTES', N'Validación de compromisos recurrentes', N'Responde con base en la previsión, la autorización y el control de variaciones.'),
  (N'REPORTES-FINANCIEROS', N'Validación de reportes financieros', N'Responde con base en la trazabilidad de las cifras y la interpretación de cada reporte.'),
  (N'LOGISTICA-COMPRAS', N'Validación de compras y recepciones', N'Responde con base en referencias, unidades y prevención de duplicados.'),
  (N'LOGISTICA-INVENTARIO', N'Validación de existencias y conteos', N'Responde con base en el corte del conteo, la investigación y la autorización del ajuste.'),
  (N'REST-POS-SERVICIO', N'Validación de punto de venta y servicio', N'Responde con base en el seguimiento del folio y la corrección con motivo.'),
  (N'REST-COCINA-PRODUCCION', N'Validación de cocina y producción', N'Responde con base en receta, rendimiento y declaración de merma.'),
  (N'REST-INVENTARIO-TURNOS', N'Validación de inventario y turnos de caja', N'Responde con base en el conteo ciego, la evidencia y la aprobación de diferencias.'),
  (N'REST-CATALOGO-CONFIG', N'Validación de catálogo y configuración', N'Responde con base en la vigencia, el impacto operativo y la reversión.'),
  (N'REST-COMERCIAL', N'Validación de promociones y contenido público', N'Responde con base en las reglas, la medición y el control de publicación.'),
  (N'RH-ASISTENCIA', N'Validación de asistencia y privacidad', N'Responde con base en el flujo de corrección autorizado y la privacidad del equipo.'),
  (N'RH-CONFIG-TIEMPO', N'Validación de configuración de tiempo', N'Responde con base en la vigencia del cambio y su impacto en periodos posteriores.'),
  (N'RH-AUSENCIAS', N'Validación de ausencias y saldos', N'Responde con base en la política, el saldo y la evidencia de la solicitud.'),
  (N'RH-PRENOMINA', N'Validación de pre-nómina', N'Responde con base en la justificación de incidencias y el efecto del bloqueo.'),
  (N'RH-EXPEDIENTES', N'Validación de expediente y privacidad', N'Responde con base en la clasificación del dato y el acceso por rol.'),
  (N'AJUSTES-PLANTILLAS', N'Validación de ajustes y plantillas', N'Responde con base en el alcance del cambio y la diferencia con una excepción puntual.'),
  (N'ADMIN-SEGURIDAD', N'Validación de seguridad y accesos', N'Responde con base en el mínimo privilegio y la trazabilidad individual.');

INSERT INTO capacitacion.Evaluacion (CursoVersionId, Titulo, Instrucciones, CalificacionMinima, Requerida)
SELECT versionInfo.CursoVersionId, source.Titulo, source.Instrucciones, 80, 1
FROM @Evaluaciones source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Evaluacion target
  WHERE target.CursoVersionId = versionInfo.CursoVersionId AND target.Titulo = source.Titulo
);

DECLARE @Preguntas TABLE
(
  CursoClave nvarchar(64) NOT NULL,
  Orden int NOT NULL,
  Texto nvarchar(1000) NOT NULL,
  Explicacion nvarchar(1000) NULL,
  Critica bit NOT NULL,
  Correcta nvarchar(1000) NOT NULL,
  Incorrecta1 nvarchar(1000) NOT NULL,
  Incorrecta2 nvarchar(1000) NOT NULL,
  PRIMARY KEY (CursoClave, Orden)
);

INSERT INTO @Preguntas (CursoClave, Orden, Texto, Explicacion, Critica, Correcta, Incorrecta1, Incorrecta2)
VALUES
  (N'CAPACITACION-MODULO', 1, N'¿Qué acredita realmente un curso de OrionERP?', N'La acreditación combina evaluación, práctica, firma y acuse.', 1, N'Evaluación aprobada, práctica validada, firma del instructor y acuse del colaborador.', N'Haber asistido a la sesión completa.', N'Haber abierto todos los bloques del curso.'),
  (N'CAPACITACION-MODULO', 2, N'¿Qué ocurre con la firma del instructor una vez registrada?', N'La evidencia de acreditación es inmutable por diseño.', 1, N'Es inmutable: un error solo puede documentarse y escalarse.', N'Puede editarse mientras la sesión siga abierta.', N'Se elimina automáticamente al reasignar el curso.'),
  (N'CAPACITACION-MODULO', 3, N'¿Para qué sirve una sesión guiada?', N'La sesión conduce al grupo bloque por bloque y registra avance.', 0, N'Para conducir al grupo por los bloques y registrar el avance de cada participante.', N'Para sustituir la asignación individual de cada persona.', N'Para publicar una nueva versión del curso.'),
  (N'CAPACITACION-MODULO', 4, N'¿Qué pasa si el contenido de una versión publicada necesita cambiar?', N'Las versiones publicadas son inmutables; el cambio exige una versión nueva.', 0, N'Se crea una versión nueva; la publicada no se modifica.', N'Se edita el bloque directamente en la versión publicada.', N'Se borra el curso y se captura otra vez.'),

  (N'RESERVAS-CALENDARIO', 1, N'¿Qué debes verificar antes de mover una reservación a otra habitación?', N'Mover ocupación exige comprobar la habitación destino y las fechas.', 1, N'Disponibilidad real de la habitación destino en todas las fechas de la estancia.', N'Solo que el precio sea igual.', N'Solo que el huésped esté de acuerdo.'),
  (N'RESERVAS-CALENDARIO', 2, N'Encuentras dos reservaciones traslapadas en la misma habitación. ¿Qué haces?', N'El traslape se documenta y escala; nunca se resuelve borrando evidencia.', 1, N'Documentar el conflicto, revisar el cobro de ambas y escalarlo.', N'Eliminar la reservación más reciente.', N'Cambiar las fechas de una hasta que dejen de cruzarse.'),
  (N'RESERVAS-CALENDARIO', 3, N'¿Qué efecto tiene un bloqueo de fecha?', N'El bloqueo retira inventario disponible para esa fecha.', 0, N'Retira esa fecha del inventario disponible y debe tener motivo.', N'Solo cambia el color en la pantalla.', N'Cancela las reservaciones existentes de esa fecha.'),
  (N'RESERVAS-CALENDARIO', 4, N'¿Cuándo se emite el recibo del huésped?', N'El recibo debe coincidir con la estancia y el cobro registrado.', 0, N'Cuando la estancia y el cobro registrado coinciden con lo que se documenta.', N'Antes de confirmar la reservación, para agilizar.', N'Solo si el huésped lo solicita por escrito.'),

  (N'ARRENDADORES-ESTADO', 1, N'¿Qué debe poder hacerse con cada concepto del estado de cuenta?', N'Todo concepto debe rastrearse hasta su documento de origen.', 1, N'Rastrearse hasta la reservación, el gasto o el movimiento que lo originó.', N'Redondearse para facilitar la lectura del propietario.', N'Agruparse en un solo total sin detalle.'),
  (N'ARRENDADORES-ESTADO', 2, N'Una cifra del estado de cuenta está mal. ¿Dónde se corrige?', N'La corrección ocurre en el origen, no en la presentación.', 1, N'En el documento de origen: reservación, gasto o póliza.', N'Directamente en el estado de cuenta antes de enviarlo.', N'En una hoja de cálculo aparte que se envía al propietario.'),
  (N'ARRENDADORES-ESTADO', 3, N'¿Qué comprueba que la liquidación se realizó?', N'La liquidación se sostiene con el movimiento bancario correspondiente.', 0, N'El movimiento bancario conciliado con el importe liquidado.', N'El correo enviado al propietario.', N'La firma del operador en el documento impreso.'),
  (N'ARRENDADORES-ESTADO', 4, N'¿Cómo se atiende una aclaración del propietario?', N'La aclaración se responde con evidencia y queda documentada.', 0, N'Con el detalle rastreado y la evidencia documentada de la respuesta.', N'Ajustando el siguiente estado de cuenta para compensar.', N'Explicando de palabra sin dejar registro.'),

  (N'OT-OPERACION', 1, N'¿Puedes cerrar una orden con un paso crítico sin evidencia?', N'Los pasos críticos requieren evidencia para poder cerrarse.', 1, N'No: se documenta el impedimento y se escala.', N'Sí, si el responsable confirma verbalmente.', N'Sí, siempre que la suite se vea limpia.'),
  (N'OT-OPERACION', 2, N'¿Qué define los pasos obligatorios de una orden?', N'La plantilla versionada define pasos y ruta crítica.', 0, N'La plantilla versionada que se aplicó al generar la orden.', N'La preferencia del operador que la ejecuta.', N'El tiempo disponible antes del siguiente huésped.'),
  (N'OT-OPERACION', 3, N'Detectas un desperfecto que no forma parte de la orden. ¿Qué haces?', N'Un hallazgo se registra para generar el trabajo correspondiente.', 1, N'Registrar el hallazgo con su descripción para que genere el trabajo correspondiente.', N'Repararlo sin registrarlo para no retrasar la orden.', N'Ignorarlo porque no está en la lista de pasos.'),
  (N'OT-OPERACION', 4, N'¿Cómo se cambia un paso de una plantilla en uso?', N'Las plantillas se versionan para no alterar órdenes en curso.', 0, N'Con una versión nueva de la plantilla, sin alterar las órdenes en curso.', N'Editando la plantilla vigente para que aplique de inmediato.', N'Creando la orden manualmente sin plantilla.'),

  (N'CFDI-SAT-OPERACION', 1, N'¿Qué determina que un comprobante ya existe en el sistema?', N'El UUID identifica de forma única al comprobante.', 1, N'Su UUID, que es único por comprobante.', N'El nombre del proveedor y el total.', N'La fecha de carga del archivo.'),
  (N'CFDI-SAT-OPERACION', 2, N'¿Qué archivos pueden cargarse en el entorno de capacitación?', N'Capacitación usa exclusivamente el XML ficticio no timbrable.', 1, N'Únicamente el XML local ficticio marcado como no timbrable.', N'Cualquier XML del mes anterior de producción.', N'Un XML real con el nombre del cliente sustituido.'),
  (N'CFDI-SAT-OPERACION', 3, N'La carga responde con un error de tiempo de espera. ¿Qué haces?', N'Antes de reintentar hay que verificar si el comprobante ya quedó registrado.', 1, N'Buscar el comprobante por UUID y confirmar si ya quedó registrado.', N'Volver a cargar el archivo de inmediato.', N'Cargar el archivo con otro nombre.'),
  (N'CFDI-SAT-OPERACION', 4, N'¿Para qué sirve el resumen fiscal?', N'El resumen muestra el estado del abasto y lo que falta conciliar.', 0, N'Para ver qué comprobantes llegaron, cuáles faltan y cuáles se rechazaron.', N'Para timbrar los comprobantes pendientes.', N'Para eliminar comprobantes duplicados automáticamente.'),

  (N'CFDI-DECLARACION-PREVIA', 1, N'¿Qué revisas antes de amarrar un comprobante a una póliza?', N'Pertenencia, vigencia, duplicidad y coherencia definen si procede.', 1, N'RFC correcto, vigencia, ausencia de duplicado y coherencia de importes.', N'Solo que el total coincida con el pago.', N'Solo que el proveedor sea conocido.'),
  (N'CFDI-DECLARACION-PREVIA', 2, N'Un comprobante fue cancelado después de emitirse. ¿Qué procede?', N'Un cancelado no genera el mismo efecto contable que uno vigente.', 1, N'Detener el amarre, documentar la cancelación y escalar el caso.', N'Contabilizarlo igual y corregir al cierre del año.', N'Amarrarlo a la póliza del comprobante que lo sustituye.'),
  (N'CFDI-DECLARACION-PREVIA', 3, N'¿Qué relación tiene un complemento de pago con la factura?', N'El complemento documenta el pago de un comprobante previo.', 0, N'Documenta el pago de un comprobante emitido antes y se revisa junto con él.', N'Sustituye a la factura original.', N'Genera un ingreso independiente de la factura.'),
  (N'CFDI-DECLARACION-PREVIA', 4, N'¿Qué demuestra que el amarre quedó bien hecho?', N'La trazabilidad debe funcionar en ambas direcciones.', 0, N'Que desde la póliza se llegue al comprobante y desde el comprobante a la póliza.', N'Que el listado ya no muestre el comprobante.', N'Que el total del periodo haya aumentado.'),

  (N'CONTA-POLIZAS', 1, N'¿Qué condición debe cumplir siempre una póliza?', N'La partida doble exige igualdad entre cargos y abonos.', 1, N'Que los cargos sean iguales a los abonos.', N'Que use una sola cuenta contable.', N'Que tenga la fecha del día en que se captura.'),
  (N'CONTA-POLIZAS', 2, N'Capturaste un asiento dos veces. ¿Cómo lo corriges?', N'La corrección se hace por el flujo autorizado, referenciando el asiento original.', 1, N'Por el flujo de corrección autorizado, con motivo y referencia al asiento original.', N'Capturando un tercer asiento que compense la diferencia.', N'Borrando ambos asientos y empezando de nuevo.'),
  (N'CONTA-POLIZAS', 3, N'¿Qué pasa cuando el periodo contable se cierra?', N'El cierre limita las modificaciones al periodo y exige otro flujo.', 0, N'Deja de aceptar cambios directos y las correcciones siguen otro flujo.', N'Se borran las pólizas del periodo.', N'Las cuentas vuelven a saldo cero automáticamente.'),
  (N'CONTA-POLIZAS', 4, N'¿Para qué sirve el registro contable auxiliar?', N'El auxiliar permite revisar el efecto por cuenta y documento.', 0, N'Para revisar el efecto de cada movimiento por cuenta y por documento.', N'Para sustituir la póliza cuando hay errores.', N'Para calcular impuestos automáticamente.'),

  (N'BANCOS-CONCILIACION', 1, N'¿Qué significa conciliar un movimiento bancario?', N'Conciliar enlaza evidencia con un efecto existente.', 1, N'Enlazarlo con su efecto contable existente sin duplicarlo.', N'Crear una póliza nueva por cada movimiento.', N'Ajustar el saldo contable hasta igualar el del banco.'),
  (N'BANCOS-CONCILIACION', 2, N'El banco muestra un cargo que la contabilidad no tiene. ¿Qué haces?', N'La diferencia se investiga y documenta antes de registrar nada.', 1, N'Documentar la diferencia, investigar su origen y escalarla.', N'Registrar un asiento improvisado para cuadrar el saldo.', N'Marcar el movimiento como conciliado y seguir.'),
  (N'BANCOS-CONCILIACION', 3, N'¿Qué indica un saldo bancario que cuadra con asientos inventados?', N'Un saldo forzado oculta el problema en lugar de resolverlo.', 0, N'Que el problema quedó oculto y la contabilidad dejó de ser confiable.', N'Que la conciliación se hizo correctamente.', N'Que el banco cometió un error.'),
  (N'BANCOS-CONCILIACION', 4, N'¿Qué debe quedar de una conciliación terminada?', N'La conciliación deja enlaces verificables y diferencias con responsable.', 0, N'Enlaces verificables y las diferencias abiertas con responsable y fecha.', N'Solo el saldo final impreso.', N'Un correo de aviso al área contable.'),

  (N'CXP-RECURRENTES', 1, N'¿Qué autoriza programar un compromiso recurrente?', N'La programación anticipa, no aprueba el desembolso.', 1, N'Nada por sí sola: cada pago sigue requiriendo comprobante y autorización.', N'El pago automático en cada vencimiento.', N'La contabilización directa sin revisión.'),
  (N'CXP-RECURRENTES', 2, N'El importe del servicio llega distinto al programado. ¿Qué haces?', N'La variación se documenta y se valida antes de pagar.', 1, N'Documentar la variación, validarla con el responsable y actualizar el compromiso.', N'Pagar el importe programado e ignorar la diferencia.', N'Cancelar el compromiso sin avisar.'),
  (N'CXP-RECURRENTES', 3, N'¿Para qué sirve el calendario de vencimientos?', N'El calendario permite anticipar el flujo de efectivo.', 0, N'Para anticipar el flujo de efectivo y evitar recargos por olvido.', N'Para contabilizar el gasto automáticamente.', N'Para sustituir la conciliación bancaria.'),
  (N'CXP-RECURRENTES', 4, N'¿Qué evidencia debe conservar un compromiso recurrente?', N'El compromiso vive de su autorización y su respaldo documental.', 0, N'La autorización vigente, el comprobante de cada periodo y su conciliación.', N'Únicamente el nombre del proveedor.', N'Solo la fecha del primer pago.'),

  (N'REPORTES-FINANCIEROS', 1, N'Una cifra del reporte parece equivocada. ¿Dónde se corrige?', N'El reporte refleja la contabilidad; la corrección va al origen.', 1, N'En la póliza, la cuenta o el periodo que la originó.', N'En el propio reporte antes de exportarlo.', N'En una hoja de cálculo que se comparte en lugar del reporte.'),
  (N'REPORTES-FINANCIEROS', 2, N'¿Qué muestra la balanza de comprobación?', N'La balanza resume saldos y movimientos por cuenta.', 0, N'Saldos y movimientos por cuenta en el periodo seleccionado.', N'La rentabilidad por producto.', N'El flujo de efectivo proyectado.'),
  (N'REPORTES-FINANCIEROS', 3, N'¿Qué debe verificarse antes de leer cualquier reporte?', N'Periodo y RFC determinan por completo el resultado mostrado.', 1, N'El periodo y el RFC seleccionados.', N'Solo que la pantalla cargue sin errores.', N'Solo el nombre del usuario que lo abre.'),
  (N'REPORTES-FINANCIEROS', 4, N'¿Qué aporta el tablero de salud financiera?', N'El tablero combina indicadores operativos y financieros.', 0, N'Una lectura combinada de ingresos, flujo, margen, ocupación y conciliación.', N'El detalle de cada póliza del periodo.', N'La lista de comprobantes pendientes de timbrar.'),

  (N'LOGISTICA-COMPRAS', 1, N'La recepción respondió con un error incierto. ¿Qué haces antes de repetirla?', N'Buscar la referencia y su estado evita una recepción duplicada.', 1, N'Buscar la referencia y confirmar el estado de la primera recepción.', N'Repetirla con otra referencia.', N'Ajustar la existencia para compensar.'),
  (N'LOGISTICA-COMPRAS', 2, N'¿Qué efecto tiene una recepción duplicada?', N'Una recepción doble infla existencia, costo y cuentas por pagar.', 1, N'Infla existencia, costo promedio y cuentas por pagar al mismo tiempo.', N'Solo duplica un renglón visual sin efecto real.', N'Se corrige sola en el siguiente conteo.'),
  (N'LOGISTICA-COMPRAS', 3, N'Llegó menos material del solicitado. ¿Qué corresponde?', N'La recepción parcial documenta lo pendiente sin cerrar la orden.', 0, N'Registrar la recepción parcial y dejar documentado el pendiente.', N'Cerrar la orden completa y ajustar después.', N'Rechazar todo el envío.'),
  (N'LOGISTICA-COMPRAS', 4, N'¿Qué debe coincidir entre orden de compra y recepción?', N'Material, unidad y referencia sostienen la trazabilidad de la compra.', 0, N'Material, unidad de medida y referencia, además del precio acordado.', N'Solo el nombre del proveedor.', N'Solo la fecha del documento.'),

  (N'LOGISTICA-INVENTARIO', 1, N'El conteo físico difiere del sistema. ¿Cuál es el primer paso?', N'Ante una diferencia se recuenta antes de cualquier ajuste.', 1, N'Recontar y verificar el corte antes de proponer cualquier ajuste.', N'Ajustar la existencia para igualar el conteo.', N'Cambiar la unidad del material.'),
  (N'LOGISTICA-INVENTARIO', 2, N'¿Por qué debe definirse un corte antes de contar?', N'Contar con movimientos abiertos genera diferencias inexistentes.', 1, N'Porque contar mientras hay movimientos genera diferencias que no existen.', N'Porque el sistema lo exige por formato.', N'Porque así el conteo se hace más rápido.'),
  (N'LOGISTICA-INVENTARIO', 3, N'¿Quién puede autorizar un ajuste de inventario?', N'El ajuste requiere autorización, evidencia y motivo.', 0, N'El responsable autorizado, con evidencia del recuento y un motivo documentado.', N'Cualquier persona que tenga acceso al módulo.', N'El proveedor que entregó el material.'),
  (N'LOGISTICA-INVENTARIO', 4, N'¿Qué indican los mínimos y máximos de una ubicación?', N'Los límites señalan riesgo de desabasto o exceso, no obligan a ajustar.', 0, N'Riesgo de desabasto o exceso; no son una instrucción para ajustar existencias.', N'La cantidad exacta que debe haber siempre.', N'El precio máximo de compra autorizado.'),

  (N'REST-POS-SERVICIO', 1, N'¿Qué conecta la venta, la preparación y la entrega?', N'El folio es el hilo que recorre todo el servicio.', 1, N'El folio de la orden.', N'El nombre del cliente.', N'La mesa asignada.'),
  (N'REST-POS-SERVICIO', 2, N'Capturaste un producto equivocado que ya fue preparado. ¿Qué haces?', N'La corrección con motivo conserva la trazabilidad y la merma.', 1, N'Usar el flujo de corrección con motivo y registrar la merma correspondiente.', N'Cancelar la orden completa y capturarla de nuevo.', N'Entregar el producto equivocado para no perderlo.'),
  (N'REST-POS-SERVICIO', 3, N'¿Qué información nunca debe mostrar la pantalla pública?', N'La pantalla pública muestra folios, no datos personales.', 1, N'Datos personales del cliente.', N'El número de folio.', N'El estado de preparación.'),
  (N'REST-POS-SERVICIO', 4, N'¿Qué diferencia hay entre un modificador de precio y uno de preparación?', N'Los modificadores cambian el cobro o la instrucción a cocina.', 0, N'Uno cambia el importe cobrado y el otro cambia la instrucción que recibe cocina.', N'Ninguna: los dos son solo comentarios.', N'El de preparación siempre aumenta el precio.'),

  (N'REST-COCINA-PRODUCCION', 1, N'¿Qué representa la merma en una producción por lotes?', N'La merma es la diferencia entre rendimiento esperado y real.', 1, N'La diferencia entre el rendimiento esperado y el obtenido, y debe declararse.', N'Un error de captura que conviene omitir.', N'Un descuento aplicado al cliente.'),
  (N'REST-COCINA-PRODUCCION', 2, N'¿Qué ocurre si no se declara la merma real?', N'La merma no declarada descuadra inventario y subestima el costo.', 1, N'El inventario queda descuadrado y el costo del producto se subestima.', N'No pasa nada porque se corrige en la siguiente compra.', N'El sistema la calcula solo al cierre del mes.'),
  (N'REST-COCINA-PRODUCCION', 3, N'¿Para qué sirve una subreceta?', N'Las subrecetas permiten componer preparaciones y costear correctamente.', 0, N'Para componer preparaciones intermedias y costear el producto final con precisión.', N'Para duplicar el rendimiento de la receta principal.', N'Para ocultar ingredientes del costo.'),
  (N'REST-COCINA-PRODUCCION', 4, N'¿Qué prioriza la pantalla de cocina?', N'El KDS organiza por partida y tiempo de preparación.', 0, N'Las comandas por partida y su tiempo de preparación.', N'El monto de la cuenta del cliente.', N'El orden alfabético de los productos.'),

  (N'REST-INVENTARIO-TURNOS', 1, N'¿Por qué el conteo de caja es ciego?', N'El conteo ciego evita que la expectativa contamine el resultado.', 1, N'Para que la expectativa del sistema no influya en lo que se cuenta.', N'Para que el corte sea más rápido.', N'Porque el sistema no muestra el efectivo esperado.'),
  (N'REST-INVENTARIO-TURNOS', 2, N'Hay una diferencia de efectivo al cerrar el turno. ¿Qué haces?', N'La diferencia se declara, se explica y se envía a aprobación.', 1, N'Declararla, explicarla con evidencia y enviarla a aprobación.', N'Ajustar el conteo hasta que cuadre con lo esperado.', N'Cubrir la diferencia con dinero propio sin registrarla.'),
  (N'REST-INVENTARIO-TURNOS', 3, N'¿Qué diferencia hay entre un traspaso y un ajuste?', N'El traspaso mueve existencia, el ajuste cambia el total.', 0, N'El traspaso mueve existencia entre almacenes; el ajuste cambia el total y exige motivo.', N'Son lo mismo con distinto nombre.', N'El traspaso solo aplica a productos terminados.'),
  (N'REST-INVENTARIO-TURNOS', 4, N'¿Qué debe acompañar a una merma registrada?', N'Toda merma requiere motivo y evidencia verificable.', 0, N'Un motivo y la evidencia que lo respalde.', N'Únicamente la hora del registro.', N'La autorización del cliente afectado.'),

  (N'REST-CATALOGO-CONFIG', 1, N'¿Desde cuándo aplica un cambio de precio publicado?', N'La publicación afecta ventas en curso según su vigencia.', 1, N'Desde su vigencia, afectando las ventas siguientes de la sede indicada.', N'Solo a partir del día siguiente en todas las sedes.', N'Solo cuando alguien reinicia el punto de venta.'),
  (N'REST-CATALOGO-CONFIG', 2, N'¿Qué debes tener listo antes de publicar un cambio de catálogo?', N'Un cambio publicado debe poder revertirse de inmediato.', 1, N'El plan de reversión y la verificación posterior en el punto de venta.', N'Solo la aprobación verbal del encargado.', N'Solo el precio nuevo escrito en una nota.'),
  (N'REST-CATALOGO-CONFIG', 3, N'¿Qué define la estación de preparación de un producto?', N'La estación determina a qué partida de cocina llega la comanda.', 0, N'A qué partida de cocina llega su comanda.', N'El precio de venta del producto.', N'El almacén donde se compra el insumo.'),
  (N'REST-CATALOGO-CONFIG', 4, N'¿Qué controla el horario de un menú?', N'El menú por horario limita cuándo puede venderse cada sección.', 0, N'Cuándo puede venderse cada sección y sus productos.', N'El tiempo máximo de preparación en cocina.', N'La duración del turno de caja.'),

  (N'REST-COMERCIAL', 1, N'¿Qué debe tener siempre una promoción antes de activarse?', N'Una promoción sin límite ni medición es un riesgo de margen.', 1, N'Reglas acotadas de tiempo, producto y canal, además de un indicador para medirla.', N'Un nombre atractivo y una imagen.', N'La aprobación del proveedor del insumo.'),
  (N'REST-COMERCIAL', 2, N'¿Qué puede hacerse con el contenido del sitio público desde capacitación?', N'El entorno de capacitación nunca publica al sitio real.', 1, N'Prepararlo y revisarlo; la publicación real requiere autorización del responsable.', N'Publicarlo de inmediato para practicar.', N'Copiar contenido de otro restaurante para probar.'),
  (N'REST-COMERCIAL', 3, N'¿Qué indica que una promoción funcionó?', N'El resultado se mide contra el objetivo definido, no contra el volumen.', 0, N'El indicador definido antes de lanzarla, comparado con su objetivo.', N'Que se hayan usado muchos códigos.', N'Que el personal la considere atractiva.'),
  (N'REST-COMERCIAL', 4, N'¿Qué representan los puntos de membresía acumulados?', N'Los puntos son un derecho futuro del cliente y afectan resultados.', 0, N'Un derecho futuro del cliente que debe considerarse al medir el margen.', N'Un dato informativo sin efecto económico.', N'Un descuento inmediato en cada venta.'),

  (N'RH-ASISTENCIA', 1, N'¿Cómo se corrige un evento de asistencia de otra persona?', N'El supervisor aprueba solicitudes; no reescribe eventos.', 1, N'Con una solicitud de corrección que el responsable aprueba o rechaza.', N'Editando directamente el evento del colaborador.', N'Creando un evento nuevo hasta que el total cuadre.'),
  (N'RH-ASISTENCIA', 2, N'¿Qué información puedes consultar en el módulo de asistencia?', N'El acceso se limita al alcance del rol y del equipo asignado.', 1, N'La del equipo que te corresponde según tu rol.', N'La de cualquier colaborador de la empresa.', N'La de personas de otras sedes por curiosidad.'),
  (N'RH-ASISTENCIA', 3, N'¿Qué es una anomalía de asistencia?', N'La anomalía marca eventos faltantes o fuera de lo esperado.', 0, N'Un evento faltante, fuera de horario o fuera del sitio esperado.', N'Cualquier registro hecho desde el kiosco.', N'Un permiso aprobado por el supervisor.'),
  (N'RH-ASISTENCIA', 4, N'¿Para qué sirve dejar el periodo revisado?', N'El periodo revisado es el insumo confiable de la pre-nómina.', 0, N'Para que la pre-nómina parta de información ya justificada.', N'Para bloquear el acceso al kiosco.', N'Para eliminar los eventos antiguos.'),

  (N'RH-CONFIG-TIEMPO', 1, N'¿Desde cuándo debe aplicar un cambio de horario o tolerancia?', N'La vigencia hacia adelante protege los periodos ya revisados.', 1, N'Desde una vigencia futura, sin alterar periodos ya cerrados.', N'Desde el inicio del año en curso.', N'Desde la fecha del primer evento registrado.'),
  (N'RH-CONFIG-TIEMPO', 2, N'¿Qué efecto tiene modificar la geocerca de un sitio?', N'La geocerca determina dónde se acepta el registro.', 1, N'Cambia dónde se aceptan los registros y qué eventos se marcan como anomalía.', N'Solo cambia el mapa que se muestra en pantalla.', N'Elimina los registros anteriores del sitio.'),
  (N'RH-CONFIG-TIEMPO', 3, N'¿Quién debe enterarse de un cambio de configuración de tiempo?', N'Un cambio silencioso se traduce en descuentos inesperados.', 0, N'Las personas afectadas y sus responsables, antes de que aplique.', N'Solo el área de sistemas.', N'Nadie: el sistema lo comunica automáticamente.'),
  (N'RH-CONFIG-TIEMPO', 4, N'¿Qué relación hay entre la política de asistencia y las anomalías?', N'La política define la tolerancia que produce o evita la anomalía.', 0, N'La política define la tolerancia con la que se evalúa cada evento.', N'Ninguna: las anomalías son aleatorias.', N'La política solo aplica a personal de nuevo ingreso.'),

  (N'RH-AUSENCIAS', 1, N'¿Cuándo se consume el saldo de una ausencia?', N'El saldo se afecta cuando la solicitud queda aprobada.', 1, N'Cuando la solicitud queda aprobada.', N'Cuando se captura la solicitud.', N'Cuando termina el periodo de nómina.'),
  (N'RH-AUSENCIAS', 2, N'El saldo de un colaborador parece incorrecto. ¿Qué haces?', N'Antes de ajustar hay que revisar devengo y solicitudes aplicadas.', 1, N'Revisar el devengo y las solicitudes aplicadas antes de proponer un ajuste auditado.', N'Editar el saldo directamente hasta que se vea correcto.', N'Aprobar una ausencia adicional para compensar.'),
  (N'RH-AUSENCIAS', 3, N'¿Qué debe acompañar a una resolución de solicitud?', N'Toda resolución requiere motivo para poder explicarse después.', 0, N'Un motivo claro, tanto si se aprueba como si se rechaza.', N'Solo la firma del supervisor.', N'Nada: la resolución se explica de palabra.'),
  (N'RH-AUSENCIAS', 4, N'¿Qué define cuánto saldo se acumula por periodo?', N'La política de devengo determina la acumulación por tipo de ausencia.', 0, N'La política de devengo del tipo de ausencia.', N'La antigüedad del supervisor.', N'El número de solicitudes previas.'),

  (N'RH-PRENOMINA', 1, N'¿Puedes bloquear un periodo con incidencias sin justificar?', N'El bloqueo convierte el periodo en la base del pago.', 1, N'No: primero se justifican o se resuelven las incidencias abiertas.', N'Sí, se corrigen después del pago.', N'Sí, siempre que el total de horas cuadre.'),
  (N'RH-PRENOMINA', 2, N'Llega una corrección después del bloqueo. ¿Qué procede?', N'Después del bloqueo la corrección viaja al periodo siguiente con referencia.', 1, N'Registrarla en el periodo siguiente con referencia al periodo bloqueado.', N'Desbloquear el periodo y editarlo.', N'Ignorarla porque el periodo ya se pagó.'),
  (N'RH-PRENOMINA', 3, N'¿Qué son las unidades de tiempo del periodo?', N'La pre-nómina convierte eventos en unidades por colaborador.', 0, N'El resultado de convertir asistencia y ausencias en tiempo por colaborador.', N'El número de días naturales del mes.', N'La suma de horas planeadas del horario.'),
  (N'RH-PRENOMINA', 4, N'¿Qué debe cumplir la exportación hacia nómina?', N'Una exportación confiable puede reproducirse igual.', 0, N'Ser reproducible: exportar otra vez debe entregar el mismo resultado.', N'Cambiar cada vez que se ejecuta.', N'Incluir datos personales completos de cada colaborador.'),

  (N'RH-EXPEDIENTES', 1, N'¿Qué archivos pueden adjuntarse en el entorno de capacitación?', N'Un documento real en un entorno de práctica es una fuga de datos.', 1, N'Únicamente archivos ficticios creados para el ejercicio.', N'Copias de documentos reales con el nombre tachado.', N'Cualquier archivo, porque el entorno se reinicia.'),
  (N'RH-EXPEDIENTES', 2, N'¿Qué determina quién puede consultar un dato del expediente?', N'El acceso depende del rol, no del interés personal.', 1, N'El rol asignado y el alcance que le corresponde.', N'La antigüedad de quien consulta.', N'La relación personal con el colaborador.'),
  (N'RH-EXPEDIENTES', 3, N'¿Qué debe registrarse al actualizar un dato laboral?', N'Todo cambio necesita quedar trazable con su motivo.', 0, N'El cambio con su fecha, su motivo y quién lo hizo.', N'Solo el valor nuevo.', N'Nada, si el cambio es menor.'),
  (N'RH-EXPEDIENTES', 4, N'¿Qué ocurre con los documentos al dar de baja a un colaborador?', N'La baja no elimina el resguardo documental ni su trazabilidad.', 0, N'Se conservan bajo resguardo con su trazabilidad, según la política aplicable.', N'Se eliminan de inmediato del sistema.', N'Se entregan a cualquier compañero del área.'),

  (N'AJUSTES-PLANTILLAS', 1, N'Un documento requiere un tratamiento contable distinto. ¿Qué haces?', N'La excepción se resuelve en el documento; la plantilla es política general.', 1, N'Resolver la excepción en el documento, sin cambiar la plantilla general.', N'Modificar la plantilla para ese caso.', N'Crear una plantilla nueva por cada documento.'),
  (N'AJUSTES-PLANTILLAS', 2, N'¿Qué debes identificar antes de modificar una plantilla?', N'Sin conocer a los consumidores no se puede anticipar el impacto.', 1, N'Qué módulos y flujos la consumen y desde cuándo aplicará el cambio.', N'Solo quién la creó originalmente.', N'Solo el nombre de la plantilla.'),
  (N'AJUSTES-PLANTILLAS', 3, N'¿Cómo se verifica un cambio de configuración?', N'La verificación compara el resultado esperado contra el obtenido.', 0, N'Probando una propuesta y comparando el resultado con lo esperado.', N'Esperando a que alguien reporte un problema.', N'Revisando únicamente que la pantalla no muestre errores.'),
  (N'AJUSTES-PLANTILLAS', 4, N'¿Qué debe existir siempre junto a un ajuste aplicado?', N'Un ajuste sin registro se convierte en una causa raíz invisible.', 0, N'El registro de qué cambió, quién lo autorizó y cómo se revierte.', N'Un respaldo de la base de datos completa.', N'Un comentario verbal al equipo.'),

  (N'ADMIN-SEGURIDAD', 1, N'¿Qué criterio rige la asignación de roles?', N'El mínimo privilegio limita el daño de cualquier error o incidente.', 1, N'El mínimo privilegio necesario para hacer el trabajo.', N'Dar el mismo rol que tiene el compañero de área.', N'Otorgar todos los roles para evitar bloqueos.'),
  (N'ADMIN-SEGURIDAD', 2, N'Alguien pide compartir un usuario para agilizar. ¿Qué respondes?', N'Un usuario compartido destruye la trazabilidad de todo el sistema.', 1, N'No: cada persona necesita su propio usuario para conservar la trazabilidad.', N'Sí, mientras el turno sea corto.', N'Sí, si el supervisor lo autoriza de palabra.'),
  (N'ADMIN-SEGURIDAD', 3, N'¿Qué delimita el RFC activo de un usuario?', N'El RFC define el alcance de la información visible.', 1, N'Qué información puede ver y operar dentro del sistema.', N'El color del tema de la aplicación.', N'La velocidad de acceso a los reportes.'),
  (N'ADMIN-SEGURIDAD', 4, N'¿Qué debe acompañar a un permiso extraordinario?', N'Un permiso temporal sin fecha de retiro es permanente en la práctica.', 0, N'Un registro con alcance, motivo y fecha de retiro.', N'Una nota mental del administrador.', N'La promesa de retirarlo cuando ya no se use.');

INSERT INTO capacitacion.Pregunta (EvaluacionId, Orden, Texto, Explicacion, Critica)
SELECT evaluation.EvaluacionId, source.Orden, source.Texto, source.Explicacion, source.Critica
FROM @Preguntas source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
JOIN capacitacion.Evaluacion evaluation ON evaluation.CursoVersionId = versionInfo.CursoVersionId
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Pregunta target
  WHERE target.EvaluacionId = evaluation.EvaluacionId AND target.Orden = source.Orden
);

INSERT INTO capacitacion.OpcionPregunta (PreguntaId, Orden, Texto, EsCorrecta)
SELECT question.PreguntaId, optionInfo.Orden, optionInfo.Texto, optionInfo.EsCorrecta
FROM @Preguntas source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
JOIN capacitacion.Evaluacion evaluation ON evaluation.CursoVersionId = versionInfo.CursoVersionId
JOIN capacitacion.Pregunta question
  ON question.EvaluacionId = evaluation.EvaluacionId AND question.Orden = source.Orden
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

/* ---------------------------------------------------------------------------
   6. Prácticas guiadas y sus pasos evaluables
   ------------------------------------------------------------------------ */

DECLARE @Practicas TABLE
(
  CursoClave nvarchar(64) NOT NULL PRIMARY KEY,
  Titulo nvarchar(160) NOT NULL,
  Instrucciones nvarchar(2000) NOT NULL,
  Ruta nvarchar(500) NULL
);

INSERT INTO @Practicas (CursoClave, Titulo, Instrucciones, Ruta)
VALUES
  (N'CAPACITACION-MODULO', N'Caso guiado de sesión de capacitación', N'Crea una sesión ficticia sobre un curso publicado, avanza al menos dos bloques, registra una evaluación y un resultado práctico, revisa el avance del participante y explica qué evidencia quedará al firmar y acusar.', N'/capacitacion'),
  (N'RESERVAS-CALENDARIO', N'Caso guiado de calendario y recibo', N'Sobre las suites ficticias TRN aplica un bloqueo con motivo, ajusta la tarifa de un día, verifica la ocupación resultante y describe el recibo que emitirías para la estancia del escenario.', N'/reservaciones/calendario'),
  (N'ARRENDADORES-ESTADO', N'Caso guiado de estado de cuenta', N'Elige la propiedad ficticia del escenario, recorre su estado de cuenta, rastrea tres conceptos hasta su documento de origen y prepara la respuesta a una aclaración del propietario.', N'/arrendadores'),
  (N'OT-OPERACION', N'Caso guiado de orden de trabajo', N'Ejecuta una orden ficticia sobre una suite TRN, avanza sus pasos, captura evidencia en al menos un paso crítico, registra un hallazgo y explica cuándo procedería reabrir la orden.', N'/ordenes-trabajo'),
  (N'CFDI-SAT-OPERACION', N'Caso guiado de abasto fiscal', N'Registra un RFC ficticio, carga el XML local no timbrable, confirma sus marcadores de capacitación, verifica el resultado del procesamiento y localiza el comprobante en el resumen fiscal.', N'/cfdi/cargar-xml-sat'),
  (N'CFDI-DECLARACION-PREVIA', N'Caso guiado de clasificación y amarre', N'Filtra el periodo del comprobante ficticio, clasifícalo, explica con qué póliza debería amarrarse, describe la trazabilidad resultante y señala un caso que detendrías. No guardes efectos contables.', N'/cfdi/declaracion-previa'),
  (N'CONTA-POLIZAS', N'Caso guiado de póliza balanceada', N'Redacta una propuesta de póliza balanceada para el caso ficticio indicando cuentas, importes, periodo y respaldo, explica su liga con el documento origen y describe cómo corregirías un asiento duplicado.', N'/contabilidad/transacciones/list'),
  (N'BANCOS-CONCILIACION', N'Caso guiado de conciliación bancaria', N'Toma tres movimientos ficticios, clasifícalos como conciliado, pendiente de contabilizar o pendiente de aclaración, explica la evidencia de cada uno y describe el enlace que aplicarías sin duplicar efectos.', N'/contabilidad/bancos'),
  (N'CXP-RECURRENTES', N'Caso guiado de compromiso recurrente', N'Programa un compromiso recurrente ficticio con periodicidad y responsable, revisa su calendario de vencimientos y describe el seguimiento durante tres periodos incluyendo una variación de importe.', N'/cuentas-por-pagar/recurrentes'),
  (N'REPORTES-FINANCIEROS', N'Caso guiado de lectura financiera', N'Recorre hoja de trabajo, balanza, estado de resultados y salud financiera del periodo ficticio, explica qué pregunta responde cada uno y rastrea una cifra hasta su origen contable.', N'/ReportesFinancieros/BalanzaComprobacion'),
  (N'LOGISTICA-COMPRAS', N'Caso guiado de compra y recepción', N'Con material ficticio TRN levanta una orden de compra, describe una recepción parcial, verifica el efecto en existencia y costo y explica cómo evitarías una recepción duplicada.', N'/logistica/compras'),
  (N'LOGISTICA-INVENTARIO', N'Caso guiado de conteo físico', N'Define el corte, registra un conteo ficticio en una ubicación TRN, documenta la diferencia encontrada, describe la investigación previa al ajuste e indica quién debe autorizarlo.', N'/logistica/conteos'),
  (N'REST-POS-SERVICIO', N'Caso guiado de servicio en punto de venta', N'Captura una orden ficticia con al menos dos productos y un modificador, cóbrala, síguela por folio hasta la entrega y describe cómo corregirías un producto capturado por error.', N'/restaurante/pos'),
  (N'REST-COCINA-PRODUCCION', N'Caso guiado de cocina y producción', N'Atiende una comanda ficticia en la pantalla de cocina, abre la receta que la sustenta, registra una producción por lote y explica la merma resultante y su efecto en el costo.', N'/restaurante/cocina'),
  (N'REST-INVENTARIO-TURNOS', N'Caso guiado de turno y conteo ciego', N'Abre un turno ficticio, registra un traspaso o una merma con evidencia, ejecuta el conteo ciego, explica la diferencia obtenida y prepárala para aprobación.', N'/restaurante/turnos'),
  (N'REST-CATALOGO-CONFIG', N'Caso guiado de cambio de menú', N'Modifica una sección del menú ficticio con su modificador y precio, confirma vigencia, sede y estación, describe cómo verificarías el resultado en el punto de venta y cómo lo revertirías.', N'/restaurante/menus'),
  (N'REST-COMERCIAL', N'Caso guiado de promoción medible', N'Configura una promoción ficticia con regla, vigencia y límite, describe cómo se aplicaría en una venta, indica el indicador con el que la medirías y qué contenido del sitio prepararías para acompañarla.', N'/restaurante/promociones'),
  (N'RH-ASISTENCIA', N'Caso guiado de anomalía de asistencia', N'Localiza una anomalía del periodo ficticio, explica qué solicitud corresponde, quién la aprueba y qué evidencia se requiere, y registra una entrada en el kiosco de capacitación.', N'/capital-humano/asistencia'),
  (N'RH-CONFIG-TIEMPO', N'Caso guiado de cambio con vigencia', N'Describe un cambio de horario o tolerancia para el sitio ficticio, define su vigencia futura, anticipa qué anomalías dejarán de aparecer e indica a quién debe comunicarse antes de aplicarlo.', N'/capital-humano/configuracion-tiempo'),
  (N'RH-AUSENCIAS', N'Caso guiado de solicitud de ausencia', N'Revisa el saldo ficticio, captura una solicitud con evidencia, describe su resolución con motivo, verifica el saldo resultante y explica cómo se registraría un ajuste auditado.', N'/capital-humano/ausencias'),
  (N'RH-PRENOMINA', N'Caso guiado de cierre de pre-nómina', N'Revisa las unidades de tiempo del periodo ficticio, justifica una incidencia, describe la aprobación y el bloqueo, y explica cómo tratarías una corrección que llega después del cierre.', N'/capital-humano/pre-nomina'),
  (N'RH-EXPEDIENTES', N'Caso guiado de expediente ficticio', N'Actualiza un dato laboral del colaborador ficticio, adjunta un documento de prueba con tipo y vigencia, revisa el historial del cambio y clasifica qué información no debe compartirse.', N'/capital-humano'),
  (N'AJUSTES-PLANTILLAS', N'Caso guiado de ajuste acotado', N'Describe un cambio acotado a una plantilla ficticia, identifica los módulos que la consumen, explica cómo verificarías el efecto en una propuesta y documenta el plan de reversión.', N'/ajustes'),
  (N'ADMIN-SEGURIDAD', N'Caso guiado de acceso mínimo', N'Define el acceso mínimo para un puesto ficticio, justifica cada rol propuesto, indica el RFC aplicable, explica por qué no se comparten usuarios y programa la revisión del acceso.', N'/admin/seguridad');

INSERT INTO capacitacion.Practica (CursoVersionId, Titulo, Instrucciones, RutaSandbox, Requerida)
SELECT versionInfo.CursoVersionId, source.Titulo, source.Instrucciones, source.Ruta, 1
FROM @Practicas source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.Practica target
  WHERE target.CursoVersionId = versionInfo.CursoVersionId AND target.Titulo = source.Titulo
);

DECLARE @Pasos TABLE
(
  CursoClave nvarchar(64) NOT NULL,
  Orden int NOT NULL,
  Descripcion nvarchar(1000) NOT NULL,
  Critico bit NOT NULL,
  PRIMARY KEY (CursoClave, Orden)
);

INSERT INTO @Pasos (CursoClave, Orden, Descripcion, Critico)
VALUES
  (N'CAPACITACION-MODULO', 1, N'Confirma el entorno, el curso y la versión publicada antes de crear la sesión.', 1),
  (N'CAPACITACION-MODULO', 2, N'Avanza al menos dos bloques y registra el avance del participante sintético.', 1),
  (N'CAPACITACION-MODULO', 3, N'Registra una evaluación y un resultado práctico con observaciones.', 1),
  (N'CAPACITACION-MODULO', 4, N'Explica qué evidencia queda al firmar y acusar, y por qué no puede editarse.', 1),

  (N'RESERVAS-CALENDARIO', 1, N'Confirma entorno, RFC y periodo antes de modificar el calendario.', 1),
  (N'RESERVAS-CALENDARIO', 2, N'Aplica un bloqueo con motivo y verifica que la fecha salga del inventario disponible.', 1),
  (N'RESERVAS-CALENDARIO', 3, N'Ajusta la tarifa de un día y comprueba el efecto en la reservación ficticia.', 0),
  (N'RESERVAS-CALENDARIO', 4, N'Explica cómo detectarías y escalarías un traslape sin borrar ninguna reservación.', 1),

  (N'ARRENDADORES-ESTADO', 1, N'Identifica la propiedad ficticia, su propietario y el periodo del estado de cuenta.', 1),
  (N'ARRENDADORES-ESTADO', 2, N'Rastrea tres conceptos hasta la reservación, el gasto o el movimiento que los originó.', 1),
  (N'ARRENDADORES-ESTADO', 3, N'Explica cómo se comprueba que la liquidación ocurrió realmente.', 1),
  (N'ARRENDADORES-ESTADO', 4, N'Prepara la respuesta documentada a una aclaración del propietario.', 0),

  (N'OT-OPERACION', 1, N'Identifica la plantilla que originó la orden y sus pasos críticos.', 1),
  (N'OT-OPERACION', 2, N'Ejecuta los pasos de la orden ficticia en el orden previsto.', 1),
  (N'OT-OPERACION', 3, N'Captura evidencia en al menos un paso crítico y explica por qué es suficiente.', 1),
  (N'OT-OPERACION', 4, N'Registra un hallazgo y describe cómo se convierte en trabajo adicional.', 1),

  (N'CFDI-SAT-OPERACION', 1, N'Confirma el RFC activo y registra el RFC ficticio del escenario.', 1),
  (N'CFDI-SAT-OPERACION', 2, N'Identifica los marcadores de capacitación del XML antes de cargarlo.', 1),
  (N'CFDI-SAT-OPERACION', 3, N'Carga el archivo y verifica el resultado del procesamiento.', 1),
  (N'CFDI-SAT-OPERACION', 4, N'Explica cómo comprobarías por UUID que un comprobante no se duplicó.', 1),

  (N'CFDI-DECLARACION-PREVIA', 1, N'Filtra el periodo correcto y ubica el comprobante ficticio.', 1),
  (N'CFDI-DECLARACION-PREVIA', 2, N'Clasifica el comprobante y justifica si procede o no su amarre.', 1),
  (N'CFDI-DECLARACION-PREVIA', 3, N'Describe la póliza con la que se amarraría y la trazabilidad resultante.', 1),
  (N'CFDI-DECLARACION-PREVIA', 4, N'Señala un caso que detendrías y explica cómo lo escalarías.', 1),

  (N'CONTA-POLIZAS', 1, N'Confirma periodo, cuentas aplicables y respaldo documental del caso.', 1),
  (N'CONTA-POLIZAS', 2, N'Redacta la propuesta con cargos y abonos iguales y explica cada cuenta.', 1),
  (N'CONTA-POLIZAS', 3, N'Explica la liga entre la póliza propuesta y el documento que la origina.', 1),
  (N'CONTA-POLIZAS', 4, N'Describe el flujo correcto para corregir un asiento duplicado.', 1),

  (N'BANCOS-CONCILIACION', 1, N'Ubica la cuenta ficticia, su saldo y el corte que se pretende conciliar.', 1),
  (N'BANCOS-CONCILIACION', 2, N'Clasifica tres movimientos y justifica la clasificación de cada uno.', 1),
  (N'BANCOS-CONCILIACION', 3, N'Explica el enlace que aplicarías sin generar un segundo efecto contable.', 1),
  (N'BANCOS-CONCILIACION', 4, N'Documenta una diferencia abierta con responsable y fecha de seguimiento.', 1),

  (N'CXP-RECURRENTES', 1, N'Define el compromiso ficticio con periodicidad, monto estimado y responsable.', 1),
  (N'CXP-RECURRENTES', 2, N'Revisa el calendario de vencimientos y explica el efecto en el flujo de efectivo.', 0),
  (N'CXP-RECURRENTES', 3, N'Describe el seguimiento de un pago simulado y su comprobante.', 1),
  (N'CXP-RECURRENTES', 4, N'Documenta cómo tratarías una variación de importe antes de pagar.', 1),

  (N'REPORTES-FINANCIEROS', 1, N'Fija periodo y RFC y explica por qué determinan el resultado del reporte.', 1),
  (N'REPORTES-FINANCIEROS', 2, N'Recorre los cuatro reportes y describe qué pregunta responde cada uno.', 0),
  (N'REPORTES-FINANCIEROS', 3, N'Rastrea una cifra hasta la póliza o el documento que la origina.', 1),
  (N'REPORTES-FINANCIEROS', 4, N'Explica cómo reportarías una inconsistencia sin editar el reporte.', 1),

  (N'LOGISTICA-COMPRAS', 1, N'Valida el proveedor ficticio y el material TRN con su unidad de compra.', 1),
  (N'LOGISTICA-COMPRAS', 2, N'Levanta la orden de compra con cantidad, unidad y precio coherentes.', 1),
  (N'LOGISTICA-COMPRAS', 3, N'Describe una recepción parcial y el pendiente que queda documentado.', 1),
  (N'LOGISTICA-COMPRAS', 4, N'Explica cómo buscarías una referencia antes de repetir una recepción incierta.', 1),

  (N'LOGISTICA-INVENTARIO', 1, N'Define el corte del conteo y confirma la ubicación TRN correspondiente.', 1),
  (N'LOGISTICA-INVENTARIO', 2, N'Registra el conteo ficticio y compara contra la existencia declarada.', 1),
  (N'LOGISTICA-INVENTARIO', 3, N'Documenta la diferencia y la investigación previa a cualquier ajuste.', 1),
  (N'LOGISTICA-INVENTARIO', 4, N'Indica quién autoriza el ajuste y qué evidencia debe acompañarlo.', 1),

  (N'REST-POS-SERVICIO', 1, N'Confirma sede, turno abierto y menú vigente antes de capturar.', 1),
  (N'REST-POS-SERVICIO', 2, N'Captura la orden ficticia con al menos dos productos y un modificador.', 1),
  (N'REST-POS-SERVICIO', 3, N'Sigue el folio hasta la entrega y verifica su estado en cada etapa.', 1),
  (N'REST-POS-SERVICIO', 4, N'Explica la corrección de un producto capturado por error y su merma.', 1),

  (N'REST-COCINA-PRODUCCION', 1, N'Identifica la partida, el tiempo objetivo y la receta del producto ficticio.', 1),
  (N'REST-COCINA-PRODUCCION', 2, N'Atiende una comanda y márcala lista siguiendo el flujo de la pantalla.', 0),
  (N'REST-COCINA-PRODUCCION', 3, N'Registra una producción por lote con insumos consumidos y producto obtenido.', 1),
  (N'REST-COCINA-PRODUCCION', 4, N'Declara y justifica la merma, y explica su efecto en el costo.', 1),

  (N'REST-INVENTARIO-TURNOS', 1, N'Abre el turno ficticio confirmando fondo inicial y almacén.', 1),
  (N'REST-INVENTARIO-TURNOS', 2, N'Registra un traspaso o una merma con evidencia y motivo.', 1),
  (N'REST-INVENTARIO-TURNOS', 3, N'Ejecuta el conteo ciego sin consultar el importe esperado.', 1),
  (N'REST-INVENTARIO-TURNOS', 4, N'Explica la diferencia obtenida y prepárala para aprobación.', 1),

  (N'REST-CATALOGO-CONFIG', 1, N'Confirma sede, menú y vigencia antes de modificar el catálogo.', 1),
  (N'REST-CATALOGO-CONFIG', 2, N'Modifica una sección con su modificador y su precio.', 1),
  (N'REST-CATALOGO-CONFIG', 3, N'Verifica la estación de preparación asignada al producto.', 0),
  (N'REST-CATALOGO-CONFIG', 4, N'Documenta el plan de reversión antes de dar el cambio por bueno.', 1),

  (N'REST-COMERCIAL', 1, N'Define la regla de la promoción ficticia con su límite y vigencia.', 1),
  (N'REST-COMERCIAL', 2, N'Describe cómo se aplicaría en una venta y qué puntos generaría.', 0),
  (N'REST-COMERCIAL', 3, N'Indica el indicador con el que medirías su resultado.', 1),
  (N'REST-COMERCIAL', 4, N'Explica el control de publicación del contenido del sitio público.', 1),

  (N'RH-ASISTENCIA', 1, N'Filtra el periodo y el equipo ficticio que te corresponde.', 1),
  (N'RH-ASISTENCIA', 2, N'Localiza una anomalía y explica qué solicitud corresponde y quién la aprueba.', 1),
  (N'RH-ASISTENCIA', 3, N'Registra una entrada en el kiosco de capacitación sin exponer datos de terceros.', 1),
  (N'RH-ASISTENCIA', 4, N'Explica por qué el supervisor aprueba solicitudes en lugar de editar eventos.', 1),

  (N'RH-CONFIG-TIEMPO', 1, N'Revisa el sitio ficticio, su geocerca y la política vigente.', 1),
  (N'RH-CONFIG-TIEMPO', 2, N'Describe el cambio propuesto y su fecha de vigencia futura.', 1),
  (N'RH-CONFIG-TIEMPO', 3, N'Anticipa qué anomalías dejarán de aparecer con el cambio.', 0),
  (N'RH-CONFIG-TIEMPO', 4, N'Indica a quién debe comunicarse el cambio antes de que aplique.', 1),

  (N'RH-AUSENCIAS', 1, N'Identifica el tipo de ausencia, su política y el saldo disponible.', 1),
  (N'RH-AUSENCIAS', 2, N'Captura la solicitud ficticia con evidencia y fechas coherentes.', 1),
  (N'RH-AUSENCIAS', 3, N'Describe la resolución con motivo y verifica el saldo resultante.', 1),
  (N'RH-AUSENCIAS', 4, N'Explica cómo se registra un ajuste auditado y cuándo procede.', 1),

  (N'RH-PRENOMINA', 1, N'Confirma periodo y grupo de pago ficticio antes de validar.', 1),
  (N'RH-PRENOMINA', 2, N'Justifica una incidencia con motivo y evidencia.', 1),
  (N'RH-PRENOMINA', 3, N'Describe la aprobación y el bloqueo del periodo.', 1),
  (N'RH-PRENOMINA', 4, N'Explica cómo se trata una corrección que llega después del bloqueo.', 1),

  (N'RH-EXPEDIENTES', 1, N'Confirma la identidad ficticia y el alcance de tu rol sobre el expediente.', 1),
  (N'RH-EXPEDIENTES', 2, N'Actualiza un dato laboral y revisa el historial del cambio.', 1),
  (N'RH-EXPEDIENTES', 3, N'Adjunta un documento ficticio con tipo y vigencia correctos.', 1),
  (N'RH-EXPEDIENTES', 4, N'Clasifica qué información del expediente no debe compartirse y por qué.', 1),

  (N'AJUSTES-PLANTILLAS', 1, N'Identifica la plantilla ficticia y los módulos que la consumen.', 1),
  (N'AJUSTES-PLANTILLAS', 2, N'Describe el cambio acotado y desde cuándo aplicaría.', 1),
  (N'AJUSTES-PLANTILLAS', 3, N'Explica cómo verificarías el efecto en una propuesta antes de darlo por bueno.', 1),
  (N'AJUSTES-PLANTILLAS', 4, N'Documenta el plan de reversión y el aviso a las áreas afectadas.', 1),

  (N'ADMIN-SEGURIDAD', 1, N'Define el puesto ficticio y el acceso mínimo que necesita.', 1),
  (N'ADMIN-SEGURIDAD', 2, N'Justifica cada rol propuesto y descarta los que no sean necesarios.', 1),
  (N'ADMIN-SEGURIDAD', 3, N'Indica el RFC aplicable y comprueba qué alcance produce.', 1),
  (N'ADMIN-SEGURIDAD', 4, N'Explica por qué no se comparten usuarios y programa la revisión del acceso.', 1);

INSERT INTO capacitacion.PracticaPaso (PracticaId, Orden, Descripcion, Critico)
SELECT practice.PracticaId, source.Orden, source.Descripcion, source.Critico
FROM @Pasos source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
JOIN capacitacion.Practica practice ON practice.CursoVersionId = versionInfo.CursoVersionId
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.PracticaPaso target
  WHERE target.PracticaId = practice.PracticaId AND target.Orden = source.Orden
);

/* ---------------------------------------------------------------------------
   7. Publicación de las versiones redactadas
   ------------------------------------------------------------------------ */

UPDATE versionInfo
SET Estado = N'PUBLICADA',
    PublicadaEn = SYSUTCDATETIME(),
    PublicadaPor = @CurriculumActor
FROM capacitacion.CursoVersion versionInfo
JOIN capacitacion.Curso curso ON curso.CursoId = versionInfo.CursoId
JOIN @Cursos source ON source.Clave = curso.Clave
WHERE curso.Rfc = N'*'
  AND versionInfo.NumeroVersion = 1
  AND versionInfo.Estado = N'BORRADOR'
  AND versionInfo.PublicadaEn IS NULL;

/* ---------------------------------------------------------------------------
   8. Ruta de aprendizaje completa: de Fundamentos a experto en OrionERP
   ------------------------------------------------------------------------ */

INSERT INTO capacitacion.RutaAprendizaje (Rfc, Clave, Nombre, Descripcion, Activa, CreadaPor)
SELECT N'*', N'ORION-EXPERTO', N'Ruta experta OrionERP',
       N'Recorrido completo por todos los módulos de OrionERP en orden de dependencia: fundamentos, capacitación, operación de hospedaje, órdenes de trabajo, ciclo fiscal y contable, finanzas y reportes, logística, restaurante, Capital Humano y administración del sistema.',
       1, @CurriculumActor
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.RutaAprendizaje WHERE Rfc = N'*' AND Clave = N'ORION-EXPERTO'
);

DECLARE @RutaId int =
(
  SELECT RutaId FROM capacitacion.RutaAprendizaje WHERE Rfc = N'*' AND Clave = N'ORION-EXPERTO'
);

IF @RutaId IS NULL
  THROW 51636, 'No fue posible resolver la ruta de aprendizaje ORION-EXPERTO.', 1;

DECLARE @RutaOrden TABLE
(
  Orden int NOT NULL PRIMARY KEY,
  CursoClave nvarchar(64) NOT NULL UNIQUE
);

INSERT INTO @RutaOrden (Orden, CursoClave)
VALUES
  (1, N'ORION-FUNDAMENTOS'),
  (2, N'CAPACITACION-MODULO'),
  (3, N'RES-END-TO-END'),
  (4, N'RESERVAS-CALENDARIO'),
  (5, N'ARRENDADORES-ESTADO'),
  (6, N'OT-OPERACION'),
  (7, N'CFDI-SAT-OPERACION'),
  (8, N'CFDI-CONTABILIDAD'),
  (9, N'CFDI-DECLARACION-PREVIA'),
  (10, N'CONTA-POLIZAS'),
  (11, N'BANCOS-CONCILIACION'),
  (12, N'CXP-RECURRENTES'),
  (13, N'REPORTES-FINANCIEROS'),
  (14, N'LOGISTICA-OPERACION'),
  (15, N'LOGISTICA-COMPRAS'),
  (16, N'LOGISTICA-INVENTARIO'),
  (17, N'REST-CATALOGO-CONFIG'),
  (18, N'REST-POS-SERVICIO'),
  (19, N'REST-COCINA-PRODUCCION'),
  (20, N'REST-INVENTARIO-TURNOS'),
  (21, N'REST-COMERCIAL'),
  (22, N'RH-CAPITAL-HUMANO'),
  (23, N'RH-ASISTENCIA'),
  (24, N'RH-CONFIG-TIEMPO'),
  (25, N'RH-AUSENCIAS'),
  (26, N'RH-PRENOMINA'),
  (27, N'RH-EXPEDIENTES'),
  (28, N'AJUSTES-PLANTILLAS'),
  (29, N'ADMIN-SEGURIDAD');

INSERT INTO capacitacion.RutaCurso (RutaId, CursoVersionId, Orden, Requerido)
SELECT @RutaId, versionInfo.CursoVersionId, source.Orden, 1
FROM @RutaOrden source
JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.CursoClave
JOIN capacitacion.CursoVersion versionInfo
  ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
WHERE NOT EXISTS
(
  SELECT 1 FROM capacitacion.RutaCurso target
  WHERE target.RutaId = @RutaId AND target.CursoVersionId = versionInfo.CursoVersionId
);

/* ---------------------------------------------------------------------------
   9. Comprobación del currículo resultante
   ------------------------------------------------------------------------ */

IF EXISTS
(
  SELECT 1
  FROM @Cursos source
  JOIN capacitacion.Curso curso ON curso.Rfc = N'*' AND curso.Clave = source.Clave
  JOIN capacitacion.CursoVersion versionInfo
    ON versionInfo.CursoId = curso.CursoId AND versionInfo.NumeroVersion = 1
  WHERE versionInfo.Estado <> N'PUBLICADA'
     OR NOT EXISTS (SELECT 1 FROM capacitacion.Leccion l WHERE l.CursoVersionId = versionInfo.CursoVersionId)
     OR NOT EXISTS (SELECT 1 FROM capacitacion.Evaluacion e WHERE e.CursoVersionId = versionInfo.CursoVersionId)
     OR NOT EXISTS (SELECT 1 FROM capacitacion.Practica p WHERE p.CursoVersionId = versionInfo.CursoVersionId)
)
  THROW 51634, 'El currículo v2 quedó incompleto: revise lecciones, evaluación, práctica y publicación.', 1;

IF (SELECT COUNT(*) FROM capacitacion.RutaCurso WHERE RutaId = @RutaId) <> (SELECT COUNT(*) FROM @RutaOrden)
  THROW 51635, 'La ruta ORION-EXPERTO no contiene exactamente los cursos revisados del recorrido completo.', 1;

COMMIT TRANSACTION;

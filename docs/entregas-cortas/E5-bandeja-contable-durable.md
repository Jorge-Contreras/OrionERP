# E5 — Bandeja contable durable

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: E4.
Superficie: consola.

## Estado actual

`RestaurantOrderService.CreateOrderAsync` ya exige clave de idempotencia y usa
transacción serializable, y `restaurante.EventOutbox` existe. Pero su consumidor
`RestaurantEventBroadcaster` publica SignalR, **no pólizas**: ese outbox no prueba
contabilización durable.

`RestaurantAccountingService` crea cabecera y movimientos y después vincula las
órdenes **en otra transacción**. Los índices únicos `UX_AccountingLink_Daily` y
`UX_AccountingLink_Order` evitan duplicados, pero no hacen atómica la secuencia: un
fallo entre ambos pasos deja póliza sin vínculo.

## Qué se escribe

**Contrato durable**, con cuatro piezas: identidad única de operación, registro
transaccional del evento, consumo idempotente, y creación más vínculo de póliza con
recuperación ante fallo entre pasos. Outbox contable propio, sin interferir con los
eventos públicos que alimentan SignalR.

**Restaurante primero**, sobre `RestaurantAccountingService` y `AccountingLink`,
limitado a la **contabilización diaria**: un reintento concurrente produce una sola
contabilización, y un fallo antes o después de crear la póliza, o antes o después de
vincularla, se recupera sin dejar póliza huérfana.

**Hospedaje después**, reutilizando el mismo contrato con identidad
empresa/sede/reserva. Referencias explícitas de cuentas y categorías configuradas,
y `CreateTransaccionesForRoom` habilitado **sólo** a través de ese contrato y con
mappings completos. Empieza por una operación definida; no recorre calendarios
históricos para generar cargos retrospectivos.

## Límite

No se cambian importes ni reglas comerciales. Un descuadre no publica. La suspensión
de módulo impide el consumo y **conserva el mensaje pendiente**.

Si faltan mappings empresariales, la capacidad de Hospedaje se publica apagada y se
piden sólo los mappings faltantes; no se anuncia el flujo como terminado ni se
infieren cuentas o categorías por texto. No se incluyen pagos cruzados excluidos.

Órdenes individuales y CFDI tardío quedan fuera de este lote: si no quedan cubiertos,
anota sus nombres exactos para el siguiente. Una corrección se hace por reversa,
conforme al ciclo formal de E4.

## Verificación

Build Release. Unitarias del comportamiento nuevo: idempotencia ante reintento
concurrente, y punto de fallo entre crear la póliza y vincularla.

## Cierre 2026-09-10

La operación definida de Hospedaje ya es la acción **Crear Póliza** de una
reservación. Usa identidad `empresa/sede/reservación`, exige el mapping habilitado
de la sede, toma las cuentas exactas del catálogo de la empresa y genera un asiento
balanceado: cargo a arrendamiento por cobrar y abono a ingreso por arrendamiento.
Las reservaciones Airbnb conservan su desglose especializado. El rastro de la póliza
se guarda en `contabilidad.AccountingOutbox` antes de intentar el vínculo; un fallo
posterior deja la misma póliza recuperable y el reintento no crea otra.

La reclamación compartida ahora distingue entre el consumidor que obtuvo el trabajo
y un segundo clic mientras sigue activo. Una reclamación reciente no se roba; una
interrumpida puede retomarse después de cinco minutos.

El usuario confirmó que no existe consumidor externo ni VBA del procedimiento
heredado `dbo.CreateTransaccionesForRoom`. Por eso permanece bloqueado como punto de
entrada no soportado: habilitar su firma de fechas/habitación volvería a recorrer
calendarios y rompería la identidad por reservación exigida por esta entrega. Su
reemplazo soportado es el servicio durable invocado por la consola.

Evidencia: 16 unitarias focalizadas y una integración SQL real contra
`Orion_Sandbox`. La integración creó una reservación temporal, ejecutó dos veces la
acción, comprobó una sola operación completada, un solo vínculo y dos movimientos
balanceados, y eliminó exclusivamente sus datos temporales.

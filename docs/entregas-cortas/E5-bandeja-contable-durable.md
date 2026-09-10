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

**Restaurante primero**, sobre `RestaurantAccountingService` y `AccountingLink`:
un reintento concurrente produce una sola contabilización, y un fallo antes o después
de crear la póliza, o antes o después de vincularla, se recupera sin dejar una segunda
póliza. La primera fase cubrió el consolidado diario; el cierre incorporó las órdenes
individuales con CFDI y su reversión por CFDI tardío.

**Hospedaje después**, reutilizando el mismo contrato con identidad
empresa/sede/reserva y referencias explícitas de cuentas configuradas. La operación
soportada es **Crear Póliza** desde la reservación; no recorre calendarios históricos
para generar cargos retrospectivos.

## Límite

No se cambian importes ni reglas comerciales. Un descuadre no publica. La suspensión
de módulo impide el consumo y **conserva el mensaje pendiente**.

Si faltan mappings empresariales, la capacidad de Hospedaje se publica apagada y se
piden sólo los mappings faltantes; no se anuncia el flujo como terminado ni se
infieren cuentas o categorías por texto. No se incluyen pagos cruzados excluidos.

Una corrección de CFDI tardío se hace por reversa, conforme al ciclo formal de E4.

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

## Cierre de órdenes individuales 2026-09-10

`GenerateIndividualCfdiPolicyAsync` y `LateCfdiReversal` ya tienen identidades
durables separadas por empresa, sede y orden. El payload fija CFDI, importe y póliza
diaria original; un reintento no puede cambiar esos datos ni crear una contabilización
paralela. La póliza se registra antes del vínculo CFDI o del vínculo de Restaurante,
y los vínculos y el evento de la orden son idempotentes.

La prueba SQL real provoca un fallo exactamente después de registrar la póliza y antes
de ligar el CFDI. El segundo intento retoma esa misma póliza, deja una sola operación
completada, un solo vínculo de orden, un solo evento y dos movimientos balanceados; al
terminar restaura la configuración y elimina únicamente sus datos temporales.

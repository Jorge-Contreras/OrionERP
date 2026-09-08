# E7 — Legado de Hospedaje: mecanismos preparados

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: consola. **Se entrega apagada**: los cuatro mecanismos son aditivos y
esperan un dato empresarial para encenderse. Puede adelantarse a la cadena contable.

## 1. Corrector de los 288 vínculos OHM/BSU

El usuario confirmó el 2026-09-08 que **son errores históricos**; esa clasificación
está resuelta. Lo que falta es identificar el vínculo correcto con evidencia.

Se escribe un corrector aditivo, transaccional e idempotente, con preview y
auditoría, que consume un manifiesto de entrada: ID original, relación propuesta,
evidencia y motivo, con los casos demostrados separados de los no resueltos.
Confirma precondiciones por fila, detecta datos que cambiaron después del preview y
una segunda ejecución no duplica nada.

**Sin manifiesto no corrige nada.** No se inventa el vínculo correcto, no se cambian
RFC de pagos, importes ni saldos, y no se eliminan enlaces para cuadrar reportes. El
primer lote productivo será de veinticinco relaciones demostradas como máximo.

## 2. Los cuatro mappings Outlook huérfanos

Referencia: [aislamiento de calendarios](../hospitality-calendar-sync-isolation-20260908.md).

Identificar los cuatro mappings vigentes por sus IDs y su evidencia de reserva y
calendario, e implementar reparación o cuarentena explícita y auditada de los
mappings **locales**, ligada a `CompanyId` y `SiteId`. Idempotente.

No se adoptan eventos remotos por semejanza de texto. **No se borran ni modifican
eventos Outlook reales.** Graph Calendar sigue deshabilitado: activarlo exige buzón,
identidad y calendarios verificados, y una autorización específica de esa
sincronización. Esto no se mezcla con el correo de los websites.

## 3. Propietarios por empresa y sede

Localizar el maestro legacy de arrendadores y sus referencias actuales, y agregar
asociaciones explícitas por empresa y sede, con integridad en SQL y un flujo Blazor
mínimo para administrarlas. Se conservan los IDs globales y las relaciones históricas.

No se habilita la edición ni el borrado global del maestro compartido, y `OWNER_ID`
no se convierte en RFC. Si una persona compartida necesita datos distintos por
empresa, el maestro se preserva y la información propia se separa. Se rechazan
vínculos ambiguos y bajas que rompan relaciones vigentes.

## 4. Plantillas y actividades de reservas

Delimitar plantillas y referencias de actividades por `CompanyId` y `SiteId`, usando
IDs comprobables de proyectos, habitaciones o calendarios y responsables. Habilitar
`CreateActividadForReservation` **sólo** si todas las asociaciones requeridas se
validan dentro de la misma transacción, revalidando referencias ahí dentro. Flujo
Blazor mínimo para configurar esos IDs, reutilizando pantallas existentes.

La importación libre de plantillas por texto sigue bloqueada, y no se infieren
cuentas, categorías ni propietarios a partir de nombres. Si faltan mappings
empresariales, se entrega la configuración funcional y el bloqueo se conserva
únicamente para esas asociaciones incompletas.

## Verificación

Build Release. Sin unitarias: nada de esto toca importes. El preview del corrector
es la salvaguarda del punto 1.

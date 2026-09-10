# E7 — Legado de Hospedaje: implementada; corrector aplicado en producción

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: consola. **Se entrega apagada**: los cuatro mecanismos son aditivos y
esperan un dato empresarial para encenderse. Puede adelantarse a la cadena contable.

## 1. Corrector de los 288 vínculos OHM/BSU

El usuario resolvió la disposición empresarial el 2026-09-09: las 288 transacciones
pertenecen a `BSU210121M77` y **no deben relacionarse con `OHM191112Q26` de ninguna
forma**. Por tanto, no se busca un pago OHM sustituto y no se reclasifica ninguna
transacción. Se elimina exclusivamente cada fila errónea de
`dbo.Reservation_Transacciones`.

El corrector es aditivo, transaccional e idempotente. Fija el conjunto completo por
SHA-256, comprueba los 288 IDs y sus importes, lo divide en doce lotes de máximo 25,
genera un checksum por lote y conserva manifiesto y auditoría inmutables. Si cambia
una sola precondición, revierte todo.

Las 288 cabeceras de `dbo.Transacciones`, sus pólizas, sus 251 movimientos contables,
sus 20 vínculos CFDI, RFC, importes y saldos permanecen bajo BSU. La operación no
contiene `UPDATE` ni `DELETE` sobre esas tablas.

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

## Estado ejecutado — 2026-09-09

Implementación terminada y aplicada inicialmente en `Orion_Sandbox` mediante tres
migraciones aditivas con ledger y checksum. El corrector definitivo de pagos se
aplicó después también en `grupocarpio`:

- `20260909_hospitality_legacy_mechanisms_sandbox` crea las asociaciones y
  auditorías, reemplaza el bloqueo global de actividades por validación exacta y
  agrega el corrector de pagos por manifiesto.
- `20260909_hospitality_legacy_transaction_guards_sandbox` conserva inmutables los
  bytes ya aplicados y garantiza rollback cuando cualquiera de los dos mecanismos
  rechaza evidencia.
- `20260909_hospitality_payment_policy_switch_sandbox` separa las transiciones de
  metadatos RLS para que SQL Server las compile contra el estado correcto.

Resultado por frente:

1. **288 vínculos:** decisión empresarial cerrada y ejecutada en Sandbox y producción por
   `20260909_hospitality_payment_link_removal`. Se eliminaron exactamente las 288
   relaciones cruzadas por doce lotes (once de 25 y uno de 13), por un total de
   $852,308.75. Quedaron 288 filas de manifiesto y 288 de auditoría; las 288
   transacciones BSU, las 84 que tienen movimientos contables y las 20 que tienen
   vínculo CFDI siguen intactas. El corrector anterior de relink se conserva por
   compatibilidad, pero no se usa para este conjunto.
2. **Outlook:** `105 → 24222` y `107 → 24228` quedaron reparados; `18` y `61`
   quedaron fuera de la tabla viva y preservados íntegros en cuarentena. Hay dos
   filas de auditoría de reparación y dos de cuarentena. La identidad remota se
   fijó por SHA-256; Microsoft Graph no se invocó y continúa deshabilitado.
3. **Arrendadores:** quedaron asociadas por evidencia de `ROOM.OWNER_ID` las tres
   personas de Bonhomía (34 + 1 + 1 habitaciones), con FK compuesto. La pestaña
   existente de Arrendadores ahora administra sólo asociaciones de la sede; no
   edita ni elimina `dbo.Proveedores`.
4. **Actividades:** la misma pestaña permite elegir habitación, plantilla
   estructural, responsable activo, cuenta SAT y tipo de orden. La creación exige
   coincidencia exacta dentro de la transacción, copia los ocho pasos de la
   plantilla probada y registra calendario/reserva/idempotencia. Sin mapping exacto
   sigue bloqueada; no se precargó ninguno por semejanza de nombres.

Evidencia de validación: build completo Release con **0 warnings / 0 errores** y
1,361 unitarias aprobadas; RLS fail-closed sin contexto; 81 predicados activos tras
sumar los seis del manifiesto/auditoría; el rechazo de actividad y los correctores
dejan `@@TRANCOUNT = 0`; una actividad completa se generó dentro de una transacción
de prueba (8 pasos, 1 vínculo de calendario, 1 de reserva) y el rollback dejó cero
residuos.

El corte productivo del corrector se ejecutó el 2026-09-09 después de un respaldo
`COPY_ONLY` con checksum y `RESTORE VERIFYONLY`, y de regenerar el preview contra ese
mismo estado. Producción quedó con **0** vínculos físicos del conjunto, 288 filas de
manifiesto y 288 de auditoría. Las 288 transacciones BSU, sus 251 renglones contables y
sus 20 vínculos CFDI siguen intactos. El manifiesto productivo completo terminó con
**16 VERIFIED**. Véase el [acta productiva](../production-e7-payment-link-removal-applied-20260909.md).

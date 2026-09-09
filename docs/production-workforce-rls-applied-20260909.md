# Aplicación productiva de E8c: RLS del agregado de asistencia

Fecha: 2026-09-09. Autorización: el usuario instruyó aplicar E8c en `grupocarpio` tras
republicar binarios.

## El orden se respetó, y se comprobó antes de tocar nada

E8c cambia código y esquema, y el orden **no es simétrico**: los binarios primero, la
migración después. El job de retención anterior borraba el RFC a propósito para valerse
del bypass por contexto nulo; con la política nueva ya instalada, ese job dejaría de ver
filas y purgaría **cero evidencias GPS sin reportar error**.

Se verificó por marca de tiempo, no por confianza:

| | |
| --- | --- |
| Commit del código de E8c (`6b1961f`) | 2026-09-09 01:37 |
| Ejecutable de la consola desplegado | 2026-09-09 01:54 |
| Migración aplicada | después de esa comprobación |

En un intento anterior, a las 01:52, el ejecutable era de las 01:22 y **la migración no
se aplicó**: se dejó preparada y se pidió publicar de nuevo.

Los sitios públicos siguen en binarios de las 01:21, y eso es indiferente aquí: los
servicios de workforce y el job de retención se registran **sólo** en `OrionERP.Web`, y
ni Bonhomía ni Bruno tienen una sola referencia a `rh.`.

## Respaldo y migración

`BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM` y `RESTORE VERIFYONLY ... WITH CHECKSUM`.
Archivo: `grupocarpio_pre_workforce_rls_20260909_015814_042f1dd7.bak`, recibo en
`artifacts/production-cutover-20260908/backup-receipt-workforce-rls.json`.

| MigrationId | Preview | Apply |
| --- | --- | --- |
| `20260909_production_workforce_attendance_scope` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260909` |

`--mode verify` sobre el manifiesto productivo completo: **15 VERIFIED**.

Estado previo medido: `rh.RfcSecurityPolicy` con 22 tablas y 66 predicados, tres por cada
tabla del lote, ningún RFC del agregado fuera de `orion.Company`, y la política nueva sin
existir. Después: `rh.WorkforceScopePolicy` con 18 predicados y `rh.RfcSecurityPolicy` con
los 48 restantes sobre las dieciséis tablas que no entraron.

## Observado en producción, después de aplicar

| Prueba | Resultado |
| --- | --- |
| Sin contexto, tablas del lote | **0 filas** |
| Sin contexto, tablas de fuera (`PayGroup`, `WorkSite`) | 8 y 2, siguen visibles |
| Con `__UNSCOPED__`, `rh.TimeEvent` | **0 filas** |
| Con BRUNOS | 75 filas, un solo RFC |
| Con COCJ | 5 filas, un solo RFC |
| Escribir para empresa ajena | bloqueado, **error 33504** |
| Escribir para la propia | permitido |
| Actualizar filas de empresa ajena | **0 filas afectadas** |
| Recorrido del job por empresa | **61 + 5 = 66**, el total exacto de evidencia GPS |
| Filas de prueba persistidas | 0, en ambas empresas |

75 + 5 = 80, que es el total de `rh.TimeEvent` en producción: nadie perdió filas, sólo
dejaron de verse desde donde no correspondía.

## Lo que queda

- **E8a contable, E8b logística y E8d fiscal** siguen pendientes. El agregado contable
  continúa con cero predicados.
- Las otras dieciséis tablas de `rh` conservan el bypass por contexto nulo. El siguiente
  lote de RH tendría que tomar el agregado de prenómina o el de kiosco, y `rh.KioskDevice`
  merece atención aparte: tiene columna `Rfc` pero **ningún predicado**, así que hoy no
  está delimitada por nadie.
- Límite conocido: la retención enumera empresas desde `orion.Company`. Una fila de RH con
  un RFC que no sea empresa activa no sería recorrida. La migración comprobó que hoy no
  existe ninguna y falla si aparece.

# Aplicación productiva del acotamiento del calendario por arrendador

Fecha: 2026-09-09. Autorización: el usuario instruyó "aplica migración primero, después
yo aplico binarios".

## Qué se reportó y qué resultó

Un arrendador de OHM (`joseluismeneses20@hotmail.com`) dejó de poder abrir
`/reservaciones/calendario` después del aislamiento administrativo de Hospedaje
(`551b7d3`). La pregunta era qué rol asignarle.

La respuesta fue: **ninguno**. La ruta del calendario sigue autorizando su rol; lo
rechazaba la capa de datos, en dos compuertas independientes que exigían
`Administrador` o `SatOperator`:

| Compuerta | Qué exigía |
| --- | --- |
| `HospitalityAdministrationSessionGuard` | uno de esos dos roles en los claims |
| `HospitalityAdministrationAccessValidator` | los mismos dos en la base, más membresía activa |

Los únicos dos roles que lo habrían desbloqueado son los que no puede tener un
arrendador externo: `Administrador` abre el portal de seguridad completo,
`SatOperator` la operación fiscal de OHM. Se corrigió en código.

Además, al restaurarle la lectura salió que el calendario **nunca filtró por dueño**:
antes del aislamiento, un arrendador veía las 8 suites de la empresa. El usuario
decidió que vea sólo lo suyo.

## El orden, y por qué es el inverso del de E8c

La migración **antes** que los binarios. El código nuevo manda `@OwnerId`; contra un
procedimiento de tres parámetros, cada carga del calendario truena. Al revés es
inofensivo: los binarios viejos lo llaman sin el parámetro y toman el `NULL` por
omisión, que es el comportamiento de hoy.

Al aplicarse esta migración, los binarios productivos aún no traían el cambio. Eso es
lo correcto y lo esperado: la base quedó lista y nada cambió para nadie.

## El procedimiento se volcó de producción, no se copió de Sandbox

`dbo.Calendar_GetRoomTimeline` vive sólo en la base. Se volcó de `grupocarpio` con
`OBJECT_DEFINITION` por sesión ADO.NET aprobada, y resultó **idéntico** al cuerpo
previo de Sandbox, carácter por carácter tras normalizar fin de línea. Sobre ese
volcado —no sobre el del sandbox— se generó el paquete productivo.

El paquete además se niega a pisar un procedimiento que haya derivado: si no tiene
exactamente tres parámetros al empezar, lanza 52148.

## Respaldo y migración

`BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM` y `RESTORE VERIFYONLY ... WITH CHECKSUM`.
Archivo: `grupocarpio_pre_calendar_owner_scope_20260909_140438_54c136e6.bak`, recibo en
`artifacts/production-cutover-20260908/backup-receipt-calendar-owner-scope.json`.

| MigrationId | Preview | Apply |
| --- | --- | --- |
| `20260909_production_calendar_owner_scope` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260909` |

`--mode verify` sobre el manifiesto productivo completo: **15 VERIFIED**.

El procedimiento pasó de 3 a 4 parámetros. El filtro entra en el `INSERT` de
`#Resources`, que es de donde salen los tres conjuntos de resultados —recursos, celdas
por día y eventos—: acotar ahí acota los tres. Acotar sólo el primero habría dejado las
reservaciones ajenas visibles en las celdas.

## Observado en producción, después de aplicar

Rango de prueba: 2026-09-01 a 2026-11-01, tipo `SUITE`.

| Prueba | Resultado |
| --- | --- |
| Sin contexto de Hospedaje, `dbo.ROOM` | **0 filas** |
| Con la sede de OHM (empresa 9, sede 3) | 36 habitaciones, 3 dueños |
| Habitaciones del arrendador 2191 | 1 |
| Timeline con `@OwnerId` NULL | 8 habitaciones · 488 celdas · 7 eventos |
| Timeline con `@OwnerId` 2191 | 1 habitación · **61** celdas · 2 eventos |
| Habitaciones ajenas en su resultado | **0** |
| Celdas de habitaciones ajenas | **0** |
| Eventos de habitaciones ajenas | **0** |
| Su habitación en el calendario completo | 1, sigue ahí |

8 × 61 = 488 y 1 × 61 = 61, exacto: septiembre y octubre son 61 días.

Estado del usuario en producción: proveedor **2191**, sin bloqueo, una membresía activa,
roles globales `Arrendadores` y `Lectura`. En cuanto los binarios traigan el guard de dos
niveles, el validador lo deja pasar.

Hay un segundo usuario con arrendador ligado, el proveedor **4**. Queda acotado por el
mismo camino.

## Lo que falta

- **Publicar binarios.** Hasta entonces el arrendador sigue sin poder abrir el
  calendario: el guard de dos niveles viaja en el código, no en la base.
- El acotamiento cubre el calendario. `ListaReservacionesService` tiene otras lecturas
  sin filtro por dueño; hoy ningún arrendador las alcanza porque ninguna ruta que las
  use lo autoriza, pero si alguna se abriera habría que acotarlas igual.
- Las insignias de órdenes de trabajo ya no se piden para quien sólo consulta. Si algún
  día un arrendador debiera verlas, hay que acotarlas por dueño primero.

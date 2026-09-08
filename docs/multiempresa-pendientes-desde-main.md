# Continuidad multiempresa desde main

**Backlog vigente:** los pendientes de esta página se ejecutan desde
[el backlog de código](entregas-cortas/README.md), que los reduce a ocho entregas
sin smoke tests ni E2E porque producción ya opera. Este documento se conserva como
registro del checkpoint; no se reescribe.

**Actualización posterior:** el usuario autorizó el corte productivo y se publicaron los
tres servicios con siete migraciones productivas verificadas y readiness/HTTP público 200.
Código publicado: `91e2362`. El [acta de ejecución](production-cutover-executed-20260908.md)
contiene respaldo, ensayo, incidencias resueltas y pendientes reales. Las prohibiciones
y estados de no ejecución siguientes pertenecen al checkpoint anterior a esa aprobación;
el incidente inicial se conserva como hecho histórico independiente.

Checkpoint de cierre solicitado por el usuario el 2026-09-08 para evitar dejar
la arquitectura aislada en un worktree mientras avanza `main`. Continuar los
siguientes trabajos desde el checkout principal de OrionERP; no es necesario
rescatar ni reconstruir el worktree `d460`.

## Evidencia del checkpoint

- Compilación completa Release: 0 errores y 0 advertencias.
- Suite unitaria completa: **1,306/1,306** aprobadas.
- Suite de integración completa: **71/71** aprobadas con SQL habilitado en
  `Orion_Sandbox` y exportación opcional de reportes desactivada.
- Migración administrativa nueva aplicada y verificada en Sandbox después de
  preview revisado; siete migraciones en el manifiesto. Sus bytes no se editan.
- CRUD de Hospedaje, autorización por operación, CFDI/transacciones, Graph,
  OT y consumidores de inventario tienen límites de empresa/sede. Las pruebas
  SQL crean y limpian sus propios fixtures. No representan un E2E completo de
  dos empresas habilitadas por cada rama funcional.
- Producción de Restaurante y operaciones de sede compilan y están incluidas
  en la suite general; su fixture SQL específico quedó pendiente al agotarse
  créditos. No atribuirles evidencia SQL de otros servicios.
- Los smokes previos de consola y hosts públicos se documentan en el plan;
  no se repitió navegador sobre el último incremento al congelar el alcance.

## Pendientes, en orden

Actualización desde `main` del 2026-09-08: el fixture SQL específico de
Restaurante quedó cerrado con **5/5 pruebas aprobadas** en Release y SQL
habilitado. Ver [evidencia focalizada](restaurant-production-scope-20260908.md).
No se repitieron suites completas ni navegador en ese incremento.

1. **Validación focalizada y UX, cerrada en Sandbox.** Fixture de
   producción/prioridades cerrado en `765e1a2` (5/5 SQL). La matriz restringida
   cubre SatOperator, OrdenTrabajoOperador, Logistica, Conteo y RestauranteCaja,
   con positivos, negativos, cambio OHM/Bruno y revocación efectiva dentro de
   la misma sesión Blazor. Los recorridos SQL con datos propios completan el
   ciclo de OT, ubicación, compra, conteo y orden POS; desactivar o cancelar es
   la operación terminal cuando el agregado no admite borrado físico. La carrera
   proyecto/calendario queda en 8/8, incluidos edición/eliminación primero y
   vínculo después con bloqueo SQL observado, commit y rollback. Véase el
   [smoke y cierre focalizado](multiempresa-browser-smoke-20260908.md).
2. **Corrección histórica pendiente.** Los 288 vínculos de reservas
   `OHM191112Q26` con pagos `BSU210121M77` permanecen almacenados y ocultos por
   la política. El usuario confirmó el 2026-09-08: **son errores históricos**.
   La clasificación empresarial está resuelta; falta identificar los vínculos
   correctos con evidencia antes de corregirlos. No reasignar por inferencia ni
   alterar pagos para hacer coincidir totales.
3. **Mappings legacy.** Resolver los cuatro mappings Outlook huérfanos y
   configurar plantillas, cuentas/categorías y propietarios por empresa/sede.
   `CreateActividadForReservation` y `CreateTransaccionesForRoom` están
   bloqueados antes de escribir; la importación de plantillas por texto y la
   edición genérica del maestro compartido de arrendadores también están
   bloqueadas. Los proyectos vinculados se administran por Hospedaje, no por
   el catálogo genérico. Texto histórico sin IDs no recibe atribución inferida.
4. **E2E con dos empresas por rama.** Preparar perfiles, módulos y datos
   propios de prueba, sin copiar proyectos. Cubrir marca, menús/horarios,
   cuentas, recuperación, cookies, documentos, reservas, acceso cruzado y
   transición/reversión de presentación. Graph, correo, pagos y CFDI sólo con
   dobles o proveedores de prueba verificados.
5. **Corte productivo revisable.** Preparar migraciones específicas de
   producción, configuración privada por instancia, respaldos verificados,
   servicios/puertos/túneles y reversión compatible de esquema y binarios.
   El expediente existente es preparación; no constituye un paquete cerrado
   ni aprobación para desplegar. Contemplar invalidación de cookies/enlaces.

Los diagnósticos de inventario de Restaurante usan exclusivamente ubicaciones
generales y nuevas claves `R13-G/R16-G/R17-G`. Los históricos sin procedencia
`R13/R16/R17` siguen almacenados, ocultos y no modificables por ese servicio.
No presentar esa exclusión como reclasificación de datos históricos.

## Límites de entorno al retomar

Código y Sandbox siguen autorizados. No consultar ni modificar `grupocarpio`,
publicar servicios ni modificar túneles sin la aprobación independiente ya
establecida. Fusionar código a `main` no autoriza desplegarlo: el código nuevo
requiere un esquema compatible y configuración de alcance.

La conexión heredada puede apuntar a producción. Antes de abrir, usar
`SqlConnectionStringBuilder.set_InitialCatalog('Orion_Sandbox')` en PowerShell,
verificar `get_InitialCatalog()` y luego comprobar `DB_NAME()`. No imprimir
conexiones ni secretos; cambiar variables sólo dentro del proceso de prueba.
La consola conserva los secretos locales de Development por encima de variables
heredadas. La base local devuelve `Orion_SandBox` (comparar sin distinguir caja).

El incidente inicial de conexión alcanzó únicamente la guarda `DB_NAME()` de
producción, que abortó antes de leer tablas. No hubo lecturas de tablas,
escrituras, migraciones ni despliegue productivo; véase el registro completo
en el plan de continuidad.

Referencias: [plan y evidencia](public-websites-rebaseline-20260908.md),
[matriz SQL y consumidores](hospitality-sql-isolation-20260908.md),
[calendarios](hospitality-calendar-sync-isolation-20260908.md),
[expediente productivo](public-websites-production-cutover-review.md).

## Núcleo contable y segunda validación desde main

La [matriz contable vigente](multiempresa-accounting-status-20260908.md)
reconcilia los diez objetivos originales contra código y metadatos de Sandbox.
No presenta el aislamiento de Hospedaje como cierre contable: identifica RLS
legacy permisiva sin contexto, el acceso de adjuntos por ID ahora corregido,
permisos de servicio pendientes y el ciclo contable formal/bandeja durable que
faltan. El corte debe declarar los invariantes realmente cubiertos.

`HospitalityProjectConcurrencyTests`: **8/8 aprobadas** en Release, SQL
habilitado y exportación opcional desactivada. Los cuatro casos originales
mantienen vínculo primero y mutación genérica después. Los cuatro nuevos toman
el orden inverso: edición o eliminación dentro de una transacción serializable,
luego intento de vínculo desde otra conexión. En ambos sentidos se observa la
espera `LCK_M_%` con `sys.dm_exec_requests`, se cubren commit/rollback y se
preserva el ganador. Cada caso limpia sus proyectos y vínculos propios. Recibo
local: `tests/OrionERP.IntegrationTests/TestResults/hospitality-project-concurrency-final.trx`.
No requirió cambios de esquema. La decisión sobre los 288 vínculos no produjo
cambios de datos.

## Incremento contable de adjuntos

`TransactionAttachmentRepository` exige la empresa de sesión antes de SQL y
comprueba propiedad en la misma consulta que devuelve los bytes. Un adjunto
de póliza exige su empresa; un XML canónico sin póliza admite emisor/receptor.
Dos pruebas propias aprobadas (una SQL A/B y otra de rechazo preconexión),
más 14 unitarias fiscales relacionadas, con build Release sin advertencias ni
errores. No se añadió RLS ni se modificaron migraciones; la matriz conserva
como parciales los demás objetivos contables. Evidencia y recibos en la matriz.

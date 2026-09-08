# Websites compartidos: plan revisado al 2026-09-08

## Arquitectura vigente

Un único proyecto reutilizable para Hospedaje y otro para Restaurante. Los
nombres técnicos actuales `OrionERP.Bonhomia.Web` y `OrionERP.Bruno.Web` se
conservan para mantener compatibilidad de compilación y publicación; la marca
visible, contacto, contenido y recursos se obtienen del perfil de cada instancia.
Cambiar de cliente no requiere copiar ni crear otro proyecto.

Cada website tiene su propio `PublicSiteKey`, empresa, sede, perfil versionado,
proceso y puerto loopback. El túnel de la cuenta Cloudflare del cliente apunta
a ese puerto. El dominio recibido valida el destino, pero no selecciona el RFC.
La base operativa y el acceso a cada dato se validan por el alcance configurado.

OpenClaw está retirado del código ejecutable y de este plan. Sus menciones en
ADR o comentarios de migraciones se mantienen como historial; no se alteran
los bytes de migraciones registradas.

## Fases completadas en esta reanudación

1. **Integración con el repositorio actual.** El punto recuperado estaba en
   `fb292e7`; se integraron los 27 commits hasta `main` `4e45213` en la rama
   `codex/public-sites-rebaseline`. Se conservaron el rediseño público de
   restaurante y los avances recientes de la consola, resolviendo los conflictos
   con la presentación parametrizada. El checkpoint `2e85544` conserva el trabajo
   recuperado anterior a la integración.
2. **Correcciones de aislamiento y compatibilidad.** El catálogo público dejó
   de usar el fallback privado del POS; se corrigió la selección por horario y
   sede, incluida la madrugada del día siguiente. Se bloquearon cambios de
   dominio mientras el website está activo o conserva una transición vigente.
   Hospedaje respeta la precedencia de variables sobre secretos de desarrollo y
   el servicio de reservas rechaza versiones legales obsoletas antes de SQL.
   Se corrigieron las regresiones de promociones, búsqueda, proveedores y
   cobertura de navegación aparecidas al integrar `main`.
   El indicador de horario distingue abierto, cerrado y desconocido; admite
   horarios parciales y valida tipos JSON sin romper la página.
3. **Reconciliación del Sandbox y validación local.** La base actual no contenía
   las seis migraciones del manifiesto. Se ejecutaron sus previews, aplicaciones
   con recibos y verificación final exclusivamente en `Orion_Sandbox`.

## Evidencia y alcance de validación

- Compilación completa de la solución en Release: cero errores y advertencias.
- Suite unitaria completa: 1,212 casos aprobados; incluye aislamiento, configuración,
  presentación, consentimiento y regresiones de la integración.
- Suite de integración: 55 casos aprobados con `ORION_RUN_SQL_INTEGRATION=1`
  y conexión fijada a `Orion_Sandbox`. La generación opcional de artefactos de
  reportes (`ORION_RUN_EXPORT_QA`) quedó desactivada y no se cuenta como QA visual.
- Seis migraciones del manifiesto con resultado final `VERIFIED`.
- En Hospedaje: 36 habitaciones, 54,319 filas de calendario, 1,329 reservaciones
  y 827 clientes con alcance; cero reservaciones sin atribuir en este Sandbox.
- En Restaurante: 60 cuentas Identity, 60 membresías y 60 vínculos aislados.
- Los perfiles Bonhomia y Bruno pasaron `Publish-All-prod.ps1 -ValidateOnly`,
  incluso con un override de direcciones conflictivo que el preflight aisló y
  restauró. No se copiaron archivos a producción.
- Arranque local en `127.0.0.1:55010` y `127.0.0.1:55020`: página inicial y
  `/readyz` respondieron 200, mostraron su marca configurada y rechazaron un
  host ajeno con 400. Los procesos temporales se cerraron.

La prueba de arranque fue HTTP y de configuración, no una prueba E2E completa
de navegadores, pagos, correo ni dos empresas simultáneas. El checkout se probó
con dobles de PayPal; no se realizaron cobros ni envíos reales. Los scripts
históricos de otras funcionalidades recibidos de `main` no se aplicaron por
el simple hecho de integrar el código.

## Fases restantes, en orden

1. **Completar Hospedaje multiempresa en Sandbox.** Inventariar y aislar CRUD,
   consultas y reportes administrativos, clientes, transacciones, CFDI,
   calendarios Outlook/Graph, procedimientos y jobs que todavía consumen datos
   globales o RFC heredados. Resolver los mapeos con evidencia explícita y
   preparar migraciones aditivas por consumidor. Criterio de cierre: todos los
   consumidores de una reserva respetan empresa y sede, incluso fuera del host
   público. Ésta es la siguiente fase recomendada.
2. **Prueba completa con dos empresas en Sandbox.** Preparar dos perfiles y
   datos de prueba por rama usando los mismos proyectos. Verificar marcas,
   catálogos, horarios, cookies, recuperación de cuentas, documentos y reservas;
   incluir intentos de acceso cruzado y transiciones/reversiones de perfiles.
   Para pagos y correo usar únicamente entornos de prueba. Criterio de cierre:
   evidencia positiva y negativa reproducible por empresa y sede.
3. **Preparar el corte productivo para revisión.** Crear las migraciones
   productivas específicas; los scripts limitados a Sandbox no se habilitan para
   producción cambiando sólo su lista de bases permitidas. Preparar perfiles,
   servicios, puertos, cuentas/túneles, credenciales privadas, respaldo y
   reversión. Contemplar la invalidación esperada de cookies y enlaces por el
   cambio de Data Protection. Esta preparación no implica ejecutar el corte.
4. **Migrar `grupocarpio` al último.** Requiere la aprobación productiva
   independiente indicada por el usuario. Después: respaldo verificado,
   `ApplyChanges=0`, revisión del resultado, `ApplyChanges=1`, verificación,
   despliegue y prueba local antes de habilitar tráfico por los túneles.

Los roles públicos y accesos externos sólo necesitan una fase adicional de
aislamiento si se habilitan. La edición central de textos y carga de logotipos
desde la consola puede añadirse como mejora: los perfiles ya permiten
personalizar las instancias. No es necesario crear websites específicos por RFC.


## Continuación: aislamiento administrativo de Hospedaje

Trabajo realizado en `codex/public-sites-rebaseline`, desde `f35fd95`, en el
worktree `d460`. Se conservaron los cambios del checkout principal, que no se
integraron ni se modificaron en esta fase.

- Nueva conexión de Hospedaje delimitada por empresa/sede. La consola deriva
  el RFC de su sesión autenticada y exige una sede habilitada; sólo elige
  automáticamente cuando hay una sola. La página `/reservaciones/sede`
  permite elegir entre las sedes autorizadas. Los permisos revocables se
  comprueban nuevamente por operación.
- CRUD, clientes asociados, experiencias, extras, documentos, totales,
  arrendadores y lecturas públicas inicializan el alcance SQL. Las acciones por
  lotes verifican todos los IDs dentro de la transacción; las actividades
  compartidas con otro calendario no se eliminan.
- CFDI valida el emisor y los pagos contra la empresa; los perfiles fiscales
  nuevos tienen asociación exclusiva empresa/sede. Contabilidad general
  conserva alcance por empresa y los vínculos a reservas exigen la sede.
- La nueva migración `20260908_hospitality_administration_scope_sandbox`
  protege 18 tablas con 54 predicados RLS y relaciones compuestas adicionales.
  Sin contexto no hay acceso operativo. Se aplicó después de preview revisado
  y recibo SHA-256 `D446C0E3748C155338E5AC6BBDD1A24B1E8E802950ECDF4C6021B89B30538DCA`.
  Siete migraciones quedaron verificadas. La excepción de `.gitattributes`
  conserva los bytes exactos del script aplicado.
- Órdenes de Trabajo valida RFC de sesión y todas las referencias a habitación,
  celda y reserva; las mutaciones revalidan en la misma transacción. Conserva OT
  generales de la empresa y bloquea adopciones legacy por nombre sin mapping.
- Logística resuelve ubicaciones visibles por RFC, habitación y todos sus
  ancestros, incluido LegacyRoomId. Protege copias de nombres, cantidades,
  movimientos y adjuntos; las escrituras revalidan dentro de la transacción.
  Compras y conteos ocultan el documento entero si alguna referencia es ajena.
  La selección explícita de todas las suites no se convierte en alcance general.
  Cambiar padre/habitación de una ubicación existente queda bloqueado para
  conservar el alcance histórico; puede crearse una ubicación nueva.
- El selector compartido de sede permite volver a Reservaciones, OT o Logística
  después de elegir; sólo aparece para los roles administrativos habilitados.
- Outlook/Graph exige identidad, buzón y calendarios configurados para el
  alcance autorizado; queda deshabilitado por defecto. Los eventos ajenos y
  legacy sin atribución comprobable no se adoptan ni eliminan.

### Límites que impiden declarar cerrada la fase

1. Existen 288 vínculos de reservas OHM con pagos BSU. Permanecen almacenados,
   sin reasignación, y excluidos de los recorridos de Hospedaje hasta decidir si
   representan cobros interempresa válidos o errores históricos. Esto puede
   cambiar los saldos calculados respecto de la lectura global anterior; no se
   alteraron pagos ni estados históricos para hacer coincidir las cifras.
2. Cuatro mapeos de Outlook apuntan a reservas inexistentes. Se conservan y
   quedan fuera de la sincronización.
3. `CreateActividadForReservation` y `CreateTransaccionesForRoom` conservan su
   firma pero fallan antes de escribir: sus plantillas, cuentas y categorías
   requieren una configuración explícita por sede. No se atribuyeron IDs
   globales por intuición.
4. Órdenes de Trabajo y los consumidores principales de Logística ahora
   autorizan sus documentos y referencias originales. Se conserva el bloqueo
   de importaciones de plantillas por texto; los textos históricos que perdieron
   sus IDs no reciben una atribución inferida. El inventario SQL detalla también
   los consumidores indirectos de inventario dentro de Restaurante, cuyo cierre
   requiere verificar esa frontera compartida con sus propios flujos.
5. Las pruebas SQL y de servicios A/B no son E2E de dos websites por rama:
   utilizan bindings existentes para comprobar el límite de datos. Aún faltan
   perfiles y fixtures de dos empresas habilitadas por módulo, navegadores,
   recuperación/cookies, presentación y proveedores externos de prueba.
6. El expediente de corte es preparación para revisión, no un corte aprobado
   ni migraciones productivas terminadas. Las migraciones específicas de
   producción dependen de resolver los límites anteriores.

### Incidente de conexión durante el preflight

La primera sustitución de base mediante una propiedad de PowerShell falló por
el adaptador de diccionario. El proceso llegó a abrir la conexión heredada a
producción y ejecutó únicamente la guarda `DB_NAME()`, que lanzó error antes
la consulta de tablas. No se ejecutaron migraciones, lecturas de tablas ni
escrituras en producción. Se notificó al usuario durante el trabajo.

Se corrigió el helper para usar `set_InitialCatalog`/`get_ConnectionString`,
validar el destino **antes** de abrir y comprobar `DB_NAME()` después. La base
local devuelve `Orion_SandBox`; la comparación segura ignora mayúsculas.
Las variables se cambiaron sólo dentro de procesos de prueba. La consola
conserva su precedencia existente de secretos de Development para impedir que
una variable heredada de producción reemplace accidentalmente el Sandbox local;
los hosts públicos conservan su contrato explícito de variables de proceso.

Documentos de evidencia:

- [SQL e inventario](hospitality-sql-isolation-20260908.md).
- [Calendarios](hospitality-calendar-sync-isolation-20260908.md).
- [Expediente de corte, no ejecutado](public-websites-production-cutover-review.md).


### Evidencia de validación de esta continuación

- Suite completa hasta el incremento administrativo: 1,279 pruebas unitarias y
  67 de integración aprobadas en Release. SQL habilitado explícitamente en
  Sandbox; exportación opcional de reportes desactivada. Los resultados TRX
  quedan en `artifacts/test-results` (artefactos locales, no versionados).
- Incluye 11 pruebas SQL transaccionales de RLS y un recorrido de servicios
  A/B con limpieza de sus propios fixtures: clientes, reservas, documentos,
  extras, experiencias, perfiles fiscales, pólizas y bloqueo de cascada sobre
  un pago vinculado. Contextos A/B explícitos de prueba no habilitan un segundo
  website de Hospedaje.
- Navegador local de consola en `127.0.0.1:55221`: login de usuario dedicado,
  selección OHM/Bonhomia Suites, lista y detalle de reservación cargados. Sin
  realizar cambios a reservas históricas ni activar botones de integración.
- Hosts públicos en `127.0.0.1:55010` y `:55020`: inicio y `/readyz` 200 después
  de RLS; host ajeno 400. PayPal se configuró en modo Sandbox con credenciales
  ficticias en el proceso y no se invocó checkout. No se hicieron cobros ni
  envíos. Los procesos temporales se cerraron.

Esta evidencia no sustituye el E2E pendiente con dos empresas habilitadas por
rama ni verifica integraciones externas reales. Las pruebas de Logística usan
fixtures propios con limpieza por ID; las cifras finales se registran tras
terminar la suite completa de esta continuación.


## Cierre para integrar en main (2026-09-08)

Por instrucción del usuario se congeló el alcance para integrar el checkpoint
en `main` y continuar allí. La lista ejecutable de pendientes está en
[multiempresa-pendientes-desde-main.md](multiempresa-pendientes-desde-main.md).
La suite final completa aprobó **1,306 unitarias y 71 de integración** con SQL
Sandbox habilitado y exportación opcional desactivada. La compilación Release
terminó con **0 advertencias y 0 errores**. Los smokes de navegador anteriores
no se presentan como una repetición del último incremento.

Se completaron también los filtros de inventario en POS, diagnóstico preventivo
y diagnóstico persistido, además de movimientos logísticos. Producción de
Restaurante y prioridades de ubicación recibieron guardas y compilan; falta
su fixture SQL específico, registrado como primer pendiente de validación.
En Ajustes, los propietarios se leen sólo a través de habitaciones visibles;
la edición del maestro global no atribuido está bloqueada. Los proyectos se
filtran por todos sus vínculos de calendario y su edición genérica vinculada
se rechaza con validación transaccional.

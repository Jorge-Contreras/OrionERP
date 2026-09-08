# Backlog de código multiempresa

Producción multiempresa está desplegada y operando desde el 2026-09-08
(código `91e2362`, [acta](../production-cutover-executed-20260908.md)). Los errores
que aparezcan los reporta el usuario y se corrigen; **este backlog no incluye smoke
tests, E2E de dos empresas, ni pruebas de permisos o seguridad**. Sólo lo que hay
que escribir.

Sustituye a los 25 prompts anteriores, que quedan recuperables con
`git show 07d6384 -- docs/entregas-cortas/`. Aquellos repetían ~31 líneas de
encabezado en cada archivo y dedicaban una sección entera a validación; ese
encabezado vive ahora una sola vez, en "Reglas permanentes".

## Reglas permanentes

Aplican a todas las entregas. No se repiten en cada archivo.

**Checkout.** Trabaja en main dentro del checkout principal
`C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Development\OrionERP`.
Sin ramas ni worktrees nuevos. Conserva los cambios ajenos concurrentes y
**nunca añadas al commit**
`src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260907_fiscal_declaracion_deploy.ps1`.
Una sola entrega con escritura a la vez.

**Identidad.** `CompanyId` técnico, `TaxRfc` fiscal y `Rfc` legacy son tres cosas
distintas; no se infiere el RFC fiscal a partir de ninguna otra. `CompanyModule` y
`SiteCapability` son la autoridad de habilitación, no el menú ni `restaurante.Site.IsEnabled`.
`BRUNOS260707L26` es una clave interna sin perfil fiscal.

**Un proyecto por rama pública.** `OrionERP.Bonhomia.Web` y `OrionERP.Bruno.Web`;
consola `OrionERP.Web`. No se crean proyectos por cliente. No se reintroduce OpenClaw.

**Base de datos.** Antes de cada conexión fija `Database=Orion_Sandbox` dentro del
proceso (`set_ConnectionString`, `set_InitialCatalog`, `get_ConnectionString`),
comprueba el catálogo antes de abrir y `DB_NAME()` después, sin distinguir caja.
Nunca imprimas cadenas de conexión ni secretos. `grupocarpio` sólo con autorización
explícita del corte correspondiente.

**Migraciones.** Los bytes de las migraciones ya aplicadas —incluidas las siete
productivas— no se editan. Todo cambio de esquema es aditivo, con su ledger y
checksum, y con paquetes distintos por entorno. Los procedimientos almacenados
viven sólo en la base: vuelca con `OBJECT_DEFINITION` antes de parchar.

**Verificación mínima.** Build Release sin errores ni advertencias en todo lo que
toques. Pruebas unitarias **sólo** en E3–E6, donde el código toca importes, pólizas
o saldos, y sólo del comportamiento nuevo. Si las apps están corriendo, los MSB302x
son bloqueos de archivo: compila con `-t:Compile` y `BuildProjectReferences=false`.

**Cierre.** Commit con tus archivos, comportamiento cambiado, migraciones y
servicios realmente publicados, y el siguiente pendiente concreto. Distingue
implementación, preparación y ejecución productiva. Si sólo cambian documentos,
no reinicies producción.

## Las ocho entregas

| Entrega | Qué se escribe | Depende de | Estado |
| --- | --- | --- | --- |
| [E1 — Guardas de suspensión de Restaurante](E1-guardas-restaurante.md) | Scope accessor de Restaurante aplicado a contabilidad, producción, operaciones de sede y el job registrado | — | Entregada en `7c4105e` |
| [E2 — Identidades SQL por website](E2-identidades-sql-websites.md) | Dos scripts de permisos mínimos, uno por instancia pública | — | Pendiente (apagada) |
| [E3 — Identidad contable y contrato CFDI](E3-identidad-contable-cfdi.md) | `CompanyId` de sesión, fábrica de conexiones contables, predicado emisor/receptor único | — | Entregada en `b3f036a`; aplicada en Sandbox |
| [E4 — Ciclo contable formal](E4-ciclo-contable.md) | Periodos, `Draft/Posted/Reversed`, publicación atómica, inmutabilidad y reversa | E3 | Pendiente (apagada) |
| [E5 — Bandeja contable durable](E5-bandeja-contable-durable.md) | Contrato durable idempotente para Restaurante y Hospedaje | E4 | Pendiente |
| [E6 — Reportes sobre pólizas publicadas](E6-reportes-publicados.md) | Balanza y resultados agregando sólo asientos publicados | E4 | Pendiente |
| [E7 — Legado de Hospedaje](E7-legado-hospedaje.md) | Corrector de vínculos, mappings Outlook, propietarios por sede, plantillas y actividades | — | Pendiente (apagada) |
| [E8 — RLS por agregado](E8-rls-por-agregado.md) | Predicados fail-closed, un agregado por lote: contable, logística, RH, fiscal | E3 (a, d), E1 (b) | Pendiente |

Orden sugerido: **E1** primero, que cierra la estabilización sin tocar contabilidad.
Después **E3 → E4 → E5/E6**, que es la cadena larga. **E2**, **E7** y **E8c** no
dependen de nada y pueden adelantarse.

Con E1 y E3 entregadas, el siguiente de la cadena larga es **E4**.

**Pendiente de autorización.** `20260908_production_accounting_company_identity`
está escrita, registrada en el manifiesto productivo y validada de sintaxis, pero
**no se ha ejecutado**. Aplicarla en `grupocarpio` exige respaldo referenciado,
preview revisado y `--production-approval "APPLY grupocarpio"`.

"Apagada" significa que el mecanismo se implementa y se entrega desactivado: falta
un dato empresarial o una decisión del usuario para encenderlo, no código.

## Qué se eliminó del backlog anterior

| Original | Motivo |
| --- | --- |
| 01 Roles y carrera | Cerrado en `1ac3f2f`: `RevocableCompanyRoleAuthorization.cs` y la carrera proyecto/calendario en ambos sentidos. Sin trabajo pendiente. |
| 06 E2E dos hospedajes | Preparar empresas sintéticas y probar. Sin entregable de código. |
| 07 E2E dos restaurantes | Igual que 06. |
| 14 Baseline contable | Consultas y expediente, no código. Sobrevive como una consulta read-only dentro de E4. |
| 25 Túneles por instancia | Comprobación de procesos, perfiles, puertos y túneles. Es operación. |

De las veinte restantes se quitó el encabezado repetido y la sección
`## Pruebas y aceptación` completa.

## Mapa contra el backlog original

| Pendiente original | Entrega |
| --- | --- |
| Roles restringidos, CRUD/revocación y carrera proyecto/calendario | Cerrado en `1ac3f2f` |
| Empresa/módulo/sede/permisos y suspensión en servicios y trabajos | E1 |
| Identidades SQL por website y separación de la migración | E2 |
| `CompanyId`/`TaxRfc`/legacy y conexiones contables con contexto | E3 |
| CFDI legítimamente compartidos y asignación por empresa | E3 |
| Periodos, `Draft/Posted/Reversed`, balance e inmutabilidad, reversa | E4 |
| Conciliación heredada y baseline | E4 (consulta read-only) |
| Idempotencia y bandeja contable durable de ambos módulos | E5 |
| `CreateTransaccionesForRoom` con mappings y contrato contable | E5 |
| Reportes de pólizas publicadas y métricas operativas separadas | E6 |
| 288 vínculos históricos sin cambiar importes | E7 |
| Cuatro mappings Outlook; Graph sigue apagado | E7 |
| Propietarios asociados, plantillas y creación de actividades | E7 |
| RLS contable y bypass de políticas legacy | E8 |
| Dos empresas por rama, procesos, puertos y túneles | Fuera: validación y operación |

## Hallazgos abiertos

**El broadcaster de Restaurante no publica nada.** `RestaurantEventBroadcaster`
corre en un alcance de fondo sin sesión, así que `SqlConnectionFactory` le fija
`OrionRfc='__UNSCOPED__'`; `logistica.RfcSecurityPolicy` cubre
`restaurante.EventOutbox` y filtra el lote a cero filas. Comprobado en Sandbox:
2058 eventos sin contexto, 0 con `__UNSCOPED__`. La guarda de módulo que agregó
E1 es correcta pero no llega a ejercerse. Corregir el contexto del trabajo es un
cambio de conducta aparte —encendería SignalR y drenaría el rezago acumulado de
una vez— y necesita decisión del usuario antes de tocarlo. Cabe en **E8c**, que
ya separa el contexto de trabajos del de usuario.

## Bloqueos que siguen deliberados

No son tareas para desactivar protecciones: cambiar padre o habitación de una
ubicación con historia, editar el maestro global de arrendadores sin asociación,
importar plantillas libremente por texto. Las claves históricas `R13/R16/R17` sin
procedencia se conservan y no se reclasifican por semejanza. No se agrega un
cliente productivo de ejemplo.

## Fuentes

[Acta productiva](../production-cutover-executed-20260908.md) ·
[Matriz contable](../multiempresa-accounting-status-20260908.md) ·
[Pendientes desde main](../multiempresa-pendientes-desde-main.md) ·
[Aislamiento de calendarios](../hospitality-calendar-sync-isolation-20260908.md)

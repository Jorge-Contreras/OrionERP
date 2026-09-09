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
| [E3 — Identidad contable y contrato CFDI](E3-identidad-contable-cfdi.md) | `CompanyId` de sesión, fábrica de conexiones contables, predicado emisor/receptor único | — | Entregada; aplicada en producción |
| [E4 — Ciclo contable formal](E4-ciclo-contable.md) | Periodos, `Draft/Posted/Reversed`, publicación atómica, inmutabilidad y reversa | E3 | Entregada; aplicada en producción, encendida sólo para el piloto Bruno |
| [E5 — Bandeja contable durable](E5-bandeja-contable-durable.md) | Contrato durable idempotente para Restaurante y Hospedaje | E4 | Restaurante entregado y aplicado en producción; Hospedaje **apagada**: mapping ya cargado, falta el código |
| [E6 — Reportes sobre pólizas publicadas](E6-reportes-publicados.md) | Balanza y resultados agregando sólo asientos publicados | E4 | Entregada y aplicada en producción; variante publicada **no adoptable aún** |
| [E7 — Legado de Hospedaje](E7-legado-hospedaje.md) | Corrector de vínculos, mappings Outlook, propietarios por sede, plantillas y actividades | — | Pendiente (apagada) |
| [E8 — RLS por agregado](E8-rls-por-agregado.md) | Predicados fail-closed, un agregado por lote: contable, logística, RH, fiscal | E3 (a, d), E1 (b) | Pendiente |

Orden sugerido: **E1** primero, que cierra la estabilización sin tocar contabilidad.
Después **E3 → E4 → E5/E6**, que es la cadena larga. **E2**, **E7** y **E8c** no
dependen de nada y pueden adelantarse.

Con E1, E3, E4, E5 y E6 entregadas, la cadena larga está completa. Queda **E2**,
**E7** y **E8**, que no dependen de nada de esto.

**Qué falta para adoptar el reporte publicado como oficial.** La balanza y el estado de
resultados quedaron versionados con `@SoloPublicadas`, apagado por omisión, así que el
reporte vigente conserva exactamente su significado. Encenderlo por empresa exige, en
orden: activar el ciclo de E4 para esa empresa, publicar sus pólizas, y conciliar las
que queden fuera. `GetPublishedReportAvailabilityAsync` devuelve ese pendiente con
nombre; mientras haya pólizas fuera del ciclo, el reporte vigente sigue siendo el
oficial.

**Lo que E5 dejó fuera, con nombre exacto.** El contrato durable cubre sólo la
contabilización diaria de Restaurante. `GenerateIndividualCfdiPolicyAsync` —órdenes
individuales y el ajuste de CFDI tardío, que hoy crea la reversión
`LateCfdiReversal`— sigue fuera de la bandeja y conserva su comportamiento actual:
borra la póliza si el vínculo falla. Es el siguiente lote.

**Mappings que faltan para encender Hospedaje.** `contabilidad.HospitalityAccountingMapping`
está creada y vacía. Por cada empresa y sede que se quiera contabilizar hacen falta,
como mínimo, `LeaseIncomeAccount` y `LeaseReceivableAccount`; opcionalmente
`VatPayableAccount`, `CategoryId` y `BankAccount`. Mientras no existan esas filas,
`dbo.CreateTransaccionesForRoom` sigue lanzando 51823 y no se infiere ninguna cuenta
ni categoría por texto.

**Producción al día en base de datos.** Los cuatro paquetes contables están aplicados
en `grupocarpio` con respaldo verificado y preview revisado, y el ledger productivo
devuelve **11 VERIFIED**. Actas: [E3 y E4](../production-accounting-packages-applied-20260908.md)
y [E5 y E6](../production-outbox-reports-applied-20260909.md).

**Los binarios productivos preceden a E5 y E6.** Son de las 19:45 del 2026-09-08; E5 se
registró a las 22:21 y E6 a las 22:34. Nada está roto —el código anterior no invoca la
bandeja y el parámetro nuevo tiene valor por omisión— pero E5 y E6 no surten efecto
hasta una publicación más.

**Ciclo encendido para un piloto (2026-09-09).** El usuario eligió `BRUNOS260707L26`
con corte 2026-09-09: sus 337 pólizas previas quedan en modo compatible y el ciclo
gobierna de esa fecha en adelante. Registrado por migración con ledger, no como
escritura suelta: `20260909_production_accounting_cycle_activation`.

`OHM191112Q26` y `BSU210121M77` quedaron **fuera del ciclo**, que no es lo mismo que
apagadas: las dos operan con normalidad, facturan, registran pólizas y sacan reportes.
Lo único que no tienen es el ciclo formal, una capacidad que no existía hasta ayer. Al
mirar el detalle resultó que casi nada de lo que las bloqueaba era un problema real, y
lo que sí lo era ya se resolvió:

- De las 15 pólizas descuadradas, **14 eran de un centavo** (±0.01, sobre la tolerancia
  de 0.005 del validador). El usuario decidió el 2026-09-09 subir la tolerancia a 0.01,
  que es el piso que las admite. La única de fondo era la `27047` de BSU, `SALDO INICIAL
  DE ENERO 2024`, con un solo renglón de 9,266.85 al debe y ningún abono; el usuario
  indicó abonarla a `401.25.01`, y quedó cuadrada por migración.
  **Hoy no queda ninguna póliza descuadrada en ninguna de las ocho empresas.**
- Los **35 CFDI con varios vínculos NO eran duplicados**: 34 son repartos cuyos importes
  suman exactamente el total del CFDI, y uno está 5.00 por debajo. Repartir un CFDI entre
  varias pólizas es una función diseñada —`Transaccion_Comprobante.Monto`— y práctica
  viva: 78 vínculos, el más reciente de junio 2026. Ningún CFDI está ligado por dos
  empresas distintas.

El baseline read-only vive en
`src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260908_accounting_cycle_baseline.query.sql`.

**Mapping de Hospedaje registrado.** Bonhomía Suites (empresa 9, sede 3) es la única sede
con Hospedaje: `LeaseIncomeAccount = 401.25.02` y `LeaseReceivableAccount = 205.01.01`,
elegidas por el usuario sobre su propio catálogo. Aviso importante:
`dbo.CreateTransaccionesForRoom` **sigue lanzando 51823**. El dato ya está; falta el
código que lo consuma a través del contrato durable.

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

**El readiness productivo es `/readyz`, no `/health/ready`.** Los tres servicios
responden 200 ahí. Probar `/` o el login sobre HTTP plano devuelve 500, y es esperado:
`Publish-All-prod.ps1` lo documenta al elegir el endpoint. La consola publica con
`PublishSingleFile`, así que su carpeta no tiene DLL sueltos y eso tampoco es una
falla. Ver la [corrección en el acta](../production-accounting-packages-applied-20260908.md).

**Índice filtrado en `dbo.Transacciones`.** `UX_Transacciones_ReversalOf` es lo que
hace que una reversa no se pueda duplicar bajo concurrencia, y por ser filtrado obliga
a que todo escritor de esa tabla tenga `QUOTED_IDENTIFIER ON`. El controlador .NET lo
fija por omisión y la base ya tiene 47 índices filtrados —entre ellos en `dbo.ROOM`,
`dbo.Extra` y `dbo.PlantillaContable`—, así que la exigencia no es nueva en el sistema;
sí lo es en esta tabla. Una herramienta externa con la opción apagada fallaría al
escribir pólizas. Comprobado en Sandbox: `sqlcmd` sin `-I` falla, con `-I` pasa.

**Una póliza descuadrada y CFDI multiligados en la historia.** El baseline los reporta:
`BSU210121M77` tiene una póliza descuadrada en enero 2024 por 9,266.85, y hay
comprobantes ligados a varias pólizas de la misma empresa —hasta cuatro—. Son historia:
E3 rechaza duplicar de aquí en adelante y no toca lo existente. Hay que resolverlos
antes de activar el ciclo en esas empresas.

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

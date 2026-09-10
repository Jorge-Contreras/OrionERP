# E8 — RLS por agregado

Lee primero las [reglas permanentes](README.md#reglas-permanentes).
Superficie: consola y esquema compartido.

**Riesgo declarado.** Es el único bloque donde publicar sin mirar puede dejar
producción sin filas: un predicado mal escrito no da error, simplemente devuelve
cero registros. Cada migración se aplica y se observa en Sandbox antes de proponerse
para producción. Eso es escribir la migración, no una suite de pruebas.

## Estado actual en Sandbox — 2026-09-09

| Política | Tablas | Predicados | Situación |
| --- | ---: | ---: | --- |
| `contabilidad.AccountingScopePolicy` | 2 | 6 | **fail-closed E8a** |
| `logistica.InventoryCoreScopePolicy` | 3 | 9 | **fail-closed E8b** |
| `logistica.RfcSecurityPolicy` | 95 | 285 | conserva el bypass sólo fuera del lote E8b |
| `rh.WorkforceScopePolicy` | 6 | 18 | **fail-closed E8c** |
| `rh.RfcSecurityPolicy` | 16 | 48 | conserva el bypass fuera del lote E8c |
| `fiscal.DeclarationScopePolicy` | 6 | 18 | **fail-closed E8d** |
| `orion.HospitalityScopePolicy` | 25 | 75 | fail-closed |

`Transacciones`, `Registro_Contable`, `CuentasContables`, `TRANSACTION_ATTACHMENT` y
`cfdi.Comprobante` no tienen ningún predicado. La fábrica convierte el contexto vacío
en `__UNSCOPED__`, defensa útil que no elimina el bypass de una conexión sin
inicializar. El nombre de una política no delimita el esquema de sus tablas: la de
Logística también contiene tablas de Restaurante y Fidelidad.

## Los cuatro lotes

Uno por ejecución, con migración aditiva. **Nunca se habilita bypass por `NULL`**, y
nunca se cambia de golpe una función compartida por decenas de tablas.

**E8a — Contable** (depende de E3). RLS fail-closed en `Transacciones` y
`Registro_Contable`, después de verificar que todos sus consumidores adaptados fijan
el contexto. Al inventariar dependencias considera adjuntos, CFDI, reportes, jobs y
consumidores externos o VBA, y no declares migrado el que no lo está. Si un escritor
externo sigue sin contexto, adáptalo o deja el paquete preparado. Adjuntos,
`CuentasContables` y `cfdi.Comprobante` son lotes posteriores con su propio contrato;
no se impone propiedad exclusiva a `cfdi.Comprobante`.

**E8b — Logística** (depende de E1). Inventariar los predicados actuales de
`logistica.fn_RfcAccessPredicate` y sus consumidores, y retirar el bypass **sólo**
para el agregado mínimo de ubicaciones, existencias y movimientos que pueda activarse
como unidad con todos sus consumidores preparados —POS, producción y diagnósticos
incluidos—. Las demás tablas conservan su política. Documenta las tablas exactas del
lote. No se desbloquean movimientos de ubicaciones con historia.

**E8c — RH** (independiente; puede adelantarse). Primer agregado autocontenido de
`rh.fn_RfcAccessPredicate` con uso productivo comprobable: adapta sus conexiones,
rutas y trabajos, y crea predicados específicos fail-closed. Separa el contexto de
usuario, el de trabajos y la identidad de migración, sin bypass administrativo en la
política operativa. No se alteran reglas laborales, nómina, expedientes ni
biométricos.

**E8d — Fiscal** (depende de E3). Primer agregado autocontenido de
`fiscal.fn_RfcAccessPredicate`, conservando el contrato emisor/receptor de E3 para
que ningún CFDI compartido legítimo se pierda por una asociación equivocada. No se
cierran declaraciones, no se timbra y no se cambian cálculos fiscales. No se edita ni
se incluye `20260907_fiscal_declaracion_deploy.ps1`.

## Límite

Al cerrar cada lote, deja la continuación con las tablas y consumidores exactos del
siguiente. La reversión debe ser compatible en esquema y código: **nunca se
deshabilita RLS para poder recuperar binarios viejos**.

## Verificación

Build Release de lo que cambie en código. La migración se aplica y se observa en
Sandbox. Sin unitarias: el cambio es SQL.

## Resultado de E8a, E8b y E8d — 2026-09-10

Los tres lotes pendientes se implementaron, previsualizaron, aplicaron y verificaron
por separado **sólo en `Orion_Sandbox`**. No hubo corte productivo.

**E8a — Contable.** `dbo.Transacciones` y `dbo.Registro_Contable` ahora exigen el
`CompanyId` técnico y el `OrionRfc` exacto de una empresa activa. Las dos pólizas OHM
y cuatro movimientos que habían nacido con `CompanyId = NULL` se resolvieron por la
clave legacy exacta, sin tocar importes ni pólizas, y las columnas quedaron `NOT NULL`
con FK compuesto movimiento→póliza. Sin contexto ambas tablas devuelven cero; Bruno
ve 337 pólizas y 660 movimientos, un RFC exacto; un insert ajeno devuelve 33504. Una
escritura local de prueba recibió el `CompanyId` esperado y el rollback dejó cero
residuos.

El inventario de consumidores corrigió cinco caminos antes de activar la política:

- la fábrica compartida fija ahora `OrionRfc` y `OrionERP.CompanyId` por el vínculo
  exacto con `orion.Company`;
- la fábrica de Hospedaje fija además esos dos contextos para sus cruces contables;
- registros contables y declaración previa abren con `AccountingConnectionFactory`;
- el timbrado abre su transacción local con la misma fábrica;
- el catálogo de proyectos inicializa el alcance contable incluso fuera de una sede
  de Hospedaje.

Los servicios de Bancos, CxP, Restaurante, Logística, reportes y adjuntos ya comparten
la primera fábrica; reservaciones usa la segunda; `TransaccionService`, ciclo y outbox
ya usaban la fábrica contable desde E3–E5. El usuario confirmó el 2026-09-09 que no
existen consumidores contables externos ni VBA; esa condición de compatibilidad ya
está cerrada. Todavía falta preparar, previsualizar y autorizar el paquete productivo.

**E8b — Logística.** El lote mínimo exacto es `logistica.Location`,
`logistica.StockBalance` y `logistica.StockTransaction`: ubicaciones, existencia
consolidada y su diario de movimientos. Sin contexto las tres dan cero; Bruno ve
22 + 407 + 5,784 filas y un solo RFC; un movimiento OHM intentado desde Bruno devuelve
33504 y deja cero residuos. `logistica.Material` continuó visible sin contexto durante
la prueba, evidencia de que las otras 95 tablas no se cerraron accidentalmente.
La siguiente unidad exacta es `Material`, `MaterialLot` y `LotBalance`; después deben
seguir conteos, compras/recepciones, transferencias, ajustes y producción, cada uno
con sus tablas hijas.

**E8d — Fiscal.** Las seis tablas del esquema fiscal quedaron juntas bajo
`fiscal.DeclarationScopePolicy`; el agregado tenía 16 filas OHM. Sin contexto devuelve
cero, OHM ve las 16 y Bruno cero. Un alta fiscal OHM intentada desde Bruno devuelve
33504 y deja cero residuos. `cfdi.Comprobante` conserva cero predicados de este lote y
sus 16,642 filas siguieron visibles en la comprobación sin contexto: no se impuso
propietario exclusivo ni se alteró el contrato emisor/receptor, cálculos, cierres o
timbrado.

Migraciones Sandbox: `20260909_accounting_rls_scope_sandbox`,
`20260909_inventory_core_rls_scope_sandbox` y
`20260909_fiscal_rls_scope_sandbox`. Build de la consola Release: 0 warnings / 0
errores.

Los tres paquetes productivos distintos ya están preparados y pasaron preview contra
`grupocarpio` con rollback completo:

- `20260909_production_accounting_rls_scope`: 8,102 pólizas, dos cabeceras y cuatro
  movimientos legacy resueltos por el vínculo exacto de OHM; seis predicados nuevos.
- `20260909_production_inventory_core_rls_scope`: 11,500 filas entre Bruno y OHM;
  mueve nueve predicados y conserva los otros 285 en la política heredada.
- `20260909_production_fiscal_rls_scope`: 16 filas OHM; mueve los 18 predicados
  fiscales y deja `cfdi.Comprobante` fuera.

Los recibos viven en `artifacts/database-previews/e8[a|b|d]-production-preview.json`.
No se aplicaron a producción: el corte todavía exige respaldo verificado y autorización
explícita sobre estos tres paquetes.

# E8 — RLS por agregado

Lee primero las [reglas permanentes](README.md#reglas-permanentes).
Superficie: consola y esquema compartido.

**Riesgo declarado.** Es el único bloque donde publicar sin mirar puede dejar
producción sin filas: un predicado mal escrito no da error, simplemente devuelve
cero registros. Cada migración se aplica y se observa en Sandbox antes de proponerse
para producción. Eso es escribir la migración, no una suite de pruebas.

## Estado actual

| Política | Tablas | Predicados | Situación |
| --- | ---: | ---: | --- |
| `logistica.RfcSecurityPolicy` | 98 | 294 | `SESSION_CONTEXT(N'OrionRfc') IS NULL OR ...` |
| `rh.RfcSecurityPolicy` | 22 | 66 | mismo bypass |
| `fiscal.RfcSecurityPolicy` | 6 | 18 | mismo bypass |
| `orion.HospitalityScopePolicy` | 18 | 54 | fail-closed |
| Agregado contable | — | **0** | sin predicados |

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

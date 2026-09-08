# E4 — Ciclo contable formal: periodos, Draft/Posted y reversa

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: E3.
Superficie: consola. **Se entrega apagada**: la capacidad se publica desactivada y
se activa por empresa cuando el usuario acepte el baseline.

## Estado actual

`GuardarMovimientosAsync` ya valida cuadre con `MovimientosCuadreValidator` y hay
auditoría SQL de cabecera y movimientos. Pero `GuardarYCerrarAsync` actualiza la
cabecera y calcula totales **sin transición a `Posted`**, y `DeleteMovimientoAsync`
borra movimientos de una póliza autorizada. SQL sólo conserva el `Estatus` legacy;
no hay columnas del ciclo nuevo. `fiscal.DeclaracionCierre` existe, pero es cierre
declarativo de impuestos, no cierre del libro mayor.

## Qué se escribe

**1. Migración aditiva.** Tabla de periodos contables por empresa, y columnas de
ciclo en el agregado: estado `Draft`/`Posted`/`Reversed`, `PostedAtUtc`,
`ReversalOfTransaccionId`, motivo y auditoría. El `Estatus` legacy se conserva tal
cual, y queda un modo compatible para la historia todavía no conciliada.

**2. Guarda de periodo abierto** en el agregado de pólizas, **distinta** de
`fiscal.DeclaracionCierre`. Cubre el cierre concurrente contra una escritura en
curso: ninguna operación se acepta después del cierre.

**3. Publicación atómica.** En una sola transacción: periodo abierto, movimientos
balanceados —reusando `MovimientosCuadreValidator`—, empresa consistente y permiso
específico. Un fallo intermedio revierte todo.

**4. Inmutabilidad de `Posted`.** En el servicio **y en SQL** (trigger o constraint),
para que ningún escritor del agregado edite o borre cabecera o movimientos ya
publicados. Hoy `DeleteMovimientoAsync` es la vía alterna que hay que cerrar.

**5. Reversa.** Estado `Reversed` y reversa ligada a la póliza original: movimientos
inversos, motivo, auditoría e identidad de operación. El asiento original queda
intacto. Transición explícita desde la pantalla Blazor de contabilidad, con control
de concurrencia para que dos publicaciones o dos reversas simultáneas no pasen.

**6. Baseline.** Una consulta SQL read-only versionada que reporte, por empresa y
periodo, pólizas, sumas, desbalances y duplicados relevantes al ciclo. Es el insumo
con el que el usuario decide qué activar. Sin UI nueva.

## Límite

No se cierran periodos históricos ni se cambia su condición por defecto. No se
convierte el `Estatus` legacy ni las pólizas históricas en `Posted`. La migración
crea la capacidad; la activación se limita a las empresas y periodos que el usuario
apruebe. No se mezclan impuestos declarativos con el cierre del libro. Mientras esté
apagada, la operación existente no cambia.

## Verificación

Build Release. Unitarias del comportamiento nuevo: cuadre y desbalance, transición
de estado, periodo cerrado, y reversa no duplicable.

# Smoke administrativo multiempresa desde main

Ejecutado el 2026-09-08 con navegador real contra `127.0.0.1:55221` y la
cuenta de pruebas de `AGENTS.md`. Sin copiar credenciales a este reporte.
Binarios de `e0236da` copiados a un directorio temporal para permitir compilar
el incremento independiente de adjuntos; este smoke no prueba su renderizado.

## Entorno comprobado

Conexión fijada a `Orion_Sandbox` antes de abrir y guarda `DB_NAME()` aprobada.
Proceso Development con variables prefijadas heredadas retiradas sólo en el
proceso, `APPDATA` aislado sin secretos locales y llaves Data Protection propias.
La configuración efectiva confirmó Sandbox, Graph deshabilitado y ausencia de
ClientSecrets no vacíos. Mantenimiento de asistencia desactivado. No se activó
checkout, timbrado, correo, Graph ni impresión. La sesión se cerró y el proceso
temporal se detuvo; el puerto 55221 quedó sin listener.

## Recorridos observados

| Empresa / recorrido | Resultado |
| --- | --- |
| OHM / login y selector | Empresa elegida durante login, sede Bonhomia Suites disponible. Aplicar sede carga reservas; invocado desde OT conserva el retorno a `/ordenes-trabajo`. |
| OHM / OT | Lista y detalle cargan. El detalle de una orden ajena al empleado actual muestra sólo lectura y explica quién puede trabajarla. No se editó esa orden. |
| OHM / ubicaciones | Selector de suite LONDON y ubicación autorizada carga sus 24 materiales. Se revisaron visualmente selectores y tabla, sin cambiar existencias. |
| OHM / compras | Lista y formulario de nueva compra cargan. El formulario exige proveedor antes de buscar materiales. No se guardó, emitió ni recibió una compra. |
| OHM / conteos | Listas y formulario de nuevo conteo cargan. La vista previa de la ubicación seleccionada devuelve 24 materiales, 1 ubicación y 24 renglones; no crea sesión ni modifica inventario. |
| OHM / POS | Informa que no hay sede configurada; no utiliza el catálogo de Bruno como fallback. |
| Bruno / nueva sesión | Se cerró OHM y se eligió Bruno al iniciar nuevamente. Encabezado y contexto corresponden a Bruno. |
| Bruno / POS | Carga BRUNO'S GARDEN & SNACKS, secciones y productos propios. Personalizar y añadir un producto calcula $80.00 en el carrito local. Cobrar/enviar permanece deshabilitado sin turno abierto. Se retiró el producto; no se creó orden ni cobro. |
| Bruno / ID de OT de OHM | La misma URL de detalle antes visible devuelve “No encontramos esta orden”. |
| Bruno / sede de Hospedaje | Informa que no tiene sedes habilitadas; selector y aplicar están deshabilitados. No conserva la selección OHM anterior. |
| Bruno / ubicaciones | El selector de suites no contiene las habitaciones de OHM. |

Se comprobaron en SQL las asignaciones reales de la cuenta: rol global
Administrador; en OHM, roles de OT, Conteo, Lectura y Operador; en Bruno también
roles RestauranteAdmin/Supervisor/Caja/Cocina, Logistica y otros de su empresa.
No se cambiaron asignaciones. Por tanto es evidencia con sesión/roles reales,
pero no una matriz completa de cuentas restringidas ni una prueba de revocación
durante la sesión. La limitación por responsable de OT sí se observó en UI.

## Límites y siguiente cierre

El navegador bloqueó antes de cargar una navegación de prueba con parámetros
`rfc`/`SiteKey` (`ERR_BLOCKED_BY_CLIENT`); no se cuenta como rechazo de OrionERP
ni como prueba aprobada de manipulación del request. No se intentó eludir ese
bloqueo. La navegación normal posterior funcionó.

No se ejecutó aquí CRUD completo desde navegador, recuperación/cookies entre
websites, exportación visual de reportes, suspensión de módulos ni dos empresas
habilitadas por cada rama. Esos casos conservan su pendiente específico y no se
deducen de este smoke. Los ciclos de escritura y accesos negativos de servicios
se acreditan por sus fixtures SQL separados.

Siguiente incremento de validación: perfiles/fixtures de dos empresas por rama
y cuentas restringidas, usando los proyectos reutilizables. El cierre contable,
las correcciones históricas verificables y el paquete productivo específico
continúan según la matriz y el expediente; producción sigue sin consultar ni
desplegar.

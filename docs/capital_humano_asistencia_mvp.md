# Capital Humano: asistencia, ausencias y pre-nómina

## Alcance

El MVP usa `dbo.Capital_Humano` como maestro de empleados y agrega el esquema `rh` para configuración efectiva, eventos de asistencia, excepciones, ausencias y snapshots de pre-nómina. No calcula impuestos, salario neto ni calificaciones de desempeño.

Rutas principales:

- `/mi-trabajo`: registro personal, historial, correcciones, saldos y solicitudes.
- `/mi-equipo`: alcance efectivo del supervisor, excepciones, correcciones, ausencias, horas extra y aprobación para pre-nómina.
- `/asistencia/kiosco`: kiosco en línea con vinculación de un solo uso, gafete opaco y PIN.
- `/capital-humano/asistencia`: operación y auditoría de asistencia.
- `/capital-humano/configuracion-tiempo`: sitios, geocercas, horarios, descansos, políticas, asignaciones, supervisores, festivos, kioscos y privacidad.
- `/capital-humano/ausencias`: políticas, inscripciones, solicitudes, saldos y acumulación.
- `/capital-humano/pre-nomina`: validación, bloqueo, reapertura versionada y exportación.

## Estado actual (verificado el 2026-09-03)

El esquema `rh` ya está aplicado en `Orion_Sandbox` y en `grupocarpio`: las 31
tablas existen en ambas, con los catálogos sembrados y sin datos operativos.

`AttendanceEnabled` está en `true` tanto en el `appsettings.json` del repositorio
como en el desplegado en producción, así que el paso 2 de abajo describe la
intención original y no el estado real. El alcance de esa exposición es acotado:
quien tenga el claim `employee_id` ve `/mi-trabajo` en el menú, pero al registrar
recibe «El empleado no tiene una asignación de trabajo vigente» porque falta la
configuración; no se escribe ningún evento ni se guarda ubicación.

## Despliegue

1. Aplicar el esquema y su seguridad a nivel de fila con
   `Install-CapitalHumanoSchema.ps1`. Sin `-Apply` valida todo dentro de una
   transacción que se revierte; con `-Apply` confirma. Cubre
   `20260805_workforce_attendance_mvp.sql` y `20260903_zz_rh_rls.sql`, en ese
   orden, porque la política necesita que las tablas ya existan.
2. Mantener `CapitalHumano:AttendanceEnabled=false` en producción durante la
   preparación.
3. Publicar y activar el aviso de privacidad aprobado desde Configuración de tiempo.
4. Crear explícitamente el sitio piloto con coordenadas confirmadas, zona horaria y geocerca. No inferirlo del texto heredado de sucursal.
5. Crear horario, grupo de pago, política, asignación de trabajo, supervisor e inscripción de ausencia para cada participante piloto.
6. Vincular los kioscos con códigos de un solo uso y entregar gafetes/PIN por un canal controlado.
7. Ejecutar dos periodos paralelos y comparar contra el proceso manual antes de activar producción.

El registro usa la hora de recepción del servidor. El reloj del navegador es diagnóstico. La ubicación exacta se solicita únicamente al confirmar un evento, se cifra con el propósito de protección de datos `OrionERP.CapitalHumano.Attendance.Gps.v1` y no existe seguimiento continuo.

## Seguridad y conservación

- `Administrador` conserva acceso de anulación; los roles específicos son `CapitalHumanoAdmin`, `CapitalHumanoSupervisor` y `CapitalHumanoNomina`.
- El servicio valida RFC y alcance de equipo, incluso si una ruta se invoca fuera de la navegación visible.
- Un supervisor debe estar ligado a un empleado antes de consultar o aprobar a su equipo.
- Los registros originales no se eliminan ni se reescriben; las correcciones agregan eventos auditados.
- La ubicación exacta cifrada se anonimiza después de 730 días. Los registros calculados con más de 1,825 días sólo se reportan como candidatos para revisión controlada; los snapshots bloqueados no se purgan automáticamente.
- Producción debe usar HTTPS. La credencial del kiosco vive en una cookie `HttpOnly`, `SameSite=Strict` y `Secure` fuera de desarrollo.

## Validación de periodo

El bloqueo falla si falta una asignación, existe una excepción o ausencia pendiente, un día sigue sin conciliar, el tiempo extra candidato no tiene decisión, o falta la aprobación del supervisor. La reapertura crea un periodo hijo con nueva versión; el snapshot y las exportaciones anteriores permanecen intactos.

Las exportaciones almacenan los bytes y hashes SHA-256 de XLSX y ZIP. El XLSX contiene `Resumen`, `Detalle`, `HorasExtra`, `Incidencias`, `Ausencias` y `Validaciones`; el ZIP contiene los CSV UTF-8 equivalentes y `manifest.json`.

## Aislamiento entre empresas

`20260903_zz_rh_rls.sql` crea `rh.RfcSecurityPolicy` sobre las 22 tablas de `rh`
que tienen columna `Rfc`. Las otras 8 no la tienen: cuelgan de un padre protegido.

`rh.KioskDevice` queda deliberadamente fuera. El kiosco es anónimo: localiza su
dispositivo por el hash SHA-256 del token antes de saber a qué empresa pertenece,
así que dentro de la política esa consulta devolvería cero filas y el kiosco no
funcionaría nunca. Una vez resuelto el dispositivo,
`WorkforceServiceBase.PinRfcScopeAsync` fija el RFC en la conexión y de ahí en
adelante credenciales, eventos y bitácora sí pasan por la política. La retención
programada usa `ClearRfcScopeAsync` porque recorre todas las empresas a propósito.

La fábrica de conexiones escribe `'__UNSCOPED__'` cuando no hay RFC de sesión, no
`NULL`; por eso estas tres rutas necesitan fijarlo o limpiarlo de forma explícita.

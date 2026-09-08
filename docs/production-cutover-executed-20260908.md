# Corte productivo ejecutado — 2026-09-08

El usuario autorizó expresamente publicar y continuar después los pendientes. Se publicó
desde `main` local el commit `91e236263e69445764544d397cda2d14e5ddc7f9`, con los
incrementos previos de aislamiento y pruebas. No se hizo push ni se creó otra rama.
El cambio ajeno de `20260907_fiscal_declaracion_deploy.ps1` sigue sin incluirse en commits.

## Resultado comprobado

| Servicio | Puerto loopback | Dominio | Resultado |
| --- | --- | --- | --- |
| OrionERP | 5000 | https://orionerp.orion.land | Running, readiness 200; acceso público 200 con formulario de inicio de sesión |
| OrionERP.Bonhomia | 5010 | https://bonhomiasuites.com | Running, readiness 200; inicio y calendario público de reservas visibles |
| OrionERP.Bruno | 5020 | https://brunosgarden.com | Running, readiness 200; marca, horarios y menú público visibles |

HTTP externo comprobado a las 09:50 UTC (03:50 de Ciudad de México), seguido de
navegador sobre inicio de ambos websites y `/reservar` de Bonhomía. Host ajeno devuelve
400 en ambos hosts públicos. No se enviaron formularios, correos, cobros ni timbrados;
no se inició sesión con la cuenta de Development en producción. Esto es smoke de
publicación y no sustituye las pruebas completas de cada flujo ni QA visual de reportes.

## Respaldo, ensayo y migraciones

Antes del corte se tomó un COPY_ONLY con CHECKSUM, pasó RESTORE VERIFYONLY WITH CHECKSUM
y se restauró sin sobrescribir otra base en `Orion_CutoverValidation_20260908`.
Los siete scripts productivos tuvieron preview, revisión, aplicación individual y verify
allí. Los tres hosts arrancaron con entorno Production sobre esa copia y readiness 200;
las integraciones del ensayo usaron valores inertes, sin invocar proveedores externos.

Los tres servicios se detuvieron a las 09:43:47 UTC. Se tomó **otro respaldo final con
las aplicaciones detenidas**, verificado a las 09:44:40 UTC. Este segundo respaldo no
se restauró: el ensayo de restauración corresponde al primero. Se conservaron copias
completas privadas de los tres directorios, configuración y llaves anteriores.

El manifiesto `database/orion-production-migrations.json` se ejecutó en `grupocarpio`
en orden: cada `ApplyChanges=0` fue revisado antes de `ApplyChanges=1`, usando su propio
recibo y referencia al respaldo final. El ledger devuelve **7 VERIFIED**. Son siete IDs
productivos nuevos, sin modificar los bytes ni inventar entradas de las migraciones
Sandbox. Los checksums de archivos en Git y de trabajo coincidieron antes de aplicar.
Véase el [contrato de migraciones](production-migration-package-20260908.md).

El conjunto conservado tiene 1,330 reservas, 36 ROOM, 54,319 filas de calendario y
60 identidades/membresías Bruno. No se infirió TaxRfc ni se alteraron RFC o importes.
El RLS administrativo tiene 18 tablas y 54 predicados: lectura directa sin contexto
devuelve 0 reservas, alcance Bonhomía devuelve 1,330 y CompanyId de Bruno combinado
con SiteId de Bonhomía devuelve 0. Son IDs técnicos, no un selector RFC de request.

De 1,492 vínculos de pago conservados, 1,204 son visibles en el alcance autorizado;
los 288 OHM/BSU confirmados como errores históricos siguen almacenados y excluidos.
No se inventó su relación correcta. Los cuatro mappings Outlook huérfanos se conservaron.

## Publicación y ajuste operativo

Se prepararon artefactos Release win-x64 antes de la parada. Los perfiles completos
pasaron el preflight del publicador. La copia se hizo con `Publish-prod`, sin recompilar
y con control externo de servicios; se arrancaron después de verificar el esquema.
Se usaron excepciones explícitas `AllowDirty` por el script ajeno y `AllowNonMain` sólo
para publicar el commit local sin sincronizar origin; una guarda propia exigió `main`,
el hash exacto y exclusivamente ese archivo ajeno modificado antes del corte.

Bonhomía rechazó inicialmente un override de puerto heredado por Windows. Se fijaron
vacíos, sólo en Environment de cada servicio público, los overrides ASPNETCORE_/DOTNET_
de URLS, HTTP_PORTS y HTTPS_PORTS. Se preservaron las demás entradas, incluidas las
credenciales privadas. Ambos servicios arrancaron después usando el puerto de su perfil.
No se deshabilitaron validadores ni RLS para arrancar. La consola permaneció disponible
tras su primer arranque. No fue necesaria restauración ni reversión de código/esquema.

Los ejecutables desplegados y las bibliotecas compartidas presentes coinciden por
SHA-256 con los artefactos preparados. Se conservaron `App_Data` y configuración privada.
Los nuevos discriminadores/keyrings públicos invalidan cookies y enlaces anteriores;
los usuarios deberán iniciar sesión de nuevo y usar nuevos enlaces cuando corresponda.
Se retiró la variable persistida obsoleta de OpenClaw; el código nuevo no publica esos
endpoints. No se cambiaron dominios ni túneles Cloudflare: la separación de cuentas y
túneles aún no está acreditada por este corte.

## Validación y pendientes posteriores

Pasaron 45 pruebas unitarias focalizadas de compatibilidad de ambos tipos de ledger.
Los tres proyectos se compilaron/publicaron en Release. No se repitieron todas las 1,306
unitarias y 71 integraciones del checkpoint anterior, ni se las cuentan como ejecutadas
en este corte. El paquete y su ensayo quedaron en `91e2362`; el acta es un commit posterior.

Continúan pendientes las credenciales SQL mínimas por website, dos empresas por rama
en Sandbox, matriz completa de roles/revocación/CRUD, el orden inverso de la carrera
proyecto-calendario y las garantías contables parciales de la
[matriz vigente](multiempresa-accounting-status-20260908.md). Publicar no las completa.

Siguen bloqueadas deliberadamente la creación automática de actividades/transacciones
legacy, las importaciones de plantillas por texto, la edición de maestros globales sin
asociación y los cambios de padre/habitación de ubicaciones existentes. Graph Calendar
permanece deshabilitado por defecto sin binding configurado; las plantillas, cuentas,
categorías, propietarios y mappings necesitan su incremento específico. La corrección
de los pagos históricos requiere identificar relaciones válidas, preservando importes.

La reversión no consiste en arrancar binarios antiguos contra RLS. Los respaldos anteriores
se conservan; después de reabrir producción, cualquier restauración exige detener escrituras
y conciliar las operaciones posteriores para no perderlas. Se prefiere corrección compatible.

Los recibos, logs, verificaciones, checksums y copias privadas están fuera de Git en
`artifacts/production-cutover-20260908`; los previews en
`artifacts/database-migrations/grupocarpio`. No contienen secretos en documentación versionada.

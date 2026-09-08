# Paquete de migración productiva — 2026-09-08

Estado de preparación: siete migraciones nuevas en `database/orion-production-migrations.json`.
La autorización del usuario para publicar sustituye el estado histórico de «producción no autorizada»
de los expedientes anteriores. Este archivo por sí mismo no acredita aplicación ni despliegue;
los recibos del operador y el acta del corte deben identificar lo realmente ejecutado.

## Contrato y diferencias respecto de Sandbox

Se conservan íntegros los siete scripts aplicados y su manifiesto de Sandbox. Los scripts
productivos tienen IDs y checksums propios; no insertan entradas ficticias de Sandbox en el ledger.
Todos requieren catálogo explícito, `ApplyChanges` explícito, `AppVersion` identificable y una
conexión nueva sin contexto de Hospedaje heredado. Sólo admiten `grupocarpio` y el ensayo aislado
`Orion_CutoverValidation_20260908`. Deben probarse con los mismos bytes en una restauración del
respaldo productivo, luego obtener su propio preview productivo antes de apply.

La fundación original permite sólo Sandbox o producción: por eso el paquete incluye una nueva
fundación específica que admite el nombre del clon, sin editar el archivo histórico ni mentir
sobre el nombre de la base. Reutilizar el DDL probado no significa reutilizar atribuciones sin evidencia.

| Migración (prefijo `20260908_production_`) | DDL reutilizado y diferencia productiva |
| --- | --- |
| `platform_foundation` | Fundación aditiva original; exige las dos empresas legacy activas de las instalaciones existentes y versión del artefacto. Genera IDs locales y conserva TaxRfc sin inferir. |
| `public_site_bindings` | Aprovisiona únicamente los dos perfiles existentes; rechaza otros PublicSite y más de una sede Bruno habilitada. No crea empresas. Resuelve CompanyId/SiteId localmente y valida nombre, zona, host, módulo y versiones. |
| `hospitality_public_scope` | Alcance compuesto y relaciones de Hospedaje; preflight exige el inventario productivo revisado de 36 ROOM por tipo, propietarios legacy permitidos, calendario sólo SUITE, reservas sólo OHM/SIN_RFC y evidencia para cada SIN_RFC; verifica también los cinco catálogos. Nunca cambia RFC de reservas ni importes/propiedad de transacciones. |
| `restaurant_public_identity_scope` | Índices, FKs y triggers Identity/membresía del contrato probado; agrega prueba de una sola sede operativa Bruno, dominio exacto y una membresía por identidad. Cuenta sin membresía, cuenta ajena o duplicada bloquea el corte. |
| `public_site_presentation_transition` | Fallback versionado y auditoría; exige bindings productivos registrados y exactamente los dos perfiles activos versión 1 ensayados. No inicia transición ni cambia versiones. |
| `hospitality_legal_consent` | Tupla legal indivisible; exige binding productivo Bonhomia y reporta reservas históricas/sin alcance. No fabrica consentimiento retrospectivo. |
| `hospitality_administration_scope` | RLS fail-closed y controles de relaciones probados; rechaza cruces de pago distintos del OHM/BSU conocido. Reporta el error histórico conservado y no lo reasigna ni elimina. |

## Excepciones conservadas y limitaciones

El usuario clasificó los 288 vínculos OHM/BSU como errores históricos. La migración no conoce
las relaciones correctas y no las inventa: conserva físicamente sus datos y aplica la exclusión
por el predicado de pagos. El preview muestra el conteo observado, que debe compararse con el
baseline del corte; no se presenta como una corrección contable. Cualquier otro patrón contradictorio
bloquea la aplicación administrativa.

El preflight productivo revisado encontró 995 reservas OHM y 335 SIN_RFC, todas estas últimas con señal de pago OHM o calendario SUITE. ROOM contiene 2 ALMACEN, 1 DESCUENTO, 1 INACTIVA, 22 SERVICIO y 10 SUITE; calendarios sólo de SUITE. Los propietarios legacy son 4 y, exclusivamente en suites, 2146/2191: se conservan como IDs de maestro global, no como identidad fiscal. Los catálogos revisados contienen Extra 22, ExperienceProvider 1, Experience 1, ExperiencePackage 3 y ExperienceAddOn 1. Un conjunto diferente o una reserva SIN_RFC sin evidencia bloquea la atribución inicial; no se generaliza esa atribución a futuras instalaciones.

Los datos sin evidencia de empresa/sede deben permanecer sin atribución y aparecer explícitamente
en el preview. Los mappings Outlook huérfanos se conservan y se reportan, con Graph deshabilitado.
Plantillas, cuentas, categorías y propietarios sin mapping siguen bloqueados. La adopción de RLS
exige código que inicialice el contexto SQL: una reversión de binarios antiguos sola no basta.

No se crean clientes futuros, no se introduce OpenClaw, no se alteran dominios/túneles desde SQL,
no se envía correo, no se realizan cobros ni timbrados. Las credenciales mínimas y los procesos
públicos son parte del expediente operativo separado.

## Evidencia requerida para cerrar

### Ensayo completado antes del corte

El usuario autorizó expresamente publicar en producción el 2026-09-08. Se tomó respaldo
COPY_ONLY con CHECKSUM y se verificó con RESTORE VERIFYONLY WITH CHECKSUM. Se restauró
sin sobrescribir otra base en `Orion_CutoverValidation_20260908`; allí se ejecutaron y
revisaron los siete previews, se aplicaron individualmente y el ledger devolvió siete
VERIFIED. La base original seguía operativa durante este ensayo.

Los tres artefactos Release win-x64 arrancaron sobre esa copia con entorno Production
y `/readyz` 200 (puertos temporales 55230/55231/55232). Las integraciones del ensayo
usaron valores inertes y no se hicieron cobros, timbrados ni envíos. Se conservaron
1,330 reservas, 54,319 filas de calendario y 60 identidades/membresías; el preview
administrativo reportó 288 pagos contradictorios excluidos y cuatro mappings Outlook.
Las 45 pruebas unitarias focalizadas de compatibilidad productiva aprobaron. Los
recibos, respaldo y logs están en `artifacts/production-cutover-20260908` y
`artifacts/database-migrations/Orion_CutoverValidation_20260908` (fuera de Git).

El ejecutor admite exclusivamente el nombre concreto de esta copia adicional a sus
dos bases conocidas; no acepta catálogos arbitrarios. Los consumidores aceptan IDs
productivos sin inventar entradas Sandbox; Bruno además exige su checksum exacto.
Los scripts Sandbox previamente aplicados conservan sus bytes.

Este checkpoint todavía no afirma ejecución de migraciones ni despliegue productivo.
El acta posterior registrará el resultado real. El corte invalida sesiones/cookies y
enlaces protegidos públicos anteriores. Las correcciones históricas y garantías
contables pendientes siguen en su matriz; esta publicación no las declara terminadas.

Registrar respaldo con checksum, verificación y restauración; preview/apply/verify por migración,
checksums del paquete final y versión del código; conteos conciliados antes/después; readiness y
smoke por servicio. El ensayo no sustituye los recibos productivos. La reversión requiere detener
escrituras y restaurar el respaldo con código/configuración compatibles, evitando perder operaciones
aceptadas. Toda excepción que impida operar una superficie debe quedar en el acta del corte.

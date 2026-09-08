# E2 — Identidades SQL mínimas por website

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: Bonhomía y Bruno. **Se entrega apagada**: los scripts quedan listos y el
usuario los aplica y cambia la cadena.

## Estado actual

Los perfiles y procesos por instancia están definidos, pero eso no demuestra permisos
SQL mínimos: la matriz contable marca este punto como pendiente de cierre operativo.
Ninguna revisión ha inspeccionado los logins productivos.

## Qué se escribe

Primero, derivar del código la matriz de permisos que cada instancia usa de verdad
—no la que se supone—, leyendo `PublicSiteResolver`,
`HospitalityAdministrationScopeAccessor` y el flujo público de Bruno (Identity,
membresía, cookies, recuperación).

Después, dos scripts aditivos en
`src/OrionERP.Infrastructure/Features/Platform/Sql/`, uno por instancia:

- usuario operativo de `OrionERP.Bonhomia.Web`: binding y readiness, cotización,
  reserva, documentos y sesión RLS;
- usuario operativo de `OrionERP.Bruno.Web`: binding, catálogo publicado, Identity,
  membresía, cookies y recuperación, incluidos índices, claves foráneas y triggers
  de Identity.

Ambos con `GRANT` acotado por objeto. Sin `db_owner`, sin DDL, sin permisos de
administración, sin acceso a la otra instancia.

La identidad del migrador se separa explícitamente y **no aparece en la
configuración pública**.

## Límite

Los secretos van sólo en configuración privada: nunca en archivos versionados, en
este backlog, en recibos ni en salida de consola. No se otorgan permisos generales
de escritura a Restaurante o POS, ni acceso de Bruno a Hospedaje, para resolver un
error de permisos. **No se deshabilita RLS** para que algo funcione.

No se toca todavía la cuenta de la consola.

## Verificación

Build Release si cambia configuración de la aplicación. Los scripts SQL no se
aplican a producción en esta entrega.

# E2 — Identidades SQL mínimas por website

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: Bonhomía y Bruno. **Cerrada y aplicada en producción el 2026-09-10.**

## Estado productivo — verificado 2026-09-10

`orion_bonhomia_web` y `orion_bruno_web` existen como login y usuario de
`grupocarpio`, con cero roles de base y contraseñas distintas generadas fuera del
repositorio. Cada servicio público usa su conexión privada; la consola conserva su
identidad administrativa separada.

Bonhomia necesitó visibilidad de metadatos únicamente sobre
`orion.HospitalityScopePolicy` y `dbo.ROOM_CALENDAR` para comprobar su política y sus
índices en `/readyz`. Es `VIEW DEFINITION` por objeto, sin DDL, escritura adicional ni
bypass. Los tres servicios quedaron en ejecución; Bonhomia y Bruno respondieron 200
en `/readyz` tanto localmente como por sus dominios públicos.

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

Aplicada en producción con identidades sin roles amplios, prueba de conexión directa
y smoke público. Véase el [acta final](../production-final-e7-e8-cutover-applied-20260910.md).

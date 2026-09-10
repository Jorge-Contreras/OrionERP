# E2 — Identidades SQL mínimas por website

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: Bonhomía y Bruno. **Se entrega apagada**: los scripts quedan listos y el
usuario los aplica y cambia la cadena.

## Estado actual — verificado 2026-09-10

Los perfiles y procesos por instancia están definidos, pero eso no demuestra permisos
SQL mínimos. El corte productivo read-only confirmó que `orion_bonhomia_web` y
`orion_bruno_web` no existen todavía como login ni como usuario de `grupocarpio`.
Los tres servicios corren como `LocalSystem` y hoy comparten la identidad SQL `orion`.

Los scripts y la matriz están completos. El cierre es operativo: crear dos contraseñas
fuertes fuera del repositorio, ejecutar los grants, guardar una conexión distinta en la
configuración privada de cada servicio público, reiniciarlos y hacer smoke test. No se
puede cerrar inventando o registrando esas credenciales en archivos versionados.

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

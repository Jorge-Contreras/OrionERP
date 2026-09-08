# Migraciones SQL de OrionERP

> Estado de esta entrega: sólo `Orion_Sandbox` está autorizado. No se debe
> ejecutar `plan`, `preview`, `verify` ni `apply` contra `grupocarpio` hasta la
> aprobación independiente de la fase productiva y su respaldo.

Esta autorización de Sandbox no incluye despliegues, servicios, dominios ni
túneles Cloudflare. Las migraciones aquí documentadas preparan y validan la
base; no publican ninguno de los websites.

Estado inicial comprobado el 2026-09-08: las seis migraciones previas del manifiesto estaban
aplicadas y verificadas en la `Orion_Sandbox` local actual. Al retomar el trabajo
las seis figuraban pendientes en ese entorno; se reconciliaron con su propio
preview y recibo antes de cada aplicación. La verificación final devolvió seis
resultados `VERIFIED`. No se desplegó código ni se modificó infraestructura pública.

Las migraciones administradas nuevas se declaran en
`database/orion-migrations.json` y se ejecutan con
`OrionERP.DatabaseMigrator`. El ejecutor calcula SHA-256 sobre el archivo,
registra el checksum en `orion.SchemaMigration` y rechaza cambios posteriores
de una migración aplicada.

La cadena de conexión nunca se acepta como argumento. Debe estar en la
variable `ASPNETCORE_ConnectionStrings__OrionDb` o en la variable nombrada con
`--connection-env`.

## Flujo de Sandbox

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode inventory --output artifacts/sql-inventory.json
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode plan --database Orion_Sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260901_platform_foundation --preview-receipt <archivo-generado-por-preview>
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox
```

El aprovisionamiento inicial de Bonhomía y Bruno es una migración de datos
separada y permitida únicamente en Sandbox. Después de aplicar y verificar la
fundación, se ejecuta con su propio preview y recibo:

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox --migration 20260902_platform_sandbox_bonhomia_bruno
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260902_platform_sandbox_bonhomia_bruno --preview-receipt <archivo-generado-por-preview>
```

El manifiesto y el propio SQL rechazan cualquier otra base. El script no
asigna `TaxRfc`, no crea empresas ni aprovisiona sitios adicionales.

## Aislamientos públicos de 20260903

Después de verificar las dos migraciones de plataforma se ejecutan, en el orden
del manifiesto, los dos aislamientos limitados a Sandbox. Cada migración exige
su propio preview y su propio recibo con el mismo checksum.

### Host público de Hospedaje

`20260903_hospitality_public_scope_sandbox` agrega el alcance compuesto de
empresa/sede a los agregados usados por el website público de Hospedaje y crea
las relaciones que impiden cruzar habitaciones, calendarios, reservaciones,
extras, experiencias y documentos entre sedes.

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox --migration 20260903_hospitality_public_scope_sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260903_hospitality_public_scope_sandbox --preview-receipt <archivo-generado-por-preview>
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox --migration 20260903_hospitality_public_scope_sandbox
```

El preflight no adjudica maestros globales ambiguos. Las reservaciones sin
evidencia suficiente permanecen sin scope y se reportan para reconciliación
explícita; una contradicción de scope sí detiene la migración. En la Sandbox
reconciliada el 2026-09-08 quedaron 1,329 reservaciones con alcance y ninguna
sin atribuir. La evidencia anterior de tres cotizaciones sin alcance corresponde
al entorno validado el 2 de septiembre y no describe la Sandbox actual.
Esta migración habilita solamente el host público; no
convierte en multiempresa el backoffice, CFDI, Outlook/Graph ni los
procedimientos heredados de Hospedaje.

### Identity y membresía pública de Restaurante

`20260903_restaurant_public_identity_scope_sandbox` enlaza usuarios y
membresías con `PublicSiteId`, reemplaza la unicidad global de correo/usuario
por unicidad dentro del website y agrega llaves foráneas y triggers que impiden
un vínculo entre sitios distintos.

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox --migration 20260903_restaurant_public_identity_scope_sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260903_restaurant_public_identity_scope_sandbox --preview-receipt <archivo-generado-por-preview>
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox --migration 20260903_restaurant_public_identity_scope_sandbox
```

El preflight exige que cada cuenta heredada tenga exactamente su membresía
correspondiente y que no exista evidencia de otro RFC o `PublicSite`. Cualquier
ambigüedad detiene la migración en lugar de asignarla a Bruno automáticamente.

## Activación protegida de presentaciones de 20260904

`20260904_public_site_presentation_transition_sandbox` agrega a
`orion.PublicSite` una pareja fallback de marca/contenido y su vencimiento. Las
tres columnas son nulas o válidas juntas; la restricción impide parejas
parciales, redundantes, no positivas o con una ventana desmedida. El trigger de
auditoría registra la pareja activa, la anterior y su vencimiento.

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox --migration 20260904_public_site_presentation_transition_sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260904_public_site_presentation_transition_sandbox --preview-receipt <archivo-generado-por-preview>
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox --migration 20260904_public_site_presentation_transition_sandbox
```

La aplicación prepara, revierte o finaliza la pareja dentro de la misma
transacción serializable y con control de concurrencia. La migración no cambió
las versiones v1 de `bonhomia-main` ni `brunos-main`; ambos fallback quedaron
nulos después de aplicarla.

## Evidencia legal de checkout de Hospedaje de 20260905

`20260905_hospitality_legal_consent_sandbox` agrega tres columnas nulas a
`dbo.RESERVATION`: versión aceptada del aviso de privacidad, versión aceptada
de términos y sello UTC generado por el servidor. Una restricción confiable
exige que las tres sean nulas para registros históricos o que las tres estén
presentes y las versiones no estén vacías; no existe backfill inferido.

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode preview --database Orion_Sandbox --migration 20260905_hospitality_legal_consent_sandbox
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode apply --database Orion_Sandbox --migration 20260905_hospitality_legal_consent_sandbox --preview-receipt <archivo-generado-por-preview>
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox --migration 20260905_hospitality_legal_consent_sandbox
```

El API exige `Accepted`, `PrivacyVersion` y `TermsVersion` tanto al crear la
orden como al capturarla. Compara ambas versiones con la presentación activa
antes de llamar a PayPal y sólo entrega al servicio de persistencia los valores
autoritativos junto con `DateTimeOffset.UtcNow`.
El servicio de reserva vuelve a comprobar las versiones contra la presentación
del proceso antes de acceder a SQL, incluso si el llamador no usa el API HTTP.

## Orden entre base y aplicación

Los hosts nuevos dependen de las columnas y relaciones de 20260903. El orden
obligatorio por ambiente es:

1. preview, revisión, apply y verify de la migración correspondiente;
2. despliegue posterior del código del host;
3. validación local de `/readyz` antes de exponer tráfico.

Desplegar primero el código deja `/readyz` en 503 por diseño. En esta entrega no
se realizó ese despliegue ni se modificó Cloudflare.

`preview` ejecuta el mismo SQL con `ApplyChanges=0`; la transacción debe quedar
revertida por el propio script. Al concluir escribe un recibo sin secretos bajo
`artifacts/database-migrations`. Incluso Sandbox exige ese recibo antes de
`apply`, garantizando que se revisó exactamente el mismo checksum. El ejecutor
revierte y rechaza cualquier script que termine con una transacción abierta, por
lo que ese estado nunca puede producir un recibo válido.

`verify` devuelve error si una migración está pendiente o si el checksum dejó
de coincidir; sólo `VERIFIED` representa una verificación exitosa.

## Guardas de producción

Esta sección describe las guardas disponibles para una fase futura; no concede
autorización actual sobre `grupocarpio`. Las migraciones `20260903`, `20260904`
y `20260905` ni siquiera incluyen producción en `allowedDatabases`.

Además del respaldo verificado exigido por el proyecto, `apply` contra
`grupocarpio` requiere:

- un recibo de preview de menos de 24 horas con el mismo checksum;
- una referencia de respaldo no vacía;
- la confirmación literal `APPLY grupocarpio`.

Estas guardas no sustituyen la revisión humana del resultado de preview. Los
scripts deben seguir siendo aditivos, idempotentes y contener validaciones
previas y posteriores.

## Scripts históricos

`--mode inventory` clasifica los scripts SQL históricos según tengan guard de
base, modo preview, `XACT_ABORT`, transacción y literales tenant legacy. No los
incorpora automáticamente al ledger: hacerlo fingiría un historial que la base
actual no posee. Se incorporarán sólo mediante baselines explícitos y
reconciliados.


## Aislamiento administrativo de Hospedaje de 20260908

La séptima migración `20260908_hospitality_administration_scope_sandbox` fue
previsualizada, aplicada con su recibo y verificada exclusivamente en Sandbox.
Añade RLS en 18 tablas, defaults de contexto, relaciones compuestas, asociación
fiscal y protección de pagos. Su checksum completo y excepciones históricas se
registran en el [plan actualizado](public-websites-rebaseline-20260908.md).
Los scripts anteriores conservaron sus checksums.

La migración limita dos procedimientos heredados de escritura hasta disponer
de configuración por sede; conserva 288 vínculos de pagos contradictorios y
cuatro mapeos Outlook huérfanos, sin adjudicación automática. Consultar
[el inventario y los límites operativos](hospitality-sql-isolation-20260908.md)
antes de probar consumidores heredados. La política requiere contexto en cada
conexión, incluidos los hosts públicos y los reportes. Esta fase aún no está
lista para producción.

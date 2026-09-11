# Aceptación de plataforma dual neutral

Fecha de validación: 2026-09-11  
Base validada: `Orion_Sandbox`  
Commit de partida: `c9cfbe1`  
Estado: **PASS en Sandbox; producción preparada pero no modificada**

## Alcance entregado

- Contratos y servicios centrales Hospitality/Restaurant sin nombres de cliente.
- `PlatformExecutionScope` inmutable, resolución exclusiva por `PublicSiteKey` y sesiones SQL que limpian y revalidan todo el contexto antes de usar una conexión.
- Binding explícito entre `orion.Site` y `restaurante.Site`; el sitio público ya no resuelve su sede Restaurant comparando `SiteKey`/`SiteCode`.
- Asociaciones de consola con `CompanyId`, settings Restaurant con `PublicSiteId`, bindings de integraciones y de principales SQL.
- Esquema neutral `public_identity`, siete tablas acotadas por `PublicSiteId`, RLS y puente unidireccional de rollback.
- Perfiles de permisos Hospitality y Restaurant versión 2, una cuenta por PublicSite, `Preview`/`Apply` y detección de deriva.
- Provisionador idempotente/reanudable, enumerador de scopes habilitados y leases de jobs por módulo, empresa y sede.
- Hosts ejecutables genéricos `OrionERP.Hospitality.Web` y `OrionERP.Restaurant.Web`, con perfiles sintéticos y los proyectos de marca conservados como compatibilidad visual/operativa.
- Allowlist versionado que bloquea referencias nuevas de marca fuera de las excepciones históricas o temporales.

## Fixture determinista

| Dato | Valor |
|---|---|
| CompanyId | `16` |
| RFC técnico heredado | `TST260910DUAL01` |
| LegacyTenantKey | `synthetic-dual-01` |
| Sede operativa | `dual-campus` (`SiteId=15`) |
| Sede de control negativo | `dual-control` (`SiteId=16`) |
| PublicSite Hospitality | `synthetic-hospitality-main` (`PublicSiteId=15`) |
| PublicSite Restaurant | `synthetic-restaurant-main` (`PublicSiteId=16`) |
| Principal Hospitality | `orion_public_synthetic_h_01` |
| Principal Restaurant | `orion_public_synthetic_r_01` |
| Operación de provisionamiento | `26091000-0000-0000-0000-000000000001` |

Ambos módulos están habilitados en `dual-campus`; ambos están declarados y deshabilitados en `dual-control`. Los dos PublicSites resuelven al mismo `CompanyId` y a módulos distintos.

## Evidencia SQL

La suite reproducible está en `database/validation/20260911_synthetic_dual_acceptance.sql`. Se ejecutó bajo los dos usuarios de base sintéticos y devolvió:

| Comprobación | Resultado |
|---|---:|
| Estado general | `PASS` |
| Filas Hospitality visibles con scope exacto | 1 |
| Filas Restaurant visibles con scope exacto | 1 |
| Aserciones de permisos presentes | 232 |
| Roles amplios en cuentas públicas | 0 |
| Políticas RLS activas y schema-bound | 8 |
| Etapas de provisionamiento completas | 7 |
| Usuarios originales preservados | 64 |
| Usuarios neutral Identity, incluidos 2 sintéticos | 66 |
| Usuarios del esquema de rollback, incluido 1 sintético Restaurant | 65 |
| Modo del bridge | `Reverse` |

También pasaron estas pruebas negativas:

- conexión sin contexto: cero filas;
- contexto parcial: cero filas;
- `PublicSiteId` suplantado: cero filas;
- lectura Hospitality → Restaurant: error de permiso `229`;
- lectura Restaurant → Hospitality: error de permiso `229`;
- scope de una capacidad deshabilitada: error fail-closed `52241`;
- alternancia de scopes sobre conexiones pooled: sin contaminación;
- mismo correo en dos PublicSites: permitido sin compartir cuenta o scope;
- dos adquisiciones concurrentes del mismo lease: solo una obtiene el candado;
- permisos efectivos de objeto/esquema: igualdad exacta con el manifiesto, sin roles de base;
- datos sintéticos: sin RFC, correo, dominio ni identidad comercial de las empresas reales.

Políticas verificadas:

| Esquema / política | Predicados |
|---|---:|
| `contabilidad.AccountingScopePolicy` | 6 |
| `fiscal.DeclarationScopePolicy` | 18 |
| `logistica.InventoryCoreScopePolicy` | 9 |
| `logistica.RfcSecurityPolicy` | 285 |
| `orion.HospitalityScopePolicy` | 81 |
| `orion.PublicIdentityScopePolicy` | 21 |
| `rh.RfcSecurityPolicy` | 48 |
| `rh.WorkforceScopePolicy` | 18 |

## Migraciones y restauraciones de Sandbox

El manifiesto de Sandbox reconoce explícitamente 17 migraciones de desarrollo sustituidas por los paquetes de corte `production_*`. Cada equivalencia incluye la ruta del script sustituto y solo se acepta si el ID está en `orion.SchemaMigration` **y su checksum SHA-256 coincide**. `plan`, `verify`, `preview` y `apply` fallan ante un checksum divergente y omiten de forma segura el script anterior cuando la equivalencia es válida.

Tres mecanismos E7 que no estaban presentes en la copia de Sandbox no se marcaron como equivalentes. Se ejecutaron en orden con `Preview` y luego `Apply`:

1. `20260909_hospitality_legacy_mechanisms_sandbox`
2. `20260909_hospitality_legacy_transaction_guards_sandbox`
3. `20260909_hospitality_payment_policy_switch_sandbox`

La revisión ampliada de compatibilidad añadió `20260911_legacy_write_compatibility`. Esta migración también se ejecutó con `Preview` y `Apply`; conserva temporalmente a los escritores anteriores mediante triggers que solo completan un binding cuando existe una coincidencia central exacta. Los índices siguen siendo únicos para valores resueltos y cualquier coincidencia ausente, ambigua o contradictoria falla cerrada.

La verificación final del manifiesto devuelve únicamente `VERIFIED` o `SATISFIED_BY`; no hay migraciones `PENDING` ni `CHECKSUM_MISMATCH` en Sandbox.

## Evidencia de aplicación

- Compilación completa de la solución: 0 errores, 0 advertencias.
- Pruebas unitarias: 1,383 aprobadas, 0 fallidas.
- Pruebas de integración regulares: 102 aprobadas, 0 fallidas.
- Batería completa de integración SQL: 45 aprobadas, 0 fallidas; incluye las 4 pruebas específicas de scope/pooling/provisionamiento/jobs.
- Escáner de frontera de marca: aprobado.
- Validación de los dos perfiles genéricos: aprobada.
- Smoke Hospitality: `/healthz`, `/readyz` y `/api/availability` respondieron HTTP 200 con datos sintéticos.
- Smoke Restaurant: `/healthz`, `/readyz` y `/api/catalog` respondieron HTTP 200 con datos sintéticos.
- `git diff --check`: sin errores de whitespace.

## Estado de producción

Solo se ejecutó `plan` de lectura contra `grupocarpio`. Las 21 migraciones históricas del manifiesto productivo aparecen aplicadas y las siete nuevas aparecen `PENDING`:

1. `20260911_platform_execution_scope`
2. `20260911_public_identity`
3. `20260911_public_provisioning_profiles`
4. `20260911_public_principal_boundary`
5. `20260911_public_rls_principal_binding`
6. `20260911_rfc_rls_fail_closed`
7. `20260911_legacy_write_compatibility`

No se ejecutó `Preview` ni `Apply` en producción, no se desplegaron binarios y no se tocaron servicios, túneles o secretos. El siguiente corte requiere backup productivo verificado, preview revisado, autorización independiente y canary por PublicSite.

## Reproducción

Con `ASPNETCORE_ConnectionStrings__OrionDb` apuntando exclusivamente a `Orion_Sandbox`:

```powershell
dotnet run --project src/OrionERP.DatabaseMigrator -- --mode verify --database Orion_Sandbox
sqlcmd -b -d Orion_Sandbox -i database/validation/20260911_synthetic_dual_acceptance.sql
$env:ORION_RUN_SQL_INTEGRATION='1'
dotnet test tests/OrionERP.IntegrationTests/OrionERP.IntegrationTests.csproj --filter FullyQualifiedName~PlatformExecutionScopeSqlTests
dotnet test tests/OrionERP.IntegrationTests/OrionERP.IntegrationTests.csproj --filter Category=SqlIntegration
```

Los adaptadores heredados continúan vigentes hasta el 31 de diciembre de 2026 y no deben retirarse antes de dos versiones estables y 30 días sin rollback.

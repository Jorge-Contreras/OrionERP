# E1 — Guardas de suspensión de Restaurante

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: consola `OrionERP.Web`.

## Estado actual

`RestaurantAccountingService` recibe `rfc` y `siteId` como parámetros y sólo llama a
`LogisticsRfc.Require`, que normaliza pero no autoriza. No consulta `CompanyModule`
ni `SiteCapability`. Lo mismo ocurre en producción de Restaurante y en operaciones
de sede. **No existe un scope accessor de Restaurante**; Hospedaje sí tiene el suyo.

## Qué se escribe

Crear `RestaurantScopeAccessor` en
`src/OrionERP.Infrastructure/Features/Restaurante/`, calcado de
[`HospitalityAdministrationScopeAccessor`](../../src/OrionERP.Infrastructure/Features/Reservaciones/HospitalityAdministrationScopeAccessor.cs).
Su consulta reproduce las mismas condiciones con `ModuleCode = PlatformModuleCodes.Restaurant`:

- `orion.CompanyModule.Status = 'Enabled'`
- vigencia `EffectiveFromUtc <= SYSUTCDATETIME() < EffectiveToUtc` (nulos permitidos)
- `orion.Module.IsActive = 1`
- `orion.SiteCapability.IsEnabled = 1` para esa empresa y sede
- `Company.IsActive` y `Site.IsActive`

Devuelve empresa, sede y RFC, y llama a `ICurrentCompanyContext.EnsureRfc` para que
una sede ajena a la sesión no pase.

Aplicarlo, **revalidando antes de cada mutación** y dentro de la transacción cuando
la haya, en:

| Archivo | Métodos |
| --- | --- |
| `Restaurante/RestaurantAccountingService.cs` | `GetDailyPreviewAsync`, `GenerateDailyPolicyAsync`, `GenerateIndividualCfdiPolicyAsync` |
| `Restaurante/RestaurantProductionService.cs` | `GetWorkspaceAsync`, `PlanAsync`, `StartAsync`, `CompleteAsync`, `CancelAsync` |
| `Restaurante/RestaurantCatalogService.cs` | `GetSiteOperationsAsync`, `SaveSiteOperationsAsync` |
| `OrionERP.Web/Features/Restaurante/RestaurantEventBroadcaster.cs` | único job registrado |

Registrar el accessor en `src/OrionERP.Web/Configuration/ServiceRegistration.cs`,
junto a `IRestaurantAccountingService`.

## Límite

`ACCOUNTING_CORE` sigue disponible: la suspensión de `RESTAURANT` no lo apaga.
Suspender no escribe ni elimina datos, y el broadcaster **no consume ni pierde** el
mensaje pendiente: lo deja para cuando el módulo vuelva.

No se reescriben permisos ni se toca el ciclo de pólizas ni la bandeja contable
—eso es E4 y E5—. Si aparecen más de tres consumidores sin guarda fuera de esta
tabla, anota sus nombres exactos y déjalos para un lote siguiente; no presentes
este lote como autorización transversal terminada.

## Verificación

Build Release. Sin pruebas nuevas: es adición de guardas y no cambia importes.

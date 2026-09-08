# Núcleo contable multiempresa: estado comprobado al 2026-09-08

La fundación multiempresa y el aislamiento de Hospedaje no completan todavía el
contrato contable original. Esta revisión conserva ese alcance sin abrir otra
implementación ni modificar datos. **Parcial** significa que existe una parte
concreta implementada; **pendiente** no equivale a que falle toda la función
legacy ni a que se haya reproducido una fuga desde el navegador.

Se leyó el código del checkout principal y los metadatos de `Orion_SandBox`:
`sys.tables`, `sys.columns`, `sys.security_policies`, `sys.security_predicates`,
`sys.sql_modules` y `sys.indexes`. Antes de cada conexión se fijó explícitamente
`Orion_Sandbox` mediante los métodos del connection-string builder; se comprobó
el catálogo antes de abrir y `DB_NAME()` después, sin distinguir mayúsculas.
La revisión inicial no consultó tablas de negocio, producción, secretos ni
integraciones. El incremento posterior de adjuntos ejecutó el fixture descrito
al final en Sandbox. Las suites del checkpoint no se atribuyen como validación
de los pendientes siguientes.

## Matriz contra el objetivo original

| Punto | Estado | Evidencia vigente y cierre que falta |
| --- | --- | --- |
| 1. `CompanyId`, `TaxRfc` y clave legacy separados | **Parcial** | `orion.Company` tiene `CompanyId`, `TaxRfc`, `LegacyTenantKey` y `Rfc`; la fundación no infiere RFC fiscal. `CurrentCompanyContext` todavía identifica la sesión por `CurrentRfc`; `dbo.Transacciones` sólo tiene `RFC` y `Registro_Contable` depende de `TransaccionID`, sin `CompanyId`. Falta trasladar el contrato contable a identidad técnica conservando la compatibilidad legacy y sin convertir `BRUNOS260707L26` en RFC SAT. [Fundación](../src/OrionERP.Infrastructure/Features/Platform/Sql/20260901_platform_foundation.sql), [sesión](../src/OrionERP.Web/State/CurrentCompanyContext.cs). |
| 2. Autorización transversal y suspensión | **Parcial** | `PlatformAdministrationService` impide suspender/eliminar `ACCOUNTING_CORE`. `PublicSiteResolver` y `HospitalityAdministrationScopeAccessor` comprueban empresa, módulo, vigencia y capacidad; Hospedaje revalida sesión/permisos. `RequireCompanyRoles` exige sesión y roles, sin consultar entitlements; `RestaurantAccountingService.GetDailyPreviewAsync` opera con RFC/sede y configuración contable sin una guarda de `CompanyModule`. Falta extender el contrato de habilitación a los servicios/trabajos pendientes y probar revocación real; no basta ocultar menú o usar `restaurante.Site.IsEnabled`. [Políticas](../src/OrionERP.Web/Identity/CompanyAuthorizationPolicyExtensions.cs), [scope Hospedaje](../src/OrionERP.Infrastructure/Features/Reservaciones/HospitalityAdministrationScopeAccessor.cs), [contabilidad Restaurante](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs). |
| 3. Eliminar accesos por ID global y conexiones sin contexto | **Parcial** | `TransaccionService` sí ejecuta `EnsureTransactionScopeAsync`, `EnsureAttachmentScopeAsync` y guardas CFDI antes de numerosas consultas por ID: esas consultas no prueban por sí solas un bypass. **Caso de adjuntos corregido y validado:** `TransactionAttachmentRepository.GetAttachmentAsync` exige empresa antes de abrir SQL y filtra pertenencia en la misma consulta. Un `TranID` propio permite leer; sólo sin `TranID` se acepta el CFDI canónico como emisor/receptor. Se probaron positivos A/B, acceso cruzado, XML compartido, prioridad del vínculo privado y sesión ausente/perdida. La tabla sigue sin RLS; las conexiones directas de `TransaccionService` aún no constituyen una fábrica contable uniforme. El cierre de este repositorio no completa los demás consumidores ni los externos. [Repositorio de adjuntos](../src/OrionERP.Infrastructure/Features/Cfdi/HtmlCFDI/TransactionAttachmentRepository.cs), [servicio de pólizas](../src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs). |
| 4. RLS sin bypass por contexto ausente | **Parcial** | La política nueva de Hospedaje protege 18 tablas con 54 predicados. Las tres funciones legacy `logistica/rh/fiscal.fn_RfcAccessPredicate` conservan `SESSION_CONTEXT(N'OrionRfc') IS NULL OR ...`, comprobado en SQL. `Transacciones`, `Registro_Contable`, `CuentasContables`, `TRANSACTION_ATTACHMENT` y `cfdi.Comprobante` tienen **cero predicados**. La fábrica convierte contexto vacío en `__UNSCOPED__`, defensa útil que no elimina el bypass de una conexión sin inicialización. Falta migración aditiva y adaptación previa de los consumidores afectados, incluida la política de CFDI compartidos. |
| 5. Identidades SQL mínimas por website y migración | **Pendiente de cierre operativo** | Los perfiles y procesos por instancia están definidos; eso no demuestra permisos SQL mínimos. El expediente productivo todavía pide completar identidades y permisos privados. Esta revisión no inspeccionó logins productivos ni afirma que estén separados. Preparar matriz de permisos y cuentas operativas/migración en Sandbox, comprobar acceso permitido/denegado usando esas identidades y agregar el paquete correspondiente al corte. [Expediente](public-websites-production-cutover-review.md). |
| 6. CFDI compartidos por emisor/receptor | **Parcial** | `EnsureComprobanteScopeAsync` y adjuntos sin póliza en `TransaccionService` aceptan a la empresa como emisor **o** receptor. Otros lectores mantienen contratos diferentes: `ComprobanteQueryService.GetUnassignedAsync` filtra sólo receptor y considera asignado un comprobante si existe cualquier `Transaccion_Comprobante`. Falta un contrato común de pertenencia fiscal, asociación por empresa y validación SQL con un CFDI legítimamente compartido; no imponer propiedad exclusiva al documento global. [Lectores de CFDI](../src/OrionERP.Infrastructure/Features/Cfdi/CargarXmlSat/Services/ComprobanteQueryService.cs). |
| 7. Periodos, estados, balance, inmutabilidad y reversa | **Parcial; ciclo formal pendiente** | `GuardarMovimientosAsync` usa `MovimientosCuadreValidator`; hay auditoría SQL de cabecera y movimientos. `GuardarYCerrarAsync` actualiza cabecera, calcula totales y confirma, sin transición a `Posted`; `DeleteMovimientoAsync` permite borrar por póliza autorizada. SQL conserva `Estatus` legacy, sin columnas del ciclo nuevo, y los triggers contables leídos son de auditoría. `fiscal.DeclaracionCierre` existe, pero no equivale al cierre del libro mayor. Restaurante genera movimientos inversos para CFDI tardío; eso no implementa `Draft/Posted/Reversed` e inmutabilidad general. Falta publicación atómica balanceada, periodo abierto, reversa ligada e imposibilidad de editar/borrar lo publicado. |
| 8. Idempotencia y bandeja transaccional hacia Contabilidad | **Parcial** | `RestaurantOrderService.CreateOrderAsync` exige clave de idempotencia y usa transacción serializable; `restaurante.EventOutbox` existe. Su consumidor `RestaurantEventBroadcaster` publica SignalR, no pólizas. `RestaurantAccountingService` crea cabecera/movimientos y después vincula órdenes en otra transacción; los índices únicos `UX_AccountingLink_Daily/Order` existen, pero no hacen atómica toda esa secuencia. Falta contrato contable durable con identidad de operación, consumo idempotente y recuperación ante fallo entre pasos para ambas ramas. [Órdenes](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs), [consumidor actual](../src/OrionERP.Web/Features/Restaurante/RestaurantEventBroadcaster.cs). |
| 9. Reportes basados en pólizas publicadas frente a operación | **Parcial** | `ReportesFinancierosService` exige RFC de sesión para balanza/resultados y usa alcance Hospedaje cuando corresponde a Salud Empresa. SQL `reporteFinanciero.Rpt_BalanzaComprobacion` agrega `Registro_Contable` unido a `Transacciones` por fechas/RFC, sin filtro de publicación. El ciclo `Posted` no existe como garantía uniforme; no presentar estos reportes como basados exclusivamente en pólizas publicadas. Ajustar consultas al introducir ese ciclo y conservar métricas operativas diferenciadas, con baseline conciliado. [Servicio](../src/OrionERP.Infrastructure/Features/ReportesFinancieros/Dapper/ReportesFinancierosService.cs). |
| 10. Conciliación heredada antes de nuevas restricciones | **Parcial** | Las migraciones aditivas y exclusiones de Hospedaje preservan los casos ambiguos. Los **288 vínculos** y **cuatro mappings Outlook** se citan del checkpoint, no como conteos nuevos de esta revisión. El usuario confirmó el 2026-09-08 que los 288 vínculos OHM/BSU son **errores históricos**: la clasificación empresarial queda resuelta. Su corrección permanece pendiente de identificar los vínculos correctos con evidencia; esta decisión no autoriza inferir reasignaciones ni ajustar importes. Mappings, plantillas y propietarios siguen pendientes. Las cifras productivas históricas y los 635 vínculos de la auditoría inicial no son cifras actuales de Sandbox. Falta baseline aprobado y conciliación específica antes de imponer estados, claves o restricciones nuevas. [Pendientes vigentes](multiempresa-pendientes-desde-main.md). |

## Evidencia SQL de esta revisión

| Política habilitada | Tablas | Predicados |
| --- | ---: | ---: |
| `logistica.RfcSecurityPolicy` | 98 | 294 |
| `rh.RfcSecurityPolicy` | 22 | 66 |
| `fiscal.RfcSecurityPolicy` | 6 | 18 |
| `orion.HospitalityScopePolicy` | 18 | 54 |

Los nombres de política no delimitan por sí solos el esquema de sus tablas:
la de Logística también contiene tablas de Restaurante y Fidelidad. Esos
conteos describen metadatos del Sandbox consultado, no cantidad de empresas,
registros reconciliados, cobertura funcional ni permisos productivos.

## Orden de cierre acotado

1. Terminar las validaciones focalizadas ya abiertas. El acceso concreto a
   adjuntos por ID quedó corregido con pruebas negativas; continuar con los
   consumidores pendientes identificados, sin reiniciar la arquitectura.
2. Cerrar entitlements de servicios/trabajos pendientes y preparar identidades
   SQL mínimas. Sólo entonces retirar el bypass legacy de contexto ausente con
   migraciones aditivas y pruebas de los consumidores realmente afectados.
3. Implementar el ciclo contable y contrato idempotente en un incremento
   dedicado; derivar de él los filtros de reportes. La conciliación empresarial
   pendiente limita cambios de datos, no la preparación de código y fixtures.

Este documento es diagnóstico acotado y backlog verificable. No modifica
migraciones aplicadas, no cierra los bloqueos deliberados del checkpoint y no
es aprobación ni evidencia de ejecución productiva. El paquete de corte debe
declarar expresamente qué invariantes cubre la versión que se proponga.

## Incremento de adjuntos validado

`TransactionAttachmentScopeTests`: **2/2 aprobadas**, con
`ORION_RUN_SQL_INTEGRATION=1` para el fixture de datos. Incluye dos empresas de
Sandbox (`OHM191112Q26` y `BSU210121M77`), cabeceras/adjuntos/CFDI sintéticos
propios y limpieza por sus IDs. El CFDI compartido es una prueba sintética del
contrato emisor/receptor; no clasifica ni corrige los 288 errores históricos.
La prueba sin empresa usa una conexión inalcanzable y acredita rechazo antes
de abrir SQL. No se ejecutan timbrado, correo ni pagos. Se valida el servicio
con contextos de prueba, no un login de navegador con dos usuarios reales.

Build Release de IntegrationTests y dependencias: **0 errores, 0 advertencias**.
Pruebas unitarias relacionadas `HospitalityFiscalScopeTests`: **14/14 aprobadas**.
Recibos locales: `artifacts/accounting-attachment-scope-20260908/`.
No se repitieron suites completas ni se aplicó migración; la protección nueva
es un filtro de acceso en el repositorio, no RLS adicional.

# E3 — Identidad contable técnica y contrato CFDI

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: nada.
Superficie: consola. **Es la base de E4, E5, E6 y E8**; conviene hacerla temprano.

## Estado actual

- `CurrentCompanyContext` identifica la sesión únicamente por `CurrentRfc`; no
  conoce `CompanyId`.
- `TransaccionService` abre unas treinta y cinco conexiones con
  `new SqlConnection(_cs)` sin fijar contexto de sesión. Sus guardas
  `EnsureTransactionScopeAsync` / `EnsureAttachmentScopeAsync` /
  `EnsureComprobanteScopeAsync` funcionan, pero no son una fábrica uniforme.
- `dbo.Transacciones` sólo tiene `RFC`, y `Registro_Contable` cuelga de
  `TransaccionID`. Ninguna tiene `CompanyId`.
- `ComprobanteQueryService.GetUnassignedAsync` filtra **sólo receptor**
  (`WHERE r.RFC = @Rfc`) y da por asignado un comprobante si existe cualquier
  `Transaccion_Comprobante`, sea de la empresa que sea.

## Qué se escribe

**1. `CompanyId` en la sesión.** `ICurrentCompanyContext` gana `CompanyId`, resuelto
desde `orion.Company` por el vínculo exacto de la clave legacy. Sin inferir `TaxRfc`
y sin sustituir `Rfc` en masa. Empresa ausente falla antes de tocar tablas.

**2. Fábrica de conexiones contables.** Una fábrica que fije
`SESSION_CONTEXT(N'OrionRfc')` antes de operar, siguiendo el patrón de
[`SqlConnectionFactory`](../../src/OrionERP.Infrastructure/Features/Cfdi/DescargaMasiva/Dapper/SqlConnectionFactory.cs)
y de `HospitalityConnectionFactory`. Reemplaza las conexiones directas de
`TransaccionService` y de sus escritores inmediatos.

**3. Migración aditiva.** `CompanyId` **nullable** en `dbo.Transacciones` y
`dbo.Registro_Contable`, con backfill exclusivamente por el vínculo legacy exacto
con `orion.Company`. Sin `NOT NULL`, sin RLS, sin índices que rompan lectores
actuales. Los casos ambiguos quedan reportados, no adivinados.

**4. Contrato fiscal único.** Extraer el predicado emisor **o** receptor de
`TransaccionService.EnsureComprobanteScopeAsync` a un helper compartido, y aplicarlo a:

- `ComprobanteQueryService.GetUnassignedAsync`: acceso por emisor o receptor, y
  "asignado" evaluado **por empresa**, no por la existencia de cualquier vínculo global.
  Un CFDI global visible a dos empresas sigue pendiente para la que no lo ha asignado.
- la asociación `Transaccion_Comprobante`, que rechaza duplicar dentro de la misma empresa.

Conservar la prioridad del `TranID` privado en adjuntos, ya corregida en `ecf3e46`.

## Límite

No se impone propiedad exclusiva al CFDI ni se duplica el XML canónico. No se abren
adjuntos privados de una póliza ajena. No se tocan descargas del SAT ni timbrado.
No se imponen `NOT NULL` ni RLS antes de acreditar los escritores afectados: eso es E8.
Los consumidores legacy siguen leyendo por `RFC`.

## Verificación

Build Release. Unitarias mínimas del comportamiento nuevo: resolución de `CompanyId`
(presente, ausente, de otra empresa) y el predicado emisor/receptor.

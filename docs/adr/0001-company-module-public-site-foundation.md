# ADR 0001: Empresa, módulos y sitios públicos

- Estado: Aceptada
- Fecha: 2026-09-01

## Contexto

OrionERP sirve varias empresas desde una base SQL compartida. Contabilidad es
obligatoria; Hospedaje y Restaurante son capacidades opcionales. Los websites
públicos deben reutilizar un host genérico por capacidad, pero cada website se
ejecutará como una instancia aislada con puerto, dominio y túnel Cloudflare
propios.

La columna histórica `Rfc` mezcla RFC fiscal, identificador tenant y marca. El
valor `BRUNOS260707L26` confirma que no todos los identificadores actuales son
RFC SAT. Por ello no puede seguir siendo la identidad técnica futura.

## Decisión

OrionERP continuará como monolito modular con una base SQL compartida.

- `CompanyId` será la identidad técnica inmutable de empresa.
- `TaxRfc` representará exclusivamente el RFC fiscal; será nullable durante la
  transición y único cuando exista.
- `Rfc` seguirá siendo la clave legacy hasta concluir la migración; ningún
  script de fundación reasignará empresas o sentinelas ambiguos.
- `Site` representará una unidad operativa propiedad de una empresa.
- Los módulos canónicos serán `ACCOUNTING_CORE`, `HOSPITALITY` y `RESTAURANT`.
- `ACCOUNTING_CORE` es obligatorio y no puede suspenderse mediante
  `CompanyModule`.
- `CompanyModule` y `SiteCapability` son entitlements; no se derivan de roles,
  menús ni flags de reportes.
- `PublicSite` representa una instancia pública y siempre pertenece a una
  empresa, un sitio y un módulo habilitado para ambos.
- `PublicSiteKey` es una clave operativa inmutable. El proceso público la toma
  de configuración confiable al arrancar; ningún request selecciona empresa o
  sitio.
- Un mismo artefacto de Hospedaje o Restaurante se despliega N veces, con
  servicio, puerto loopback, configuración, secretos, key ring y túnel
  independientes.

## Topología de websites públicos

Habrá solamente dos codebases públicas reutilizables: una para
`HOSPITALITY` y otra para `RESTAURANT`. No se creará ni bifurcará un proyecto
por RFC, empresa, sede o marca.

Los nombres actuales `OrionERP.Bonhomia.Web` y `OrionERP.Bruno.Web` son nombres
legacy de esas dos ramas funcionales; su conversión y eventual cambio de nombre
se aprobarán aparte. Agregar, por ejemplo, una marca como Los Patos consistirá
en provisionar datos y desplegar otra instancia del artefacto de Hospedaje, no
en crear un tercer proyecto.

Cada instancia escucha en un puerto local propio y recibe por configuración un
`PublicSiteKey` fijo, además de la empresa, sede, módulo y dominio esperados. Un
túnel Cloudflare independiente, bajo la cuenta del cliente correspondiente,
expone ese puerto en su dominio. Ni el dominio solicitado ni parámetros del
request pueden seleccionar otro tenant en tiempo de ejecución.

## Restricciones de la primera entrega

La fundación es aditiva. No activa módulos opcionales, no crea Los Patos, no
modifica reservaciones y no cambia producción. Los identificadores
`BRUNOS260707L26`, `SEED`, `SIN_RFC`, `NO_RFC` y vínculos interempresa quedan
pendientes de reconciliación explícita.

Las migraciones nuevas deben estar en el manifiesto administrado, ejecutarse
primero con `ApplyChanges=0` y registrar checksum. Producción exige además un
respaldo verificado y una aprobación independiente.

El ADR 0002 aclara que el binding por instancia ya implementado no autoriza un
segundo cliente mientras Hospedaje, Identity y branding sigan sin tenantizar.

## Consecuencias

La consola podrá administrar la plataforma con una fuente única de verdad y
los futuros hosts podrán resolver un contexto fijo. Las consultas y tablas
legacy todavía no quedan aisladas por agregar esta fundación; la aplicación de
`CompanyId`, RLS fail-closed, claves compuestas y tenantización de Hospedaje
pertenece a fases posteriores.

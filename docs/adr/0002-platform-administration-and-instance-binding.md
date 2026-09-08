# ADR 0002: Administración y binding de instancias públicas

- Estado: Aceptada; aclarada y parcialmente sustituida por el ADR 0003
- Fecha: 2026-09-02

> Evolución: el ADR 0003 implementa en `Orion_Sandbox` el aislamiento del
> recorrido público de Hospedaje y de Identity/membresías de Restaurante. Eso
> elimina parcialmente dos pendientes enumerados aquí, pero no levanta la
> restricción de desplegar un segundo RFC: el resto de Hospedaje y la capa de
> marca/contenido de ambos websites siguen sin estar listos.

## Contexto

La fundación de plataforma ya distingue empresa, sede, módulo, capacidad y
website público. La siguiente entrega debe permitir administrar esa estructura
sin aceptar un tenant desde el navegador y preparar el modelo operativo en el
que cada dominio usa su propio proceso, puerto loopback y túnel Cloudflare.

Al momento de aceptar esta decisión, los módulos heredados todavía no estaban
completamente tenantizados. En Hospedaje existían tablas globales de
habitaciones, calendarios y reservaciones; en Restaurante, la identidad y
recuperación de cuentas todavía eran globales. Los dos proyectos públicos
conservaban además contenido y recursos de marca específicos de Bonhomía y
Bruno's. El ADR 0003 registra el avance posterior y sus límites.

## Decisión

Se implementan dos incrementos:

1. Una administración company-scoped para identidad de plataforma, sedes,
   asignaciones de módulos, capacidades y websites. El alcance se deriva de la
   sesión autenticada; los comandos no aceptan `CompanyId` ni RFC. Las
   escrituras usan `rowversion`, transacción serializable y auditoría de SQL.
2. Una fundación de binding y despliegue por instancia para las dos ramas
   públicas. Cada proceso recibe configuración fija de `PublicSiteKey`, empresa
   esperada, sede, módulo, dominio y puerto; el request nunca selecciona el
   tenant. El proceso falla cerrado si la cadena registrada no coincide.

Los bindings actuales de Bonhomía y Bruno se aprovisionan mediante una
migración de datos permitida exclusivamente en `Orion_Sandbox`. No se infiere
`TaxRfc`, no se crea Los Patos y no se modifica `grupocarpio`.

## Restricción de despliegue

El segundo incremento, por sí solo, no convierte los artefactos en aplicaciones
multiempresa seguras. El ADR 0003 completa únicamente partes de esta lista.
Queda prohibido publicar un segundo cliente hasta que se aprueben e implementen
los pendientes que continúan vigentes:

- tenantización de todo flujo de Hospedaje ajeno al host público ya aislado;
- aislamiento de roles y logins externos de Restaurante antes de habilitarlos;
- branding, contenido, datos legales y recursos por `PublicSite`;
- pruebas negativas de aislamiento entre dos empresas reales de Sandbox.

## Consecuencias

La consola ya puede mantener la topología y las instancias actuales quedan con
puerto, dominio, configuración, cookies y key rings independientes. La próxima
fase podrá trabajar sobre una identidad técnica estable, pero no deberá
confundir esa preparación operativa con aislamiento de datos terminado.

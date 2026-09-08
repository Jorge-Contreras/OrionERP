# ADR 0003: Aislamiento público de Hospedaje e identidad de Restaurante

- Estado: Aceptada con alcance exclusivo de Sandbox
- Fecha: 2026-09-02
- Aclara y sustituye parcialmente: ADR 0002

## Contexto

El binding definido en el ADR 0002 fija una empresa, sede y módulo por proceso,
pero esa identidad operativa no bastaba mientras los datos y Identity
continuaran globales. Se eligieron dos incrementos que reducen el riesgo sin
autorizar producción ni el alta de otro cliente:

1. aislar el recorrido de reservación que ejecuta el host público de
   Hospedaje;
2. aislar las cuentas públicas y membresías que ejecuta el host de
   Restaurante.

Ambos incrementos se diseñaron y validaron solamente para `Orion_Sandbox`.
No forman parte de esta decisión una migración de `grupocarpio`, un despliegue
de servicios ni la creación o modificación de túneles Cloudflare.

## Decisión para Hospedaje

El host público reutilizable de Hospedaje obtiene su alcance exclusivamente del
`PublicSiteBinding` verificado y lo expresa como la pareja inmutable
`CompanyId`/`SiteId`. Ninguna ruta, encabezado, dominio solicitado, cotización
o parámetro del cliente puede elegir ese alcance.

La migración `20260903_hospitality_public_scope_sandbox` agrega el alcance
compuesto y sus relaciones a los agregados usados por el recorrido público:
habitaciones, calendario, reservaciones, extras, experiencias, vínculos de
pago, adjuntos y desglose de Airbnb. El vínculo de cliente se resuelve mediante
`orion.HospitalitySiteCustomer`, sin adjudicar automáticamente los maestros
globales `Clientes` o `Transacciones` a una empresa.

El host público usa lectores y escrituras propios que incluyen ambos
identificadores en cada consulta. Las cotizaciones incorporan `PublicSiteKey`,
por lo que una cotización emitida por otro website se rechaza. `/readyz`
comprueba el ledger, las columnas, relaciones e inventario mínimo de la sede y
responde 503 si el esquema aislado no está disponible.

Las órdenes PayPal quedan ligadas a la cotización protegida mediante
`custom_id` y `reference_id`. El host verifica ambos valores antes de capturar
el pago y nuevamente antes de crear la reservación; una orden de otra
cotización o website se rechaza sin capturarla. La clave de idempotencia también
incluye un hash de la cotización para evitar colisiones entre instancias que
compartan una cuenta comercial.

Este alcance no tenantiza el módulo completo de Hospedaje. Permanecen fuera de
la decisión y bloquean un segundo RFC:

- CRUD, consultas y reportes del backoffice de OrionERP;
- integraciones y acciones de OpenClaw;
- generación y flujos CFDI;
- sincronización de calendarios y correo mediante Outlook/Graph;
- procedimientos almacenados, jobs y consultas heredadas que todavía operan
  con claves globales o RFC legacy;
- cualquier otro consumidor de `Clientes`, `Transacciones` o tablas de
  reservaciones que no use el contrato público aislado.

Por ello esta fase se denomina aislamiento del **host público**, no
tenantización completa de Hospedaje.

## Decisión para Restaurante

Las cuentas de `brunos_auth.AspNetUsers` y las membresías de
`fidelidad.MemberAccount` se ligan a `PublicSiteId`. La relación con
`orion.PublicSite` fija de manera verificable la empresa, sede y módulo
`RESTAURANT`.

La migración `20260903_restaurant_public_identity_scope_sandbox` sustituye la
unicidad global de usuario y correo por unicidad dentro de cada `PublicSite`, y
agrega relaciones directas y compuestas que impiden enlazar una membresía con
una identidad de otro sitio. El código usa un store y filtro de Identity
tenant-aware para registro, login, confirmación, reenvío, recuperación,
restablecimiento y carga de la sesión. Los claims y la cookie deben coincidir
exactamente con el binding verificado; un claim ausente, duplicado o ajeno
invalida la sesión.

Las operaciones públicas de perfil, verificación, QR, consentimientos y baja
también exigen el mismo RFC y `PublicSiteId`. `/readyz` verifica columnas,
composición de índices y llaves foráneas, triggers e integridad antes de
declarar lista la instancia.

La identidad básica y la membresía pública quedan aisladas, pero siguen
pendientes:

- branding, contenido, textos legales, remitentes y recursos gráficos por
  `PublicSite`;
- la eliminación de nombres y presentación específicos de Bruno's del
  artefacto reutilizable;
- roles públicos por tenant, si en el futuro se habilitan;
- logins externos por tenant, si en el futuro se habilitan, porque su clave
  primaria heredada continúa siendo global.

Actualmente los roles y logins externos no forman parte del recorrido público
habilitado. No deberán activarse para un segundo cliente hasta completar ese
aislamiento.

## Límite de base y operación

Las dos migraciones se declaran en el manifiesto exclusivamente para
`Orion_Sandbox` y sus propios scripts rechazan cualquier otra base. Se
ejecutaron allí mediante preview con checksum, revisión del resultado, apply
con el recibo correspondiente y verify. El código dependiente sólo puede
desplegarse después de esas migraciones; esta decisión no incluye ese
despliegue.

Esta decisión no autoriza ninguna operación sobre `grupocarpio`. Tampoco
autoriza publicar los nuevos binarios, cambiar servicios, asignar puertos
productivos, configurar dominios ni crear o modificar túneles Cloudflare.

## Consecuencias

La implementación incorpora dos límites de seguridad verificables y
negativos: el host público de Hospedaje no puede leer ni escribir otra
empresa/sede, y las cuentas o membresías de Restaurante no pueden cruzar otro
`PublicSite` incluso cuando comparten correo. Las migraciones quedaron aplicadas
y verificadas únicamente en `Orion_Sandbox`; no se activó código productivo.

Sin embargo, un nuevo `PublicSite` sigue siendo sólo configuración de
plataforma. No podrá recibir datos reales ni tráfico público hasta completar
los pendientes indicados, repetir pruebas con dos empresas de Sandbox y obtener
aprobaciones independientes para migración productiva y despliegue.

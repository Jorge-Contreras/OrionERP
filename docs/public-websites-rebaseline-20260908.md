# Websites compartidos: plan revisado al 2026-09-08

## Arquitectura vigente

Un único proyecto reutilizable para Hospedaje y otro para Restaurante. Los
nombres técnicos actuales `OrionERP.Bonhomia.Web` y `OrionERP.Bruno.Web` se
conservan para mantener compatibilidad de compilación y publicación; la marca
visible, contacto, contenido y recursos se obtienen del perfil de cada instancia.
Cambiar de cliente no requiere copiar ni crear otro proyecto.

Cada website tiene su propio `PublicSiteKey`, empresa, sede, perfil versionado,
proceso y puerto loopback. El túnel de la cuenta Cloudflare del cliente apunta
a ese puerto. El dominio recibido valida el destino, pero no selecciona el RFC.
La base operativa y el acceso a cada dato se validan por el alcance configurado.

OpenClaw está retirado del código ejecutable y de este plan. Sus menciones en
ADR o comentarios de migraciones se mantienen como historial; no se alteran
los bytes de migraciones registradas.

## Fases completadas en esta reanudación

1. **Integración con el repositorio actual.** El punto recuperado estaba en
   `fb292e7`; se integraron los 27 commits hasta `main` `4e45213` en la rama
   `codex/public-sites-rebaseline`. Se conservaron el rediseño público de
   restaurante y los avances recientes de la consola, resolviendo los conflictos
   con la presentación parametrizada. El checkpoint `2e85544` conserva el trabajo
   recuperado anterior a la integración.
2. **Correcciones de aislamiento y compatibilidad.** El catálogo público dejó
   de usar el fallback privado del POS; se corrigió la selección por horario y
   sede, incluida la madrugada del día siguiente. Se bloquearon cambios de
   dominio mientras el website está activo o conserva una transición vigente.
   Hospedaje respeta la precedencia de variables sobre secretos de desarrollo y
   el servicio de reservas rechaza versiones legales obsoletas antes de SQL.
   Se corrigieron las regresiones de promociones, búsqueda, proveedores y
   cobertura de navegación aparecidas al integrar `main`.
   El indicador de horario distingue abierto, cerrado y desconocido; admite
   horarios parciales y valida tipos JSON sin romper la página.
3. **Reconciliación del Sandbox y validación local.** La base actual no contenía
   las seis migraciones del manifiesto. Se ejecutaron sus previews, aplicaciones
   con recibos y verificación final exclusivamente en `Orion_Sandbox`.

## Evidencia y alcance de validación

- Compilación completa de la solución en Release: cero errores y advertencias.
- Suite unitaria completa: 1,212 casos aprobados; incluye aislamiento, configuración,
  presentación, consentimiento y regresiones de la integración.
- Suite de integración: 55 casos aprobados con `ORION_RUN_SQL_INTEGRATION=1`
  y conexión fijada a `Orion_Sandbox`. La generación opcional de artefactos de
  reportes (`ORION_RUN_EXPORT_QA`) quedó desactivada y no se cuenta como QA visual.
- Seis migraciones del manifiesto con resultado final `VERIFIED`.
- En Hospedaje: 36 habitaciones, 54,319 filas de calendario, 1,329 reservaciones
  y 827 clientes con alcance; cero reservaciones sin atribuir en este Sandbox.
- En Restaurante: 60 cuentas Identity, 60 membresías y 60 vínculos aislados.
- Los perfiles Bonhomia y Bruno pasaron `Publish-All-prod.ps1 -ValidateOnly`,
  incluso con un override de direcciones conflictivo que el preflight aisló y
  restauró. No se copiaron archivos a producción.
- Arranque local en `127.0.0.1:55010` y `127.0.0.1:55020`: página inicial y
  `/readyz` respondieron 200, mostraron su marca configurada y rechazaron un
  host ajeno con 400. Los procesos temporales se cerraron.

La prueba de arranque fue HTTP y de configuración, no una prueba E2E completa
de navegadores, pagos, correo ni dos empresas simultáneas. El checkout se probó
con dobles de PayPal; no se realizaron cobros ni envíos reales. Los scripts
históricos de otras funcionalidades recibidos de `main` no se aplicaron por
el simple hecho de integrar el código.

## Fases restantes, en orden

1. **Completar Hospedaje multiempresa en Sandbox.** Inventariar y aislar CRUD,
   consultas y reportes administrativos, clientes, transacciones, CFDI,
   calendarios Outlook/Graph, procedimientos y jobs que todavía consumen datos
   globales o RFC heredados. Resolver los mapeos con evidencia explícita y
   preparar migraciones aditivas por consumidor. Criterio de cierre: todos los
   consumidores de una reserva respetan empresa y sede, incluso fuera del host
   público. Ésta es la siguiente fase recomendada.
2. **Prueba completa con dos empresas en Sandbox.** Preparar dos perfiles y
   datos de prueba por rama usando los mismos proyectos. Verificar marcas,
   catálogos, horarios, cookies, recuperación de cuentas, documentos y reservas;
   incluir intentos de acceso cruzado y transiciones/reversiones de perfiles.
   Para pagos y correo usar únicamente entornos de prueba. Criterio de cierre:
   evidencia positiva y negativa reproducible por empresa y sede.
3. **Preparar el corte productivo para revisión.** Crear las migraciones
   productivas específicas; los scripts limitados a Sandbox no se habilitan para
   producción cambiando sólo su lista de bases permitidas. Preparar perfiles,
   servicios, puertos, cuentas/túneles, credenciales privadas, respaldo y
   reversión. Contemplar la invalidación esperada de cookies y enlaces por el
   cambio de Data Protection. Esta preparación no implica ejecutar el corte.
4. **Migrar `grupocarpio` al último.** Requiere la aprobación productiva
   independiente indicada por el usuario. Después: respaldo verificado,
   `ApplyChanges=0`, revisión del resultado, `ApplyChanges=1`, verificación,
   despliegue y prueba local antes de habilitar tráfico por los túneles.

Los roles públicos y accesos externos sólo necesitan una fase adicional de
aislamiento si se habilitan. La edición central de textos y carga de logotipos
desde la consola puede añadirse como mejora: los perfiles ya permiten
personalizar las instancias. No es necesario crear websites específicos por RFC.

# Corte productivo de websites compartidos — expediente para revisión

**Estado al 2026-09-08: NO EJECUTADO. NO AUTORIZADO PARA PRODUCCIÓN.**

Este documento prepara la revisión futura. No acredita consultas, respaldos,
migraciones, cambios de servicios, túneles, pagos ni correo en producción.
`grupocarpio` se migrará al último y requiere aprobación productiva independiente.
El trabajo autorizado actual comprende código y `Orion_Sandbox`.

## Bloqueos de salida

Actualización del 2026-09-08: el usuario clasificó los 288 vínculos OHM/BSU
como **errores históricos**. La decisión ya está tomada; continúa pendiente
su corrección con evidencia de las relaciones correctas y el baseline posterior.
Esta aclaración no autoriza una reasignación inferida ni cambia el estado
productivo del expediente. El [estado contable](multiempresa-accounting-status-20260908.md)
detalla las garantías aún parciales que deben considerarse en el paquete.

1. La fase de Hospedaje multiempresa no está cerrada: el inventario de Sandbox
   identificó **288 pagos cruzados** que necesitan atribución con evidencia. No
   se reasignan por coincidencia de nombre, fecha o RFC heredado.
2. Los procedimientos de plantillas que no tienen mapping explícito de empresa
   y sede permanecen bloqueados. Deben inventariarse, atribuirse y probarse antes
   de activar consumidores dependientes.
   El preview de Sandbox también identificó cuatro mapeos Outlook con referencia
   de reserva huérfana o fuera del alcance; se conservan excluidos de reconciliación
   hasta resolver su atribución con evidencia.
3. Falta el cierre reproducible con dos empresas y dos sedes para reservas,
   clientes, cobros, CFDI, reportes, Graph y jobs. Los dobles HTTP comprueban el
   comportamiento del código; no prueban permisos remotos ni configuración real.
4. Se necesitan migraciones **específicas de producción**, revisadas y con su
   preview. Los scripts cuyo contrato permite únicamente `Orion_Sandbox` no se
   convierten en scripts productivos editando su lista de bases permitidas.
5. No se ha comprobado aquí la titularidad de cuentas Cloudflare, IDs de túnel,
   credenciales de servicio, certificados de respaldo ni puertos efectivos.
   Deben registrarse en el expediente operativo sin publicar secretos.
6. Falta aprobación independiente que identifique versión, scripts, ventanas,
   servicios afectados, plan de reversión y responsable de decisión.

## Instancias previstas según el repositorio

Los valores siguientes proceden de `deployment/public-sites/*.json` y
`Publish-All-prod.ps1`; no afirman el estado de máquinas o DNS productivos.

| Superficie | Perfil / binding | Empresa / sede | Servicio Windows | Destino loopback | Host público |
| --- | --- | --- | --- | --- | --- |
| Consola OrionERP | Sin perfil público; sesión administrativa | Alcance autorizado del usuario | `OrionERP` | `127.0.0.1:5000` | `orionerp.orion.land` |
| Hospedaje | `bonhomia-main.json`, `PublicSiteKey=bonhomia-main`, `HOSPITALITY` | RFC `OHM191112Q26`, `SiteKey=bonhomia-suites` | `OrionERP.Bonhomia` | `127.0.0.1:5010` | `bonhomiasuites.com` |
| Restaurante | `brunos-main.json`, `PublicSiteKey=brunos-main`, `RESTAURANT` | RFC `BRUNOS260707L26`, `SiteKey=brunos-01` | `OrionERP.Bruno` | `127.0.0.1:5020` | `brunosgarden.com` |

Ambos perfiles existentes declaran `BrandingVersion=1` y `ContentVersion=1`.
Los identificadores numéricos CompanyId/SiteId deben obtenerse del binding
verificado en el ambiente autorizado; no se copian IDs de Sandbox a producción.
Los proyectos reutilizables siguen siendo `OrionERP.Bonhomia.Web` y
`OrionERP.Bruno.Web`; un cliente adicional necesita perfil, proceso y puerto
propios, sin duplicar el proyecto.

Para cada fila pública, completar antes del corte: titular de la cuenta
Cloudflare del cliente, identificador de cuenta, ID/nombre de túnel, servicio
local del conector, hostname y origen loopback exacto. Registrar también el
túnel de la consola. Un túnel de cliente no debe compartir por conveniencia la
identidad ni las credenciales del túnel de otro cliente. Los IDs aún no están
verificados; no se inventan en este expediente.

## Paquete revisable antes de solicitar aprobación

Congelar commit limpio y artefactos Release. Registrar SHA-256 de cada binario,
perfil público, script de migración nuevo, manifiesto y evidencia de pruebas.
Conservar el ledger existente y los bytes de migraciones ya registradas.
Comparar cada checksum con el paquete ensayado en Sandbox; un cambio exige
repetir el preview afectado y volver a revisar el paquete.

El paquete debe incluir las excepciones de atribución resueltas con sus recibos,
la matriz de consumidores y pruebas positivas/negativas por empresa y sede,
pruebas de recuperación de cuentas y documentos, validación de consentimiento y
versiones, transiciones/reversiones y comportamiento sin alcance. El preflight
de los publicadores se documenta por servicio; aprobar un preflight no constituye
aprobación para desplegar.

Preparar un registro privado de configuración con nombres de almacenes y
referencias de secretos, sin sus valores: conexión `ConnectionStrings:OrionDb`
o `ASPNETCORE_ConnectionStrings__OrionDb`, identidades de servicio, permisos SQL,
PayPal por empresa, Graph/correo por empresa y sede, y tokens del conector.
Mantener Graph deshabilitado hasta disponer de binding validado explícito y de
autorización específica de integraciones reales. Los defaults públicos de
`BonhomiaGraphCalendarSync` son vacíos y `Enabled=false`.

## Secuencia futura, sólo después de aprobación independiente

1. Abrir ventana controlada, detener nuevas escrituras/tráfico de las superficies
   afectadas y registrar versión/configuración anterior. La decisión de pausa y
   los responsables deben formar parte de la aprobación.
2. Crear respaldo de `grupocarpio` con checksum; verificarlo y ensayar restauración
   en un destino aislado. Registrar cadena de recuperación, ubicación privada,
   hora y recibo. Un archivo existente sin validación no basta. Respaldar además
   configuración privada y key rings con protección y acceso restringido.
3. Ejecutar cada migración productiva con `ApplyChanges=0`. Revisar base destino,
   CompanyId/SiteId/RFC, conteos, huérfanos, asociaciones cruzadas, dependencias,
   bloqueos, permisos y plan de reversión. Guardar salida y checksums. Detenerse
   ante cualquier diferencia respecto del plan autorizado.
4. Aplicar exactamente el mismo paquete con `ApplyChanges=1`; guardar recibos y
   verificar ledger, esquema, relaciones, policy RLS, defaults, triggers y
   operaciones autorizadas/rechazadas. No continuar con atribuciones pendientes.
5. Desplegar el código compatible mediante los publicadores existentes, por
   servicios explícitos. Conservar `App_Data` y configuración privada; comprobar
   el perfil público materializado y retirar overrides heredados de
   `ASPNETCORE_URLS`, `HTTP_PORTS`, `HTTPS_PORTS` y `Kestrel:Endpoints` que
   contradigan el puerto del binding.
6. Validar `/readyz` en cada loopback antes del tráfico. En los websites, verificar
   binding activo, marca y versiones correctas, rechazo de host ajeno y bloqueo
   de IDs de otra empresa/sede. `/healthz` sólo prueba que el proceso vive.
7. Confirmar que cada túnel, en la cuenta de su cliente, apunta al loopback
   correcto. Habilitar tráfico gradualmente después del readiness y comprobar
   host/HTTPS, reservas y cuentas con datos de prueba autorizados. No realizar
   cobros, timbrados ni envíos reales como efectos secundarios del smoke test.
8. Registrar aceptación o reversión dentro de la ventana acordada. La evidencia
   debe distinguir pruebas locales, E2E, pagos de prueba y comprobaciones reales.

## Reversión de binarios, datos y presentación

`Publish-prod.ps1` conserva un release anterior no secreto bajo
`_rollback/<servicio>/previous`; los wrappers de Bonhomia y Bruno admiten
`-RollbackToPreviousRelease`. Verificar previamente que el release retenido
pertenece al servicio y que su esquema/configuración son compatibles.

Para un cambio posterior de presentación con fallback vigente, restaurar primero
el release anterior, comprobar `/readyz` y después confirmar la reversión de la
pareja de versiones en la consola, dentro de la ventana de 30 minutos. No mezclar
versiones de branding y contenido ni iniciar una segunda transición sin cerrar
la primera. El cambio de dominio exige desactivar el website y resolver cualquier
transición antes de editar dominio/perfil/túnel.

La primera adopción de RLS y del aislamiento administrativo **no se revierte sólo
copiando un binario antiguo**: el binario anterior puede carecer de contexto SQL
y quedar bloqueado. Preparar y ensayar una reversión de esquema y código
compatible, o restaurar el respaldo verificado con las escrituras detenidas y
conciliación de cualquier operación posterior. No deshabilitar RLS para hacer
que un binario antiguo funcione. Una restauración de datos nunca debe descartar
operaciones aceptadas sin una decisión explícita y su plan de conciliación.

## Corte criptográfico

Los websites usan `App_Data/keys/<PublicSiteKey>` y estos discriminadores:

- Hospedaje: `OrionERP.PublicWebsite.Hospitality.<PublicSiteKey>`.
- Restaurante: `OrionERP.PublicWebsite.Restaurant.<PublicSiteKey>`.

La cookie de membresía Restaurante es
`__Host-OrionRestaurant.<PublicSiteKey>.Member`. Mantener cada key ring aislado,
persistente y accesible sólo por su servicio. No copiar las llaves de una
instancia a otra ni restaurar un `ApplicationName` compartido para conservar
sesiones antiguas.

El primer corte invalida cookies, sesiones, tokens y enlaces protegidos con la
configuración anterior. Programar el nuevo inicio de sesión y comprobar
confirmación, recuperación y enlaces de documentos pendientes; cuando haga falta,
regenerarlos mediante el flujo autorizado. Preservar el key ring anterior en el
respaldo privado para la reversión de esa misma instancia. La aprobación debe
reconocer expresamente esta invalidación prevista.

## Acta pendiente

No hay fecha de corte, aprobador, respaldo productivo verificado, recibo de
preview/apply productivo, ID de túnel verificado ni evidencia de tráfico para
este expediente. Completar esos campos después de resolver los bloqueos y antes
de pasar de revisión a ejecución. El estado permanece **NO EJECUTADO**.

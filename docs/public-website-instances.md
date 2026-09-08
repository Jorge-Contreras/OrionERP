# Fundación de instancias para websites públicos

Los proyectos con nombres legacy `OrionERP.Bonhomia.Web` y
`OrionERP.Bruno.Web` son, respectivamente, las ramas funcionales previstas
para Hospedaje (`HOSPITALITY`) y Restaurante (`RESTAURANT`). Esta entrega hace
parametrizable y verificable la identidad y presentación de cada proceso. Las
fases `20260903` aíslan el recorrido público de Hospedaje y
Identity/membresías de Restaurante; `20260904` agrega una activación versionada
con reversión temporal, y `20260905` añade evidencia indivisible del
consentimiento legal al checkout de Hospedaje. El alta productiva de una segunda
empresa sigue pendiente de los aislamientos y validaciones descritos abajo.

El [plan revisado de septiembre](public-websites-rebaseline-20260908.md)
registra la integración con `main` y el orden de las fases restantes. Se conserva
un proyecto compartido por rama: cada cliente recibe su perfil e instancia,
sin duplicar el código del website.

El alcance ejecutable continúa siendo exclusivamente `Orion_Sandbox`. No se ha
migrado `grupocarpio`, desplegado una versión productiva, cambiado un servicio
ni creado o modificado un túnel Cloudflare como parte de estas fases.
Las dos migraciones de aislamiento `20260903`, la migración de transición
`20260904` y la migración de consentimiento `20260905` sí quedaron aplicadas y verificadas en Sandbox. Los dos hosts se
validaron localmente contra ese ambiente.

## Actualización administrativa de 20260908

La continuación añade RLS en Sandbox y alcance administrativo para reservas,
clientes, documentos, experiencias, CFDI y calendarios. Los hosts públicos
inicializan también el contexto por conexión. El [plan actualizado](public-websites-rebaseline-20260908.md)
y el [inventario SQL](hospitality-sql-isolation-20260908.md) describen los
avances y límites actuales. Permanecen pendientes la reconciliación de pagos,
procedimientos sin configuración de sede, consumidores auxiliares y el E2E de
dos empresas por rama. Esto sigue sin autorizar el alta productiva de otro RFC.

## Límite de seguridad vigente

El estado de aislamiento debe interpretarse por superficie, no por proyecto:

### Hospedaje

El host público ya deriva `CompanyId`/`SiteId` del binding verificado y aplica
esa pareja en sus lecturas, cotizaciones y escrituras de reservación. Su
readiness exige el esquema compuesto de Sandbox. Este avance cubre sólo el
recorrido público.

En checkout, la orden PayPal incluye la huella y el identificador de la
cotización protegida. Ambos se verifican antes de capturar y antes de persistir;
una orden de otro website no puede convertirse en una reservación ni ser
capturada por esta instancia.

El mismo recorrido exige la aceptación explícita de las versiones vigentes del
aviso de privacidad y de los términos antes de crear o capturar una orden. La
hora aceptada se genera en UTC en el servidor y las tres evidencias se guardan
juntas en `dbo.RESERVATION`; los registros históricos permanecen nulos.
El servicio de persistencia también compara las versiones contra el perfil
vigente del proceso antes de resolver el alcance o abrir SQL; esta comprobación
no depende exclusivamente del API HTTP.

La interfaz histórica de OpenClaw fue retirada y ya no forma parte de la
arquitectura ni de sus pendientes. Siguen bloqueando un segundo RFC los CRUD,
consultas y reportes del backoffice, CFDI, Outlook/Graph, procedimientos
almacenados, jobs y cualquier
otro consumidor heredado que opere con RFC legacy o maestros globales como
`Clientes` y `Transacciones`. Ninguno de esos recorridos queda aislado por el
hecho de que el website público sí lo esté.

### Restaurante

Identity y membresías públicas ya están ligadas a `PublicSiteId`. Registro,
login, confirmación, reenvío, recuperación, restablecimiento, claims, cookies y
operaciones de membresía verifican el sitio configurado; el mismo correo puede
existir en dos sitios sin que uno encuentre o modifique al otro.

Identidad visible, contacto, legal, remitente, tema y recursos ya provienen del
perfil versionado de la instancia. Hero editorial, redes, horario y flags
continúan en `restaurante.PublicSiteSettings`, pero se consultan por RFC y sede
exactos. El endpoint público de imágenes también exige la sede resuelta y sólo
acepta productos globales o asociados a esa misma sede. Las reglas de puntos
siguen su catálogo operativo.

El menú público exige un menú activo y publicado del RFC. Cuando ese menú tiene
horarios, sólo es elegible en la sede y franja configuradas, incluyendo la
continuación del día anterior en turnos que cruzan medianoche. Un menú publicado
sin horarios mantiene su alcance general dentro del RFC. Si no hay un menú
elegible, el website muestra el estado vacío; sólo el POS conserva el fallback
al catálogo operativo. El recorrido público no carga mesas ni proveedores.

El administrador ya toma el RFC autorizado de la sesión, solicita una sede
explícita cuando existen varias y usa la ruta canónica
`/restaurante/sitio-publico`; la ruta histórica sólo redirige. El recorrido
habilitado cuenta con guardas por sede; su aceptación con dos empresas
simultáneas sigue pendiente de las pruebas E2E. Como mantenimiento posterior
puede decidirse qué columnas duplicadas de identidad se eliminan de
`PublicSiteSettings`. Los roles públicos
y logins externos sí requerirán aislamiento adicional si se habilitan; hoy no
forman parte de ese recorrido.

### Pendientes comunes

Antes de desplegar un segundo RFC deben completarse y aprobarse por separado:

- el resto de la tenantización de Hospedaje descrita arriba;
- aislamiento de roles o logins externos si se decide habilitarlos;
- pruebas E2E negativas con dos empresas reales de Sandbox sobre todas las
  superficies que se pretendan publicar;
- migraciones, respaldo, corte y despliegue productivos aprobados de forma
  independiente.

La edición central de textos y carga de archivos desde OrionERP puede añadirse
después; no es requisito para reutilizar el código, porque cada proceso ya
recibe un perfil completo. Hasta cerrar la tenantización heredada de Hospedaje
y las validaciones anteriores, crear otro `PublicSite` sólo prepara
configuración: no autoriza datos reales ni un túnel Cloudflare nuevo.

## Identidad confiable de una instancia

Cada proceso recibe una sección `PublicWebsite` completa:

```json
{
  "PublicWebsite": {
    "PublicSiteKey": "clave-inmutable",
    "ExpectedCompanyRfc": "CLAVE-LEGACY-DE-EMPRESA",
    "SiteKey": "sede-operativa",
    "ModuleCode": "HOSPITALITY",
    "CanonicalHost": "reservas.example.com",
    "LoopbackPort": 5030
  }
}
```

`ModuleCode` no es intercambiable: el host de Hospedaje sólo acepta
`HOSPITALITY` y el de Restaurante sólo acepta `RESTAURANT`. Los demás valores
se normalizan y validan al arrancar. En producción, una sección incompleta
impide el arranque.

La identidad se acompaña con `PublicWebsitePresentation`: nombre público y
legal, contacto, versiones legales, colores y un manifiesto de recursos
locales. Hospedaje agrega `HospitalityWebsite` para políticas visibles,
galerías, habitaciones y etiquetas operativas. Todos se validan al arrancar y
quedan inmutables hasta reiniciar el proceso.

La configuración efectiva puede llegar desde `appsettings.Instance.json` o
variables como `ASPNETCORE_PublicWebsite__PublicSiteKey`; las variables tienen
precedencia. El archivo generado en el directorio publicado no se edita a mano
ni contiene contraseñas. Sus fuentes no secretas y versionadas son:

- `deployment/public-sites/bonhomia-main.json`;
- `deployment/public-sites/brunos-main.json`.

Los publicadores cargan el perfil completo correspondiente y materializan
`appsettings.Instance.json`; aplican un esquema cerrado por módulo, rechazan
campos desconocidos o secretos y arrancan el artefacto publicado en modo de
preflight para validar el perfil y todos sus recursos antes de detener el
servicio. `Publish-All-prod.ps1 -ValidateOnly` ejecuta ese mismo preflight sin
tocar producción. Los defaults productivos permanecen vacíos y fallan de forma segura.
Un destino nuevo reutiliza uno de los dos proyectos, pero necesita su propio
perfil, proceso, puerto, binding y aprobación operativa.

## Puerto local y túnel

Producción escucha únicamente en `127.0.0.1` usando `LoopbackPort`. Los hosts
rechazan al arrancar cualquier override mediante `ASPNETCORE_URLS`,
`HTTP_PORTS`, `HTTPS_PORTS` o `Kestrel:Endpoints`; por tanto un ajuste externo
no puede abrir accidentalmente el website a la LAN. Los puertos compatibles
actuales son 5010 para Bonhomía y 5020 para Bruno.

La arquitectura prevista es que el túnel Cloudflare de cada cliente apunte al
puerto de su propia instancia. Los forwarded headers sólo se aceptan desde
loopback. El `Host` recibido se compara contra `CanonicalHost`; puede aceptarse
el host canónico o redirigirse el alias explícito `www`, pero nunca se usa para
elegir empresa, sede o módulo.

Esta topología es una decisión futura de despliegue. Las fases descritas aquí
no crearon, modificaron ni probaron túneles Cloudflare.

`CanonicalHost` forma parte de la identidad operativa, no de la presentación.
Para cambiarlo se debe desactivar y guardar primero el website, finalizar o
revertir cualquier ventana de presentación, actualizar el perfil y el túnel,
validar el nuevo destino y sólo entonces reactivarlo. La consola rechaza el
cambio mientras el website esté activo o exista una reversión vigente.

Antes del primer despliegue productivo de esta versión se debe retirar de la
configuración del servicio cualquier override de direcciones heredado. El
preflight y el arranque lo rechazarán hasta que la escucha efectiva dependa
exclusivamente del puerto loopback ligado al túnel esperado.

## Validación y aislamiento

Antes de servir contenido, el proceso consulta `PublicSite` exclusivamente por
el `PublicSiteKey` configurado y exige que coincidan empresa, sede, módulo y
host, además de toda la cadena de habilitación. Un error produce 503 sin buscar
otro tenant. Una verificación exitosa se reutiliza durante 30 segundos y se
vuelve a comprobar después.

- `/healthz` sólo indica que el proceso vive y no consulta la base.
- `/readyz` admite la comprobación local del publicador, pero sí valida el
  binding completo en la base y devuelve 503 si no está disponible. En
  Hospedaje también valida el ledger, columnas y relaciones del alcance
  empresa/sede; en Restaurante valida columnas, composición de índices y
  llaves foráneas, triggers e integridad de Identity/membresías.
- El key ring y `ApplicationName` de Data Protection incluyen
  `PublicSiteKey`.
- La cookie de membresía del host Restaurante incluye `PublicSiteKey`.
- Los claims de membresía de Restaurante incluyen el `PublicSite`, empresa y
  sede verificados; valores ausentes, duplicados o ajenos invalidan la sesión.

La pareja de versión de presentación debe coincidir con la pareja activa de
`PublicSite`. Durante una activación también puede coincidir con una única
pareja anterior, completa y con vencimiento; nunca se aceptan mezclas de marca
y contenido. La caché no prolonga esa pareja después de su vencimiento.

Estas guardas impiden que el dominio solicitado seleccione otro tenant y evitan
compartir cookies o llaves criptográficas entre procesos. No sustituyen la
tenantización pendiente del resto de Hospedaje ni los pendientes de Restaurante.

## Orden de primera adopción

En cualquier ambiente autorizado, las migraciones deben pasar por preview,
apply y verify antes del código que las consume. La primera adopción de
`20260904` agrega columnas nulas y no cambia las versiones activas; por ello el
binario vigente continúa funcionando. Después se despliega esta capacidad con
el mismo perfil v1 y se comprueba `/readyz`.

Para esta entrega el único ambiente autorizado fue `Orion_Sandbox`; no se
ejecutó este orden en producción ni se expuso tráfico.

## Cambio posterior de presentación sin ventana 503

Una actualización futura usa siempre la pareja completa y este orden:

1. preparar el perfil nuevo y ejecutar `Publish-All-prod.ps1 -ValidateOnly`
   sobre el host correspondiente;
2. aumentar las versiones desde la consola; SQL mueve la pareja anterior a
   fallback y abre 30 minutos de reversión en la misma transacción;
3. publicar el perfil nuevo y comprobar `/readyz` en loopback;
4. finalizar la activación para retirar el fallback.

Mientras se publica, el binario anterior coincide con la pareja fallback y el
nuevo con la activa. Si el health check falla, el publicador restaura el binario
anterior, que sigue autorizado. Tras un publish exitoso también conserva un
release anterior no secreto bajo `_rollback/<servicio>/previous`; no lo elimina
al terminar el proceso.

El flujo coordinado exige que el servicio esté ejecutándose y que `/readyz` se
compruebe mediante una URL HTTP(S) de loopback. Un servicio detenido o una URL
externa detienen el publish antes de copiar archivos. `-SkipServiceControl` queda
reservado para una instalación inicial cuyo ciclo de servicio y validación se
coordinen explícitamente fuera del script.

Si la validación funcional posterior exige revertir, primero se ejecuta
`Publish-Bonhomia-prod.ps1 -RollbackToPreviousRelease` o
`Publish-Bruno-prod.ps1 -RollbackToPreviousRelease`. El script detiene el
servicio, restaura el release retenido, comprueba `/readyz` mientras esa versión
todavía coincide con el fallback y sólo entonces indica confirmar la reversión
de la pareja en la consola. Invertir ese orden puede introducir hasta 30
segundos de 503 por la caché del proceso nuevo. Al finalizar una activación, el
release retenido puede permanecer como evidencia operativa y será reemplazado
por el siguiente publish exitoso; ya no será aceptado por el gate una vez que
SQL cierre el fallback. No se prepara otra versión hasta finalizar o revertir
la transición vigente.

## Corte de criptografía pendiente de aprobación

La primera publicación productiva con esta versión cambia tanto el directorio
como el `ApplicationName` de Data Protection para aislar cada `PublicSiteKey`.
Por diseño, las cookies, sesiones, tokens y enlaces protegidos con el key ring
anterior dejarán de ser válidos. El despliegue deberá programarse como un corte
controlado, anunciar el nuevo inicio de sesión y comprobar los flujos de
confirmación, recuperación y documentos pendientes. Esta entrega no ejecuta
ese corte.

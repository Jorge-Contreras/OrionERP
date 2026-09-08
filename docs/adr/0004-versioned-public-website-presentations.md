# ADR 0004: Presentación versionada por instancia pública

- Estado: Aceptada con alcance de código y Sandbox
- Fecha: 2026-09-02
- Continúa: ADR 0003

## Contexto

Los hosts públicos ya fijan `PublicSiteKey`, empresa, sede, módulo, dominio y
puerto por proceso. Sin embargo, todavía contenían nombres, datos legales,
contacto, colores y rutas de recursos propios de Bonhomía o Bruno's. Copiar el
proyecto para cambiar esos valores contradiría la arquitectura de un solo host
reutilizable para Hospedaje y otro para Restaurante.

Cada cliente tendrá un proceso y puerto loopback propios, expuestos por su
propio túnel Cloudflare. Esa separación operativa permite entregar al mismo
artefacto un paquete de presentación distinto sin aceptar el tenant desde el
request.

## Decisión

Cada proceso público debe declarar una sección no secreta
`PublicWebsitePresentation`, ligada al mismo `PublicSiteKey` que su identidad
confiable. El paquete incluye identidad pública y legal, localidad, contacto,
versiones de documentos, colores y un manifiesto de recursos locales.

El paquete se normaliza una sola vez al arrancar y queda inmutable durante la
vida del proceso. Se rechazan correos, teléfonos E.164, colores, culturas o
rutas de recursos inválidos. Los recursos deben usar rutas locales desde la
raíz; no se aceptan hosts externos, query strings ni traversal.

`BrandingVersion` y `ContentVersion` forman una pareja indivisible. Deben
coincidir con la pareja activa del `PublicSite` resuelto desde SQL Server o,
únicamente durante una transición, con la pareja fallback completa antes de su
vencimiento. Nunca se combinan un contador activo y otro fallback. Una clave o
pareja no autorizada deja la instancia en 503, sin buscar Bonhomía, Bruno's ni
otro website.

Los datos operativos conservan sus fuentes autoritativas:

- Hospedaje mantiene habitaciones, calendario, precios y reservaciones en su
  recorrido público aislado; el perfil aporta únicamente presentación y
  políticas visibles.
- Restaurante conserva hero editorial, redes, horario y flags en
  `restaurante.PublicSiteSettings`, consultados por RFC y sede exactos; el
  perfil manda para identidad base, legal, contacto, tema y recursos.
- Credenciales Graph, PayPal, Turnstile y demás secretos no pertenecen al
  perfil y continúan en el almacén privado de cada proceso.

Los nombres internos `Bonhomia*` y `Bruno*` pueden permanecer temporalmente en
ensamblados, namespaces y CSS legacy. No seleccionan tenant ni deben aparecer
como identidad visible de otra instancia. Las rutas funcionales nuevas deben
ser neutrales; los alias heredados sólo pueden conservarse por compatibilidad
explícita.

## Operación y despliegue

Los perfiles actuales se proporcionan en configuración de desarrollo y en
`deployment/public-sites`, desde donde los publicadores materializan
`appsettings.Instance.json`. Modificar ese archivo no recarga el perfil en
caliente: exige reiniciar el proceso.

La migración `20260904` agrega a `PublicSite` una sola pareja fallback y su
vencimiento. Al aumentar versiones, la consola mueve la pareja activa anterior
al fallback y abre una ventana de 30 minutos en la misma transacción. El host
anterior puede seguir atendiendo mientras se inicia el nuevo. Si falla el
health check, el binario restaurado sigue autorizado; la consola permite
revertir la pareja completa dentro de la ventana o finalizarla tras validar.
La caché de binding nunca prolonga el fallback más allá de su vencimiento.
El publicador conserva el release anterior después de un despliegue exitoso.
Una reversión funcional restaura y valida primero ese release mientras aún es
fallback, y después confirma en SQL la pareja anterior. La finalización cierra
el fallback en SQL; el paquete retenido se reemplaza en el siguiente publish.

Los perfiles usan un esquema cerrado y se validan contra el host publicado
antes de tocar el servicio. En producción tampoco se admiten overrides de
dirección: cada proceso escucha exclusivamente en loopback mediante su puerto
configurado.

Esta decisión no publica binarios, no cambia servicios, puertos o túneles y no
modifica `grupocarpio`. Tampoco autoriza un segundo cliente: antes se mantienen
las pruebas negativas con dos empresas reales de Sandbox y los pendientes de
tenantización ajenos a los recorridos públicos ya aislados.

## Consecuencias

El mismo binario puede representar marcas distintas mediante configuración
versionada y recursos por instancia, sin bifurcar proyectos. La instancia
falla cerrada frente a perfiles cruzados o desactualizados y la consola deja
visible qué versión espera SQL Server.

Una administración central de archivos binarios y contenido editorial por
`PublicSite` puede añadirse después. No es necesaria para reutilizar los hosts,
pero permitiría cambiar logos y textos sin preparar un nuevo paquete de
instancia.

# Entregas cortas de OrionERP: planes y prompts

Preparado desde main `aa09074`, después del despliegue de código `91e2362`. La producción ya está operativa; estos planes continúan las garantías y pendientes documentados. Se generaron prompts, **no se crearon ni iniciaron chats ni se ejecutaron cambios productivos en esta entrega**.

## Cómo ejecutarlos

1. Abre el archivo del chat y copia el texto que sigue a la línea separadora como primer mensaje.
2. Ejecuta **un solo chat con escritura o despliegue a la vez**. Todos trabajan en el mismo checkout main: crear varios chats no aísla archivos, índice Git, base o servicios.
3. Empieza por **01 → 02 → 03 → 04 → 05**. Después sigue dependencias; puedes adelantar una entrega independiente si sus datos y accesos están disponibles.
4. Cada prompt incluye autorización acotada para implementar, validar y publicar su incremento. Al copiarlo y enviarlo como encargo, autorizas ese corte concreto; no autoriza pagos, timbrados, envíos reales ni atribuciones históricas inventadas. Puedes quitar la publicación de un prompt si sólo quieres preparar ese paquete.
5. Al terminar, exige commit y evidencia. Actualiza Estado con `Validado`, `Preparado para producción`, `Publicado <commit>` o `Bloqueado: <dato preciso>`, según lo realmente hecho. El siguiente chat inspecciona main actual y no vuelve al hash del checkpoint.

**Tiempo y tokens son objetivos orientativos, no garantías.** Tokens incluyen contexto, lecturas y salidas de herramientas. No son límites impuestos por la aplicación. Las esperas de compilación, SQL o acceso empresarial pueden aumentar el tiempo. No calculamos una suma como promesa de finalización de toda la arquitectura.

Una entrega de pruebas/conciliación puede terminar sin desplegar binarios: no reiniciar servicios sólo para afirmar que se publicó. Una capacidad contable aditiva puede publicarse desactivada hasta cumplir su dependencia; eso no equivale a función activada.

## Lista de chats

Los números de dependencias significan objetivos terminados o evidencia equivalente ya presente en main; no basta que el chat anterior haya acabado su turno.

| Prompt | Tokens objetivo | Tiempo objetivo | Depende de | Estado |
| --- | --- | --- | --- | --- |
| [01 — Validación de roles y carrera pendiente](01-roles-y-carrera.md) | 6–10k | 30–60 min | — | Pendiente |
| [02 — Suspensión en contabilidad de Restaurante](02-permisos-contabilidad-restaurante.md) | 6–10k | 30–60 min | — | Pendiente |
| [03 — Suspensión en producción, catálogo y trabajos de Restaurante](03-permisos-servicios-restaurante.md) | 8–12k | 45–75 min | 02 | Pendiente |
| [04 — Cuenta SQL mínima para el website de Hospedaje](04-identidad-sql-hospedaje.md) | 8–12k | 45–90 min | — | Pendiente |
| [05 — Cuenta SQL mínima para el website de Restaurante](05-identidad-sql-restaurante.md) | 8–12k | 45–90 min | 04 | Pendiente |
| [06 — Dos empresas de Hospedaje sobre el mismo proyecto](06-e2e-dos-hospedajes.md) | 8–12k | 45–90 min | 01 | Pendiente |
| [07 — Dos empresas de Restaurante sobre el mismo proyecto](07-e2e-dos-restaurantes.md) | 8–12k | 45–90 min | 03, 05 | Pendiente |
| [08 — Corrección trazable de los vínculos históricos](08-pagos-historicos.md) | 6–10k | 30–60 min | — | Pendiente |
| [09 — Resolver los cuatro mappings Outlook](09-outlook-huerfanos.md) | 6–10k | 30–60 min | — | Pendiente |
| [10 — Asociaciones de propietarios por empresa y sede](10-propietarios-por-sede.md) | 8–12k | 45–90 min | — | Pendiente |
| [11 — Plantillas estructuradas y actividades de reservas](11-plantillas-actividades.md) | 8–14k | 45–90 min | 10 | Pendiente |
| [12 — CompanyId y conexiones contables compatibles](12-identidad-contable.md) | 10–14k | 60–90 min | — | Pendiente |
| [13 — CFDI compartidos y asignación por empresa](13-cfdi-compartidos.md) | 8–12k | 45–90 min | 12 | Pendiente |
| [14 — Conciliación del baseline contable](14-baseline-contable.md) | 6–10k | 30–60 min | 12, 13 | Pendiente |
| [15 — Periodos y guardas de cierre contable](15-periodos-contables.md) | 8–12k | 45–90 min | 12, 14 | Pendiente |
| [16 — Publicación atómica e inmutabilidad de pólizas](16-publicacion-polizas.md) | 12–18k | 60–120 min | 12, 15 | Pendiente |
| [17 — Reversa contable y activación del ciclo](17-reversa-polizas.md) | 10–14k | 60–90 min | 14, 16 | Pendiente |
| [18 — Contabilización durable de Restaurante](18-bandeja-contable-restaurante.md) | 12–18k | 60–120 min | 03, 17 | Pendiente |
| [19 — Contabilización durable de Hospedaje](19-bandeja-contable-hospedaje.md) | 12–18k | 60–120 min | 11, 17, 18 | Pendiente |
| [20 — Reportes financieros basados en pólizas publicadas](20-reportes-publicados.md) | 8–14k | 45–90 min | 14, 17 | Pendiente |
| [21 — RLS del agregado contable](21-rls-contabilidad.md) | 10–16k | 60–120 min | 12, 13, 17, 18, 19 | Pendiente |
| [22 — Retirar bypass RLS en un agregado de Logística](22-rls-logistica.md) | 10–14k | 60–90 min | 01, 03 | Pendiente |
| [23 — Retirar bypass RLS de RH por agregado](23-rls-rh.md) | 8–14k | 45–90 min | — | Pendiente |
| [24 — Retirar bypass RLS fiscal por agregado](24-rls-fiscal.md) | 8–14k | 45–90 min | 13 | Pendiente |
| [25 — Separación operativa de túneles por instancia](25-tuneles-instancias.md) | 6–10k | 30–75 min | 04, 05 | Pendiente |

## Orden por entregas

- **Estabilización inmediata:** 01–03. Cerrar validaciones y suspensión de servicios sin rediseñar el producto.
- **Aislamiento operativo y público:** 04–07. Cuentas SQL independientes y dos empresas por rama en Sandbox. El 25 completa la separación de túneles cuando las cuentas estén disponibles.
- **Legado de Hospedaje:** 08–11. Correcciones con evidencia, mappings Outlook, asociaciones de propietarios y plantillas de actividades. No hacen falta para comenzar los cambios contables independientes.
- **Núcleo contable:** 12–17. Identidad y CFDI primero; después baseline, periodos, publicación e inmutabilidad, y reversa. El 16 publica capacidad desactivada; el 17 completa el ciclo antes de su activación delimitada.
- **Integración y reportes:** 18–20. Restaurante, Hospedaje y reportes financieros por separado. Hospedaje reutiliza el contrato del 18 y los mappings del 11.
- **Endurecimiento SQL:** 21–24. RLS por agregado y sólo cuando sus consumidores sean compatibles. RH puede avanzar independientemente; no necesita esperar toda la contabilidad.

## Entregas que pueden necesitar más de un chat

Los prompts 03, 08, 18 y 21–24 tienen un primer lote explícito. No sería honesto prometer migrar de una vez todos los servicios, 288 relaciones o las políticas legacy de 98/22/6 tablas dentro de pocos tokens.

Al terminar uno de esos lotes, el mismo chat debe generar una continuación con **tablas, métodos, IDs y pruebas exactos** del siguiente lote. Se numera, por ejemplo, `22b`, y se añade aquí con sus dependencias. No debe volver a pedir una auditoría general. Este mecanismo también cubre los consumidores externos/VBA que se identifiquen: no se consideran migrados hasta tener evidencia o retirarse explícitamente de la operación afectada.

Casos especialmente delimitados:

- 03: máximo tres consumidores todavía sin guardas por lote.
- 08: máximo 25 correcciones demostradas por lote. Confirmar que son errores históricos ya está resuelto; identificar el vínculo correcto aún puede requerir evidencia.
- 18: primero una contabilización concreta; órdenes individuales y CFDI tardío siguen en lotes posteriores si no quedaron cubiertos.
- 21: primero Transacciones/Registro_Contable; después adjuntos, cuentas y CFDI compartidos con sus contratos particulares.
- 22–24: un agregado compatible por lote, sin modificar de golpe funciones compartidas por múltiples tablas.

## Mapa de cobertura del backlog original

| Pendiente | Entregas |
| --- | --- |
| Roles restringidos, CRUD/revocación y carrera inversa proyecto/calendario | 01 |
| Empresa/módulo/sede/permisos y suspensión en servicios/trabajos | 02–03 y lotes restantes concretos |
| Identidades SQL por website y separación de migración | 04–05 |
| Dos empresas por rama, requests cruzados, cookies/documentos y transiciones | 06–07 |
| 288 vínculos históricos sin cambiar importes | 08 |
| Cuatro mappings Outlook; no activar Graph sin identidad/buzón/calendarios | 09 |
| Propietarios asociados, plantillas y creación de actividades | 10–11 |
| CompanyId/TaxRfc/legacy y conexiones contables con contexto | 12 |
| CFDI legítimamente compartidos y asociaciones contables por empresa | 13 |
| Conciliación heredada, baseline y decisiones de activación | 14 |
| Periodos, Draft/Posted/Reversed, balance e inmutabilidad | 15–17 |
| Idempotencia y bandeja contable durable de ambos módulos | 18–19 y variantes pendientes concretas |
| CreateTransaccionesForRoom con mappings y contrato contable | 19 |
| Reportes de pólizas publicadas y separación de métricas operativas | 20 |
| RLS contable y bypass de políticas legacy | 21–24 y sus lotes hasta completar tablas/consumidores |
| Procesos/perfiles/puertos y túneles independientes por cliente | 25, conservando lo ya desplegado |

Cambiar padre/habitación de una ubicación con historia, editar maestros globales sin asociación e importar plantillas libremente por texto **siguen siendo bloqueos deliberados**, no tareas para desactivar protecciones. Las claves históricas R13/R16/R17 sin procedencia se conservan; no se reclasifican por semejanza. No se agrega un cliente productivo de ejemplo.

## Fuentes y cierre de esta preparación

- [Acta productiva](../production-cutover-executed-20260908.md).
- [Matriz contable](../multiempresa-accounting-status-20260908.md).
- [Pendientes desde main](../multiempresa-pendientes-desde-main.md).
- [Matriz de navegador](../multiempresa-browser-smoke-20260908.md).

No se repitieron pruebas de la aplicación: esta entrega sólo agrega documentación y prompts. Se verifican archivos, dependencias y conservación del cambio ajeno antes del commit.

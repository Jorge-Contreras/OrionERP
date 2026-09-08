# Aislamiento SQL de Hospedaje: evidencia de Sandbox

La migración nueva `20260908_hospitality_administration_scope_sandbox` se
previsualizó con rollback y recibo `D446C0E3748C`, se revisó y se aplicó
exclusivamente en `Orion_Sandbox`. No modificó producción. El incidente de conexión inicial está registrado en el plan de continuidad.

`orion.HospitalityScopePolicy` protege 18 tablas mediante 54 predicados:
un filtro de SELECT/UPDATE/DELETE y bloqueos AFTER INSERT y AFTER UPDATE por
tabla. Las tablas legacy conservan `OrionCompanyId`/`OrionSiteId`; los mappings
`HospitalitySiteCustomer` y el nuevo `HospitalityFiscalCustomer` usan
`CompanyId`/`SiteId`. Las tablas opcionales existentes `RESERVATION_DETAIL` y
`ROOM_CALENDAR_OUTLOOK_SYNC` también quedaron protegidas.

La conexión necesita ambos valores positivos en
`SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')` y
`SESSION_CONTEXT(N'OrionERP.HospitalitySiteId')`. Un contexto ausente o inválido
no permite leer filas ni insertarlas. No existe bypass implícito para dbo,
administradores ni jobs. Los defaults de las columnas toman ese contexto para
inserts heredados que omiten las columnas; escribir NULL explícito no recibe
el default y es rechazado. El contexto debe provenir de la sesión autorizada
o del binding validado del proceso, nunca de parámetros del navegador.

Se añadieron relaciones compuestas de proveedores/experiencias/paquetes y
extras, detalles de reserva y habitación del mapping Outlook. Los códigos de
proveedor y experiencia son únicos dentro de empresa/sede. El mapping fiscal
se creó vacío y garantiza que un BusinessPartner mutable no se comparta entre
sedes. No se atribuyeron perfiles fiscales históricos.

## Hallazgos que requieren reconciliación

El preview encontró 1,492 vínculos de pago. De ellos, **288** pertenecen al
scope de empresa `OHM191112Q26`, pero el pago global declara RFC
`BSU210121M77`. No se reasignó, eliminó ni corrigió ninguno. Un predicado
específico verifica también que el RFC del pago coincida con la empresa, por
lo que esos vínculos no aparecen en consultas, totales ni procedimientos de
ninguna sede. Los inserts y cambios de vínculo contradictorios quedan
bloqueados. Esto cambia los totales visibles; no constituye reconciliación
contable. Su resolución necesita evidencia empresarial explícita.

La FK del pago global tenía DELETE CASCADE; ahora es restrictiva. Un borrado
de Transacciones no puede eliminar silenciosamente vínculos que RLS oculta.

También existen **cuatro** mappings Outlook cuyo ID de reserva no resuelve
en su scope. Se conservaron. No se creó una FK no confiable ni se reasignaron
IDs por intuición. El trigger rechaza nuevos vínculos huérfanos y el consumidor
de Graph excluye los históricos sin adoptar ni eliminar sus eventos externos.
La relación compuesta con habitación sí es válida y quedó confiable.

El trigger de ROOM_CALENDAR valida RoomId/nombre/scope y rechaza un
LOCK_DESCRIPTION numérico que no corresponda a una reserva visible de esa
empresa/sede. Esta guarda cubre la relación textual heredada.

## Procedimientos y consumidores SQL inventariados

El inventario leyó `sys.sql_modules` de Sandbox, sin asumir que todos los
scripts históricos del repositorio estuvieran instalados.

| Consumidor | Resultado |
| --- | --- |
| `dbo.CreateActividadForReservation` | Utilizaba IDs de plantillas globales y búsqueda por texto de suite; conserva firma pero ahora lanza error antes de escribir. Requiere mapping explícito de plantillas por sede. |
| `dbo.CreateTransaccionesForRoom` | Utilizaba categoría numérica y cuenta bancaria heredadas; conserva firma pero ahora lanza error antes de escribir. Requiere configuración explícita de cuentas/categorías por sede. |
| `Reservaciones.GetActividadForRoomCalendar` | Su WHERE exige ROOM_CALENDAR.id; RLS delimita esa fila aun con el LEFT/RIGHT JOIN heredado. |
| `Reservaciones.GetRoomCalendarByReservationID`, `dbo.Calendar_GetRoomTimeline`, `GetCurrentMonthCalendar`, `GetRoomCalendarByMonthAndStatus`, `ROOMS_BY_DATE`, `ROOMS_BY_DATE_AND_ROOM`, `GET_FULL_CALENDAR` | Lectores de tablas de habitación/calendario protegidas. Necesitan inicializar contexto en cada conexión. |
| `dbo.LISTA_DE_RESERVACIONES`, `VIEW_FULL_CALENDAR`, `GetReservationsByTransaction`, `Reservation_Transactions_Detailed` | Vistas/lectores filtrados en sus tablas base; los pagos contradictorios tampoco aparecen. |
| `dbo.SP_CALCULAR_VENTAS_MENSUALES_POR_ANIO`, `SP_CALCULAR_OCUPACION`, `SP_CALCULAR_PORCENTAJE_OCUPACION_POR_HABITACION` | Agregaciones sobre tablas protegidas. Sin contexto sus métricas de Hospedaje quedan vacías. |
| `reporteFinanciero.Reporte_Salud_Empresa` y `_Conciliacion` | RLS limita filas operativas; el primer procedimiento conserva código fiscal/legacy que requiere revisión separada del consumidor de reportes. |

El bloqueo de los dos procedimientos de escritura es intencional. Esta entrega
no puede afirmar que todas las funciones heredadas sigan operativas: evita
escrituras de otra empresa mientras faltan mappings verificables.

## Consumidores de Órdenes de Trabajo, Logística y Restaurante

La revisión adicional encontró que filtrar ROOM dejaba visibles cantidades,
documentos y nombres copiados en otros módulos. Los consumidores indicados
abajo ahora autorizan sus filas base y relaciones. Esta matriz registra la
implementación y pruebas de servicio; no declara terminadas las pruebas E2E.

`LogisticsLocationScope` exige el RFC autenticado de la conexión y construye
el conjunto temporal de ubicaciones visibles: raíces generales del RFC,
habitaciones de la empresa/sede autorizada y descendientes cuyo árbol completo
es visible. `RoomId` y `LegacyRoomId` cuentan como referencias sensibles;
huérfanos y ciclos quedan fuera. Sin acceso a Hospedaje se conservan las ramas
generales. Las escrituras revalidan bajo transacción y no pueden desprender
una ubicación privada de su raíz para convertirla en general.

Los archivos de servicio de la tabla están bajo
`src/OrionERP.Infrastructure/Features/`; las rutas son de la consola OrionERP.

| Ruta y archivo | Métodos afectados | Resultado actual y trabajo pendiente |
| --- | --- | --- |
| `/ordenes-trabajo`, detalle y calendario — `OrdenesTrabajo/OrdenTrabajoService.cs` | Dashboard, búsqueda, detalle, evidencias, badges, creación y mutaciones | Empresa autenticada también para OT generales; referencias a habitación/calendario deben estar autorizadas. La escritura revalida la OT con `UPDLOCK/HOLDLOCK` en la misma conexión/transacción. Los casos genéricos sin referencia conservan su flujo. Evidencia SQL en `HospitalityAdministrativeCrudTests`; no reatribuye textos históricos ambiguos. |
| `/ordenes-trabajo/plantillas` — mismo servicio | Plantillas, mappings y seed legacy | Los vínculos de habitación se revisan en el alcance autorizado; los procedimientos SQL legacy de plantillas siguen bloqueados hasta contar con mappings empresariales verificables. Esta entrega no declara restaurado todo flujo histórico de plantillas. |
| `/logistica/ubicaciones` — `Logistica/Locations/LocationService.cs` | Lista, detalle, árbol, lookup, Room lookup y Save | Usa cierre completo de ubicaciones; oculta nombres copiados en raíz, hijo y nieto. Rechaza Room/Parent ajenos y separación de una rama privada. `HospitalityLogisticsLocationTests` valida SQL real, incluido LegacyRoomId y RFC ausente/incompatible. |
| Misma ruta — `Logistica/Stock/StockService.cs` | Saldos, movimientos, umbrales, alta/retiro/reactivación, adjuntos y bytes | Filtra filas base por RFC y ubicación visible; movimientos exigen coherencia con saldo/material/ubicación. Revalida lecturas y escrituras bajo transacción Serializable. Adjuntos privados no aparecen ni pueden descargarse por ID. SQL real en `HospitalityStockMaterialScopeTests`. |
| `/logistica/materiales` — `Logistica/Materials/MaterialService.cs` | Lista agregada, inventario, movimientos, evaluación y cambios de ciclo de vida | Agrega cantidades sólo después de filtrar ubicaciones. RFC explícito debe coincidir con sesión. Si cualquier referencia logística o documento de compra completo cruza el alcance, evaluación/baja/reactivación devuelve denegación opaca antes de ejemplos o cambios globales del material. El maestro sigue siendo de empresa. SQL real en `HospitalityStockMaterialScopeTests`. |
| `/logistica/conteos` — `Logistica/PhysicalCounts/PhysicalCountService.cs` | Sesiones, líneas, adjuntos, creación, captura, submit/approve/post y borrado | Oculta la sesión completa si contiene una ubicación invisible. Creación y mutaciones revalidan referencias en transacción; conflictos privados bloquean sin exponer nombres. SQL real de captura y archivo privado, negativas y ciclo propio completo en `HospitalityLogisticsLocationTests`. |
| `/logistica/compras` — `Logistica/Purchasing/PurchaseOrderService.cs` | Documento completo, catálogo, asignaciones, recepciones, Auto PO y mutaciones | Requiere RFC sesión; oculta/deniega toda orden con habitación, asignación o recepción invisible, incluso si otras líneas son generales. Escrituras Serializable. `HospitalityPurchasingScopeTests` cubre otra sede del mismo RFC, ausencia de scope, árbol hijo, documento ROOM, negativas Issue/Cancel/Save y operación general. |
| Inventario — `Logistica/Stock/InventoryMovementService.cs` | Workspace, transferencia y ajuste | Helper y RFC explícito; comprueba ambos extremos y todas las líneas en transacción. Reintentos idempotentes también autorizan las ubicaciones del documento original. Validación independiente del agente SQL; no se añadió migración. |
| POS — `Restaurante/RestaurantOrderService.cs` | Orden, recibo, paneles, eventos, pagos, reservas y mutaciones | Oculta orden completa si reserva/material/lote/ubicación no es visible. Escrituras revalidan documento en Serializable; reserva y fallback usan únicamente ubicaciones visibles. Fixture SQL ampliado de Location cubre orden mixta privada/general, detalle/recibo/panel ocultos y cancelación/prioridad denegadas. |
| POS, diagnóstico preventivo — `Restaurante/RestaurantSaleReadinessService.cs` | `AnalyzeAsync`, carga de contexto operativo | RFC sesión antes de consultar catálogo; saldos, lotes, ubicaciones, prioridades y nombres limitados al árbol visible. SQL real compara stock y lotes útiles: 10 autorizados frente a 3 generales sin scope o con otra sede; no expone el nombre privado. |
| Diagnóstico contable — `Restaurante/RestaurantDiagnosticsService.cs` y `.Facts.cs` | Run, historia y aceptación | Inventario exclusivamente general, aunque el usuario tenga acceso a Hospedaje. Nuevas reglas `R13-G`, `R16-G`, `R17-G`. Historia `R13/R16/R17` sin procedencia se conserva en SQL pero queda oculta y no puede aceptarse; resumen se recalcula a partir de hallazgos visibles. SQL real verifica monto general 3 frente a total 10 y preservación del histórico. |
| Producción y operaciones de sede — `Restaurante/RestaurantProductionService.cs`, `RestaurantCatalogService.cs` | Workspace/plan/fuentes/salida y prioridades de Location | Guardas de RFC sesión y árbol visible implementadas para cantidades, fuentes, destino y prioridades. Compilación y suite general aprobadas; fixture SQL específico pendiente al congelar el alcance. No inferir evidencia SQL desde otros servicios. |

Hay evidencia concreta de copias de nombres fuera de ROOM:
`Logistica/Sql/20260323_logistics_wm_migration.sql` toma `ROOM_NAME` como
`LocationName` y lo guarda en `logistica.Location`. Árboles, lookups, detalles,
recepciones y notas de auditoría utilizan LocationName sin depender de ROOM.
Los snapshots de compras y órdenes ahora se autorizan mediante sus referencias
completas. El texto histórico sin referencia verificable sigue siendo ambiguo:
no se atribuye a otra empresa/sede por inferencia. No se afirma que todo snapshot
de módulos no inventariados haya quedado clasificado.

El RFC autenticado es obligatorio; la habilitación de Hospedaje no lo es para
operar inventario general. La excepción deliberada es el diagnóstico persistido
de Restaurante: sus reglas de inventario no incluyen ninguna ubicación de
Hospedaje porque el modelo de hallazgos carece de procedencia por sede. Las
claves nuevas identifican únicamente cálculos generales nuevos; no reclasifican
históricos. Las acciones sobre hallazgos legacy `R13/R16/R17` están deshabilitadas.

### Ejecución genérica de procedimientos

La búsqueda de `CreateActividadForReservation` y `CreateTransaccionesForRoom`
en C# y Razor no encontró llamadores literales; sus antiguos usos externos/VBA
no se consideran migrados. El servicio
`src/OrionERP.Infrastructure/Common/DbStoredProcService.cs` acepta el nombre del
procedimiento en `ExecuteAsync`, abre la fábrica general y establece sólo
contexto de auditoría. No resuelve el scope de Hospedaje. Los dos wrappers
bloqueados devolverán su error explícito por esa vía; los lectores protegidos
seguirán vacíos sin contexto. Antes de habilitar un procedimiento de Hospedaje
por configuración debe usarse un contrato específico que resuelva y valide
su alcance; no adoptar automáticamente cualquier procedimiento por un prefijo.

Las pruebas de servicio reducen los riesgos inventariados, pero no completan
la fase E2E de Hospedaje multiempresa ni reconcilian los 288 pagos contradictorios.
Los procedimientos legacy bloqueados y referencias históricas ambiguas siguen
siendo límites explícitos.

## Pruebas ejecutadas

Consumidores añadidos en esta revisión, todos contra `Orion_Sandbox`, con
catálogo fijado antes de abrir la conexión y comprobación de `DB_NAME()`:

- `HospitalityAdministrativeCrudTests` y `HospitalityLogisticsLocationTests`:
  **2/2 aprobadas**; el segundo fixture se amplió con conteos físicos y orden
  mixta de restaurante. OT y conteos: **81 unitarias aprobadas**; órdenes POS:
  **22 unitarias focalizadas aprobadas** (ejecuciones del agente responsable).
- `HospitalityPurchasingScopeTests`: **1/1 aprobada**, además de **23 unitarias**
  de compras (ejecución del agente responsable).
- `HospitalityStockMaterialScopeTests`: **1/1 aprobada** en la ejecución final
  ampliada a diagnóstico preventivo y diagnóstico persistido de Restaurante.
  Usa datos temporales propios y limpieza en `finally`: misma empresa con otra
  sede temporal, usuario sin acceso a Hospedaje y otra empresa. Comprueba
  cantidades, nombres, movimientos, bytes de adjuntos, negativas de escritura,
  preservación de inventario general, denegación opaca de ciclo de vida,
  cantidades de lotes y ocultamiento/no modificación de diagnósticos legacy.
- Stock, Material, guardas negativas y diagnóstico preventivo:
  **62 unitarias aprobadas**, sin advertencias de compilación en esa ejecución.

No se suman estos contadores como una suite única: corresponden a filtros y
ejecuciones independientes y algunos fixtures fueron ampliados después.
La validación de navegador y el cierre de los consumidores de producción se
registran separadamente.

`HospitalityRowSecuritySqlTests`: **11/11 aprobadas** con
`ORION_RUN_SQL_INTEGRATION=1`, conexión forzada a Sandbox antes de abrir y
comprobación de DB_NAME. Los fixtures escritos usan transacciones revertidas.

- Todas las 18 tablas devuelven cero filas sin contexto.
- Dos empresas/sedes resueltas desde bindings reales pueden usar el mismo
  código de catálogo; no leen, actualizan ni eliminan el registro ajeno.
- Defaults reciben el scope de la conexión.
- Contexto ausente, inválido, compañía incorrecta, sede incorrecta, NULL
  explícito y cambio de scope son rechazados por SQL.
- LOCK_DESCRIPTION no puede apuntar a una reserva creada en otra empresa.
- Un pago creado en otra empresa no puede vincularse a la reserva actual;
  los históricos contradictorios permanecen invisibles.
- La reutilización comprobada del mismo SPID del pool limpia el contexto
  anterior antes de inicializar la siguiente sede.

Estas son pruebas de SQL, no E2E de pagos, correo, timbrado ni Graph. Los dos
bindings de prueba identifican empresas existentes; el segundo pertenece a
Restaurante y se usa únicamente para comprobar el límite SQL de empresa/sede,
sin habilitar un host público de Hospedaje adicional.

El diseño usa filtros y bloqueos conforme a la documentación de
[Row-level security de SQL Server](https://learn.microsoft.com/en-us/sql/relational-databases/security/row-level-security).
Los logins de aplicación siguen siendo parte de la frontera de confianza:
RLS no sustituye permisos mínimos ni protege frente a un administrador SQL
autorizado a deshabilitar la política. Esos permisos operativos se revisan en
la preparación productiva independiente.


## Checkpoint para main

Validación final conjunta: build Release sin errores/advertencias, **1,306
unitarias y 71 integraciones aprobadas** con SQL Sandbox habilitado. Incluye
`HospitalityInventoryMovementTests` (workspace, transferencias, ajustes, replay
idempotente y destinos privados/generales). El backlog explícito está en
[multiempresa-pendientes-desde-main.md](multiempresa-pendientes-desde-main.md).
Ajustes limita propietarios a ROOM visible y protege proyectos con vínculos
de calendario; no permite mutar el maestro global de propietarios sin mapping.

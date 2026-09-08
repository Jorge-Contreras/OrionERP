# Inventario de aislamiento de calendarios de Hospedaje

Alcance: código y pruebas locales; ninguna llamada real a SQL, OAuth, Graph,
Outlook ni producción fue realizada por esta revisión de CalendarSync.

## Consumidores y cambios

| Consumidor | Hallazgo | Límite implementado |
| --- | --- | --- |
| `ListaReservacionesPage` y `ReservacionPage` | Disparaban una sincronización global de habitaciones. | La interfaz conserva las fechas; el servicio resuelve `IHospitalityScopeAccessor` autorizado, sin aceptar empresa o sede desde la página. |
| `BonhomiaRoomCalendarSyncService` | Solicitaba OAuth antes de leer el calendario local; utilizaba opciones globales. | Exige activación y binding explícitos; compara empresa, sede y RFC contra sesión y valida datos locales antes de OAuth. |
| `GetBlockedBlocksAsync` | Filtraba `ROOM_CALENDAR` por nombres; enlazaba cualquier `RESERVATION`. | Contexto SQL y predicados de empresa/sede en calendario, habitación y reserva; exige `RoomId` coherente y que todos los calendarios configurados pertenezcan al alcance. |
| Lectura de `ROOM_CALENDAR_OUTLOOK_SYNC` | Mapeos globales por nombres. | Empresa/sede explícitas, `RoomId` coherente y reserva del mismo alcance cuando existe. |
| Upsert de mapeos | `MERGE` global por clave y calendario. | Revalida autorización, empresa/sede en `MERGE`, verifica habitación única y reserva propia; escribe alcance y `RoomId`. |
| Borrado de mapeos | Borraba identificadores globalmente. | Contexto SQL y predicados de empresa/sede además de identificadores. |
| Ownership de eventos Graph | Cualquier marcador `OrionSync` permitía reconciliar/borrar un evento. | Los eventos llevan `OrionScope:CompanyId:SiteId`; sólo admite ese marcador exacto o un evento legacy sin marcador de alcance cuyo ID ya tiene mapeo local atribuido. Un marcador ajeno, malformado o duplicado nunca concede ownership. |
| Jobs y automatizaciones | No se encontraron jobs de CalendarSync en el código o scripts del repositorio. | No se introduce un fallback de servicio sin sesión. Un futuro job debe registrar un accessor de alcance validado para su identidad de servicio y binding explícito; nunca el contexto global. |

## Configuración y compatibilidad

`BonhomiaGraphCalendarSync` requiere `Enabled=true`, `CompanyId`, `SiteId`,
`CompanyRfc`, `MailboxAddress`, `TargetCalendars` y credenciales Graph explícitas.
El valor predeterminado es deshabilitado; no existe fallback de buzón ni de
habitaciones Bonhomia. El nombre histórico de la sección y los proyectos se
conservan por compatibilidad. Un proceso configurado para una sede rechaza una
sesión de otra sede antes de leer datos de calendario o llamar a HTTP. La
resolución autorizada del alcance puede consultar las tablas de autorización.
La integración deshabilitada se rechaza incluso antes de esa resolución.

Se reutilizan `OrionCompanyId`, `OrionSiteId` y `RoomId` agregados por la migración
registrada `20260903_hospitality_public_scope_sandbox.sql`. La política RLS y la
inicialización de conexión pertenecen a la migración administrativa nueva y al
factory compartido. No se alteraron los bytes de migraciones registradas.

Los eventos legacy con mapeo atribuido conservan la clave y su ID remoto; al
reconciliarlos se actualiza el cuerpo con el marcador compuesto. Un evento legacy
sin mapeo no se adopta ni borra automáticamente. Su atribución requiere evidencia
operativa. Los índices históricos globales de calendario/evento continúan
impidiendo reutilizar el mismo evento remoto entre alcances; no se relajan aquí.

El preview de Sandbox realizado por el frente SQL de esta misma tarea reportó
cuatro mapeos cuya `RESERVATION_ID` está huérfana o no coincide con el alcance;
sus `RoomId` sí son válidos. El lector excluye esos cuatro mapeos por la
validación de reserva. Se conservan para investigación: no se reasignan ni se
borra su evento legacy automáticamente. Los nuevos upserts rechazan esa relación.

## Verificación y pendientes explícitos

Pruebas unitarias usan exclusivamente repositorio falso y `HttpMessageHandler`
falso. Cubren activación, binding incompleto, otra empresa, otra sede, otro RFC,
rechazo local previo a OAuth, propiedad de marcadores y eventos legacy sin
atribuir, además de creación y persistencia del mapeo con alcance.
Las credenciales compartidas sólo pueden rotar el secreto cuando TenantId y
ClientId ya están configurados explícitamente y coinciden; nunca completan una
identidad Graph vacía o cambian el tenant por herencia.

El inventario se limita al repositorio. No inspecciona SQL Agent, el Programador
de tareas de Windows ni configuración Graph de producción. La ejecución SQL de
los predicados y la política RLS debe verificarse separadamente en Orion_Sandbox;
no se declara validada por las pruebas unitarias. Configurar dos sedes Graph
simultáneamente dentro del mismo proceso requerirá un catálogo de bindings
autorizados; esta revisión conserva una integración explícita por proceso.

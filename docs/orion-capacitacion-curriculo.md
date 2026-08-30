# Currículo de Capacitación de OrionERP

El módulo `capacitacion` guarda el contenido en versiones inmutables. El
catálogo completo se arma con dos scripts, en este orden:

1. `src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_capacitacion_v1.sql`
   instala el esquema, los triggers de inmutabilidad y los cinco cursos piloto.
2. `src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260819_capacitacion_curriculum_v2.sql`
   agrega un curso por cada módulo de OrionERP y la ruta `ORION-EXPERTO` que
   ordena el recorrido completo.

Ambos son idempotentes y comparten el mismo guardarraíl de catálogo
(`ExpectedDatabase` debe ser `Orion_Training`, `Orion_Sandbox` o `grupocarpio`).
`Install-CapacitacionSchema.ps1` los aplica juntos y
el ambiente `Orion_Training` los recibe normalmente al clonar `grupocarpio`.

## Estructura de cada curso nuevo

Cada curso del currículo v2 sigue el mismo recorrido de seis pasos usado por los
cursos más recientes del piloto:

| Lección | Bloques | Propósito |
| --- | --- | --- |
| `PREPARAR` | `OBJETIVOS`, `TEORIA` | Contexto, alcance y modelo mental del módulo. |
| `OPERAR` | `DEMOSTRACION`, `ALERTA`, `PRACTICA` | Demostración guiada, el riesgo que más daño causa y la práctica en la pantalla real. |
| `CERRAR` | `EVALUACION`, `RESUMEN` | Evaluación de cuatro preguntas y criterio de cierre. |

Además, cada curso incluye una evaluación con calificación mínima de 80, cuatro
preguntas (con preguntas críticas de respuesta obligatoria) y una práctica con
cuatro pasos evaluables. El bloque `PRACTICA` enlaza las rutas locales reales
del módulo; no se usan destinos externos.

## Ruta `ORION-EXPERTO`

La ruta de aprendizaje enumera los 29 cursos en orden de dependencia y es el
manifiesto de lo que significa dominar OrionERP:

| # | Clave | Módulo cubierto |
| --- | --- | --- |
| 1 | `ORION-FUNDAMENTOS` | Entornos, navegación y buenas prácticas |
| 2 | `CAPACITACION-MODULO` | Capacitación: catálogo, sesiones, firma y acuse |
| 3 | `RES-END-TO-END` | Reservaciones de principio a fin |
| 4 | `RESERVAS-CALENDARIO` | Calendario de ocupación, tarifas y recibos |
| 5 | `ARRENDADORES-ESTADO` | Arrendadores: estado de cuenta y liquidación |
| 6 | `OT-OPERACION` | Órdenes de trabajo y plantillas |
| 7 | `CFDI-SAT-OPERACION` | Alta de RFC, descarga masiva y carga de XML |
| 8 | `CFDI-CONTABILIDAD` | Del CFDI a la contabilidad |
| 9 | `CFDI-DECLARACION-PREVIA` | Declaración previa y amarre de comprobantes |
| 10 | `CONTA-POLIZAS` | Pólizas y registros contables |
| 11 | `BANCOS-CONCILIACION` | Bancos, movimientos y conciliación |
| 12 | `CXP-RECURRENTES` | Cuentas por pagar y cobrar recurrentes |
| 13 | `REPORTES-FINANCIEROS` | Hoja de trabajo, balanza, resultados y salud financiera |
| 14 | `LOGISTICA-OPERACION` | Logística: materiales, compras e inventario |
| 15 | `LOGISTICA-COMPRAS` | Proveedores, compras y recepciones |
| 16 | `LOGISTICA-INVENTARIO` | Ubicaciones, existencias y conteos |
| 17 | `REST-CATALOGO-CONFIG` | Menús, productos y configuración operativa |
| 18 | `REST-POS-SERVICIO` | POS, órdenes y entregas |
| 19 | `REST-COCINA-PRODUCCION` | Cocina KDS, recetas y producción |
| 20 | `REST-INVENTARIO-TURNOS` | Inventario de restaurante y turnos de caja |
| 21 | `REST-COMERCIAL` | Promociones, membresía, reportes y sitio público |
| 22 | `RH-CAPITAL-HUMANO` | Autoservicio del colaborador |
| 23 | `RH-ASISTENCIA` | Asistencia, kiosco y equipo |
| 24 | `RH-CONFIG-TIEMPO` | Sitios, horarios, geocercas y kioscos |
| 25 | `RH-AUSENCIAS` | Ausencias, políticas y saldos |
| 26 | `RH-PRENOMINA` | Pre-nómina, bloqueo y exportación |
| 27 | `RH-EXPEDIENTES` | Expediente del colaborador |
| 28 | `AJUSTES-PLANTILLAS` | Ajustes y plantillas contables |
| 29 | `ADMIN-SEGURIDAD` | Portal de seguridad, roles y RFC |

`CapacitacionCurriculumSqlTests` comprueba que cada destino del menú de
navegación de OrionERP aparezca en el contenido sembrado, de modo que un módulo
nuevo sin curso hace fallar la prueba.

## Usuarios y permisos en el entorno de capacitación

Training no crea una cohorte artificial. El clon conserva `auth.AspNetUsers`,
roles, relaciones con empresas y contraseñas tal como están en producción. Cada
persona entra con su cuenta habitual y conserva las mismas pantallas y permisos.
Las asignaciones de cursos también forman parte del clon y pueden administrarse
normalmente desde el propio módulo.

## Agregar un curso nuevo

1. Añade el curso, sus lecciones, bloques, recursos, evaluación, preguntas,
   práctica y pasos al script del currículo, siempre en la versión `BORRADOR`.
2. Agrégalo a `@RutaOrden` para que forme parte de la ruta completa.
3. Ejecuta las pruebas: `dotnet test tests/OrionERP.UnitTests/OrionERP.UnitTests.csproj`.

Una versión ya publicada nunca se edita: para cambiar contenido se crea una
versión nueva del curso.

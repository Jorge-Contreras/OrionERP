# Continuidad multiempresa desde main

Checkpoint de cierre solicitado por el usuario el 2026-09-08 para evitar dejar
la arquitectura aislada en un worktree mientras avanza `main`. Continuar los
siguientes trabajos desde el checkout principal de OrionERP; no es necesario
rescatar ni reconstruir el worktree `d460`.

## Evidencia del checkpoint

- Compilación completa Release: 0 errores y 0 advertencias.
- Suite unitaria completa: **1,306/1,306** aprobadas.
- Suite de integración completa: **71/71** aprobadas con SQL habilitado en
  `Orion_Sandbox` y exportación opcional de reportes desactivada.
- Migración administrativa nueva aplicada y verificada en Sandbox después de
  preview revisado; siete migraciones en el manifiesto. Sus bytes no se editan.
- CRUD de Hospedaje, autorización por operación, CFDI/transacciones, Graph,
  OT y consumidores de inventario tienen límites de empresa/sede. Las pruebas
  SQL crean y limpian sus propios fixtures. No representan un E2E completo de
  dos empresas habilitadas por cada rama funcional.
- Producción de Restaurante y operaciones de sede compilan y están incluidas
  en la suite general; su fixture SQL específico quedó pendiente al agotarse
  créditos. No atribuirles evidencia SQL de otros servicios.
- Los smokes previos de consola y hosts públicos se documentan en el plan;
  no se repitió navegador sobre el último incremento al congelar el alcance.

## Pendientes, en orden

Actualización desde `main` del 2026-09-08: el fixture SQL específico de
Restaurante quedó cerrado con **5/5 pruebas aprobadas** en Release y SQL
habilitado. Ver [evidencia focalizada](restaurant-production-scope-20260908.md).
No se repitieron suites completas ni navegador en ese incremento.

1. **Validación focalizada y UX.** Añadir fixture SQL propio de
   `RestaurantProductionService` y `GetSiteOperations/SaveSiteOperations` (ya
   validado en el incremento descrito arriba):
   destino, insumos, prioridades, orden completa, contexto ausente y otra sede
   del mismo RFC. Repetir navegador del selector, OT, ubicaciones/compras/conteos
   y POS con roles reales. Añadir prueba concurrente de proyecto genérico que
   se vincula a calendario mientras se intenta editar/eliminar.
2. **Decisión contable pendiente.** Los 288 vínculos de reservas
   `OHM191112Q26` con pagos `BSU210121M77` permanecen almacenados y ocultos por
   la política. Confirmar si son cobros interempresa legítimos o errores
   históricos antes de diseñar su conciliación. No reasignar ni alterar pagos
   para hacer coincidir totales.
3. **Mappings legacy.** Resolver los cuatro mappings Outlook huérfanos y
   configurar plantillas, cuentas/categorías y propietarios por empresa/sede.
   `CreateActividadForReservation` y `CreateTransaccionesForRoom` están
   bloqueados antes de escribir; la importación de plantillas por texto y la
   edición genérica del maestro compartido de arrendadores también están
   bloqueadas. Los proyectos vinculados se administran por Hospedaje, no por
   el catálogo genérico. Texto histórico sin IDs no recibe atribución inferida.
4. **E2E con dos empresas por rama.** Preparar perfiles, módulos y datos
   propios de prueba, sin copiar proyectos. Cubrir marca, menús/horarios,
   cuentas, recuperación, cookies, documentos, reservas, acceso cruzado y
   transición/reversión de presentación. Graph, correo, pagos y CFDI sólo con
   dobles o proveedores de prueba verificados.
5. **Corte productivo revisable.** Preparar migraciones específicas de
   producción, configuración privada por instancia, respaldos verificados,
   servicios/puertos/túneles y reversión compatible de esquema y binarios.
   El expediente existente es preparación; no constituye un paquete cerrado
   ni aprobación para desplegar. Contemplar invalidación de cookies/enlaces.

Los diagnósticos de inventario de Restaurante usan exclusivamente ubicaciones
generales y nuevas claves `R13-G/R16-G/R17-G`. Los históricos sin procedencia
`R13/R16/R17` siguen almacenados, ocultos y no modificables por ese servicio.
No presentar esa exclusión como reclasificación de datos históricos.

## Límites de entorno al retomar

Código y Sandbox siguen autorizados. No consultar ni modificar `grupocarpio`,
publicar servicios ni modificar túneles sin la aprobación independiente ya
establecida. Fusionar código a `main` no autoriza desplegarlo: el código nuevo
requiere un esquema compatible y configuración de alcance.

La conexión heredada puede apuntar a producción. Antes de abrir, usar
`SqlConnectionStringBuilder.set_InitialCatalog('Orion_Sandbox')` en PowerShell,
verificar `get_InitialCatalog()` y luego comprobar `DB_NAME()`. No imprimir
conexiones ni secretos; cambiar variables sólo dentro del proceso de prueba.
La consola conserva los secretos locales de Development por encima de variables
heredadas. La base local devuelve `Orion_SandBox` (comparar sin distinguir caja).

El incidente inicial de conexión alcanzó únicamente la guarda `DB_NAME()` de
producción, que abortó antes de leer tablas. No hubo lecturas de tablas,
escrituras, migraciones ni despliegue productivo; véase el registro completo
en el plan de continuidad.

Referencias: [plan y evidencia](public-websites-rebaseline-20260908.md),
[matriz SQL y consumidores](hospitality-sql-isolation-20260908.md),
[calendarios](hospitality-calendar-sync-isolation-20260908.md),
[expediente productivo](public-websites-production-cutover-review.md).

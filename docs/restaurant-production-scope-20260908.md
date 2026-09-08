# Validación SQL de producción y operaciones de Restaurante

Incremento desde `main` el 2026-09-08. Se añade
`HospitalityRestaurantProductionScopeTests`, sin modificar servicios ni esquema.
Las guardas existentes superaron **5/5 pruebas** en Release con
`ORION_RUN_SQL_INTEGRATION=1` y `ORION_RUN_EXPORT_QA=0`.

## Cobertura ejecutada

- Fuentes con y sin lotes: prioridades autorizadas, insuficiencia cuando sólo
  hay existencias privadas suficientes y rollback de reservas parciales.
- Destino privado rechazado sin alcance de Hospedaje y desde otra sede temporal
  de la misma empresa. Empresa de sesión ausente y RFC solicitado ajeno rechazados.
- Orden completa oculta si cualquier insumo es privado, aunque su destino sea
  general; también si sólo el destino es privado. Inicio, cancelación,
  finalización y reintento idempotente no permiten eludir su alcance original.
- Ciclo autorizado plan/inicio/finalización y cancelación con liberación;
  reintentos sin duplicar consumo, salida ni eventos. Verificación SQL de saldos,
  lotes, estados y eventos. Producción general sigue disponible sin Hospedaje.
- Lectura de prioridades filtra ubicaciones privadas. Guardar referencias
  ajenas, o sustituir una configuración original que contiene filas ocultas,
  se rechaza sin borrar prioridades ni cambiar el corte operativo.

El fixture crea y elimina sus propios IDs de sede, habitación, ubicaciones,
materiales, BOM, lotes y órdenes. La sede `restaurante.Site` del fixture pertenece
al RFC de Bonhomía; no se atribuye automáticamente a Bruno. La segunda sede
`orion.Site` prueba la frontera de Hospedaje del mismo RFC; no representa un
segundo website ni prueba habilitación administrativa de módulos.

## Entorno y evidencia

Catálogo fijado explícitamente a `Orion_Sandbox` antes de abrir, comparación
sin distinguir mayúsculas y comprobación `DB_NAME()` antes de consultar tablas.
Las siete migraciones del manifiesto devolvieron `VERIFIED`; sus bytes siguen
intactos. Compilación de los proyectos dependientes sin advertencias ni errores.
Recibo local: `artifacts/test-results/restaurant-production-scope.trx`.

Los primeros intentos ajustaron datos del fixture a las restricciones vigentes
(SiteKey minúscula, rol productivo y sede operativa habilitada); no se relajaron
restricciones SQL ni guardas de aplicación para lograr la aprobación.

No se ejecutaron suites completas, navegador, exportaciones visuales, cobros,
timbrados, correo, Graph, consultas productivas, migraciones ni despliegues.
Los bloqueos deliberados e históricos del checkpoint continúan vigentes.
Siguiente validación: carrera real de proyecto/calendario y navegador por roles.

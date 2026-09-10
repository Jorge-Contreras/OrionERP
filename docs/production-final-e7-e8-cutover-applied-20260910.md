# Cierre productivo E2, E7 y E8 — 2026-09-10

## Resultado

El corte autorizado terminó aplicado sobre `grupocarpio`. El manifiesto productivo
quedó con sus **21 migraciones aplicadas** y sin pendientes. Los tres servicios usan
los binarios de `main`, están en ejecución y respondieron 200 local y públicamente.

E6 conserva deliberadamente el reporte vigente compatible; `@SoloPublicadas` no se
adopta todavía como reporte oficial. Esa decisión no deja código ni operación
pendiente en este proyecto.

## Respaldo

Antes del primer cambio se creó el respaldo completo `COPY_ONLY`
`grupocarpio_pre_final_e7_e8_20260910_105509_a94fb7c4.bak`, de 4,788,720,640 bytes,
con checksum. `RESTORE VERIFYONLY WITH CHECKSUM` terminó correctamente. Cada paquete
se volvió a previsualizar contra el estado respaldado antes de aplicarse.

## E7 y eventos de Restaurante

- `20260910_production_hospitality_legacy_mechanisms` quedó aplicada con checksum
  `E84BA126CBFE...`: tres asociaciones de arrendador, 81 predicados activos, mappings
  `105 → 24222` y `107 → 24228` reparados, y mappings 18 y 61 preservados en
  cuarentena local. No se invocó Microsoft Graph.
- Las actividades continúan fail-closed cuando no existe un mapping completo; no se
  inventó ninguna plantilla por coincidencia de texto.
- `20260910_production_restaurant_event_outbox_disposition` cerró exactamente los
  eventos 125–2124 de Bruno. Quedaron 2,000 filas de auditoría inmutable, cero de ese
  conjunto pendientes y el payload original protegido por hash individual.
- `RestaurantEventBroadcasting__Enabled=true` quedó en la configuración privada de
  la consola después del cierre histórico. Sólo los eventos posteriores pueden ser
  difundidos; al terminar el corte no había eventos nuevos pendientes.

## E8

- E8a: 8,102 pólizas y 18,706 movimientos quedaron bajo seis predicados contables.
  Las dos cabeceras y cuatro movimientos legacy sin empresa se resolvieron por su
  vínculo OHM exacto, sin cambiar importes.
- E8b: 11,500 filas de ubicación, existencia y movimientos quedaron bajo nueve
  predicados del núcleo de inventario.
- E8c ya estaba aplicada: 18 predicados del agregado de asistencia.
- E8d: las 16 filas fiscales OHM quedaron bajo 18 predicados; `cfdi.Comprobante`
  permaneció fuera del lote.

La comprobación posterior devolvió cero filas sin contexto en los tres agregados
nuevos. Con contexto, OHM vio 6,242 pólizas, 15,581 movimientos, 5,257 filas de
inventario y 16 fiscales; Bruno vio 339 pólizas, 664 movimientos, 6,243 filas de
inventario y cero fiscales.

## E2

Se crearon `orion_bonhomia_web` y `orion_bruno_web` con contraseñas aleatorias
distintas, guardadas únicamente en la configuración privada de sus respectivos
servicios. Ninguna contraseña entró al repositorio, recibos o salida de consola.

Ambas identidades tienen cero roles de base. La matriz final conserva permisos por
objeto y vetos explícitos: Bonhomia no accede a Restaurante y Bruno no accede a
Hospedaje. Bonhomia recibió además `VIEW DEFINITION` solamente sobre
`orion.HospitalityScopePolicy` y `dbo.ROOM_CALENDAR`, necesario para que su readiness
compruebe la política RLS y los dos índices filtrados sin conceder DDL ni bypass.

## Publicación y smoke test

Se publicaron `OrionERP`, `OrionERP.Bonhomia` y `OrionERP.Bruno`, reteniendo la
versión anterior de cada servicio para reversión. Resultado final:

| Superficie | Prueba | Resultado |
| --- | --- | --- |
| Consola | `http://127.0.0.1:5000/readyz` | 200 |
| Bonhomia | `/readyz` local y `https://bonhomiasuites.com/readyz` | 200 |
| Bruno | `/readyz` local y `https://brunosgarden.com/readyz` | 200 |
| Consola pública | `https://orionerp.orion.land` → login | 200 |
| Bonhomia pública | `https://bonhomiasuites.com` | 200 |
| Bruno público | `https://brunosgarden.com` | 200 |

El conjunto unitario terminó con **1,389 pruebas aprobadas**, incluidas las guardas
para los dos permisos mínimos de metadata.

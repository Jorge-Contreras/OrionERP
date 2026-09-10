# Aplicación productiva del corrector E7 de 288 vínculos de pago

Fecha: 2026-09-09. El usuario autorizó expresamente respaldar `grupocarpio` y aplicar
la corrección de los 288 vínculos.

## Decisión aplicada

Las 288 transacciones pertenecen a `BSU210121M77` y no deben estar relacionadas con
reservaciones de `OHM191112Q26`. El corrector eliminó únicamente las 288 filas
aprobadas de `dbo.Reservation_Transacciones`. No reasignó pagos y no hizo `UPDATE` ni
`DELETE` sobre transacciones, movimientos contables o vínculos CFDI.

## Respaldo y preview

Antes del corte se creó un respaldo completo `COPY_ONLY` con checksum y se verificó
con `RESTORE VERIFYONLY ... WITH CHECKSUM`.

- Archivo en SQL Server:
  `grupocarpio_pre_e7_payment_links_20260909_223246_5aa54f29.bak`.
- Recibo local:
  `artifacts/production-cutover-20260908/backup-receipt-e7-payment-links.json`.
- Preview posterior al respaldo:
  `artifacts/database-previews/e7-payment-link-removal-production-preview-post-backup-20260909.json`.
- Checksum del paquete:
  `9891C1F8BE6594E41AE7E38FA903893BC35FE7AAC09C952139BEA5CD7F0EE5D9`.

El preview releyó exactamente las 288 filas aprobadas y los mismos doce checksums de
lote antes de permitir la aplicación.

## Resultado observado en producción

| Comprobación | Resultado |
| --- | ---: |
| Vínculos eliminados | 288 |
| Importe informativo de los vínculos | $852,308.75 |
| Lotes | 12 |
| Tamaño máximo de lote | 25 |
| Vínculos físicos restantes del conjunto | 0 |
| Filas de manifiesto inmutable | 288 |
| Filas de auditoría inmutable | 288 |
| Transacciones BSU preservadas | 288 |
| Transacciones con movimientos contables | 84 |
| Movimientos contables preservados | 251 |
| Transacciones con vínculo CFDI | 20 |
| Vínculos CFDI preservados | 20 |
| Deriva en contabilidad o CFDI | 0 |

El predicado fuerte de pagos quedó restaurado con sus tres predicados; manifiesto y
auditoría quedaron cubiertos por seis predicados adicionales. Las pruebas negativas
confirmaron que no se puede editar el manifiesto aprobado, borrar auditoría ni recrear
un vínculo OHM→BSU. No quedaron transacciones SQL abiertas.

`20260909_hospitality_payment_link_removal` terminó **VERIFIED** y el manifiesto
productivo completo terminó con **16 VERIFIED**.

## Alcance operativo

Fue una corrección exclusivamente de datos y evidencia SQL. No requiere publicar ni
reiniciar los binarios de OrionERP. Los demás frentes pendientes de E7 y E8 conservan
su estado independiente.

# Aplicación productiva de E5 y E6

Fecha: 2026-09-09. Autorización: el usuario instruyó aplicar E5 y E6 en `grupocarpio`
tras desplegar binarios.

## Respaldo

`BACKUP DATABASE [grupocarpio] ... WITH COPY_ONLY, CHECKSUM` y
`RESTORE VERIFYONLY ... WITH CHECKSUM`, mismo procedimiento y directorio predeterminado
que los cortes anteriores. Archivo:
`grupocarpio_pre_outbox_reports_20260909_000243_e8d5de72.bak`. Recibo local en
`artifacts/production-cutover-20260908/backup-receipt-outbox-reports.json`. Los recibos
anteriores no se sobrescribieron.

## Migraciones aplicadas

| MigrationId | Preview | Apply |
| --- | --- | --- |
| `20260908_production_accounting_outbox` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260908` |
| `20260908_production_published_reports` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260908` |

Los cuerpos de los dos procedimientos de reportes se volcaron con `OBJECT_DEFINITION`
**desde producción** antes de parchar, y se comprobó que eran idénticos a los de
Sandbox. El único cambio es el parámetro `@SoloPublicadas`, con valor por omisión 0.

`--mode verify` sobre el manifiesto productivo completo devuelve **11 VERIFIED**.

## Comprobación posterior, contra producción

- `contabilidad.AccountingOutbox` y `contabilidad.HospitalityAccountingMapping` existen
  y están **vacías**: la bandeja sin operaciones y Hospedaje apagada por mappings
  faltantes.
- Los dos procedimientos tienen `@SoloPublicadas`.
- La balanza de OHM para agosto 2026 devuelve **108 filas con `@SoloPublicadas = 0`** y
  **0 filas con 1**, que es exactamente lo esperado: ninguna empresa tiene el ciclo
  encendido, y por eso el valor por omisión es 0 y el reporte vigente sigue siendo el
  oficial.
- Los tres servicios responden `200` en `/readyz`.

## El código de E5 y E6 todavía no está desplegado

Los binarios productivos son de las **19:45–19:46 del 2026-09-08**. E5 se registró a las
22:21 y E6 a las 22:34, así que lo desplegado contiene E1, E3 y E4, no E5 ni E6.

Consecuencias, todas benignas: la contabilización diaria de Restaurante sigue usando el
camino anterior de dos transacciones, y la variante publicada de los reportes no es
alcanzable desde la aplicación. Nada está roto, porque el código anterior no invoca la
bandeja ni pasa el parámetro nuevo, y ese parámetro tiene valor por omisión.

Para que E5 y E6 surtan efecto hace falta una publicación más de binarios.

### Corrección

Al empezar esta sesión afirmé que balanza, resultados y la póliza diaria estaban
fallando en producción por falta de estas migraciones. **Era falso**: el binario
desplegado precede a ese código. La comprobación que faltaba era la marca de tiempo del
ejecutable contra la del commit, no sólo la ausencia de los objetos en la base.

## Lo que sigue pendiente

- Publicar binarios para que E5 y E6 entren en vigor.
- Encender el ciclo por empresa, que exige aprobar su baseline. Sin eso, la variante
  publicada de los reportes seguirá devolviendo cero y no es adoptable.
- Mappings de Hospedaje: por empresa y sede, al menos `LeaseIncomeAccount` y
  `LeaseReceivableAccount` en `contabilidad.HospitalityAccountingMapping`. Mientras no
  existan, `dbo.CreateTransaccionesForRoom` sigue lanzando 51823.
- `GenerateIndividualCfdiPolicyAsync`, con órdenes individuales y el ajuste
  `LateCfdiReversal`, sigue fuera de la bandeja durable.

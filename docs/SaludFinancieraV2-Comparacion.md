# Salud Financiera v2 - conciliación de 12 meses

Fecha de validación: 24/08/2026. RFC: `OHM191112Q26`. Las cifras son internas y no auditadas.

## Método

Se capturó la salida productiva de `reporteFinanciero.Reporte_Salud_Empresa` antes de sustituir el procedimiento y se volvió a ejecutar la v2 sobre la misma base. Septiembre de 2025 a julio de 2026 se comparan como meses completos; agosto de 2026 se compara MTD al 24/08/2026. Antes del cambio se creó y verificó el respaldo `grupocarpio_PreSaludFinancieraV2_20260824_0203.bak`.

## Resultado

| Mes | Habitación anterior | Habitación v2 | Extras v2 | Ocupación anterior | Ocupación v2 | Reservas anterior/v2 | Pipeline v2 | Resultado anterior | Resultado v2 | Delta resultado |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 2025-09 | $81,731.71 | $81,731.69 | $172.41 | 33.33% | 29.17% | 32 / 32 | 0 | $28,298.05 | $30,798.05 | $2,500.00 |
| 2025-10 | $85,594.02 | $85,594.03 | $172.41 | 35.48% | 31.05% | 32 / 32 | 0 | $55,455.94 | $57,830.96 | $2,375.02 |
| 2025-11 | $132,544.42 | $132,544.38 | $2,157.96 | 43.81% | 38.33% | 40 / 40 | 0 | -$62,610.68 | -$56,877.44 | $5,733.24 |
| 2025-12 | $119,551.94 | $119,551.85 | $1,896.52 | 43.32% | 37.90% | 56 / 56 | 0 | -$23,530.75 | $395,712.41 | $419,243.16 |
| 2026-01 | $51,686.81 | $51,686.92 | $344.83 | 16.53% | 16.53% | 19 / 19 | 0 | -$88,877.01 | -$80,830.05 | $8,046.96 |
| 2026-02 | $15,450.31 | $15,450.34 | $0.00 | 4.91% | 4.91% | 8 / 8 | 0 | -$35,832.70 | -$15,323.10 | $20,509.60 |
| 2026-03 | $82,209.11 | $82,209.15 | $689.66 | 22.58% | 22.58% | 33 / 33 | 0 | $18,256.62 | $50,311.13 | $32,054.51 |
| 2026-04 | $52,497.18 | $52,497.22 | $4,051.72 | 16.25% | 16.25% | 24 / 24 | 0 | -$121,067.74 | -$118,084.30 | $2,983.44 |
| 2026-05 | $125,496.89 | $119,839.60 | $172.41 | 32.26% | 31.45% | 50 / 49 | 0 | $63,457.42 | $77,717.32 | $14,259.90 |
| 2026-06 | $73,617.96 | $73,617.99 | $1,746.70 | 21.25% | 21.25% | 41 / 41 | 0 | $14,221.24 | $21,731.11 | $7,509.87 |
| 2026-07 | $125,471.80 | $124,717.53 | $879.31 | 29.03% | 29.03% | 48 / 46 | 2 | $64,981.22 | $60,604.72 | -$4,376.50 |
| 2026-08 | $75,653.33 | $60,567.17 | $1,551.71 | 20.56% | 19.27% | 28 / 26 | 2 | -$9,624.69 | -$5,239.95 | $4,384.74 |

## Explicación de diferencias

- La v2 reconoce sólo `ACTIVA` y `PAGADA`. En mayo excluye una reservación `CANCELADA` con dos noches y $5,086.21 de tarifa; en julio y agosto presenta las cotizaciones sólo como pipeline. Agosto excluye 14 noches cotizadas por $12,823.30 de ingreso de habitación.
- El ingreso de habitación ahora aplica el descuento de suite noche por noche y redondea a centavos. Esto explica los centavos de meses anteriores y, junto con los estados válidos, los cambios materiales de mayo, julio y agosto.
- La ocupación usa noches vendidas entre noches esperadas de ocho habitaciones activas y rentables. La oficina/lavandería ya no infla el inventario y los huecos del calendario se informan como calidad de datos.
- Extras se presentan por separado; el esquema y los cálculos de experiencias están activos, aunque no existen experiencias reservadas en el periodo revisado.
- El delta del resultado neto concilia exactamente con la incorporación de otros ingresos 403 y las familias antes omitidas. La familia 601 reduce el resultado en $21,359.08 entre enero y agosto de 2026; los ingresos 403 y demás grupos explican el resto, incluyendo $423,047.59 de 403 en diciembre de 2025.
- En flujo de efectivo, las transferencias internas entre 101/102 dejan de inflar entradas y salidas brutas. En agosto se eliminan $12,500 de ambos lados sin cambiar el flujo neto.

La v2 no modifica asientos contables ni datos históricos: cambia su clasificación, el universo válido de reservaciones y la presentación gerencial.

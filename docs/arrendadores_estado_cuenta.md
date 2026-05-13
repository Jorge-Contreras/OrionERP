# Arrendadores - Estado de cuenta

## Alcance

La pagina `/arrendadores` permite consultar el estado de cuenta mensual de una propiedad administrada por Bonhomia. La primera version es de solo lectura y no modifica informacion contable ni reservaciones.

## Flujo de usuario

1. Seleccionar arrendador desde `dbo.Proveedores`.
2. Seleccionar propiedad desde `dbo.ROOM` usando `ROOM.OWNER_ID`.
3. Elegir anio y mes calendario.
4. Consultar:
   - Resumen del periodo.
   - Noches pagadas.
   - Noches excluidas y motivo.
5. Generar PDF formal del estado de cuenta con QuestPDF.

## Relaciones usadas

```sql
dbo.Proveedores.id = dbo.ROOM.OWNER_ID
dbo.ROOM.ROOM_NAME = dbo.ROOM_CALENDAR.ROOM
TRY_CONVERT(int, dbo.ROOM_CALENDAR.LOCK_DESCRIPTION) = dbo.RESERVATION.ID
dbo.RESERVATION.ID = dbo.Reservation_Transacciones.ReservationID
dbo.Reservation_Transacciones.TransaccionID = dbo.Transacciones.ID
dbo.Transacciones.ID = dbo.Registro_Contable.TransaccionID
```

## Criterio de noche reportable

Una noche entra al estado de cuenta si:

- `ROOM_CALENDAR.IS_LOCKED = 1`.
- `ROOM_CALENDAR.STATUS = 'ACTIVA'`.
- `ROOM_CALENDAR.PRECIO > 0`.
- `ROOM_CALENDAR.LOCK_DESCRIPTION` contiene un `RESERVATION.ID` valido.
- `RESERVATION.STATUS = 'ACTIVA'`.
- La reservacion tiene pagos en `Reservation_Transacciones` con `Amount > 0`.
- Cada pago considerado tiene al menos un asiento en `Registro_Contable`.
- La suma de `Reservation_Transacciones.Amount` contabilizada cubre `RESERVATION.TOTAL_PRICE`.

## Calculo

El importe base del reparto sale de `ROOM_CALENDAR.PRECIO`.

```text
arrendador_30 = ROOM_CALENDAR.PRECIO * ROOM_CALENDAR.PORCENTAJE_ARRENDAMIENTO
isr_10 = arrendador_30 * 0.10
pago_final_arrendador = arrendador_30 - isr_10
```

## Motivos de exclusion

- `PRECIO_CERO`
- `SIN_RESERVATION_ID_EN_LOCK_DESCRIPTION`
- `RESERVACION_NO_ENCONTRADA`
- `RESERVACION_NO_ACTIVA`
- `SIN_PAGO_CONTABILIZADO`
- `PAGO_PARCIAL`

## Caso validado

Para CASA GRECIA / GEORGINA CONTRERAS HERNANDEZ en `Orion_SandBox`, al 2026-05-13:

| Mes | Noches | Cobrado | Arrendador 30% | ISR 10% | Pago final |
| --- | ---: | ---: | ---: | ---: | ---: |
| 2026-02 | 1 | 2,088.24 | 626.47 | 62.65 | 563.82 |
| 2026-03 | 5 | 9,547.31 | 2,864.19 | 286.42 | 2,577.77 |
| 2026-04 | 3 | 9,935.23 | 2,980.57 | 298.06 | 2,682.51 |
| 2026-05 | 1 | 3,027.95 | 908.39 | 90.84 | 817.55 |

Exclusiones detectadas:

| Fecha | Reservacion | Motivo |
| --- | ---: | --- |
| 2026-04-12 | 23934 | PRECIO_CERO |
| 2026-05-16 | 23935 | SIN_PAGO_CONTABILIZADO |

## Notas tecnicas

- El pago debe validarse con `EXISTS` contra `Registro_Contable` para no duplicar `Reservation_Transacciones.Amount` por la cantidad de asientos.
- `Transacciones.Monto` puede contener una poliza que agrupa mas de una reservacion; para el saldo de la reservacion se usa `Reservation_Transacciones.Amount`.
- El filtro mensual usa `ROOM_CALENDAR.ROOM_DATE`.
- Si en el futuro `ROOM_CALENDAR` recibe una FK hacia `ROOM`, conviene cambiar el join por ID y dejar de depender de `ROOM_NAME`.

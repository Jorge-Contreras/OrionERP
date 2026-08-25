# Manual de uso: CxCR / AP recurrente

## Proposito del modulo

CxCR controla cuentas por pagar recurrentes: servicios, impuestos, seguros, rentas u otros pagos que se repiten por proveedor. El modulo crea vencimientos programados, permite ligarlos a polizas/transacciones, guardar archivos de soporte y dar seguimiento a pagos pendientes, parciales, pagados, omitidos o cancelados.

## Alta de una cuenta recurrente

1. Inicia sesión con la empresa en la que trabajarás. Para cambiar de empresa, cierra sesión y vuelve a ingresar.
2. Abre `CxCR`.
3. Usa `Nuevo recurrente`.
4. Captura:
   - `Nombre`: nombre operativo del pago.
   - `Proveedor`: proveedor ligado, si existe en el catalogo.
   - `Categoria`: tipo de pago.
   - `Monto esperado`: monto base para nuevos vencimientos.
   - `Frecuencia`, `Cada`, `Inicio`, `Fin`, `Dia venc.` y `Mes venc.`: reglas para crear vencimientos.
   - `Descripcion`: notas generales del recurrente. Estas notas se muestran en cada ocurrencia.
   - `Website`, `UserName`, `Password`: datos para entrar al portal del proveedor. El password se guarda cifrado.
5. Guarda. El sistema genera vencimientos hacia adelante usando la ventana activa del modulo.

## Uso de vencimientos

- La tabla `Vencimientos` muestra fecha de vencimiento, periodo, monto esperado, pagado, estatus, polizas ligadas y archivos.
- Al seleccionar un vencimiento se abre su detalle.
- `Informacion recurrente` muestra datos generales del recurrente, notas, proveedor, frecuencia y datos de acceso.
- Usa `Copiar` en `Website`, `UserName` o `Password` para llevar el dato al portapapeles y entrar al portal del proveedor.
- Los usuarios de solo lectura pueden ver la informacion operativa, pero no pueden copiar credenciales ni modificar vencimientos.

## Dashboard de inicio

El dashboard muestra ocurrencias CxCR abiertas, vencidas o por vencer. En `Ajustes > Notificaciones CxCR`, un administrador puede definir cuantos dias de anticipacion se usan para mostrar proximos vencimientos. El valor predeterminado es 5 dias.

## Migracion desde servicios legacy

La importacion desde `dbo.Servicios` llena `Website` desde `Pagina_Web` y `UserName` desde `Usuario` cuando el recurrente queda ligado por `LegacyServicioId`. El password legacy de `Contrasena` no se copia con SQL plano: debe ejecutarse el script de credenciales para cifrar el valor con la llave AES-GCM de la aplicacion y guardarlo en `AP.RecurringPayable.PasswordEnc`.

Por defecto, el importador conserva credenciales existentes en AP y solo llena valores vacios. Usa reemplazo solamente cuando quieras sobrescribir los datos actuales con los valores de `dbo.Servicios`.

## Pago manual y monto esperado del vencimiento

Cada vencimiento puede tener un monto esperado distinto al recurrente base. Esto sirve cuando el proveedor factura un importe variable.

1. Selecciona el vencimiento.
2. Cambia `Esperado` al importe real de esa ocurrencia.
3. Si ya hay una poliza ligada, guarda el estatus para que el sistema recalcule el pago contra el nuevo esperado.
4. Si el total ligado cubre el esperado, el estatus cambia a `Pagado`; si no lo cubre, queda `Parcial`.

Cuando no hay polizas ligadas, puedes editar manualmente `Estatus`, `Monto`, `Fecha pago` y `Notas`.

## Ligar polizas/transacciones

1. Selecciona un vencimiento.
2. Busca una transaccion por ID, concepto, referencia o memo.
3. Usa `Ligar`.
4. El sistema toma el monto absoluto y fecha de la transaccion, suma los pagos ligados y recalcula el estatus.

Para corregir una liga, usa `Desligar`. Al desligar se recalcula el total pagado del vencimiento.

## Archivos

Usa `Archivos` para adjuntar recibos, comprobantes, capturas o documentos del proveedor. Los archivos ligados a una ocurrencia hacen que esa ocurrencia se conserve durante una resiembra.

## Resembrar vencimientos

`Resembrar vencimientos` reconstruye los vencimientos futuros de la cuenta recurrente seleccionada.

La resiembra elimina y recrea solamente ocurrencias futuras que estan:

- Pendientes.
- Sin monto pagado.
- Sin fecha de pago.
- Sin notas.
- Sin cambios manuales.
- Sin polizas ligadas.
- Sin archivos activos.

La resiembra conserva ocurrencias pagadas, parciales, canceladas, con polizas, con archivos, con notas o editadas manualmente. Usa esta accion cuando cambies fecha, frecuencia, monto base o regla del recurrente y necesites regenerar el calendario futuro sin perder historial.

## Cancelar una ocurrencia

Usa `Cancelar vencimiento` cuando un pago programado no aplica para ese periodo.

- Solo se puede cancelar si no tiene polizas ligadas.
- Si existe una poliza ligada, primero usa `Desligar`.
- Cancelar no borra el recurrente ni afecta otros vencimientos.
- Los vencimientos cancelados no cuentan como pendientes ni vencidos.

## Estatus

- `Pendiente`: no tiene pagos registrados.
- `Parcial`: tiene pagos, pero el total ligado no cubre el monto esperado.
- `Pagado`: el total pagado cubre el monto esperado.
- `Omitido`: se decide no pagar, pero no se cancela la ocurrencia.
- `Cancelado`: el vencimiento no aplica y queda fuera del seguimiento pendiente.

## Flujo recomendado

1. Crea o actualiza el recurrente con sus reglas, notas y credenciales del proveedor.
2. Revisa vencimientos por vencer o vencidos.
3. Entra al portal del proveedor usando los botones de copiar.
4. Actualiza el monto esperado del vencimiento si la factura del periodo cambio.
5. Liga la poliza/transaccion real.
6. Adjunta el soporte.
7. Si cambias la agenda del recurrente, usa `Resembrar vencimientos` para reconstruir solo el futuro seguro de modificar.

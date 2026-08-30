# OrionERP Training: clon operativo de producción

`OrionERP.Training` es un sandbox para practicar con una copia completa de
`grupocarpio`. No sanitiza datos, no crea usuarios ficticios y no cambia roles o
contraseñas. Al restaurar producción sobre `Orion_Training`, cada colaborador
entra con la misma cuenta y ve la misma información que conoce, pero todas sus
modificaciones quedan en la base de capacitación.

## Direcciones y servicio

| Elemento | Valor |
| --- | --- |
| URL pública | `https://capacitacion.orion.land` |
| URL local | `http://localhost:5030` |
| Servicio de Windows | `OrionERP.Training` |
| Base de datos | `Orion_Training` |
| Ambiente de .NET | `Training` |

El servicio usa inicio automático retrasado, depende de SQL Server y tiene tres
intentos automáticos de recuperación. La pantalla conserva una franja visible de
“ENTORNO DE PRÁCTICA” para evitar confundirla con el sistema en vivo.

## Actualizar el ambiente

1. Detén `OrionERP.Training` si la herramienta de restauración lo requiere.
2. Crea o elige un respaldo reciente de `grupocarpio`.
3. Restaura ese respaldo con el nombre exacto `Orion_Training` y déjala en modo
   `MULTI_USER`.
4. Inicia `OrionERP.Training`.

Eso es todo. No hay saneamiento, atestación, aprovisionamiento de usuarios ni
contraseñas especiales después del clon. El login SQL normal `orion` ya existe
con el mismo SID en `grupocarpio`, `Orion_Sandbox` y `Orion_Training`, por lo que
su vínculo viaja en la restauración.

La configuración de Training copia el material de cifrado que necesita para leer
campos protegidos que vienen del clon. Las cookies siguen usando nombres propios
de `capacitacion.orion.land`, así que el usuario inicia sesión otra vez con su
contraseña habitual sin reemplazar la sesión de `orionerp.orion.land`.

## Configurar o reparar el servicio

`Configure-TrainingService.ps1` valida únicamente que la conexión apunte a
`Orion_Training`, que use cifrado SQL y que el puerto sea el dedicado a Training.
La cadena se guarda en la configuración privada del servicio y no se imprime.

```powershell
$env:ORION_TRAINING_ConnectionStrings__OrionDb = '<conexión normal de OrionERP cambiando Database a Orion_Training>'
.\Configure-TrainingService.ps1 -Restart
Remove-Item Env:ORION_TRAINING_ConnectionStrings__OrionDb
```

## Publicar cambios de aplicación

```powershell
.\Publish-Training.ps1
```

La publicación reinicia el servicio y acepta el despliegue cuando `/readyz`
confirma `Training`, `Orion_Training`, conexión activa y modo
`production_clone`.

## Comprobación rápida

```powershell
Get-Service OrionERP.Training
Invoke-RestMethod http://127.0.0.1:5030/readyz
```

La respuesta debe indicar `status = ready`, `database.catalog = Orion_Training`
y `training.mode = production_clone`.

## Alcance de las integraciones externas

Las pantallas y las operaciones sobre la base clonada funcionan igual que en
producción. Las credenciales de servicios externos no se copian automáticamente
desde las variables del servicio de producción; sólo funcionan en Training si se
configuran explícitamente con el prefijo `ORION_TRAINING_`. Esto no cambia los
datos ni los permisos del clon y evita depender accidentalmente de una
configuración que no forma parte del respaldo SQL.

# Runbook: entornos y scripts de OrionERP Training

Guía operativa para saber **qué script se corre una sola vez**, **cuál se corre
cada vez**, y **cuándo y cómo** se sanitiza el entorno de capacitación.

El detalle de lo que hace internamente la sanitización está en
[orion-training-sanitization.md](orion-training-sanitization.md); el contenido de
los cursos está en [orion-capacitacion-curriculo.md](orion-capacitacion-curriculo.md).
Este documento es el índice operativo de ambos.

## 1. Los tres entornos

| | Base de datos | Servicio | Origen público | Para qué sirve |
| --- | --- | --- | --- | --- |
| **Producción** | `grupocarpio` | `OrionERP` | `orionerp.orion.land` | Operación real. Aquí viven las asignaciones, evaluaciones, firmas y constancias de capacitación de personas reales. |
| **Capacitación** | `Orion_Training` | `OrionERP.Training` | `capacitacion.orion.land` | Copia desechable y sanitizada. Aquí se hace la práctica que sí escribe (POS, conteos, carga de XML). Se borra por completo en cada reinicio. |
| **Desarrollo** | `Orion_Sandbox` | (local) | — | Validación de esquema y contenido antes de publicar. |

El módulo de Capacitación corre en producción; el botón de práctica de cada
curso abre la ruta correspondiente en `capacitacion.orion.land` mediante
`Capacitacion:SandboxBaseUrl`. Si esa clave está vacía, el botón aparece
deshabilitado con la leyenda "Sandbox no configurado".

## 2. Scripts de una sola vez (por servidor o por host)

Se corren al montar el entorno y **sólo se repiten si cambia lo que configuran**.

| Script | Cuándo | Se repite si |
| --- | --- | --- |
| `Provision-TrainingRuntimeSqlLogin.ps1` | Al crear el login SQL de mínimo privilegio `orion_training_runtime` | Se rota la contraseña, se reconstruye la instancia SQL o se pierde el login |
| `Configure-TrainingService.ps1` | Al registrar y asegurar el servicio `OrionERP.Training` (ACLs, llaves de Data Protection, variables de entorno) | Cambia la URL pública, la cadena de conexión, la ruta de llaves o los hosts permitidos |
| `20260817_training_runtime_login.sql` | Lo ejecuta `Provision-TrainingRuntimeSqlLogin.ps1`. **Nunca a mano** | Igual que el script anterior |

```bash
pwsh -File Provision-TrainingRuntimeSqlLogin.ps1 -RuntimePassword (Read-Host -AsSecureString) -Apply
```

```bash
pwsh -File Configure-TrainingService.ps1 -Restart
```

`Configure-TrainingService.ps1` toma la cadena de conexión de
`ORION_TRAINING_ConnectionStrings__OrionDb` y escribe en el registro del servicio
`ORION_TRAINING_Capacitacion__SandboxBaseUrl` con el origen público de Training.

## 3. Scripts recurrentes

| Script | Frecuencia | Qué hace |
| --- | --- | --- |
| `Run-TrainingSanitizer.ps1` | **Cada reinicio**, desde el acceso directo del escritorio | Lanzador interactivo: eleva, detiene el servicio, corre la vista previa, pide confirmación escrita, aplica el reinicio y vuelve a iniciar el servicio |
| `Sanitize-OrionTraining.ps1` | **Cada reinicio del entorno de capacitación** | Orquesta el borrado, la reinstalación del catálogo, la cohorte sintética y la atestación. Es el único camino autorizado para tocar `Orion_Training`. |
| `Publish-Training.ps1` | Cada despliegue de la app de capacitación | Publica el binario y controla el servicio `OrionERP.Training` |
| `Publish-prod.ps1` | Cada despliegue de producción | Publica el servicio `OrionERP` |
| `Install-CapacitacionSchema.ps1` | Cada vez que cambia el esquema o el currículo | Aplica el esquema y el catálogo de cursos a `grupocarpio` u `Orion_Sandbox`. Vista previa por defecto; `-Apply` para confirmar. |

### SQL que corre solo dentro del orquestador

Estos archivos **no son ejecutables a mano**: exigen un guardarraíl de sesión
(`SESSION_CONTEXT('OrionTrainingSanitizerApply')`), parámetros de credenciales o
ambos, y fallan si se abren sueltos en SSMS. Los ejecuta
`Sanitize-OrionTraining.ps1` en este orden:

| # | Archivo | Papel |
| --- | --- | --- |
| 1 | `20260818_orion_training_neutralize_clone.sql` | Neutraliza por manifiesto exacto los objetos heredados del clon (RLS, sinónimos, módulos) |
| 2 | `20260817_orion_training_sanitize.sql` | Borra todas las filas salvo `__EFMigrationsHistory` y `DateDimension`, reinicia identidades y secuencias |
| 3 | `20260817_capacitacion_v1.sql` | Reinstala esquema, triggers de inmutabilidad y los 5 cursos piloto |
| 4 | `20260819_capacitacion_curriculum_v2.sql` | Agrega los 24 cursos por módulo y la ruta `ORION-EXPERTO` |
| 5 | `20260817_orion_training_reviewed_triggers.sql` | Instala los triggers revisados de mantenimiento |
| 6 | `20260818_orion_training_cfdi_fixture_parser.sql` | Sustituye el importador CFDI por el exclusivo de Training |
| 7 | `20260817_orion_training_provision.sql` | Crea la cohorte sintética y las asignaciones (requiere los 4 hashes de contraseña) |
| 8 | `20260817_orion_training_scenarios.sql` | Siembra los escenarios RH ficticios |
| 9 | `20260817_orion_training_attest.sql` | Revisa el manifiesto completo y sólo entonces marca la atestación positiva |

Los archivos 3 y 4 son los únicos que además se aplican fuera de Training, a
través de `Install-CapacitacionSchema.ps1`. Ambos son idempotentes.

## 4. Cuándo sanitizar

Obligatorio:

- **Inmediatamente después de clonar o restaurar producción sobre `Orion_Training`**, antes de que se conecte cualquier persona. La app se niega a arrancar hasta encontrar una atestación positiva, así que un clon fresco queda inservible hasta sanitizarlo.
- **Cuando la atestación deje de leer `ATTESTED`**.
- **Después de cambiar el currículo, los escenarios o cualquier SQL del manifiesto revisado**, para que la atestación vuelva a cubrir el contenido nuevo. Aplicar cursos a Training por fuera del orquestador deja la atestación describiendo un estado que ya no existe.

Recomendado:

- **Antes de cada grupo nuevo de capacitación**, para que todos empiecen desde el mismo estado.
- **Al terminar un grupo**, si quedó ruido que estorbe al siguiente.
- **Periódicamente** (por ejemplo cada trimestre) para que el esquema de Training no se aleje del de producción.

Ten presente que cada reinicio **borra el avance y las evidencias de
capacitación de Training y regenera las contraseñas sintéticas**. Por eso la
evidencia que importa vive en producción.

## 5. Cómo sanitizar

### Camino normal: el acceso directo del escritorio

**Sanitizar Orion_Training** en el escritorio ejecuta `Run-TrainingSanitizer.ps1`
elevado. Al hacer doble clic pide UAC y luego, en orden:

1. pregunta el servidor SQL (recuerda el último que usaste; también acepta una
   cadena de conexión completa pegada) y comprueba que el catálogo sea exactamente
   `Orion_Training`;
2. detiene el servicio `OrionERP.Training`;
3. lista cualquier otra sesión conectada a `Orion_Training` y ofrece cerrarla;
4. corre la vista previa completa y muestra el inventario;
5. pide que escribas `SANITIZAR` para continuar;
6. pide las cuatro contraseñas iniciales;
7. aplica el reinicio y vuelve a iniciar el servicio.

El paso 3 existe porque detener el servicio no basta: el neutralizador rechaza
**cualquier** sesión de usuario cuyo contexto sea `Orion_Training`, incluida una
ventana ociosa de SSMS con ese catálogo seleccionado, y falla con
`TRAINING NEUTRALIZATION BLOCKED: close every other Orion_Training session`.
El diagnóstico se ejecuta desde `master` para no contarse a sí mismo. Para
revisarlo a mano, desde una ventana conectada a `master`:

```sql
SELECT session_id, login_name, host_name, program_name, status
FROM sys.dm_exec_sessions
WHERE is_user_process = 1 AND database_id = DB_ID('Orion_Training');
```

Si algo falla, el servicio **queda detenido a propósito** y la ventana lo avisa.
El lanzador no guarda credenciales: la cadena de conexión se arma en memoria con
autenticación integrada y las contraseñas nunca salen de `SecureString`. Lo único
que recuerda entre ejecuciones es el nombre del servidor, en la variable de
usuario `ORION_TRAINING_SANITIZER_SERVER`.

La cuenta de Windows que usas debe ser **sysadmin en SQL Server**, porque la
conexión se arma con `Integrated Security`. Si tu servidor usa autenticación SQL,
pega la cadena completa cuando la pida.

### Camino manual

Los pasos siguientes son lo que hace el lanzador; úsalos si necesitas control fino.

### Requisitos previos

1. Conexión administrativa **sysadmin** cuya `Initial Catalog` sea exactamente `Orion_Training` y con `Encrypt=True`.
2. **Ninguna otra sesión** conectada a `Orion_Training`: detén el servicio `OrionERP.Training` y cierra SSMS.
3. Cuatro contraseñas iniciales **distintas** para instructor, trainee01, trainee02 y auditor.

```bash
Stop-Service OrionERP.Training
```

La cadena se pasa por parámetro o por la variable de proceso
`ORION_TRAINING_SANITIZER_CONNECTION_STRING`. Nunca se imprime ni se guarda.

### Paso 1: vista previa (no cambia nada)

```bash
pwsh -File Sanitize-OrionTraining.ps1
```

Abre una transacción, valida el manifiesto de objetos heredados, ejecuta el
preflight estricto y revierte. Devuelve el inventario del catálogo. Si algo del
clon no coincide con lo revisado, aquí se detiene.

### Paso 2: aplicar

```bash
pwsh -File Sanitize-OrionTraining.ps1 -Apply -ConfirmDatabase Orion_Training -InstructorPassword (Read-Host -AsSecureString) -Trainee01Password (Read-Host -AsSecureString) -Trainee02Password (Read-Host -AsSecureString) -AuditorPassword (Read-Host -AsSecureString)
```

Agrega `-ReviewedBy "Nombre de quien revisa"` para dejar constancia de quién
autorizó el reinicio (por omisión queda `Sanitize-OrionTraining.ps1/v1`).

### Paso 3: verificar

El script imprime el inventario final. **Debe leer `DataAttestation = ATTESTED`.**
Si no, el proceso terminó a medias: los triggers DML quedan deshabilitados a
propósito y el servicio `OrionERP.Training` **debe permanecer detenido** hasta
completar un reinicio limpio.

El sanitizado borra **todos** los usuarios de base de datos, incluido el que usa
la aplicación para conectarse. Por eso, y sólo después de leer una atestación
positiva, el orquestador vuelve a crear el usuario `orion_training_runtime` con
`db_datareader`/`db_datawriter` y el manifiesto revisado de diez permisos; debe
imprimir `Runtime database user orion_training_runtime restored...`. El login de
servidor y su contraseña siguen siendo responsabilidad del flujo aparte de mínimo
privilegio: si el script advierte que ese login no existe, corre primero
`Provision-TrainingRuntimeSqlLogin.ps1` o el servicio fallará al arrancar con
`SQL 4060: Cannot open database "Orion_Training" requested by the login`.

```bash
Start-Service OrionERP.Training
```

### Si falla a la mitad

No intentes reparar a mano ni ejecutar los `.sql` sueltos. Corrige la causa que
reportó el error y vuelve a correr el flujo completo desde la vista previa: todos
los pasos son idempotentes o transaccionales.

## 6. Aplicar cambios de currículo a cada entorno

| Entorno | Cómo |
| --- | --- |
| Desarrollo (`Orion_Sandbox`) | `Install-CapacitacionSchema.ps1 -ExpectedDatabase Orion_Sandbox` y luego `-Apply` |
| Producción (`grupocarpio`) | Igual, con `-ExpectedDatabase grupocarpio`. Requiere un login con permisos DDL, no el login de la aplicación |
| Capacitación (`Orion_Training`) | Reinicio completo con `Sanitize-OrionTraining.ps1`, para que la atestación cubra el contenido nuevo |

Al agregar un curso hay que tocar tres lugares: el script del currículo, el
manifiesto `@CursoManifiesto` de la atestación y la ruta `@RutaOrden`. Las
pruebas de `CapacitacionCurriculumSqlTests` fallan si falta alguno.

## 7. Reglas que no se rompen

- La sanitización **sólo** corre contra `Orion_Training`. El script rechaza cualquier otro catálogo por comparación ordinal.
- Los `.sql` con guardarraíl de sesión **no se ejecutan a mano**.
- Ningún dato real entra a Training: ni XML timbrados, ni documentos personales, ni RFC reales, ni contraseñas de producción.
- Una versión de curso publicada **no se edita**: se publica una versión nueva.
- Las contraseñas sintéticas se entregan por un canal controlado y se regeneran en cada reinicio.
- **Los roles se cambian en el aprovisionamiento, no en la interfaz.** `auth.AspNetUserRoles` se borra en cada reinicio, así que cualquier asignación hecha a mano dura hasta el siguiente sanitizado. Para un ajuste permanente se edita `20260817_orion_training_provision.sql`, se actualizan los conteos de la atestación (`AspNetRoles) <> 12`, `AspNetUserRoles) <> 22`) y sus pruebas, y se reinicia el entorno. Para un ajuste temporal a media sesión, usa `instructor@training.orion.local` en `/admin/seguridad`.

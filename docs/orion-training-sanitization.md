# Sanitización y reinicio de Orion_Training

`Orion_Training` se trata como un entorno desechable. Una copia de producción no
se considera segura por tener otro nombre: OrionERP bloquea el arranque de
`Training` hasta encontrar una atestación positiva en
`capacitacion.EntornoSeguridad` y hasta validar por separado el usuario SQL de
ejecución con privilegios mínimos.

## Qué hace el flujo

`Sanitize-OrionTraining.ps1` tiene vista previa por defecto y sólo muta datos
cuando recibe simultáneamente `-Apply`, `-ConfirmDatabase Orion_Training`, la
confirmación destructiva de PowerShell y cuatro contraseñas iniciales seguras y
distintas para las identidades ficticias.

La vista previa abre una transacción, valida y neutraliza dentro de ella únicamente
los artefactos heredados cuyo nombre, tipo, relación y hash coinciden con el
manifiesto revisado, ejecuta el preflight estricto y revierte la transacción. Por
eso demuestra que la copia actual es compatible sin persistir DDL ni DML.

El flujo aplicado:

1. comprueba por comparación ordinal que el catálogo activo sea exactamente
   `Orion_Training`;
2. exige que no haya otra sesión conectada y neutraliza por manifiesto exacto la
   política RLS clonada, módulos de diagramas, procedimientos heredados con
   dependencias externas o ambiguas y un trigger de prueba; cualquier diferencia
   de nombre, tipo, destino o hash bloquea el proceso;
3. elimina un sinónimo heredado roto y sin consumidores, conserva exactamente
   doce sinónimos locales requeridos por los módulos CFDI, valida que apunten a
   tablas del catálogo activo y rechaza cualquier sinónimo faltante, extra o
   remoto;
4. invalida cualquier atestación previa;
5. dentro de una transacción, desactiva temporalmente triggers y constraints,
   borra filas de todas las tablas salvo dos referencias revisadas, vuelve a
   habilitar y validar los constraints, y comprueba que el borrado fue total;
6. reinstala el catálogo español versionado de Capacitación, agrega el currículo
   completo con un curso por cada módulo de OrionERP y la ruta de aprendizaje
   `ORION-EXPERTO`, y sustituye el importador CFDI por uno exclusivo de Training
   que sólo acepta el XML ficticio publicado por el repositorio;
7. crea cuatro empleados y usuarios inequívocamente ficticios, una reservación,
   inventario mínimo y referencias RH ficticias (sitio, horario, política de
   asistencia sin ubicación, saldos de permiso y aviso de privacidad);
8. asigna el programa completo a `trainee01@training.orion.local` (un curso por
   cada entrada de la ruta `ORION-EXPERTO`) y las cinco asignaciones del piloto a
   `trainee02@training.orion.local`, siempre con
   `instructor@training.orion.local` como instructor y responsable de la
   asignación;
9. reinicia identidades, secuencias y estadísticas, limpia Query Store y bloquea
   RLS, Broker, replicación, claves, permisos/principales clonados y módulos con
   efectos externos;
10. revisa una lista cerrada de tablas y marcadores sintéticos; sólo entonces
   cambia la atestación a `DatosSanitizados=1` y `DatosSinteticos=1`.

Las únicas filas clonadas que se conservan son:

- `dbo.__EFMigrationsHistory`, limitada a sus dos columnas estándar;
- `dbo.DateDimension`, limitada a sus 34 columnas derivadas de fechas.

CFDI, contabilidad, bancos, pagos, adjuntos, membresías, restaurante, Capital
Humano real, reservaciones reales, logs y tablas `codex_recovery` quedan vacíos.
No se fabrica un CFDI timbrado ni se guarda una póliza o movimiento bancario. El
curso usa un XML local, inequívocamente inválido/no timbrable, y limita la parte
contable a explicar una propuesta ficticia de 1,000 + 160 = 1,160; así ninguna
evidencia parece un documento fiscal u operación real.

## Procedimiento operativo

1. Detén `OrionERP.Training` y cualquier proceso que use `Orion_Training`.
2. Obtén una conexión administrativa sysadmin temporal cuyo `Initial Catalog`
   sea exactamente `Orion_Training` y que declare `Encrypt=True`. No reutilices
   esa conexión en la configuración del servicio.
3. Coloca la conexión sólo en el entorno del proceso actual y ejecuta la vista
   previa:

   ```powershell
   $env:ORION_TRAINING_SANITIZER_CONNECTION_STRING = '<conexión sysadmin temporal a Orion_Training;Encrypt=True>'
   .\Sanitize-OrionTraining.ps1
   ```

   La salida contiene únicamente nombre de catálogo, conteos agregados y estado
   de atestación; no consulta ni muestra valores de filas. También confirma que
   la neutralización exacta y el preflight estricto funcionaron dentro de una
   transacción revertida.

4. Lee cuatro contraseñas distintas sin dejarlas en el historial y aplica:

   ```powershell
   $instructorPassword = Read-Host 'Contraseña del instructor ficticio' -AsSecureString
   $trainee01Password = Read-Host 'Contraseña de trainee01' -AsSecureString
   $trainee02Password = Read-Host 'Contraseña de trainee02' -AsSecureString
   $auditorPassword = Read-Host 'Contraseña del auditor ficticio' -AsSecureString
   .\Sanitize-OrionTraining.ps1 `
     -Apply `
     -ConfirmDatabase Orion_Training `
     -InstructorPassword $instructorPassword `
     -Trainee01Password $trainee01Password `
     -Trainee02Password $trainee02Password `
     -AuditorPassword $auditorPassword
   ```

   Para automatización atendida y ya aprobada puede agregarse `-Confirm:$false`;
   `-Apply` y `-ConfirmDatabase` siguen siendo obligatorios.

5. Elimina inmediatamente la conexión administrativa del proceso:

   ```powershell
   Remove-Item Env:ORION_TRAINING_SANITIZER_CONNECTION_STRING
   ```

6. Con el servicio todavía detenido, crea una contraseña aleatoria distinta de
   las cuatro contraseñas de aplicación y ejecuta el flujo separado del principal
   SQL fijo. La conexión administrativa debe ser sysadmin y declarar
   `Initial Catalog=master` y `Encrypt=True`; el script no la reutiliza como
   conexión del servicio:

   ```powershell
   $env:ORION_TRAINING_ADMIN_ConnectionString = '<conexión administrativa temporal a master;Encrypt=True>'
   $runtimePassword = Read-Host 'Contraseña de orion_training_runtime' -AsSecureString
   .\Provision-TrainingRuntimeSqlLogin.ps1 -Apply -RuntimePassword $runtimePassword
   Remove-Item Env:ORION_TRAINING_ADMIN_ConnectionString
   ```

   El aprovisionador crea o rota únicamente `orion_training_runtime`, mantiene
   `CHECK_POLICY` y `CHECK_EXPIRATION` activos, recrea su usuario sólo dentro de
   `Orion_Training` y comprueba sus permisos mediante una conexión SQL-auth real.
   También intenta conexiones reales a `grupocarpio` y `Orion_Sandbox`; ambas
   deben fallar. Si ya existe un usuario mapeado en cualquiera de esos catálogos,
   el flujo se detiene sin modificar producción ni desarrollo.
7. Publica primero los binarios sin intentar controlar un servicio todavía
   inexistente:

   ```powershell
   .\Publish-Training.ps1 -SkipServiceControl
   ```

8. Construye la conexión de servicio con `Initial Catalog=Orion_Training`,
   `User Id=orion_training_runtime`, la contraseña anterior y `Encrypt=True`.
   Ejemplo interactivo, sin imprimirla:

   ```powershell
   $credential = [pscredential]::new('orion_training_runtime', $runtimePassword)
   $runtimePlainText = $credential.GetNetworkCredential().Password
   try {
     $env:ORION_TRAINING_ConnectionStrings__OrionDb = "Server=<servidor>;Initial Catalog=Orion_Training;User Id=orion_training_runtime;Password=$runtimePlainText;Encrypt=True;TrustServerCertificate=True"
      .\Configure-TrainingService.ps1 `
        -AllowedHosts 'localhost;127.0.0.1;capacitacion.orion.land' `
        -PublicTrainingOrigin 'https://capacitacion.orion.land' `
        -Restart
   }
   finally {
     $runtimePlainText = $null
     Remove-Item Env:ORION_TRAINING_ConnectionStrings__OrionDb -ErrorAction SilentlyContinue
   }
   ```

   El origen de producción `orionerp.orion.land` está prohibido. Configuración
   inicia el servicio y valida `/readyz` en loopback antes de publicar el túnel.
   Sólo después de esa validación se debe habilitar el ingress/DNS de
   `capacitacion.orion.land` y la política de Cloudflare Access.

Usuarios de aplicación sintéticos (cada uno usa su propia contraseña capturada):

- `instructor@training.orion.local`: `CapacitacionAdmin`,
  `CapacitacionInstructor`, `SatOperator` y `Logistica`, para administrar la
  capacitación y demostrar las mismas prácticas de módulo;
- `trainee01@training.orion.local`: `Lectura`, `SatOperator` y `Logistica`;
- `trainee02@training.orion.local`: `Lectura`, `SatOperator` y `Logistica`;
- `auditor@training.orion.local`: `CapacitacionAuditor`.

Ninguna identidad sintética recibe `Administrador` ni roles privilegiados de
Capital Humano. Los participantes tampoco reciben `CapacitacionAdmin`. El
autoservicio de RH se limita por su vínculo de empleado y RFC, no por un rol
administrativo.

Las contraseñas no se imprimen ni se guardan en el repositorio. Comunícalas por
un canal aprobado y rótalas cuando dejen de ser necesarias.

Limitación operativa: Windows conserva la conexión runtime en el valor
`Environment` de la clave SCM de `OrionERP.Training`. El script no la muestra,
protege la DACL de esa clave y deja acceso sólo a SYSTEM y Administradores, pero
todavía no existe integración con un proveedor de secretos. La contraseña debe ser
exclusiva de `Orion_Training`, aleatoria y rotarse según su expiración.

## Reinicio determinista

El mismo comando con `-Apply` sirve para reiniciar el sandbox. Borra todo el
progreso y cualquier dato creado durante las prácticas, vuelve a sembrar la
cohorte y los escenarios v1, y emite una nueva atestación. Si cualquier fase
posterior al borrado falla, la atestación permanece ausente o negativa y
OrionERP.Training debe permanecer detenido. Los triggers quedan deshabilitados;
el mismo flujo guardado puede ejecutarse otra vez y reinstala sus cuerpos
revisados antes de volverlos a habilitar.

## Límites deliberados

- El script no crea respaldos: un respaldo de la copia sin sanitizar preservaría
  precisamente los datos que se busca eliminar. La política externa de respaldo
  y destrucción segura debe decidirse antes de ejecutar.
- La atestación verifica ausencia lógica de filas accesibles a OrionERP; no es
  una certificación de borrado forense. `DELETE` puede dejar contenido recuperable
  temporalmente en el log, páginas no asignadas, snapshots o respaldos. Si la
  política de datos exige purga física, el DBA debe recrear `Orion_Training`
  desde un despliegue sólo-esquema y destruir la copia, archivos y respaldos
  anteriores mediante el procedimiento aprobado antes de atestarla.
- El sanitizador elimina principales, membresías y permisos clonados dentro de
  `Orion_Training`, normaliza propietario/esquemas y deja el catálogo sin usuario
  runtime. Crear el login/usuario dedicado corresponde después a
  `Provision-TrainingRuntimeSqlLogin.ps1`.
- No se ejecuta contra `grupocarpio` ni `Orion_Sandbox`, ni cambia el catálogo de
  una conexión recibida. Una conexión cuyo catálogo no coincida se rechaza.
- Los cambios de esquema futuros son fail-closed: una tabla nueva se vacía y no
  puede contener filas al atestar hasta que se revise y agregue explícitamente al
  manifiesto sintético.

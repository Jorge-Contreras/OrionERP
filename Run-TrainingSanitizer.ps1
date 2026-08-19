#requires -Version 7.0
<#
  Lanzador interactivo del reinicio de Orion_Training.

  Envuelve a Sanitize-OrionTraining.ps1 para el uso repetido desde un acceso
  directo del escritorio: eleva, detiene el servicio de capacitación, corre la
  vista previa, pide una confirmación escrita y sólo entonces aplica el reinicio
  destructivo.

  No guarda ni escribe credenciales. La cadena de conexión se arma en memoria a
  partir del servidor que captures (o se toma de
  ORION_TRAINING_SANITIZER_CONNECTION_STRING si ya existe), y las cuatro
  contraseñas sintéticas se leen como SecureString y se pasan sin convertirse a
  texto en ningún momento.

  La confirmación escrita ("SANITIZAR") sustituye al prompt de alto impacto de
  PowerShell: es una sola confirmación explícita en vez de dos genéricas, para
  que un flujo que se repite seguido no entrene a nadie a aceptar sin leer.
#>
[CmdletBinding()]
param(
  [string]$ServiceName = 'OrionERP.Training',

  [string]$Server,

  [switch]$SkipServiceControl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredDatabase = 'Orion_Training'
$serverMemoryVariable = 'ORION_TRAINING_SANITIZER_SERVER'
$connectionEnvironmentVariable = 'ORION_TRAINING_SANITIZER_CONNECTION_STRING'

function Test-Elevated {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step {
  param([string]$Text)
  Write-Host ''
  Write-Host "== $Text" -ForegroundColor Cyan
}

# El neutralizador rechaza cualquier sesión de usuario cuyo contexto sea
# Orion_Training, incluida una ventana ociosa de SSMS. Se consulta desde master
# para que esta misma conexión de diagnóstico no cuente como bloqueo.
function Get-TrainingBlockingSession {
  param(
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][string]$Database
  )

  $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
  $builder['Initial Catalog'] = 'master'

  $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
  try {
    $connection.Open()
    $command = $connection.CreateCommand()
    try {
      $command.CommandText = @'
SELECT sessionInfo.session_id, sessionInfo.login_name, sessionInfo.host_name,
       ISNULL(sessionInfo.program_name, N'') AS program_name, sessionInfo.status
FROM sys.dm_exec_sessions sessionInfo
WHERE sessionInfo.is_user_process = 1
  AND sessionInfo.database_id = DB_ID(@Database)
  AND sessionInfo.session_id <> @@SPID
ORDER BY sessionInfo.session_id;
'@
      $databaseParameter = $command.Parameters.Add('@Database', [System.Data.SqlDbType]::NVarChar, 128)
      $databaseParameter.Value = $Database

      $sessions = @()
      $reader = $command.ExecuteReader()
      try {
        while ($reader.Read()) {
          $sessions += [pscustomobject]@{
            SessionId = [int]$reader['session_id']
            Login     = [string]$reader['login_name']
            Host      = [string]$reader['host_name']
            Program   = [string]$reader['program_name']
            Status    = [string]$reader['status']
          }
        }
      }
      finally {
        $reader.Dispose()
      }

      return , $sessions
    }
    finally {
      $command.Dispose()
    }
  }
  finally {
    $connection.Dispose()
  }
}

function Close-TrainingSession {
  param(
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][int[]]$SessionId
  )

  $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
  $builder['Initial Catalog'] = 'master'

  $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
  try {
    $connection.Open()
    foreach ($id in $SessionId) {
      $command = $connection.CreateCommand()
      try {
        # KILL no acepta parámetros, así que la sentencia se arma con un entero ya
        # validado y sólo después de reconfirmar que la sesión sigue en el
        # catálogo de capacitación.
        $command.CommandText = @'
DECLARE @KillStatement nvarchar(64);

IF EXISTS
(
  SELECT 1 FROM sys.dm_exec_sessions
  WHERE session_id = @SessionId
    AND is_user_process = 1
    AND database_id = DB_ID(@Database)
    AND session_id <> @@SPID
)
BEGIN
  SET @KillStatement = N'KILL ' + CONVERT(nvarchar(10), @SessionId);
  EXEC sys.sp_executesql @KillStatement;
END;
'@
        $sessionParameter = $command.Parameters.Add('@SessionId', [System.Data.SqlDbType]::Int)
        $sessionParameter.Value = $id
        $databaseParameter = $command.Parameters.Add('@Database', [System.Data.SqlDbType]::NVarChar, 128)
        $databaseParameter.Value = $Database
        [void]$command.ExecuteNonQuery()
      }
      finally {
        $command.Dispose()
      }
    }
  }
  finally {
    $connection.Dispose()
  }
}

# El acceso directo ya pide elevación, pero si alguien ejecuta el archivo
# directamente conviene volver a lanzarlo elevado en lugar de fallar a la mitad.
if (-not (Test-Elevated)) {
  Write-Host 'Se requieren privilegios de administrador para detener el servicio de capacitación.' -ForegroundColor Yellow
  $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
  if ($PSBoundParameters.ContainsKey('ServiceName')) { $arguments += @('-ServiceName', $ServiceName) }
  if ($PSBoundParameters.ContainsKey('Server')) { $arguments += @('-Server', $Server) }
  if ($SkipServiceControl) { $arguments += '-SkipServiceControl' }

  try {
    Start-Process -FilePath ([Environment]::ProcessPath) -ArgumentList $arguments -Verb RunAs
  }
  catch {
    Write-Host 'No se concedió la elevación. El reinicio no se ejecutó.' -ForegroundColor Red
    Read-Host 'Enter para cerrar'
  }
  return
}

$serviceStoppedByLauncher = $false
$service = $null

try {
  $sanitizer = Join-Path $PSScriptRoot 'Sanitize-OrionTraining.ps1'
  if (-not (Test-Path -LiteralPath $sanitizer -PathType Leaf)) {
    throw "No se encontró Sanitize-OrionTraining.ps1 junto a este lanzador ($PSScriptRoot)."
  }

  Write-Host '=======================================================' -ForegroundColor Yellow
  Write-Host ' Reinicio del entorno de capacitación  ·  Orion_Training' -ForegroundColor Yellow
  Write-Host ' Borra TODOS los datos del entorno y regenera la cohorte' -ForegroundColor Yellow
  Write-Host '=======================================================' -ForegroundColor Yellow

  # ---------------------------------------------------------------------------
  # 1. Cadena de conexión (nunca se guarda en disco)
  # ---------------------------------------------------------------------------
  Write-Step 'Conexión administrativa'
  $connectionString = [Environment]::GetEnvironmentVariable($connectionEnvironmentVariable, 'Process')

  if ([string]::IsNullOrWhiteSpace($connectionString)) {
    $defaultServer = if (-not [string]::IsNullOrWhiteSpace($Server)) {
      $Server
    }
    else {
      [Environment]::GetEnvironmentVariable($serverMemoryVariable, 'User')
    }

    $prompt = if ([string]::IsNullOrWhiteSpace($defaultServer)) {
      'Servidor SQL (o pega una cadena de conexión completa)'
    }
    else {
      "Servidor SQL [$defaultServer]"
    }

    $answer = Read-Host $prompt
    if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $defaultServer }
    if ([string]::IsNullOrWhiteSpace($answer)) { throw 'Se requiere un servidor SQL o una cadena de conexión.' }

    if ($answer.Contains('=')) {
      $connectionString = $answer
    }
    else {
      $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
      $builder['Data Source'] = $answer
      $builder['Initial Catalog'] = $requiredDatabase
      $builder['Integrated Security'] = $true
      $builder['Encrypt'] = $true
      $builder['TrustServerCertificate'] = $true
      $connectionString = $builder.ConnectionString
      [Environment]::SetEnvironmentVariable($serverMemoryVariable, $answer, 'User')
    }
  }
  else {
    Write-Host "Usando $connectionEnvironmentVariable del proceso actual."
  }

  # Falla temprano y con un mensaje claro en vez de hacerlo dentro del sanitizador.
  $verify = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
  if (-not [string]::Equals([string]$verify['Initial Catalog'], $requiredDatabase, [StringComparison]::Ordinal)) {
    throw "La cadena de conexión apunta a '$([string]$verify['Initial Catalog'])'. Debe apuntar exactamente a $requiredDatabase."
  }
  Write-Host "Catálogo destino: $requiredDatabase en $([string]$verify['Data Source'])" -ForegroundColor Green

  # ---------------------------------------------------------------------------
  # 2. El sanitizador exige que no haya otra sesión conectada
  # ---------------------------------------------------------------------------
  if (-not $SkipServiceControl) {
    Write-Step "Deteniendo el servicio $ServiceName"
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
      Write-Host "El servicio $ServiceName no está instalado en este equipo; se continúa." -ForegroundColor Yellow
    }
    elseif ($service.Status -eq 'Stopped') {
      Write-Host "El servicio $ServiceName ya estaba detenido."
    }
    else {
      Stop-Service -Name $ServiceName -Force
      (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(2))
      $serviceStoppedByLauncher = $true
      Write-Host "Servicio $ServiceName detenido." -ForegroundColor Green
    }
  }

  # ---------------------------------------------------------------------------
  # 3. Ninguna otra sesión puede tener el contexto de Orion_Training
  # ---------------------------------------------------------------------------
  Write-Step 'Sesiones conectadas a Orion_Training'
  $blocking = Get-TrainingBlockingSession -ConnectionString $connectionString -Database $requiredDatabase

  if ($blocking.Count -eq 0) {
    Write-Host 'No hay otras sesiones conectadas.' -ForegroundColor Green
  }
  else {
    Write-Host "Hay $($blocking.Count) sesión(es) que bloquean el reinicio:" -ForegroundColor Yellow
    $blocking | Format-Table SessionId, Login, Host, Program, Status -AutoSize | Out-String | Write-Host
    Write-Host 'Suele ser una ventana de SSMS con Orion_Training seleccionado, aunque esté ociosa.'

    $closeAnswer = Read-Host 'Cerrar estas sesiones ahora? (S/N)'
    if ($closeAnswer -match '^[SsYy]') {
      Close-TrainingSession `
        -ConnectionString $connectionString `
        -Database $requiredDatabase `
        -SessionId ([int[]]($blocking.SessionId))

      Start-Sleep -Seconds 2
      $blocking = Get-TrainingBlockingSession -ConnectionString $connectionString -Database $requiredDatabase
    }

    if ($blocking.Count -gt 0) {
      $detail = ($blocking | ForEach-Object { "$($_.SessionId) ($($_.Program) en $($_.Host))" }) -join ', '
      throw "Siguen abiertas sesiones sobre $requiredDatabase : $detail. Ciérralas y vuelve a intentar."
    }

    Write-Host 'Sesiones cerradas.' -ForegroundColor Green
  }

  # ---------------------------------------------------------------------------
  # 4. Vista previa: valida el clon y revierte todo
  # ---------------------------------------------------------------------------
  Write-Step 'Vista previa (no cambia nada)'
  & $sanitizer -ConnectionString $connectionString

  # ---------------------------------------------------------------------------
  # 5. Confirmación escrita
  # ---------------------------------------------------------------------------
  Write-Step 'Confirmación'
  Write-Host 'El siguiente paso BORRA todas las filas de Orion_Training y regenera' -ForegroundColor Red
  Write-Host 'la cohorte sintética. El avance de capacitación del entorno se pierde.' -ForegroundColor Red
  $confirmation = Read-Host 'Escribe SANITIZAR para continuar (cualquier otra cosa cancela)'
  if (-not [string]::Equals($confirmation, 'SANITIZAR', [StringComparison]::Ordinal)) {
    Write-Host 'Cancelado. No se modificó nada.' -ForegroundColor Yellow
    return
  }

  # ---------------------------------------------------------------------------
  # 6. Contraseñas sintéticas (SecureString de principio a fin)
  # ---------------------------------------------------------------------------
  Write-Step 'Contraseñas iniciales de la cohorte'
  Write-Host 'Deben ser cuatro contraseñas distintas. Guárdalas en el gestor de contraseñas del equipo.'
  $instructorPassword = Read-Host 'Contraseña para instructor@training.orion.local' -AsSecureString
  $trainee01Password = Read-Host 'Contraseña para trainee01@training.orion.local' -AsSecureString
  $trainee02Password = Read-Host 'Contraseña para trainee02@training.orion.local' -AsSecureString
  $auditorPassword = Read-Host 'Contraseña para auditor@training.orion.local' -AsSecureString

  # ---------------------------------------------------------------------------
  # 7. Reinicio
  # ---------------------------------------------------------------------------
  Write-Step 'Aplicando el reinicio'
  & $sanitizer `
    -ConnectionString $connectionString `
    -Apply `
    -ConfirmDatabase $requiredDatabase `
    -InstructorPassword $instructorPassword `
    -Trainee01Password $trainee01Password `
    -Trainee02Password $trainee02Password `
    -AuditorPassword $auditorPassword `
    -ReviewedBy "$env:USERNAME@$env:COMPUTERNAME via Run-TrainingSanitizer.ps1" `
    -Confirm:$false

  Write-Step 'Reinicio completado'
  Write-Host 'La atestación quedó positiva. Recuerda registrar las contraseñas nuevas.' -ForegroundColor Green

  if ($serviceStoppedByLauncher) {
    Start-Service -Name $ServiceName
    Write-Host "Servicio $ServiceName iniciado de nuevo." -ForegroundColor Green
    $serviceStoppedByLauncher = $false
  }
}
catch {
  Write-Host ''
  Write-Host 'EL REINICIO NO SE COMPLETÓ' -ForegroundColor Red
  Write-Host $_.Exception.Message -ForegroundColor Red
  if ($serviceStoppedByLauncher) {
    Write-Host ''
    Write-Host "El servicio $ServiceName queda DETENIDO a propósito." -ForegroundColor Yellow
    Write-Host 'No lo inicies hasta completar un reinicio limpio: el entorno puede contener datos sin sanitizar.' -ForegroundColor Yellow
  }
}
finally {
  $instructorPassword = $null
  $trainee01Password = $null
  $trainee02Password = $null
  $auditorPassword = $null
  $connectionString = $null
  Write-Host ''
  Read-Host 'Enter para cerrar'
}

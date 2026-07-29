#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$GracefulShutdownSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-CurrentProcessIsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Start-ScriptElevated {
    $powershellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        ('"{0}"' -f $PSCommandPath)
        '-GracefulShutdownSeconds'
        $GracefulShutdownSeconds
    )

    Write-Host 'Requesting administrator access through Windows UAC...'
    Start-Process -FilePath $powershellPath -Verb RunAs -ArgumentList $arguments
}

function Get-CodexPackage {
    $package = Get-AppxPackage -Name 'OpenAI.Codex' |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if (-not $package) {
        throw 'The OpenAI Codex Windows app package (OpenAI.Codex) is not installed for this user.'
    }

    $executablePath = Join-Path $package.InstallLocation 'app\ChatGPT.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Codex is installed, but ChatGPT.exe was not found at '$executablePath'."
    }

    return [pscustomobject]@{
        InstallLocation = $package.InstallLocation.TrimEnd('\')
        ExecutablePath  = $executablePath
        Version         = $package.Version
    }
}

function Get-CodexAppProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$InstallLocation
    )

    $pathPrefix = "$($InstallLocation.TrimEnd('\'))\"

    return @(
        Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ChatGPT.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.StartsWith(
                    $pathPrefix,
                    [StringComparison]::OrdinalIgnoreCase
                )
            }
    )
}

function Stop-ExistingCodexApp {
    param(
        [Parameter(Mandatory)]
        [string]$InstallLocation,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $existingProcesses = @(Get-CodexAppProcesses -InstallLocation $InstallLocation)
    if ($existingProcesses.Count -eq 0) {
        return
    }

    Write-Host 'Closing the existing Codex app instance...'

    foreach ($processInfo in $existingProcesses) {
        $process = Get-Process -Id $processInfo.ProcessId -ErrorAction SilentlyContinue
        if ($process -and $process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $remainingProcesses = @(Get-CodexAppProcesses -InstallLocation $InstallLocation)
    } while (
        $remainingProcesses.Count -gt 0 -and
        [DateTime]::UtcNow -lt $deadline
    )

    if ($remainingProcesses.Count -gt 0) {
        Write-Warning 'Codex did not exit in time. Stopping only the remaining OpenAI.Codex app processes.'
        foreach ($processInfo in $remainingProcesses) {
            Stop-Process -Id $processInfo.ProcessId -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 500
    }

    $stillRunning = @(Get-CodexAppProcesses -InstallLocation $InstallLocation)
    if ($stillRunning.Count -gt 0) {
        throw 'One or more existing Codex app processes could not be stopped.'
    }
}

function Add-ProcessElevationInspector {
    if ('CodexProcessElevationInspector' -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class CodexProcessElevationInspector
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength
    );

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool IsElevated(int processId)
    {
        IntPtr processHandle = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId
        );

        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            IntPtr tokenHandle;
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out tokenHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                int isElevated;
                int returnLength;
                if (!GetTokenInformation(
                    tokenHandle,
                    TokenElevation,
                    out isElevated,
                    sizeof(int),
                    out returnLength
                ))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return isElevated != 0;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }
}
'@
}

function Confirm-CodexIsElevated {
    param(
        [Parameter(Mandatory)]
        [string]$InstallLocation
    )

    Add-ProcessElevationInspector

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $processes = @(Get-CodexAppProcesses -InstallLocation $InstallLocation)
        $elevatedProcesses = @(
            $processes |
                Where-Object {
                    try {
                        [CodexProcessElevationInspector]::IsElevated($_.ProcessId)
                    }
                    catch {
                        $false
                    }
                }
        )
    } while (
        $elevatedProcesses.Count -eq 0 -and
        [DateTime]::UtcNow -lt $deadline
    )

    if ($elevatedProcesses.Count -eq 0) {
        throw 'Codex started, but an elevated ChatGPT.exe process could not be verified.'
    }

    Write-Host "Verified: Codex is running as administrator (PID $($elevatedProcesses[0].ProcessId))." -ForegroundColor Green
}

try {
    if (-not (Test-CurrentProcessIsAdministrator)) {
        Start-ScriptElevated
        return
    }

    $codexPackage = Get-CodexPackage
    Write-Host "Found OpenAI Codex $($codexPackage.Version)."

    Stop-ExistingCodexApp `
        -InstallLocation $codexPackage.InstallLocation `
        -TimeoutSeconds $GracefulShutdownSeconds

    Write-Host 'Starting Codex as administrator...'
    Start-Process `
        -FilePath $codexPackage.ExecutablePath `
        -ArgumentList '--do-not-de-elevate'

    Confirm-CodexIsElevated -InstallLocation $codexPackage.InstallLocation
}
catch {
    Write-Error $_ -ErrorAction Continue
    Write-Host 'Press Enter to close this window.'
    [void](Read-Host)
    exit 1
}

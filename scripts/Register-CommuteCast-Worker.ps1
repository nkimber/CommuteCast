[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Register')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Register')]
    [string]$Executable,
    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$account = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $taskName = "CommuteCast remote worker ($($account.User.Value))"
    $description = "Launch CommuteCast at sign-in for $($account.User.Value). Managed by Register-CommuteCast-Worker.ps1."
    $existing = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    if ($existing -and $existing.Description -ne $description) { throw 'A different task already uses the worker task name.' }

    if ($Remove) {
        if ($existing -and $PSCmdlet.ShouldProcess($taskName, 'Remove the CommuteCast sign-in task')) {
            Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
        }
        return
    }

    $application = Get-Item -LiteralPath $Executable
    if ($application.PSIsContainer -or $application.Name -notin @('CommuteCast.exe', 'CommuteCast.Desktop.exe')) {
        throw 'Choose the installed CommuteCast.exe or the portable CommuteCast.Desktop.exe.'
    }

    $action = New-ScheduledTaskAction -Execute $application.FullName -WorkingDirectory $application.DirectoryName
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account.Name
    $principal = New-ScheduledTaskPrincipal -UserId $account.Name -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    if ($PSCmdlet.ShouldProcess($taskName, 'Launch CommuteCast each time this Windows account signs in')) {
        Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Trigger $trigger -Principal $principal `
            -Settings $settings -Description $description -Force:([bool]$existing) | Out-Null
        Write-Output 'CommuteCast will start at the next sign-in to this Windows account.'
    }
} finally { $account.Dispose() }

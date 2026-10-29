#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Manage DynReport System administrators on APP-01 (Windows administrators only).
.DESCRIPTION
  Manages %ProgramData%\DynReportSystem\Config\system-administrators.json.
  Does not grant any reporting permissions and does not alter AD memberships.
  Requires an elevated local/server PowerShell session. The DynReport IIS
  application pool receives READ only to this specific file.
.EXAMPLE
  .\Manage-DynReportSystemAdmins.ps1 -Action Add -Account 'CNC-DOMAIN\DynReport-Admins'
.EXAMPLE
  .\Manage-DynReportSystemAdmins.ps1 -Action List
.EXAMPLE
  .\Manage-DynReportSystemAdmins.ps1 -Action Remove -Account 'CNC-DOMAIN\OldAdmin'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Add','Remove','List')]
    [string]$Action,

    [string]$Account
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# App code cannot modify this file; only elevated server administrators can.
$configDir = Join-Path $env:ProgramData 'DynReportSystem\Config'
$configFile = Join-Path $configDir 'system-administrators.json'
$appPoolIdentity = 'IIS AppPool\DynReportSystem'

function Resolve-Sid([string]$IdentityName) {
    if ([string]::IsNullOrWhiteSpace($IdentityName)) {
        throw 'Bitte -Account DOMAIN\Name oder DOMAIN\Gruppe angeben.'
    }
    $account = [System.Security.Principal.NTAccount]::new($IdentityName)
    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier])
    $value = $sid.Value
    if ($value -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545') -or
        $value -match '^S-1-5-21-(\d+-){3}513$') {
        throw "Breite Benutzergruppen sind für die Systemadministration nicht zulässig: $IdentityName"
    }
    return $value
}

function Read-Config {
    if (-not (Test-Path -LiteralPath $configFile)) {
        return [pscustomobject]@{
            SchemaVersion = 1
            Administrators = @()
        }
    }
    $value = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($value.SchemaVersion -ne 1 -or $null -eq $value.Administrators) {
        throw 'Systemadministrator-Datei hat ein ungültiges Schema; keine Änderungen durchgeführt.'
    }
    return $value
}

function Set-SecureFileAcl([string]$FileName) {
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)

    $system = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administrators = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $appPool = ([System.Security.Principal.NTAccount]::new($appPoolIdentity)).
        Translate([System.Security.Principal.SecurityIdentifier])

    foreach ($item in @(
        @{ Sid = $system; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        @{ Sid = $administrators; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        @{ Sid = $appPool; Rights = [System.Security.AccessControl.FileSystemRights]::Read }
    )) {
        $acl.AddAccessRule(
            [System.Security.AccessControl.FileSystemAccessRule]::new(
                $item.Sid, $item.Rights,
                [System.Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $FileName -AclObject $acl
}

function Write-Config($Config) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    $temp = Join-Path $configDir ('system-administrators.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $Config | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Set-SecureFileAcl -FileName $temp
        # Same directory/volume, replace only after ACL has been set.
        Move-Item -LiteralPath $temp -Destination $configFile -Force
        Set-SecureFileAcl -FileName $configFile
    }
    finally {
        if (Test-Path -LiteralPath $temp) {
            Remove-Item -LiteralPath $temp -Force
        }
    }
}

$config = Read-Config

if ($Action -eq 'List') {
    if (-not (Test-Path -LiteralPath $configFile)) {
        Write-Host 'Es sind noch keine DynReport-Systemadministratoren eingerichtet.'
    } else {
        @($config.Administrators) | Select-Object Account, Sid, AddedUtc |
            Format-Table -AutoSize
    }
    return
}

$sid = Resolve-Sid $Account
$list = @($config.Administrators)

if ($Action -eq 'Add') {
    if ($list.Where({ $_.Sid -eq $sid }).Count -gt 0) {
        Write-Host "Bereits vorhanden: $Account ($sid)"
        return
    }
    $list += [pscustomobject]@{
        Account = $Account
        Sid = $sid
        AddedUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-Host "Systemadministrator hinzugefügt: $Account"
} else {
    $remaining = @($list | Where-Object { $_.Sid -ne $sid })
    if ($remaining.Count -eq $list.Count) {
        Write-Host "Nicht gefunden: $Account"
        return
    }
    if ($remaining.Count -eq 0) {
        throw 'Der letzte Systemadministrator darf nicht entfernt werden. Bitte zuerst einen anderen hinzufügen.'
    }
    $list = $remaining
    Write-Host "Systemadministrator entfernt: $Account"
}

$config.Administrators = @($list)
Write-Config $config
Write-Host "Gespeichert: $configFile"
Write-Host 'Änderungen werden bei der nächsten Seitenanfrage wirksam; IIS-Neustart ist nicht erforderlich.'

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('localhost', '127.0.0.1')]
    [string]$HostName = 'localhost',

    [ValidateRange(1, 65535)]
    [int]$Port = 5432,

    [ValidateNotNullOrEmpty()]
    [string]$AdminUser = 'postgres',

    [Security.SecureString]$AdminPassword,

    [ValidateNotNullOrEmpty()]
    [string]$PsqlPath = 'psql'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MaintenanceDatabase = 'postgres'
$RequiredPrefix = 'mistchess_test_'
$AllowedNamePattern = '^mistchess_test_[a-z0-9_]+$'
$ProtectedDatabases = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('postgres', 'template0', 'template1', 'mistchess_dev') | ForEach-Object {
    [void]$ProtectedDatabases.Add($_)
}

function ConvertFrom-SecureValue {
    param([Parameter(Mandatory)][Security.SecureString]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Invoke-Psql {
    param([Parameter(Mandatory)][string]$Sql)

    $arguments = @(
        "--host=$HostName"
        "--port=$Port"
        "--username=$AdminUser"
        "--dbname=$MaintenanceDatabase"
        '--no-psqlrc'
        '--set=ON_ERROR_STOP=1'
        '--tuples-only'
        '--no-align'
        '--quiet'
    )

    $output = $Sql | & $script:PsqlCommand.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed with exit code $LASTEXITCODE."
    }

    return ($output -join "`n").Trim()
}

$script:PsqlCommand = Get-Command $PsqlPath -CommandType Application -ErrorAction Stop
if ($null -eq $AdminPassword) {
    $AdminPassword = Read-Host "Password for PostgreSQL administrator '$AdminUser'" -AsSecureString
}

$adminPasswordText = ConvertFrom-SecureValue $AdminPassword
$hadPgPassword = Test-Path Env:PGPASSWORD
$previousPgPassword = $env:PGPASSWORD

try {
    $env:PGPASSWORD = $adminPasswordText

    $serverVersionText = Invoke-Psql -Sql "SELECT current_setting('server_version_num');"
    $serverVersion = 0
    if (-not [int]::TryParse($serverVersionText, [ref]$serverVersion) -or $serverVersion -lt 180000 -or $serverVersion -ge 190000) {
        throw "This script requires a PostgreSQL 18 server; the local server reported '$serverVersionText'."
    }

    $databaseOutput = Invoke-Psql -Sql "SELECT datname FROM pg_database WHERE left(datname, length('$RequiredPrefix')) = '$RequiredPrefix' ORDER BY datname;"
    $databaseNames = @($databaseOutput -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($databaseNames.Count -eq 0) {
        Write-Host 'No stale MistChess test databases were found.'
        return
    }

    foreach ($databaseName in $databaseNames) {
        if ($ProtectedDatabases.Contains($databaseName)) {
            throw "Refusing to drop protected database '$databaseName'."
        }
        if (-not $databaseName.StartsWith($RequiredPrefix, [StringComparison]::Ordinal) -or $databaseName -notmatch $AllowedNamePattern) {
            throw "Refusing to drop database '$databaseName' because its name is outside the required test-database pattern."
        }

        if ($PSCmdlet.ShouldProcess("database '$databaseName' on $HostName`:$Port", 'Drop stale test database with FORCE')) {
            Invoke-Psql -Sql "DROP DATABASE `"$databaseName`" WITH (FORCE);" | Out-Null
        }
    }
}
finally {
    if ($hadPgPassword) {
        $env:PGPASSWORD = $previousPgPassword
    }
    else {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }

    $adminPasswordText = $null
}

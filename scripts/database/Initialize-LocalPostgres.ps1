[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('localhost', '127.0.0.1')]
    [string]$HostName = 'localhost',

    [ValidateRange(1, 65535)]
    [int]$Port = 5432,

    [ValidateNotNullOrEmpty()]
    [string]$AdminUser = 'postgres',

    [Security.SecureString]$AdminPassword,

    [Security.SecureString]$AppPassword,

    [ValidateNotNullOrEmpty()]
    [string]$PsqlPath = 'psql',

    [switch]$SkipMigrations,

    [switch]$ResetAppPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ApplicationRole = 'mistchess_app'
$DevelopmentDatabase = 'mistchess_dev'
$MaintenanceDatabase = 'postgres'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$InfrastructureProject = Join-Path $RepositoryRoot 'src/MistChess.Infrastructure/MistChess.Infrastructure.csproj'
$StartupProject = Join-Path $RepositoryRoot 'src/MistChess.Api/MistChess.Api.csproj'

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

function ConvertTo-SqlStringLiteral {
    param([Parameter(Mandatory)][string]$Value)

    return $Value.Replace("'", "''")
}

function ConvertTo-ConnectionStringValue {
    param([Parameter(Mandatory)][string]$Value)

    return '"' + $Value.Replace('"', '""') + '"'
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Sql
    )

    $arguments = @(
        "--host=$HostName"
        "--port=$Port"
        "--username=$AdminUser"
        "--dbname=$Database"
        '--no-password'
        '--no-psqlrc'
        '--set=ON_ERROR_STOP=1'
        '--tuples-only'
        '--no-align'
        '--quiet'
    )

    $output = $Sql | & $script:PsqlCommand.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "无法使用 PostgreSQL 管理员 '$AdminUser' 连接到 $HostName`:$Port。请确认 PostgreSQL 服务正在运行，并输入安装该实例时设置的管理员密码。"
    }

    return ($output -join "`n").Trim()
}

$script:PsqlCommand = Get-Command $PsqlPath -CommandType Application -ErrorAction Stop

if ($null -eq $AdminPassword) {
    $AdminPassword = Read-Host "请输入 PostgreSQL 管理员 '$AdminUser' 的密码（输入不会显示）" -AsSecureString
}
if ($null -eq $AppPassword) {
    $AppPassword = Read-Host "请输入 '$ApplicationRole' 的现有或新密码（输入不会显示）" -AsSecureString
}

$adminPasswordText = ConvertFrom-SecureValue $AdminPassword
$appPasswordText = ConvertFrom-SecureValue $AppPassword
if ([string]::IsNullOrEmpty($adminPasswordText)) {
    throw 'PostgreSQL 管理员密码不能为空。'
}
if ([string]::IsNullOrEmpty($appPasswordText)) {
    throw "'$ApplicationRole' 的密码不能为空。"
}
$hadPgPassword = Test-Path Env:PGPASSWORD
$previousPgPassword = $env:PGPASSWORD
$connectionVariableName = 'ConnectionStrings__MistChess'
$hadConnectionString = Test-Path "Env:$connectionVariableName"
$previousConnectionString = [Environment]::GetEnvironmentVariable($connectionVariableName, 'Process')

try {
    $env:PGPASSWORD = $adminPasswordText

    $serverVersionText = Invoke-Psql -Database $MaintenanceDatabase -Sql "SELECT current_setting('server_version_num');"
    $serverVersion = 0
    if (-not [int]::TryParse($serverVersionText, [ref]$serverVersion) -or $serverVersion -lt 180000 -or $serverVersion -ge 190000) {
        throw "This script requires a PostgreSQL 18 server; the local server reported '$serverVersionText'."
    }

    $roleExists = (Invoke-Psql -Database $MaintenanceDatabase -Sql "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$ApplicationRole');") -eq 't'
    if (-not $roleExists) {
        if ($PSCmdlet.ShouldProcess("role '$ApplicationRole' on $HostName`:$Port", 'Create local PostgreSQL login role')) {
            $passwordLiteral = ConvertTo-SqlStringLiteral $appPasswordText
            Invoke-Psql -Database $MaintenanceDatabase -Sql "CREATE ROLE $ApplicationRole WITH LOGIN CREATEDB PASSWORD '$passwordLiteral';" | Out-Null
            $roleExists = $true
        }
    }
    else {
        if ($ResetAppPassword) {
            if ($PSCmdlet.ShouldProcess("role '$ApplicationRole' on $HostName`:$Port", 'Synchronize the local application password and ensure LOGIN and CREATEDB attributes')) {
                $passwordLiteral = ConvertTo-SqlStringLiteral $appPasswordText
                Invoke-Psql -Database $MaintenanceDatabase -Sql "ALTER ROLE $ApplicationRole WITH LOGIN CREATEDB PASSWORD '$passwordLiteral';" | Out-Null
            }
        }
        else {
            Write-Verbose "Role '$ApplicationRole' already exists; its password will not be changed."
            if ($PSCmdlet.ShouldProcess("role '$ApplicationRole' on $HostName`:$Port", 'Ensure LOGIN and CREATEDB attributes without changing password')) {
                Invoke-Psql -Database $MaintenanceDatabase -Sql "ALTER ROLE $ApplicationRole WITH LOGIN CREATEDB;" | Out-Null
            }
        }
    }

    if (-not $roleExists) {
        Write-Verbose "Database creation is skipped because role '$ApplicationRole' was not created."
        return
    }

    $databaseExists = (Invoke-Psql -Database $MaintenanceDatabase -Sql "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$DevelopmentDatabase');") -eq 't'
    if (-not $databaseExists) {
        if ($PSCmdlet.ShouldProcess("database '$DevelopmentDatabase' on $HostName`:$Port", 'Create local development database')) {
            Invoke-Psql -Database $MaintenanceDatabase -Sql "CREATE DATABASE $DevelopmentDatabase OWNER $ApplicationRole;" | Out-Null
            $databaseExists = $true
        }
    }
    else {
        Write-Verbose "Database '$DevelopmentDatabase' already exists and will not be replaced."
    }

    if ($databaseExists -and -not $SkipMigrations -and $PSCmdlet.ShouldProcess("database '$DevelopmentDatabase'", 'Apply Entity Framework Core migrations')) {
        $passwordValue = ConvertTo-ConnectionStringValue $appPasswordText
        $connectionString = "Host=$HostName;Port=$Port;Database=$DevelopmentDatabase;Username=$ApplicationRole;Password=$passwordValue"
        [Environment]::SetEnvironmentVariable($connectionVariableName, $connectionString, 'Process')

        Push-Location $RepositoryRoot
        try {
            & dotnet tool restore
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet tool restore failed with exit code $LASTEXITCODE."
            }

            & dotnet tool run dotnet-ef database update --project $InfrastructureProject --startup-project $StartupProject
            if ($LASTEXITCODE -ne 0) {
                throw "Entity Framework Core migration failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
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

    if ($hadConnectionString) {
        [Environment]::SetEnvironmentVariable($connectionVariableName, $previousConnectionString, 'Process')
    }
    else {
        [Environment]::SetEnvironmentVariable($connectionVariableName, $null, 'Process')
    }

    $adminPasswordText = $null
    $appPasswordText = $null
}

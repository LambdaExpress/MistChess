[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ServiceName = 'postgresql-x64-18',

    [switch]$Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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

function Wait-ForServiceRunning {
    param([Parameter(Mandatory)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
}

function Get-EnvironmentConnectionString {
    param([Parameter(Mandatory)][string]$FilePath)

    $line = Get-Content -LiteralPath $FilePath |
        Where-Object { $_.TrimStart().StartsWith('ConnectionStrings__MistChess=') } |
        Select-Object -First 1
    if ($null -eq $line) {
        throw '.env 中缺少 ConnectionStrings__MistChess。'
    }

    $value = $line.Substring($line.IndexOf('=') + 1).Trim()
    if ($value.Length -ge 2 -and
        (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or
         ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    }

    return $value
}

function Get-NewAdministratorPassword {
    while ($true) {
        $password = Read-Host "请输入 PostgreSQL 管理员 'postgres' 的新密码（至少 8 个字符，输入不会显示）" -AsSecureString
        $confirmation = Read-Host '请再次输入新密码' -AsSecureString
        $passwordText = ConvertFrom-SecureValue $password
        $confirmationText = ConvertFrom-SecureValue $confirmation

        if ($passwordText.Length -lt 8) {
            Write-Host '密码长度不足 8 个字符，请重新输入。' -ForegroundColor Yellow
        }
        elseif ($passwordText -cne $confirmationText) {
            Write-Host '两次输入不一致，请重新输入。' -ForegroundColor Yellow
        }
        else {
            $confirmation.Dispose()
            $confirmationText = $null
            return [pscustomobject]@{
                SecureValue = $password
                PlainText = $passwordText
            }
        }

        $password.Dispose()
        $confirmation.Dispose()
        $passwordText = $null
        $confirmationText = $null
    }
}

if (-not (Test-Administrator)) {
    if ($Elevated) {
        throw '提升后的 PowerShell 仍没有管理员权限。'
    }

    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction Stop
    $arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ServiceName `"$ServiceName`" -Elevated"
    $process = Start-Process -FilePath $pwsh.Source -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$environmentFile = Join-Path $repositoryRoot '.env'
$initializer = Join-Path $PSScriptRoot 'Initialize-LocalPostgres.ps1'
$service = Get-CimInstance Win32_Service -Filter "Name = '$ServiceName'" -ErrorAction Stop
if ($null -eq $service) {
    throw "未找到 Windows 服务 '$ServiceName'。"
}
if ($service.State -ne 'Running') {
    throw "PostgreSQL 服务 '$ServiceName' 当前未运行。"
}

if ($service.PathName -match '-D\s+"([^"]+)"') {
    $dataDirectory = $Matches[1]
}
elseif ($service.PathName -match '-D\s+(\S+)') {
    $dataDirectory = $Matches[1]
}
else {
    throw "无法从服务配置中解析 PostgreSQL 数据目录：$($service.PathName)"
}

if ($service.PathName -match '^"([^"]+)"') {
    $pgCtlPath = $Matches[1]
}
elseif ($service.PathName -match '^(\S+)') {
    $pgCtlPath = $Matches[1]
}
else {
    throw "无法从服务配置中解析 pg_ctl 路径：$($service.PathName)"
}

$psqlPath = Join-Path (Split-Path $pgCtlPath -Parent) 'psql.exe'
$hbaPath = Join-Path $dataDirectory 'pg_hba.conf'
if (-not (Test-Path -LiteralPath $psqlPath -PathType Leaf)) {
    throw "未找到 psql：$psqlPath"
}
if (-not (Test-Path -LiteralPath $hbaPath -PathType Leaf)) {
    throw "未找到 pg_hba.conf：$hbaPath"
}
if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw "未找到项目环境文件：$environmentFile"
}
if (-not (Test-Path -LiteralPath $initializer -PathType Leaf)) {
    throw "未找到数据库初始化脚本：$initializer"
}

$connectionString = Get-EnvironmentConnectionString -FilePath $environmentFile
$connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
$connectionBuilder.set_ConnectionString($connectionString)
foreach ($key in 'Host', 'Port', 'Database', 'Username', 'Password') {
    if (-not $connectionBuilder.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$connectionBuilder[$key])) {
        throw ".env 中的 ConnectionStrings__MistChess 缺少 $key。"
    }
}
if ([string]$connectionBuilder['Host'] -notin 'localhost', '127.0.0.1') {
    throw '管理员密码恢复只允许连接本机 PostgreSQL。'
}

$passwordResult = $null
$appPassword = $null
$appPasswordText = [string]$connectionBuilder['Password']
$backupPath = "$hbaPath.mistchess-reset-$([Guid]::NewGuid().ToString('N')).bak"
$originalHash = (Get-FileHash -LiteralPath $hbaPath -Algorithm SHA256).Hash
$hbaModified = $false
$hbaRestored = $false
$hadPgPassword = Test-Path Env:PGPASSWORD
$previousPgPassword = $env:PGPASSWORD

$failureMessage = $null
try {
    $passwordResult = Get-NewAdministratorPassword
    $appPassword = ConvertTo-SecureString $appPasswordText -AsPlainText -Force

    try {
        Copy-Item -LiteralPath $hbaPath -Destination $backupPath -ErrorAction Stop
        $hbaModified = $true
        $originalHba = [IO.File]::ReadAllText($hbaPath)
        $temporaryRule = "# MistChess temporary administrator password reset`r`nhost all postgres 127.0.0.1/32 trust`r`n"
        [IO.File]::WriteAllText($hbaPath, $temporaryRule + $originalHba, [Text.UTF8Encoding]::new($false))

        Restart-Service -Name $ServiceName -Force -ErrorAction Stop
        Wait-ForServiceRunning -Name $ServiceName

        $passwordLiteral = $passwordResult.PlainText.Replace("'", "''")
        $alterSql = "ALTER ROLE postgres WITH PASSWORD '$passwordLiteral';"
        $env:PGPASSWORD = $null
        $alterSql | & $psqlPath `
            '--host=127.0.0.1' `
            '--port=5432' `
            '--username=postgres' `
            '--dbname=postgres' `
            '--no-password' `
            '--no-psqlrc' `
            '--set=ON_ERROR_STOP=1' *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "PostgreSQL 管理员密码修改失败，退出码：$LASTEXITCODE"
        }
    }
    finally {
        if ($hbaModified -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            Copy-Item -LiteralPath $backupPath -Destination $hbaPath -Force -ErrorAction Stop
            Restart-Service -Name $ServiceName -Force -ErrorAction Stop
            Wait-ForServiceRunning -Name $ServiceName
            $hbaRestored = $true
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not $hbaRestored -or (Get-FileHash -LiteralPath $hbaPath -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'pg_hba.conf 未恢复到重置前的内容。'
    }

    $env:PGPASSWORD = $passwordResult.PlainText
    & $psqlPath `
        '--host=127.0.0.1' `
        '--port=5432' `
        '--username=postgres' `
        '--dbname=postgres' `
        '--no-password' `
        '--no-psqlrc' `
        '--tuples-only' `
        '--no-align' `
        '--command=SELECT 1;' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw '恢复认证规则后，新 PostgreSQL 管理员密码验证失败。'
    }

    & $initializer `
        -HostName ([string]$connectionBuilder['Host']) `
        -Port ([int]$connectionBuilder['Port']) `
        -AdminPassword $passwordResult.SecureValue `
        -AppPassword $appPassword `
        -ResetAppPassword
    if ($LASTEXITCODE -ne 0) {
        throw "应用数据库同步失败，退出码：$LASTEXITCODE"
    }

    $env:PGPASSWORD = $appPasswordText
    & $psqlPath `
        "--host=$([string]$connectionBuilder['Host'])" `
        "--port=$([int]$connectionBuilder['Port'])" `
        "--username=$([string]$connectionBuilder['Username'])" `
        "--dbname=$([string]$connectionBuilder['Database'])" `
        '--no-password' `
        '--no-psqlrc' `
        '--tuples-only' `
        '--no-align' `
        '--command=SELECT 1;' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw '.env 中的应用数据库连接在同步后仍无法认证。'
    }

    Write-Host ''
    Write-Host 'PostgreSQL 管理员密码已重置，应用数据库也已同步。' -ForegroundColor Green
    Start-Sleep -Seconds 3
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    if ($hadPgPassword) {
        $env:PGPASSWORD = $previousPgPassword
    }
    else {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    if ($null -ne $passwordResult) {
        $passwordResult.SecureValue.Dispose()
        $passwordResult.PlainText = $null
    }
    if ($null -ne $appPassword) {
        $appPassword.Dispose()
    }
    $appPasswordText = $null
    $connectionString = $null
}

if ($null -ne $failureMessage) {
    Write-Host ''
    Write-Host "重置失败：$failureMessage" -ForegroundColor Red
    if ($hbaModified -and -not $hbaRestored) {
        Write-Host '认证配置可能仍处于临时 trust 状态或尚未重新加载，请不要继续使用数据库。' -ForegroundColor Red
        Write-Host "原始认证配置备份保留在：$backupPath" -ForegroundColor Red
    }
    try {
        $null = Read-Host '按 Enter 键关闭'
    }
    catch {
        # Non-interactive hosts cannot display a prompt.
    }
    exit 1
}

[CmdletBinding()]
param(
    [switch]$ValidateOnly,
    [switch]$NoBrowser,

    [ValidateRange(5, 300)]
    [int]$StartupTimeoutSeconds = 60,

    [switch]$ListenOnLan,
    [string]$WebHost
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$apiProject = Join-Path $repoRoot 'src/MistChess.Api/MistChess.Api.csproj'
$webDirectory = Join-Path $repoRoot 'apps/web'
$webNodeModules = Join-Path $webDirectory 'node_modules'
$environmentFile = Join-Path $repoRoot '.env'
$databaseInitializer = Join-Path $repoRoot 'scripts/database/Initialize-LocalPostgres.ps1'
$infrastructureProject = Join-Path $repoRoot 'src/MistChess.Infrastructure/MistChess.Infrastructure.csproj'
$apiUrl = 'http://localhost:5052'
$webUrl = $null

function Get-RequiredCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$InstallHint
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "未找到 $Name。$InstallHint"
    }

    return $command
}

function Import-LocalEnvironment {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        return
    }

    foreach ($rawLine in Get-Content -LiteralPath $FilePath) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }

        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            throw ".env 中存在无效配置行：$rawLine"
        }

        $name = $line.Substring(0, $separator).Trim()
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            throw ".env 中存在无效变量名：$name"
        }

        if (Test-Path "Env:$name") {
            continue
        }

        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2) {
            $firstCharacter = $value[0]
            $lastCharacter = $value[$value.Length - 1]
            if (($firstCharacter -eq '"' -and $lastCharacter -eq '"') -or
                ($firstCharacter -eq "'" -and $lastCharacter -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}

function Get-LocalDatabaseSettings {
    $connectionString = $env:ConnectionStrings__MistChess
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        return $null
    }

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($connectionString)
    }
    catch {
        throw '.env 中的 ConnectionStrings__MistChess 不是有效的数据库连接字符串。'
    }

    $requiredKeys = 'Host', 'Database', 'Username', 'Password'
    foreach ($key in $requiredKeys) {
        if (-not $builder.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$builder[$key])) {
            throw ".env 中的 ConnectionStrings__MistChess 缺少 $key。"
        }
    }

    $hostName = [string]$builder['Host']
    $database = [string]$builder['Database']
    $username = [string]$builder['Username']
    if (($hostName -ne 'localhost' -and $hostName -ne '127.0.0.1') -or
        $database -ne 'mistchess_dev' -or
        $username -ne 'mistchess_app') {
        return $null
    }

    $port = 5432
    if ($builder.ContainsKey('Port') -and -not [int]::TryParse([string]$builder['Port'], [ref]$port)) {
        throw '.env 中的 PostgreSQL Port 必须是整数。'
    }

    return [pscustomobject]@{
        HostName = $hostName
        Port = $port
        Password = [string]$builder['Password']
    }
}

function Test-LocalDatabaseConnection {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Settings,

        [Parameter(Mandatory)]
        [System.Management.Automation.CommandInfo]$PsqlCommand
    )

    $hadPassword = Test-Path Env:PGPASSWORD
    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $Settings.Password
        & $PsqlCommand.Source `
            "--host=$($Settings.HostName)" `
            "--port=$($Settings.Port)" `
            '--username=mistchess_app' `
            '--dbname=mistchess_dev' `
            '--no-password' `
            '--no-psqlrc' `
            '--tuples-only' `
            '--no-align' `
            '--command=SELECT 1;' *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
    finally {
        if ($hadPassword) {
            $env:PGPASSWORD = $previousPassword
        }
        else {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
    }
}

function Update-LocalDatabase {
    Write-Host '正在检查数据库 migrations...' -ForegroundColor Cyan
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore 执行失败，退出码：$LASTEXITCODE"
    }

    & dotnet tool run dotnet-ef database update `
        --project $infrastructureProject `
        --startup-project $apiProject
    if ($LASTEXITCODE -ne 0) {
        throw "数据库 migration 执行失败，退出码：$LASTEXITCODE"
    }
}

function Test-TcpPort {
    param(
        [Parameter(Mandatory)]
        [string]$HostName,

        [Parameter(Mandatory)]
        [int]$Port
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync($HostName, $Port)
        if (-not $connectTask.Wait(300)) {
            return $false
        }

        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Get-PreferredLanAddress {
    $candidates = foreach ($configuration in Get-NetIPConfiguration) {
        if ($configuration.NetAdapter.Status -ne 'Up' -or
            -not $configuration.NetAdapter.HardwareInterface -or
            $null -eq $configuration.IPv4DefaultGateway) {
            continue
        }

        $ipInterface = Get-NetIPInterface `
            -InterfaceIndex $configuration.InterfaceIndex `
            -AddressFamily IPv4 `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        foreach ($address in @($configuration.IPv4Address)) {
            if ([string]::IsNullOrWhiteSpace($address.IPAddress)) {
                continue
            }

            [pscustomobject]@{
                IPAddress = $address.IPAddress
                InterfaceAlias = $configuration.InterfaceAlias
                InterfaceIndex = $configuration.InterfaceIndex
                InterfaceMetric = if ($null -eq $ipInterface) {
                    [int]::MaxValue
                }
                else {
                    $ipInterface.InterfaceMetric
                }
            }
        }
    }

    $selected = $candidates |
        Sort-Object InterfaceMetric, InterfaceAlias |
        Select-Object -First 1
    if ($null -eq $selected) {
        throw '未找到带默认网关的活动物理网卡。请连接局域网，或使用 -WebHost 指定当前电脑的 IPv4 地址。'
    }

    return $selected
}

function ConvertTo-EncodedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command
    )

    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
}

function Test-DatabaseConnectionConfigured {
    if (-not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__MistChess)) {
        return $true
    }

    $secretOutput = & dotnet user-secrets list --project $apiProject 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return $null -ne ($secretOutput | Select-String -Pattern '^\s*ConnectionStrings:MistChess\s*=')
}

function Wait-ForHttpEndpoint {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,

        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [string]$ServiceName,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "$ServiceName 启动进程已退出，请查看对应的 PowerShell 窗口。"
        }

        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) {
                return
            }
        }
        catch {
            # The service may still be starting.
        }

        Start-Sleep -Milliseconds 300
    }

    throw "$ServiceName 未在 $TimeoutSeconds 秒内就绪，请查看对应的 PowerShell 窗口。"
}

try {
    $selectedLan = $null
    if ([string]::IsNullOrWhiteSpace($WebHost)) {
        if ($ListenOnLan) {
            $selectedLan = Get-PreferredLanAddress
            $WebHost = $selectedLan.IPAddress
            Write-Host "已自动选择局域网地址：$WebHost（$($selectedLan.InterfaceAlias)）" -ForegroundColor Cyan
        }
        else {
            $WebHost = '127.0.0.1'
        }
    }

    $webUrl = "http://${WebHost}:5173"
    $webHostAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($WebHost, [ref]$webHostAddress) -or
        $webHostAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
        $webHostAddress.Equals([System.Net.IPAddress]::Any)) {
        throw "WebHost 必须是本机明确的 IPv4 地址，不能使用主机名或 0.0.0.0：$WebHost"
    }

    if (-not [System.Net.IPAddress]::IsLoopback($webHostAddress)) {
        $isLocalAddress = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            ForEach-Object { $_.GetIPProperties().UnicastAddresses.Address } |
            Where-Object { $_.Equals($webHostAddress) } |
            Select-Object -First 1
        if ($null -eq $isLocalAddress) {
            throw "WebHost 不是当前电脑的 IPv4 地址：$WebHost"
        }

        Write-Host "局域网监听已启用：$webUrl" -ForegroundColor Yellow
        Write-Host '请只在受信任网络使用；若其他设备无法连接，请将当前网络设为“专用”，并允许 node.exe 通过 Windows Defender 防火墙的 TCP 5173 入站。' -ForegroundColor Yellow
    }

    $Host.UI.RawUI.WindowTitle = 'MistChess Launcher'
    Set-Location -LiteralPath $repoRoot

    Write-Host '正在检查开发环境...' -ForegroundColor Cyan
    Import-LocalEnvironment -FilePath $environmentFile
    $pwshCommand = Get-RequiredCommand -Name 'pwsh' -InstallHint '请安装 PowerShell 7，并确保 pwsh 已加入 PATH。'
    $null = Get-RequiredCommand -Name 'dotnet' -InstallHint '请安装 .NET 10 SDK，并确保 dotnet 已加入 PATH。'
    $null = Get-RequiredCommand -Name 'node' -InstallHint '请安装 Node.js 24，并确保 node 已加入 PATH。'
    $null = Get-RequiredCommand -Name 'npm.cmd' -InstallHint '请安装 npm，并确保 npm.cmd 已加入 PATH。'

    if (-not (Test-Path -LiteralPath $apiProject -PathType Leaf)) {
        throw "未找到 API 项目：$apiProject"
    }

    if (-not (Test-Path -LiteralPath $webDirectory -PathType Container)) {
        throw "未找到前端项目：$webDirectory"
    }

    if (-not (Test-DatabaseConnectionConfigured)) {
        throw '未配置 ConnectionStrings__MistChess。请检查仓库根目录的 .env。'
    }

    $localDatabaseSettings = Get-LocalDatabaseSettings

    if (-not (Test-Path -LiteralPath $webNodeModules -PathType Container)) {
        Write-Host '首次启动，正在安装前端依赖...' -ForegroundColor Cyan
        & npm.cmd ci --prefix $webDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci 执行失败，退出码：$LASTEXITCODE"
        }
    }

    if (Test-TcpPort -HostName 'localhost' -Port 5052) {
        throw '端口 5052 已被占用。请先关闭已有 API 或占用该端口的程序。'
    }

    if (Test-TcpPort -HostName $WebHost -Port 5173) {
        throw '端口 5173 已被占用。请先关闭已有前端服务或占用该端口的程序。'
    }

    if ($ValidateOnly) {
        Write-Host '启动前置条件检查通过。' -ForegroundColor Green
        return
    }

    if ($null -ne $localDatabaseSettings) {
        if (-not (Test-Path -LiteralPath $databaseInitializer -PathType Leaf)) {
            throw "未找到数据库初始化脚本：$databaseInitializer"
        }

        $psqlCommand = Get-RequiredCommand -Name 'psql' -InstallHint '请安装 PostgreSQL 18，并确保 psql 已加入 PATH。'
        $databaseWasInitialized = $false
        if (-not (Test-LocalDatabaseConnection -Settings $localDatabaseSettings -PsqlCommand $psqlCommand)) {
            Write-Host '本地数据库需要初始化或同步应用密码。' -ForegroundColor Yellow
            $secureAppPassword = ConvertTo-SecureString $localDatabaseSettings.Password -AsPlainText -Force
            try {
                & $databaseInitializer `
                    -HostName $localDatabaseSettings.HostName `
                    -Port $localDatabaseSettings.Port `
                    -AppPassword $secureAppPassword `
                    -ResetAppPassword
                if ($LASTEXITCODE -ne 0) {
                    throw "数据库初始化脚本执行失败，退出码：$LASTEXITCODE"
                }
            }
            finally {
                $secureAppPassword.Dispose()
            }

            if (-not (Test-LocalDatabaseConnection -Settings $localDatabaseSettings -PsqlCommand $psqlCommand)) {
                throw '数据库初始化后仍无法使用 .env 中的连接配置登录。'
            }

            $databaseWasInitialized = $true
        }

        if (-not $databaseWasInitialized) {
            Update-LocalDatabase
        }
    }

    $escapedRepoRoot = $repoRoot.Replace("'", "''")
    $escapedWebUrl = $webUrl.Replace("'", "''")
    $escapedWebHost = $WebHost.Replace("'", "''")
    $apiCommand = @"
`$Host.UI.RawUI.WindowTitle = 'MistChess API'
Set-Location -LiteralPath '$escapedRepoRoot'
`$env:WebSockets__AllowedOrigins__0 = '$escapedWebUrl'
`$env:WebSockets__AllowedOrigins__1 = 'http://127.0.0.1:5173'
`$env:WebSockets__AllowedOrigins__2 = 'http://localhost:5173'
dotnet run --project 'src/MistChess.Api/MistChess.Api.csproj' --launch-profile http
if (`$LASTEXITCODE -ne 0) {
    Write-Host "API 已退出，退出码：`$LASTEXITCODE" -ForegroundColor Red
}
"@
    $webCommand = @"
`$Host.UI.RawUI.WindowTitle = 'MistChess Web'
Set-Location -LiteralPath '$escapedRepoRoot'
npm.cmd run dev --prefix apps/web -- --host '$escapedWebHost' --port 5173
if (`$LASTEXITCODE -ne 0) {
    Write-Host "前端已退出，退出码：`$LASTEXITCODE" -ForegroundColor Red
}
"@

    Write-Host '正在启动 API...' -ForegroundColor Cyan
    $apiProcess = Start-Process `
        -FilePath $pwshCommand.Source `
        -ArgumentList '-NoLogo', '-NoProfile', '-NoExit', '-EncodedCommand', (ConvertTo-EncodedCommand -Command $apiCommand) `
        -WorkingDirectory $repoRoot `
        -PassThru
    Wait-ForHttpEndpoint `
        -Uri "$apiUrl/health/ready" `
        -Process $apiProcess `
        -ServiceName 'API' `
        -TimeoutSeconds $StartupTimeoutSeconds

    Write-Host '正在启动前端...' -ForegroundColor Cyan
    $webProcess = Start-Process `
        -FilePath $pwshCommand.Source `
        -ArgumentList '-NoLogo', '-NoProfile', '-NoExit', '-EncodedCommand', (ConvertTo-EncodedCommand -Command $webCommand) `
        -WorkingDirectory $repoRoot `
        -PassThru
    Wait-ForHttpEndpoint `
        -Uri $webUrl `
        -Process $webProcess `
        -ServiceName '前端' `
        -TimeoutSeconds $StartupTimeoutSeconds

    Write-Host "前后端已启动：$webUrl" -ForegroundColor Green
    if (-not $NoBrowser) {
        Start-Process $webUrl
    }
}
catch {
    Write-Host ''
    Write-Host "启动失败：$($_.Exception.Message)" -ForegroundColor Red
    try {
        $null = Read-Host '按 Enter 键关闭'
    }
    catch {
        # Non-interactive hosts cannot display a prompt.
    }
    exit 1
}

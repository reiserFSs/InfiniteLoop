[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'AscNetLauncher\local'),
    [string]$Repository = 'https://github.com/reiserFSs/InfiniteLoop.git',
    [string]$Branch = 'master',
    [string]$PreparedCheckout,
    [switch]$SetupLockHeld
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Fail([string]$Message) { throw "AscNet local setup: $Message" }
function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}
function Find-Command([string]$Name, [string[]]$Fallbacks = @()) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($path in $Fallbacks) { if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { return $path } }
    return $null
}
function Invoke-Checked([string]$Program, [string[]]$Arguments, [string]$Description) {
    Write-Host "+ $Program $($Arguments -join ' ')"
    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "$Description failed with exit code $LASTEXITCODE." }
}
function Ensure-WinGet {
    $winget = Find-Command 'winget.exe' @((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'))
    if ($winget) { return $winget }
    Write-Host 'WinGet is missing; repairing Microsoft App Installer for the current user.'
    try {
        Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Host
        Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery -Scope CurrentUser | Out-Host
        Import-Module Microsoft.WinGet.Client -Force
        Repair-WinGetPackageManager | Out-Host
    } catch {
        Fail "Microsoft's WinGet bootstrap could not install App Installer and its dependencies. Ensure PowerShell Gallery is reachable, then retry. $($_.Exception.Message)"
    }
    Refresh-Path
    $winget = Find-Command 'winget.exe' @((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'))
    if (-not $winget) { Fail 'Microsoft WinGet repair completed but winget.exe is unavailable. Restart Windows, then retry.' }
    return $winget
}
function Install-WinGetPackage([string]$Id, [string]$Label, [string[]]$Extra = @()) {
    $winget = Ensure-WinGet
    $arguments = @('install', '--id', $Id, '--exact', '--source', 'winget', '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity') + $Extra
    Invoke-Checked $winget $arguments "Installing $Label"
    Refresh-Path
}
function Ensure-Git {
    $git = Find-Command 'git.exe' @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe")
    if (-not $git) { Install-WinGetPackage 'Git.Git' 'Git'; $git = Find-Command 'git.exe' @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe") }
    if (-not $git) { Fail 'Git was installed but git.exe could not be found. Restart the launcher and retry.' }
    return $git
}
function Git-Output([string]$Git, [string]$Directory, [string[]]$Arguments) {
    $output = & $Git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    return (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}
function Update-Checkout([string]$Git, [string]$Checkout, [bool]$Pull = $true) {
    if (-not (Test-Path -LiteralPath $Checkout)) {
        $parent = Split-Path -Parent $Checkout
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        try { Invoke-Checked $Git @('clone', '--single-branch', '--branch', $Branch, '--', $Repository, $Checkout) 'Repository clone' }
        catch { if (Test-Path -LiteralPath $Checkout) { Remove-Item -LiteralPath $Checkout -Recurse -Force }; throw }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Checkout '.git'))) { Fail "Checkout path is not a Git repository: $Checkout" }
    $origin = Git-Output $Git $Checkout @('remote', 'get-url', 'origin')
    if ($origin -cne $Repository) { Fail "Checkout origin is '$origin', expected exactly '$Repository'. Refusing to replace it." }
    $current = Git-Output $Git $Checkout @('branch', '--show-current')
    if ($current -cne $Branch) { Fail "Checkout is on branch '$current', expected '$Branch'. Switch it manually; setup will not reset your work." }
    $dirty = Git-Output $Git $Checkout @('status', '--porcelain', '--untracked-files=normal')
    if ($dirty) { Fail "Checkout has local changes. Commit or remove them before updating; setup will not reset, clean, or stash files.`n$dirty" }
    if ($Pull) { Invoke-Checked $Git @('-C', $Checkout, 'pull', '--ff-only', 'origin', $Branch) 'Fast-forward repository update' }
}
function Ensure-DotNet {
    $dotnet = Find-Command 'dotnet.exe' @("$env:ProgramFiles\dotnet\dotnet.exe")
    $ok = $false
    if ($dotnet) { $ok = (& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^8\.' } | Measure-Object).Count -gt 0 }
    if (-not $ok) { Install-WinGetPackage 'Microsoft.DotNet.SDK.8' '.NET 8 SDK'; $dotnet = Find-Command 'dotnet.exe' @("$env:ProgramFiles\dotnet\dotnet.exe") }
    if (-not $dotnet) { Fail '.NET 8 SDK was installed but dotnet.exe could not be found. Restart the launcher and retry.' }
    return [IO.Path]::GetFullPath($dotnet)
}
function Ensure-Rust {
    $rustup = Find-Command 'rustup.exe' @((Join-Path $env:USERPROFILE '.cargo\bin\rustup.exe'))
    if (-not $rustup) { Install-WinGetPackage 'Rustlang.Rustup' 'Rustup'; $rustup = Find-Command 'rustup.exe' @((Join-Path $env:USERPROFILE '.cargo\bin\rustup.exe')) }
    if (-not $rustup) { Fail 'Rustup was installed but rustup.exe could not be found. Restart the launcher and retry.' }
    $installed = & $rustup toolchain list 2>$null
    if (-not ($installed | Where-Object { $_ -match '^1\.92\.0-' })) { Invoke-Checked $rustup @('toolchain', 'install', '1.92.0', '--profile', 'minimal') 'Installing Rust 1.92 toolchain' }
    $targets = & $rustup target list --toolchain 1.92.0 --installed 2>$null
    if ($targets -notcontains 'x86_64-pc-windows-msvc') { Invoke-Checked $rustup @('target', 'add', 'x86_64-pc-windows-msvc', '--toolchain', '1.92.0') 'Installing Rust Windows MSVC target' }
    $cargo = Find-Command 'cargo.exe' @((Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'))
    if (-not $cargo) { Fail 'cargo.exe is unavailable after installing Rustup.' }
    return $cargo
}
function Ensure-MSBuild {
    $vswhere = Find-Command 'vswhere.exe' @("${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe")
    $msbuild = $null
    if ($vswhere) { $msbuild = (& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1) }
    if (-not $msbuild) {
        $installPath = $null
        if ($vswhere) { $installPath = (& $vswhere -latest -version '[17.0,18.0)' -products Microsoft.VisualStudio.Product.BuildTools -property installationPath | Select-Object -First 1) }
        if ($installPath) {
            $installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe"
            if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { Fail 'Visual Studio Installer is missing; repair it before adding the C++ workload.' }
            $modifyArguments = @('modify', '--installPath', ('"{0}"' -f $installPath), '--add', 'Microsoft.VisualStudio.Workload.VCTools', '--includeRecommended', '--passive', '--norestart')
            try { $process = Start-Process -FilePath $installer -ArgumentList $modifyArguments -Verb RunAs -Wait -PassThru }
            catch { Fail "Visual Studio C++ workload elevation was cancelled or could not start: $($_.Exception.Message)" }
            if ($process.ExitCode -eq 3010) { Fail 'Visual Studio C++ workload was added but Windows must restart before setup can continue.' }
            if ($process.ExitCode -ne 0) { Fail "Adding Visual Studio 2022 C++ workload failed with exit code $($process.ExitCode)." }
        } else {
            Install-WinGetPackage 'Microsoft.VisualStudio.2022.BuildTools' 'Visual Studio 2022 Build Tools C++ workload' @('--override', '--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended')
        }
        $vswhere = Find-Command 'vswhere.exe' @("${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe")
        if ($vswhere) { $msbuild = (& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1) }
    }
    if (-not $msbuild) { Fail 'Visual Studio Build Tools C++ workload was installed but MSBuild with v143 C++ tools was not found. Restart the launcher and retry.' }
    return [IO.Path]::GetFullPath("$msbuild")
}
function Ensure-MongoDB([string]$Tools, [object]$PreviousState) {
    if ($PreviousState -and $PreviousState.PSObject.Properties['mongod']) {
        $pinned = [IO.Path]::GetFullPath([string]$PreviousState.mongod)
        $ownedRoot = [IO.Path]::GetFullPath($Tools).TrimEnd('\') + '\'
        if (-not $pinned.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase)) { Fail "Active build-state mongod is outside the owned tools directory: $pinned" }
        if (Test-Path -LiteralPath $pinned -PathType Leaf) { return $pinned }
        if (Test-Path -LiteralPath (Join-Path $Root 'data\mongo')) {
            $data = Get-ChildItem -LiteralPath (Join-Path $Root 'data\mongo') -Force -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($data) { Fail "The pinned MongoDB executable is missing ($pinned), but persistent database files exist. Restore that owned MongoDB version; setup will not upgrade the database engine opportunistically." }
        }
    }
    $existing = Get-ChildItem -LiteralPath $Tools -Filter mongod.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) { return $existing.FullName }
    Write-Host 'Resolving the current MongoDB 8.0 Community portable archive and official checksum.'
    try { $catalog = Invoke-RestMethod -UseBasicParsing -Uri 'https://downloads.mongodb.org/full.json' }
    catch { Fail "Could not read MongoDB's official download metadata: $($_.Exception.Message)" }
    $version = $catalog.versions | Where-Object { $_.version -match '^8\.0\.\d+$' } | Sort-Object { [version]$_.version } -Descending | Select-Object -First 1
    if (-not $version) { Fail 'MongoDB metadata contains no stable 8.0 release.' }
    $download = $version.downloads | Where-Object { $_.target -eq 'windows' -and $_.arch -eq 'x86_64' -and $_.edition -in @('base', 'community') -and $_.archive.url -match '\.zip$' } | Select-Object -First 1
    if (-not $download) { Fail "MongoDB metadata contains no Windows x64 Community ZIP for $($version.version)." }
    $url = "$($download.archive.url)"
    $expected = "$($download.archive.sha256)".Trim().ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$') {
        try { $checksumText = (Invoke-WebRequest -UseBasicParsing -Uri "$url.sha256").Content; $expected = ([regex]::Match($checksumText, '(?i)\b[0-9a-f]{64}\b')).Value.ToLowerInvariant() }
        catch { Fail "Could not read MongoDB's official SHA-256 metadata for ${url}: $($_.Exception.Message)" }
    }
    if ($expected -notmatch '^[0-9a-f]{64}$') { Fail "MongoDB's official metadata did not provide a valid SHA-256 for $url." }
    $archive = Join-Path $env:TEMP "mongodb-$($version.version)-$PID.zip"
    $extract = Join-Path $env:TEMP "mongodb-$($version.version)-$PID"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $expected) { Fail "MongoDB archive checksum mismatch (expected $expected, received $actual)." }
        Expand-Archive -LiteralPath $archive -DestinationPath $extract
        $mongod = Get-ChildItem -LiteralPath $extract -Filter mongod.exe -Recurse | Select-Object -First 1
        if (-not $mongod) { Fail 'The verified MongoDB archive did not contain mongod.exe.' }
        $destination = Join-Path $Tools "mongodb-$($version.version)"
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Copy-Item -LiteralPath (Split-Path -Parent (Split-Path -Parent $mongod.FullName)) -Destination $destination -Recurse
        $installed = Get-ChildItem -LiteralPath $destination -Filter mongod.exe -Recurse | Select-Object -First 1
        if (-not $installed) { Fail 'MongoDB extraction did not produce an executable.' }
        return $installed.FullName
    } finally {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    }
}
function New-FreePort([int[]]$Excluded) {
    while ($true) {
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
        try { $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
        if ($Excluded -notcontains $port) { return $port }
    }
}
function Assert-FreePort([int]$Port, [string]$Label) {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch { Fail "$Label port $Port is already in use. Stop that process or preserve the current build and choose new ports; setup will not adopt or stop it." }
    finally { $listener.Stop() }
}
function Set-NetworkConfig([object]$Config, [int]$GamePort, [int]$MongoPort) {
    if (-not $Config.PSObject.Properties['GameServer']) { $Config | Add-Member NoteProperty GameServer ([pscustomobject]@{}) }
    if (-not $Config.PSObject.Properties['Database']) { $Config | Add-Member NoteProperty Database ([pscustomobject]@{}) }
    foreach ($pair in @(@($Config.GameServer, 'Host', '127.0.0.1'), @($Config.GameServer, 'Port', $GamePort), @($Config.Database, 'Host', '127.0.0.1'), @($Config.Database, 'Port', $MongoPort), @($Config.Database, 'Name', 'asc_net'))) {
        $object, $name, $value = $pair
        if ($object.PSObject.Properties[$name]) { $object.$name = $value } else { $object | Add-Member NoteProperty $name $value }
    }
}
function Write-JsonAtomic([string]$Path, [object]$Value) {
    $temp = "$Path.$PID.tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, $null) } else { [IO.File]::Move($temp, $Path) }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { Fail 'This bootstrap currently supports native Windows only.' }
if (-not [IO.Path]::IsPathRooted($Root)) { Fail '-Root must be an absolute path.' }
$Root = [IO.Path]::GetFullPath($Root)
if ($Branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$' -or $Branch.Contains('..') -or $Branch.EndsWith('.lock')) { Fail "Unsafe Git branch name: '$Branch'." }
try { $repoUri = [Uri]$Repository } catch { Fail '-Repository must be a valid HTTPS GitHub repository URL.' }
if ($repoUri.Scheme -ne 'https' -or $repoUri.Host -cne 'github.com' -or -not $repoUri.IsDefaultPort -or $repoUri.UserInfo -or $repoUri.Query -or $repoUri.Fragment -or $repoUri.AbsolutePath -notmatch '^/[^/]+/[^/]+/?$') { Fail '-Repository must be a plain https://github.com/<owner>/<repo> URL without credentials, query, or fragment.' }

[IO.Directory]::CreateDirectory($Root) | Out-Null
$lock = $null
if (-not $SetupLockHeld) {
    $lockPath = Join-Path $Root 'setup.lock'
    try { $lock = New-Object IO.FileStream($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { Fail 'Another AscNet setup is already running. Wait for it to finish; setup never overwrites a concurrent build.' }
}
try {
    $checkout = Join-Path $Root 'checkout'
    $git = Ensure-Git
    if (-not $PreparedCheckout) {
        Update-Checkout $git $checkout
        $pulledScript = Join-Path $checkout 'AscNet.Launcher\setup-local.ps1'
        if (-not (Test-Path -LiteralPath $pulledScript -PathType Leaf)) { Fail "The updated repository does not contain AscNet.Launcher\setup-local.ps1. Publish the source script before using the bundled bootstrap." }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pulledScript -Root $Root -Repository $Repository -Branch $Branch -PreparedCheckout $checkout -SetupLockHeld
        if ($LASTEXITCODE -ne 0) { Fail "The prepared repository setup failed with exit code $LASTEXITCODE." }
        exit 0
    }
    if ([IO.Path]::GetFullPath($PreparedCheckout).TrimEnd('\') -cne [IO.Path]::GetFullPath($checkout).TrimEnd('\')) { Fail '-PreparedCheckout must identify Root\checkout; it is only valid for bootstrap handoff.' }
    if ([IO.Path]::GetFullPath($PSCommandPath) -cne [IO.Path]::GetFullPath((Join-Path $checkout 'AscNet.Launcher\setup-local.ps1'))) { Fail '-PreparedCheckout may only run the script from the prepared checkout.' }
    Update-Checkout $git $checkout $false
    $revision = Git-Output $git $checkout @('rev-parse', 'HEAD')
    $statePath = Join-Path $Root 'build-state.json'
    $pendingStatePath = Join-Path $Root 'build-state.pending.json'
    $persistentConfig = Join-Path $Root 'config.json'
    $oldState = $null
    if (Test-Path -LiteralPath $statePath) { try { $oldState = [IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8) | ConvertFrom-Json } catch { Fail "Existing build-state.json is invalid: $($_.Exception.Message)" } }

    $dotnet = Ensure-DotNet
    $cargo = Ensure-Rust
    $msbuild = Ensure-MSBuild
    $tools = Join-Path $Root 'tools'; [IO.Directory]::CreateDirectory($tools) | Out-Null
    $mongod = Ensure-MongoDB $tools $oldState
    [IO.Directory]::CreateDirectory((Join-Path $Root 'data\mongo')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $Root 'logs')) | Out-Null

    if ($oldState) {
        $sdkPort = [int]$oldState.sdkPort; $gamePort = [int]$oldState.gamePort; $mongoPort = [int]$oldState.mongoPort
    } elseif (Test-Path -LiteralPath $persistentConfig) {
        try { $config = [IO.File]::ReadAllText($persistentConfig, [Text.Encoding]::UTF8) | ConvertFrom-Json; $gamePort = [int]$config.GameServer.Port; $mongoPort = [int]$config.Database.Port }
        catch { Fail "Persistent config.json has invalid GameServer/Database ports: $($_.Exception.Message)" }
        $sdkPort = New-FreePort @($gamePort, $mongoPort)
    } else {
        $sdkPort = New-FreePort @(); $gamePort = New-FreePort @($sdkPort); $mongoPort = New-FreePort @($sdkPort, $gamePort)
    }
    if (@($sdkPort, $gamePort, $mongoPort) | Where-Object { $_ -lt 1 -or $_ -gt 65535 }) { Fail 'Persisted ports must each be between 1 and 65535.' }
    if (@($sdkPort, $gamePort, $mongoPort) | Group-Object | Where-Object { $_.Count -ne 1 }) { Fail 'Persisted SDK, game, and MongoDB ports must be distinct.' }
    Assert-FreePort $sdkPort 'SDK'
    Assert-FreePort $gamePort 'Game'
    Assert-FreePort $mongoPort 'MongoDB'
    if (-not (Test-Path -LiteralPath $persistentConfig)) {
        $config = [IO.File]::ReadAllText((Join-Path $checkout 'Resources\Configs\config.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
        Set-NetworkConfig $config $gamePort $mongoPort
        Write-JsonAtomic $persistentConfig $config
    }
    try {
        $validatedConfig = [IO.File]::ReadAllText($persistentConfig, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $configuredGameHost = [string]$validatedConfig.GameServer.Host
        $configuredGamePort = [int]$validatedConfig.GameServer.Port
        $configuredDatabaseHost = [string]$validatedConfig.Database.Host
        $configuredDatabasePort = [int]$validatedConfig.Database.Port
        $configuredDatabaseName = [string]$validatedConfig.Database.Name
    } catch { Fail "Persistent config.json is invalid: $($_.Exception.Message)" }
    if ($configuredGameHost -cne '127.0.0.1' -or $configuredGamePort -ne $gamePort -or $configuredDatabaseHost -cne '127.0.0.1' -or $configuredDatabasePort -ne $mongoPort -or $configuredDatabaseName -cne 'asc_net') {
        Fail 'Persistent config.json network fields do not match the reserved local ports. Restore GameServer/Database loopback settings; setup will not overwrite user configuration.'
    }

    $buildRoot = Join-Path $Root 'build'; [IO.Directory]::CreateDirectory($buildRoot) | Out-Null
    $final = Join-Path $buildRoot $revision
    $stage = Join-Path $buildRoot "$revision.tmp-$PID"
    if (-not (Test-Path -LiteralPath (Join-Path $final 'server\AscNet.dll')) -or -not (Test-Path -LiteralPath (Join-Path $final 'patch\supported-client.json'))) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        try {
            $server = Join-Path $stage 'server'
            Invoke-Checked $dotnet @('publish', (Join-Path $checkout 'AscNet\AscNet.csproj'), '-c', 'Release', '-o', $server, '--artifacts-path', (Join-Path $stage 'dotnet-artifacts')) 'Publishing AscNet server'
            if (-not (Test-Path -LiteralPath (Join-Path $server 'Configs\version_config.json') -PathType Leaf)) { Fail 'Published server is missing Configs\version_config.json.' }
            Copy-Item -LiteralPath $persistentConfig -Destination (Join-Path $server 'Configs\config.json') -Force
            $patch = Join-Path $stage 'patch'; [IO.Directory]::CreateDirectory($patch) | Out-Null
            $targetDir = Join-Path $stage 'cargo-target'
            Invoke-Checked $cargo @('+1.92.0', 'build', '--manifest-path', (Join-Path $checkout 'AscNet.Patch\Cargo.toml'), '--locked', '--release', '--target', 'x86_64-pc-windows-msvc', '--target-dir', $targetDir) 'Building client patch'
            Copy-Item -LiteralPath (Join-Path $targetDir 'x86_64-pc-windows-msvc\release\lucia.dll') -Destination (Join-Path $patch 'lucia.dll')
            Copy-Item -LiteralPath (Join-Path $targetDir 'x86_64-pc-windows-msvc\release\KRSDK.dll') -Destination (Join-Path $patch 'KRSDK.dll')
            $loaderOut = Join-Path $stage 'loader'; [IO.Directory]::CreateDirectory($loaderOut) | Out-Null
            Invoke-Checked $msbuild @((Join-Path $checkout 'AscNet.Patch\VersionShim\src\VersionShim.vcxproj'), '/m:1', '/p:Configuration=Release', '/p:Platform=x64', "/p:OutDir=$loaderOut\", "/p:IntDir=$(Join-Path $stage 'loader-obj')\") 'Building version loader'
            Copy-Item -LiteralPath (Join-Path $loaderOut 'VersionShim.dll') -Destination (Join-Path $patch 'version.dll')
            [IO.File]::WriteAllText((Join-Path $patch 'libraries.txt'), "*PGR.exe`nlucia.dll`n", (New-Object Text.UTF8Encoding($false)))
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'supported-client.json') -Destination (Join-Path $patch 'supported-client.json')
            if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Recurse -Force }
            Move-Item -LiteralPath $stage -Destination $final
        } catch { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue; throw }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'supported-client.json') -Destination (Join-Path $final 'patch\supported-client.json') -Force

    $serverDirectory = Join-Path $final 'server'
    $resourceDirectory = $serverDirectory
    $state = [ordered]@{ schemaVersion = 1; revision = $revision; repository = $Repository; dotnet = $dotnet; mongod = [IO.Path]::GetFullPath($mongod); serverDirectory = [IO.Path]::GetFullPath($serverDirectory); resourceDirectory = [IO.Path]::GetFullPath($resourceDirectory); patchDirectory = [IO.Path]::GetFullPath((Join-Path $final 'patch')); sdkPort = $sdkPort; gamePort = $gamePort; mongoPort = $mongoPort }
    Write-JsonAtomic $pendingStatePath $state
    Write-Host "AscNet local build prepared for launcher validation: $revision"
    Write-Host "Pending state: $pendingStatePath"
} finally {
    if ($lock) { $lock.Dispose() }
}

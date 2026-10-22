[CmdletBinding()]
param(
    [string] $DotaGamePath,
    [switch] $Restore
)

$ErrorActionPreference = 'Stop'

function Resolve-DotaGamePath {
    param([string] $ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $process = Get-CimInstance Win32_Process -Filter "Name='dota2.exe'" |
        Select-Object -First 1

    if ($null -ne $process -and -not [string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
        $win64 = Split-Path $process.ExecutablePath -Parent
        $bin = Split-Path $win64 -Parent
        return (Split-Path $bin -Parent)
    }

    throw 'Dota 2 is not running. Pass -DotaGamePath with the full ...\dota 2 beta\game directory.'
}

function Install-CfgHook {
    param(
        [string] $Path,
        [string] $BeginMarker,
        [string] $Command,
        [string] $EndMarker
    )

    $backup = "$Path.dota-utility.bak"
    $content = ''
    if (Test-Path -LiteralPath $Path) {
        $content = [IO.File]::ReadAllText($Path)
        if (-not (Test-Path -LiteralPath $backup)) {
            Copy-Item -LiteralPath $Path -Destination $backup
        }
    }

    $nl = [Environment]::NewLine
    $block = $BeginMarker + $nl + $Command + $nl + $EndMarker + $nl

    if ($content.Contains($BeginMarker)) {
        $escapedBegin = [Text.RegularExpressions.Regex]::Escape($BeginMarker)
        $escapedEnd = [Text.RegularExpressions.Regex]::Escape($EndMarker)
        $pattern = "(?ms)^$escapedBegin\r?\n.*?^$escapedEnd\r?\n?"
        $content = [Text.RegularExpressions.Regex]::Replace(
            $content,
            $pattern,
            [Text.RegularExpressions.MatchEvaluator] { param($match) $block })
        [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
        return
    }

    if ($content.Length -gt 0 -and -not $content.EndsWith($nl)) {
        $content += $nl
    }

    $content += $block
    [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
}

function Restore-CfgHook {
    param(
        [string] $Path,
        [string] $BeginMarker,
        [string] $EndMarker
    )

    $backup = "$Path.dota-utility.bak"
    if (Test-Path -LiteralPath $backup) {
        Copy-Item -LiteralPath $backup -Destination $Path -Force
        return
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $content = [IO.File]::ReadAllText($Path)
    $escapedBegin = [Text.RegularExpressions.Regex]::Escape($BeginMarker)
    $escapedEnd = [Text.RegularExpressions.Regex]::Escape($EndMarker)
    $pattern = "(?ms)^$escapedBegin\r?\n.*?^$escapedEnd\r?\n?"
    $updated = [Text.RegularExpressions.Regex]::Replace($content, $pattern, '')
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$gamePath = Resolve-DotaGamePath -ExplicitPath $DotaGamePath
$sourceScript = Join-Path $PSScriptRoot 'local-lobby\dotautility_visibility.lua'
$targetScript = Join-Path $gamePath 'dota\scripts\vscripts\dotautility_visibility.lua'
$targetBackup = "$targetScript.dota-utility.bak"
$cfgDirectory = Join-Path $gamePath 'dota\cfg'

if (-not (Test-Path -LiteralPath $sourceScript)) {
    throw "Bridge source script not found: $sourceScript"
}

New-Item -ItemType Directory -Path (Split-Path $targetScript -Parent) -Force | Out-Null
New-Item -ItemType Directory -Path $cfgDirectory -Force | Out-Null

$beginMarker = '// BEGIN DotaUtility local-lobby visibility bridge'
$endMarker = '// END DotaUtility local-lobby visibility bridge'
$command = @(
    'sv_cheats 1',
    'script_reload_code dotautility_visibility.lua',
    'sv_cheats 0'
) -join [Environment]::NewLine
$cfgPaths = @(
    (Join-Path $cfgDirectory 'listenserver.cfg'),
    (Join-Path $cfgDirectory 'lan_server.cfg')
)

if ($Restore) {
    if (Test-Path -LiteralPath $targetBackup) {
        Copy-Item -LiteralPath $targetBackup -Destination $targetScript -Force
    }
    elseif (Test-Path -LiteralPath $targetScript) {
        Remove-Item -LiteralPath $targetScript -Force
    }

    foreach ($cfgPath in $cfgPaths) {
        Restore-CfgHook -Path $cfgPath -BeginMarker $beginMarker -EndMarker $endMarker
    }

    Write-Host 'Restored DotaUtility local-lobby bridge files.'
    Write-Host 'Restart the local lobby/map once for the change to take effect.'
    return
}

if ((Test-Path -LiteralPath $targetScript) -and -not (Test-Path -LiteralPath $targetBackup)) {
    Copy-Item -LiteralPath $targetScript -Destination $targetBackup
}

Copy-Item -LiteralPath $sourceScript -Destination $targetScript -Force

foreach ($cfgPath in $cfgPaths) {
    Install-CfgHook -Path $cfgPath -BeginMarker $beginMarker -Command $command -EndMarker $endMarker
}

Write-Host "Installed local-lobby VScript: $targetScript"
Write-Host "Installed cfg hook: $($cfgPaths[0])"
Write-Host "Installed cfg hook: $($cfgPaths[1])"
Write-Host 'Restart the local lobby/map once. Dota will then load the bridge automatically.'

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

$gamePath = Resolve-DotaGamePath -ExplicitPath $DotaGamePath
$heroDemoPath = Join-Path (Join-Path $gamePath 'dota_addons') 'hero_demo'
$vscriptPath = Join-Path (Join-Path $heroDemoPath 'scripts') 'vscripts'
$target = Join-Path $vscriptPath 'addon_game_mode.lua'
$backup = "$target.dota-utility.bak"

if (-not (Test-Path -LiteralPath $target)) {
    throw "Hero Demo VScript not found: $target"
}

if ($Restore) {
    if (-not (Test-Path -LiteralPath $backup)) {
        throw "Backup not found: $backup"
    }

    Copy-Item -LiteralPath $backup -Destination $target -Force
    Write-Host "Restored Hero Demo VScript from $backup"
    Write-Host 'Restart/reload Hero Demo before testing.'
    return
}

if (Test-Path -LiteralPath $backup) {
    $currentContent = [IO.File]::ReadAllText($target)
    if ($currentContent.Contains('particles/ui_mouseactions/range_display.vpcf') -and $currentContent.Contains('particles/items2_fx/true_sight_debuff.vpcf') -and $currentContent.Contains('UpdateDotaUtilityEnemyMinimapIcons')) {
        Write-Host "DotaUtility Hero Demo visibility bridge (particle ring + overhead sentry particle + minimap icons) is already installed: $target"
        return
    }

    # Restore clean backup before applying latest bridge
    Copy-Item -LiteralPath $backup -Destination $target -Force
}

$content = [IO.File]::ReadAllText($target)

if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup
}

$newLine = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }

$playerMarker = "`tself.m_nPlayerID = 0"
if (-not $content.Contains($playerMarker)) {
    throw 'Hero Demo player initialization marker was not found; Dota may have updated the addon.'
}

$playerReplacement = @(
    "`tself.m_nPlayerID = 0",
    '',
    "`t-- DotaUtility visibility bridge: exact engine FoW state for local Hero Demo.",
    "`tself.m_bDotaUtilityLastVisibility = nil",
    "`tself.m_bDotaUtilityLastSentry = nil",
    "`tself.m_flDotaUtilityNextHeartbeat = 0",
    "`tself.m_nDotaUtilityRingParticle = nil",
    "`tself.m_hDotaUtilityRingHero = nil",
    "`tself.m_nDotaUtilitySentryParticle = nil",
    "`tself.m_hDotaUtilitySentryHero = nil"
) -join $newLine

$content = $content.Replace($playerMarker, $playerReplacement)

$mainThinkMarker = '-- Main Think'
if (-not $content.Contains($mainThinkMarker)) {
    throw 'Hero Demo GameThink marker was not found; Dota may have updated the addon.'
}

$bridgeFunction = @(
    '-- DotaUtility local visibility bridge',
    '-----------------------------------------------------------------------------',
    'function CHeroDemo:ClearDotaUtilityVisibilityRing()',
    "`tif self.m_nDotaUtilityRingParticle ~= nil and ParticleManager ~= nil then",
    "`t`tParticleManager:DestroyParticle( self.m_nDotaUtilityRingParticle, true )",
    "`t`tParticleManager:ReleaseParticleIndex( self.m_nDotaUtilityRingParticle )",
    "`tend",
    "`tself.m_nDotaUtilityRingParticle = nil",
    "`tself.m_hDotaUtilityRingHero = nil",
    'end',
    '',
    'function CHeroDemo:DrawDotaUtilityVisibilityRing( hHero, bVisible )',
    "`tif bVisible == false or hHero == nil or hHero:IsNull() then",
    "`t`tself:ClearDotaUtilityVisibilityRing()",
    "`t`treturn",
    "`tend",
    "`tif self.m_nDotaUtilityRingParticle ~= nil and self.m_hDotaUtilityRingHero == hHero then",
    "`t`treturn",
    "`tend",
    "`tself:ClearDotaUtilityVisibilityRing()",
    "`tif ParticleManager == nil or ParticleManager.CreateParticle == nil then",
    "`t`treturn",
    "`tend",
    "`tlocal nFXIndex = ParticleManager:CreateParticle( `"particles/ui_mouseactions/range_display.vpcf`", PATTACH_ABSORIGIN_FOLLOW, hHero )",
    "`tif nFXIndex ~= nil and nFXIndex >= 0 then",
    "`t`tParticleManager:SetParticleControl( nFXIndex, 1, Vector( 250, 0, 0 ) )",
    "`t`tself.m_nDotaUtilityRingParticle = nFXIndex",
    "`t`tself.m_hDotaUtilityRingHero = hHero",
    "`tend",
    'end',
    '',
    'function CHeroDemo:ClearDotaUtilitySentryParticle()',
    "`tif self.m_nDotaUtilitySentryParticle ~= nil and ParticleManager ~= nil then",
    "`t`tParticleManager:DestroyParticle( self.m_nDotaUtilitySentryParticle, true )",
    "`t`tParticleManager:ReleaseParticleIndex( self.m_nDotaUtilitySentryParticle )",
    "`tend",
    "`tself.m_nDotaUtilitySentryParticle = nil",
    "`tself.m_hDotaUtilitySentryHero = nil",
    'end',
    '',
    'function CHeroDemo:IsInDotaUtilityEnemySentryRange( hHero )',
    "`tif hHero == nil or hHero:IsNull() then",
    "`t`treturn false",
    "`tend",
    "`tif hHero.IsAlive ~= nil and not hHero:IsAlive() then",
    "`t`treturn false",
    "`tend",
    "`tlocal nHeroTeam = hHero:GetTeamNumber()",
    "`tlocal vOrigin = hHero:GetAbsOrigin()",
    "`tif hHero.FindAllModifiersByName ~= nil then",
    "`t`tlocal tMods = hHero:FindAllModifiersByName( `"modifier_truesight`" )",
    "`t`tif tMods ~= nil then",
    "`t`t`tfor _, hMod in pairs( tMods ) do",
    "`t`t`t`tif hMod ~= nil and not hMod:IsNull() and hMod.GetCaster ~= nil then",
    "`t`t`t`t`tlocal hCaster = hMod:GetCaster()",
    "`t`t`t`t`tif hCaster ~= nil and not hCaster:IsNull() and hCaster.GetTeamNumber ~= nil and hCaster:GetTeamNumber() ~= nHeroTeam then",
    "`t`t`t`t`t`treturn true",
    "`t`t`t`t`tend",
    "`t`t`t`tend",
    "`t`t`tend",
    "`t`tend",
    "`tend",
    "`tif Entities ~= nil and Entities.FindAllInSphere ~= nil then",
    "`t`tlocal tEntities = Entities:FindAllInSphere( vOrigin, 1050 )",
    "`t`tif tEntities ~= nil then",
    "`t`t`tfor _, hEnt in pairs( tEntities ) do",
    "`t`t`t`tif hEnt ~= nil and not hEnt:IsNull() and hEnt.GetTeamNumber ~= nil and hEnt:GetTeamNumber() ~= nHeroTeam then",
    "`t`t`t`t`tlocal bAlive = ( hEnt.IsAlive == nil ) or hEnt:IsAlive()",
    "`t`t`t`t`tif bAlive then",
    "`t`t`t`t`t`tlocal sClass = ( hEnt.GetClassname ~= nil and hEnt:GetClassname() ) or `"`"",
    "`t`t`t`t`t`tlocal sUnit = ( hEnt.GetUnitName ~= nil and hEnt:GetUnitName() ) or `"`"",
    "`t`t`t`t`t`tlocal bHasAura = ( hEnt.HasModifier ~= nil and hEnt:HasModifier( `"modifier_item_ward_true_sight`" ) )",
    "`t`t`t`t`t`tif sClass == `"npc_dota_ward_base_truesight`" or sUnit == `"npc_dota_sentry_wards`" or bHasAura then",
    "`t`t`t`t`t`t`treturn true",
    "`t`t`t`t`t`tend",
    "`t`t`t`t`tend",
    "`t`t`t`tend",
    "`t`t`tend",
    "`t`tend",
    "`tend",
    "`treturn false",
    'end',
    '',
    'function CHeroDemo:UpdateDotaUtilitySentryParticle( hHero, bInSentry )',
    "`tif bInSentry == false or hHero == nil or hHero:IsNull() then",
    "`t`tself:ClearDotaUtilitySentryParticle()",
    "`t`treturn",
    "`tend",
    "`tif self.m_nDotaUtilitySentryParticle ~= nil and self.m_hDotaUtilitySentryHero == hHero then",
    "`t`treturn",
    "`tend",
    "`tself:ClearDotaUtilitySentryParticle()",
    "`tif ParticleManager == nil or ParticleManager.CreateParticle == nil then",
    "`t`treturn",
    "`tend",
    "`tlocal nFXIndex = ParticleManager:CreateParticle( `"particles/items2_fx/true_sight_debuff.vpcf`", PATTACH_OVERHEAD_FOLLOW, hHero )",
    "`tif nFXIndex ~= nil and nFXIndex >= 0 then",
    "`t`tself.m_nDotaUtilitySentryParticle = nFXIndex",
    "`t`tself.m_hDotaUtilitySentryHero = hHero",
    "`tend",
    'end',
    '',
    'function CHeroDemo:UpdateDotaUtilityEnemyMinimapIcons( hHero )',
    "`tif not self.m_bDotaUtilityShowEnemyMinimap or hHero == nil or hHero:IsNull() or AddFOWViewer == nil then",
    "`t`treturn",
    "`tend",
    "`tlocal nHostTeam = hHero:GetTeamNumber()",
    "`tif Entities ~= nil and Entities.FindAllByClassname ~= nil then",
    "`t`tlocal tHeroes = Entities:FindAllByClassname( `"npc_dota_hero_*`" )",
    "`t`tif tHeroes ~= nil then",
    "`t`t`tfor _, hEnemy in pairs( tHeroes ) do",
    "`t`t`t`tif hEnemy ~= nil and not hEnemy:IsNull() and hEnemy.GetTeamNumber ~= nil and hEnemy:GetTeamNumber() ~= nHostTeam then",
    "`t`t`t`t`tlocal bAlive = ( hEnemy.IsAlive == nil ) or hEnemy:IsAlive()",
    "`t`t`t`t`tif bAlive then",
    "`t`t`t`t`t`tAddFOWViewer( nHostTeam, hEnemy:GetAbsOrigin(), 24, 0.25, false )",
    "`t`t`t`t`tend",
    "`t`t`t`tend",
    "`t`t`tend",
    "`t`tend",
    "`tend",
    'end',
    '',
    '--------------------------------------------------------------------------------',
    'function CHeroDemo:UpdateDotaUtilityVisibilityBridge()',
    "`tlocal hHero = PlayerResource:GetSelectedHeroEntity( self.m_nPlayerID )",
    "`tif hHero == nil then",
    "`t`tself:ClearDotaUtilityVisibilityRing()",
    "`t`tself:ClearDotaUtilitySentryParticle()",
    "`t`treturn",
    "`tend",
    '',
    "`tlocal bVisible = hHero:CanBeSeenByAnyOpposingTeam()",
    "`tlocal bInSentry = self:IsInDotaUtilityEnemySentryRange( hHero )",
    "`tself:DrawDotaUtilityVisibilityRing( hHero, bVisible )",
    "`tself:UpdateDotaUtilitySentryParticle( hHero, bInSentry )",
    "`tself:UpdateDotaUtilityEnemyMinimapIcons( hHero )",
    '',
    "`tlocal flNow = GameRules:GetGameTime()",
    "`tif self.m_bDotaUtilityLastVisibility == bVisible and self.m_bDotaUtilityLastSentry == bInSentry and flNow < self.m_flDotaUtilityNextHeartbeat then",
    "`t`treturn",
    "`tend",
    '',
    "`tself.m_bDotaUtilityLastVisibility = bVisible",
    "`tself.m_bDotaUtilityLastSentry = bInSentry",
    "`tself.m_flDotaUtilityNextHeartbeat = flNow + 0.5",
    '',
    "`tlocal requestFactory = CreateHTTPRequestScriptVM or CreateHTTPRequest",
    "`tif requestFactory == nil then",
    "`t`treturn",
    "`tend",
    '',
    "`tlocal nVisible = 0",
    "`tif bVisible then",
    "`t`tnVisible = 1",
    "`tend",
    '',
    "`tlocal nSentry = 0",
    "`tif bInSentry then",
    "`t`tnSentry = 1",
    "`tend",
    '',
    "`tlocal url = string.format(",
    "`t`t`"http://127.0.0.1:39077/dota-utility/vision?visible=%d&player=%d&sentry=%d`",",
    "`t`tnVisible,",
    "`t`tself.m_nPlayerID,",
    "`t`tnSentry )",
    '',
    "`tlocal request = requestFactory( `"GET`", url )",
    "`tif request ~= nil then",
    "`t`tif request.SetHTTPRequestAbsoluteTimeoutMS ~= nil then",
    "`t`t`trequest:SetHTTPRequestAbsoluteTimeoutMS( 100 )",
    "`t`tend",
    "`t`trequest:Send( function( result )",
    "`t`t`tif result ~= nil then",
    "`t`t`t`tself.m_bDotaUtilityShowEnemyMinimap = ( result.StatusCode == 200 ) or ( result.Body ~= nil and string.find( result.Body, `"minimap=1`", 1, true ) ~= nil )",
    "`t`t`tend",
    "`t`tend )",
    "`tend",
    'end',
    '',
    '--------------------------------------------------------------------------------',
    '-- Main Think'
) -join $newLine

$content = $content.Replace($mainThinkMarker, $bridgeFunction)

$thinkPattern = 'function CHeroDemo:GameThink\(\)\r?\n\treturn 0\.5\r?\nend'
$thinkReplacement = @(
    'function CHeroDemo:GameThink()',
    "`tself:UpdateDotaUtilityVisibilityBridge()",
    "`treturn 0.1",
    'end'
) -join $newLine

$updated = [Text.RegularExpressions.Regex]::Replace(
    $content,
    $thinkPattern,
    [Text.RegularExpressions.MatchEvaluator] { param($match) $thinkReplacement },
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)

if ($updated -eq $content) {
    throw 'Hero Demo GameThink body was not updated; Dota may have changed the function.'
}

[IO.File]::WriteAllText($target, $updated, [Text.UTF8Encoding]::new($false))

Write-Host "Installed DotaUtility Hero Demo visibility bridge (particle ring + overhead sentry particle): $target"
Write-Host "Backup: $backup"
Write-Host 'Restart/reload Hero Demo once before testing the bridge.'

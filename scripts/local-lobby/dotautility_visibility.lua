-- DotaUtility local-lobby visibility bridge.
-- Loaded by listenserver.cfg/lan_server.cfg with: script_reload_code dotautility_visibility.lua

if DotaUtilityVisibility == nil then
	DotaUtilityVisibility = {}
end

DotaUtilityVisibility.VERSION = "1.7.0"
DotaUtilityVisibility.LISTENER_URL = "http://127.0.0.1:39077/dota-utility/vision"
DotaUtilityVisibility.THINK_INTERVAL = 0.05
DotaUtilityVisibility.HEARTBEAT_INTERVAL = 0.50
DotaUtilityVisibility.MAX_PLAYER_ID = 23
DotaUtilityVisibility.RING_PARTICLE = "particles/ui_mouseactions/range_display.vpcf"
DotaUtilityVisibility.RING_RADIUS = 250
DotaUtilityVisibility.SENTRY_PARTICLE = "particles/items2_fx/true_sight_debuff.vpcf"
DotaUtilityVisibility.SENTRY_RADIUS = 1050
DotaUtilityVisibility.m_tLastVisibility = DotaUtilityVisibility.m_tLastVisibility or {}
DotaUtilityVisibility.m_tLastSentry = DotaUtilityVisibility.m_tLastSentry or {}
DotaUtilityVisibility.m_tNextHeartbeat = DotaUtilityVisibility.m_tNextHeartbeat or {}
DotaUtilityVisibility.m_nRingParticleIndex = DotaUtilityVisibility.m_nRingParticleIndex or nil
DotaUtilityVisibility.m_hRingHero = DotaUtilityVisibility.m_hRingHero or nil
DotaUtilityVisibility.m_nSentryParticleIndex = DotaUtilityVisibility.m_nSentryParticleIndex or nil
DotaUtilityVisibility.m_hSentryHero = DotaUtilityVisibility.m_hSentryHero or nil
DotaUtilityVisibility.m_bShowEnemyMinimap = DotaUtilityVisibility.m_bShowEnemyMinimap or false

function DotaUtilityVisibility:GetHostPlayerID()
	local hPlayer = nil
	if GetListenServerHost ~= nil then
		hPlayer = GetListenServerHost()
	end

	if hPlayer ~= nil and hPlayer.GetPlayerID ~= nil then
		local nPlayerID = hPlayer:GetPlayerID()
		if nPlayerID ~= nil and nPlayerID >= 0 then
			return nPlayerID
		end
	end

	return 0
end

function DotaUtilityVisibility:GetPlayerHero( nPlayerID )
	if PlayerResource == nil or PlayerResource.GetSelectedHeroEntity == nil then
		return nil
	end

	local hHero = PlayerResource:GetSelectedHeroEntity( nPlayerID )
	if hHero == nil or hHero:IsNull() then
		return nil
	end

	return hHero
end

function DotaUtilityVisibility:ClearRingParticle()
	if self.m_nRingParticleIndex ~= nil and ParticleManager ~= nil then
		ParticleManager:DestroyParticle( self.m_nRingParticleIndex, true )
		ParticleManager:ReleaseParticleIndex( self.m_nRingParticleIndex )
	end

	self.m_nRingParticleIndex = nil
	self.m_hRingHero = nil
end

function DotaUtilityVisibility:UpdateRing( hHero, bVisible )
	if not bVisible or hHero == nil or hHero:IsNull() then
		self:ClearRingParticle()
		return
	end

	if self.m_nRingParticleIndex ~= nil and self.m_hRingHero == hHero then
		return
	end

	self:ClearRingParticle()

	if ParticleManager == nil or ParticleManager.CreateParticle == nil then
		return
	end

	local nFXIndex = ParticleManager:CreateParticle(
		self.RING_PARTICLE,
		PATTACH_ABSORIGIN_FOLLOW,
		hHero )
	if nFXIndex ~= nil and nFXIndex >= 0 then
		ParticleManager:SetParticleControl( nFXIndex, 1, Vector( self.RING_RADIUS, 0, 0 ) )
		self.m_nRingParticleIndex = nFXIndex
		self.m_hRingHero = hHero
	end
end

function DotaUtilityVisibility:ClearSentryParticle()
	if self.m_nSentryParticleIndex ~= nil and ParticleManager ~= nil then
		ParticleManager:DestroyParticle( self.m_nSentryParticleIndex, true )
		ParticleManager:ReleaseParticleIndex( self.m_nSentryParticleIndex )
	end

	self.m_nSentryParticleIndex = nil
	self.m_hSentryHero = nil
end

function DotaUtilityVisibility:IsInEnemySentryRange( hHero )
	if hHero == nil or hHero:IsNull() then
		return false
	end

	if hHero.IsAlive ~= nil and not hHero:IsAlive() then
		return false
	end

	local nHeroTeam = hHero:GetTeamNumber()
	local vOrigin = hHero:GetAbsOrigin()

	if hHero.FindAllModifiersByName ~= nil then
		local tMods = hHero:FindAllModifiersByName( "modifier_truesight" )
		if tMods ~= nil then
			for _, hMod in pairs( tMods ) do
				if hMod ~= nil and not hMod:IsNull() and hMod.GetCaster ~= nil then
					local hCaster = hMod:GetCaster()
					if hCaster ~= nil and not hCaster:IsNull() and hCaster.GetTeamNumber ~= nil and hCaster:GetTeamNumber() ~= nHeroTeam then
						return true
					end
				end
			end
		end
	end

	if Entities ~= nil and Entities.FindAllInSphere ~= nil then
		local tEntities = Entities:FindAllInSphere( vOrigin, self.SENTRY_RADIUS )
		if tEntities ~= nil then
			for _, hEnt in pairs( tEntities ) do
				if hEnt ~= nil and not hEnt:IsNull() and hEnt.GetTeamNumber ~= nil and hEnt:GetTeamNumber() ~= nHeroTeam then
					local bAlive = ( hEnt.IsAlive == nil ) or hEnt:IsAlive()
					if bAlive then
						local sClass = ( hEnt.GetClassname ~= nil and hEnt:GetClassname() ) or ""
						local sUnit = ( hEnt.GetUnitName ~= nil and hEnt:GetUnitName() ) or ""
						local bHasAura = ( hEnt.HasModifier ~= nil and hEnt:HasModifier( "modifier_item_ward_true_sight" ) )
						if sClass == "npc_dota_ward_base_truesight" or sUnit == "npc_dota_sentry_wards" or bHasAura then
							return true
						end
					end
				end
			end
		end
	end

	return false
end

function DotaUtilityVisibility:UpdateSentryParticle( hHero, bInSentry )
	if not bInSentry or hHero == nil or hHero:IsNull() then
		self:ClearSentryParticle()
		return
	end

	if self.m_nSentryParticleIndex ~= nil and self.m_hSentryHero == hHero then
		return
	end

	self:ClearSentryParticle()

	if ParticleManager == nil or ParticleManager.CreateParticle == nil then
		return
	end

	local nFXIndex = ParticleManager:CreateParticle(
		self.SENTRY_PARTICLE,
		PATTACH_OVERHEAD_FOLLOW,
		hHero )
	if nFXIndex ~= nil and nFXIndex >= 0 then
		self.m_nSentryParticleIndex = nFXIndex
		self.m_hSentryHero = hHero
	end
end

function DotaUtilityVisibility:SendHeartbeat( bVisible, bInSentry, nPlayerID )
	local requestFactory = CreateHTTPRequestScriptVM or CreateHTTPRequest
	if requestFactory == nil then
		return
	end

	local nVisible = 0
	if bVisible then
		nVisible = 1
	end

	local nSentry = 0
	if bInSentry then
		nSentry = 1
	end

	local sUrl = string.format(
		"%s?visible=%d&player=%d&sentry=%d",
		self.LISTENER_URL,
		nVisible,
		nPlayerID,
		nSentry )

	local request = requestFactory( "GET", sUrl )
	if request ~= nil then
		if request.SetHTTPRequestAbsoluteTimeoutMS ~= nil then
			request:SetHTTPRequestAbsoluteTimeoutMS( 100 )
		end
		request:Send( function( result )
			if result ~= nil then
				self.m_bShowEnemyMinimap = ( result.StatusCode == 200 )
					or ( result.Body ~= nil and string.find( result.Body, "minimap=1", 1, true ) ~= nil )
			end
		end )
	end
end

function DotaUtilityVisibility:UpdateEnemyMinimapIcons( hHostHero )
	if not self.m_bShowEnemyMinimap or hHostHero == nil or hHostHero:IsNull() or AddFOWViewer == nil then
		return
	end

	local nHostTeam = hHostHero:GetTeamNumber()
	if Entities ~= nil and Entities.FindAllByClassname ~= nil then
		local tHeroes = Entities:FindAllByClassname( "npc_dota_hero_*" )
		if tHeroes ~= nil then
			for _, hEnemy in pairs( tHeroes ) do
				if hEnemy ~= nil and not hEnemy:IsNull() and hEnemy.GetTeamNumber ~= nil and hEnemy:GetTeamNumber() ~= nHostTeam then
					local bAlive = ( hEnemy.IsAlive == nil ) or hEnemy:IsAlive()
					if bAlive then
						AddFOWViewer( nHostTeam, hEnemy:GetAbsOrigin(), 24, 0.14, false )
					end
				end
			end
		end
	end
end

function DotaUtilityVisibility:UpdatePlayer( nPlayerID, hHero, nHostPlayerID, flNow )
	local bVisible = hHero:CanBeSeenByAnyOpposingTeam()
	local bInSentry = self:IsInEnemySentryRange( hHero )

	if nPlayerID == nHostPlayerID then
		self:UpdateRing( hHero, bVisible )
		self:UpdateSentryParticle( hHero, bInSentry )
		self:UpdateEnemyMinimapIcons( hHero )
	end

	local bLastVisible = self.m_tLastVisibility[ nPlayerID ]
	local bLastSentry = self.m_tLastSentry[ nPlayerID ]
	local flNext = self.m_tNextHeartbeat[ nPlayerID ] or 0
	if bLastVisible ~= bVisible or bLastSentry ~= bInSentry or flNow >= flNext then
		self.m_tLastVisibility[ nPlayerID ] = bVisible
		self.m_tLastSentry[ nPlayerID ] = bInSentry
		self.m_tNextHeartbeat[ nPlayerID ] = flNow + self.HEARTBEAT_INTERVAL
		self:SendHeartbeat( bVisible, bInSentry, nPlayerID )
	end
end

function DotaUtilityVisibility:Think()
	local nHostPlayerID = self:GetHostPlayerID()
	local flNow = GameRules:GetGameTime()

	for nPlayerID = 0, self.MAX_PLAYER_ID do
		local hHero = self:GetPlayerHero( nPlayerID )
		if hHero ~= nil then
			self:UpdatePlayer( nPlayerID, hHero, nHostPlayerID, flNow )
		end
	end

	return self.THINK_INTERVAL
end

function DotaUtilityVisibility:Start()
	local hGameMode = GameRules:GetGameModeEntity()
	if hGameMode == nil then
		print( "[DotaUtility] visibility bridge could not find GameModeEntity" )
		return
	end

	hGameMode:SetContextThink(
		"DotaUtilityVisibilityThink",
		function()
			return DotaUtilityVisibility:Think()
		end,
		0.01 )

	print( "[DotaUtility] local-lobby visibility bridge loaded v" .. self.VERSION )
end

DotaUtilityVisibility:Start()

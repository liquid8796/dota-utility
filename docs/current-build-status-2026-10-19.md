# Current Dota build checkpoint — 2026-10-19

## Scope

This checkpoint records the full binary audit, schema offset update, live Arcade session trace, and test suite verification performed on `2026-10-19` using **Jarvis MCP** and the local **REA CLI** (`rea`).

The production memory accessor (`Win32ProcessMemoryAccessor`) remains strictly external and read-only (`PROCESS_VM_READ`, `PROCESS_QUERY_INFORMATION`, `PROCESS_QUERY_LIMITED_INFORMATION`, and `SYNCHRONIZE`). It requests no process write, injection, remote-thread, or debugging rights.

## Installed build identity

The audited Dota 2 process was `dota2.exe` (PID `1412`, x64) running from:

`D:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe`

| Module | SHA-256 | PE timestamp | `SizeOfImage` |
| --- | --- | --- | --- |
| `client.dll` | `dfec84e4e3b89518dcd78c308e27db35d04c7e286d67a01166eecd5a39ceefd6` | `1792097557` (`0x6AD16115`) | `0x6E3A000` |
| `server.dll` | `079ea6b8f2629f3e66b73ae62ce1e4034b12e19c316ca279e66ad1bce0f5fec3` | `1792097571` (`0x6AD16123`) | `0x5B13000` |
| `engine2.dll` | `afd11eadb99e7296bd56d0c5b77aa979914e73a549e8f5e71cd6c1a0334d15c4` | `1791316574` (`0x6AC5765E`) | `0x907000` |

## Signature resolution audit

All six RIP-relative instruction signatures in `DotaAddressResolver` and `DotaHeroScreenProjector` resolve uniquely (`matchCount = 1`) on the `2026-10-19` binaries:

| Pattern | Module | Match RVA | Target RVA |
| --- | --- | --- | --- |
| `EntitySystemPattern` | `client.dll` | `0x3692709` | `0x65A92D8` |
| `EntitySystemFallbackPattern` | `client.dll` | `0x17C897B` | `0x65A92D8` |
| `TeamDataHandleArrayPattern` | `client.dll` | `0x1A8C320` | `0x5B35390` |
| `LocalControllerArrayPattern` | `client.dll` | `0x171EFA0` | `0x61E7D48` |
| `ViewMatrixPattern` | `client.dll` | `0x19DB493` | `0x6220CB0` |
| `NetworkGameClientPattern` | `engine2.dll` | `0x1CE330` | `0x8B3C60` |

## Schema field offset audit (`client.dll` & `server.dll`)

Inspection of the Source 2 schema field descriptors in `client.dll` (`dfec84e4...`) established why `LocalPlayerContextReader` had previously rejected newer October builds: `CBasePlayerController` / `C_DOTAPlayerController` fields and `C_BaseModelEntity::m_iTeamVisibilityBitmask` shifted by `+0x8`, while core entity, scene-node, and `C_DOTA_DataNonSpectator::m_bNPCVisibleState` offsets remained unchanged.

### `client.dll` field descriptors

| Field | Previous Offset | Audited `2026-10-19` Offset | Descriptor RVA |
| --- | --- | --- | --- |
| `CBasePlayerController::m_bIsLocalPlayerController` | `0x770` | `0x778` | `0x5B1C6A0` |
| `CBasePlayerController::m_bIsHLTV` | `0x6D0` | `0x6D8` | `0x5B1C600` |
| `C_DOTAPlayerController::m_nPlayerID` | `0x908` | `0x910` | `0x5B3AB10` |
| `C_DOTAPlayerController::m_hAssignedHero` | `0x90C` | `0x914` | `0x5B3AB30` |
| `C_BaseEntity::m_iTeamNum` | `0x3E7` | `0x3E7` (unchanged) | `0x597D2E0` |
| `C_BaseEntity::m_pGameSceneNode` | `0x330` | `0x330` (unchanged) | `0x597CE20` |
| `C_BaseModelEntity::m_iTeamVisibilityBitmask` | `0x780` | `0x788` | `0x597EB40` |
| `CGameSceneNode::m_vecAbsOrigin` | `0xD8` | `0xD8` (unchanged) | `0x5984310` |
| `C_DOTA_DataNonSpectator::m_bNPCVisibleState` | `0xE58` | `0xE58` (unchanged) | `0x5B331F0` |
| `C_DOTA_BaseNPC::m_flInvisibilityLevel` | `0xC64` | `0xD5C` | `0x5B36910` |
| `C_DOTA_BaseNPC::m_ModifierManager` | `0xD38` | `0xE30` | embedded |
| `C_DOTA_BaseNPC::m_nUnitState64` | `0x11C0` | `0x12B8` | `0x5B36D90` |
| `C_DOTA_BaseNPC_Hero::m_iPlayerID` | `0x1A68` | `0x1B68` | `0x5B38590` |

### `server.dll` field descriptors and team-data vtables

- `CBaseEntity::m_iTeamNum`: `0x344` (descriptor RVA `0x48EB370`, unchanged)
- `CBaseModelEntity::m_iTeamVisibilityBitmask`: `0x544` (descriptor RVA `0x48E77C0`, unchanged)
- `CDOTA_DataNonSpectator::m_bNPCVisibleState`: `0xD00` (descriptor RVA `0x4AD86D0`, unchanged)
- `C_DOTA_DataRadiant` (`client.dll` vtable): `0x490D660`
- `C_DOTA_DataDire` (`client.dll` vtable): `0x490DD88`
- `CDOTA_DataRadiant` (`server.dll` vtable `0x3DAE828`) and `CDOTA_DataDire` (`server.dll` vtable `0x3DAEF90`): recipient selector at virtual slot `95` (`0x1FB8180` / `0x1FB80C0`).

## Production updates

1. **`DotaOffsets.Current`**: Updated `PlayerControllerIsLocalPlayer` (`0x778`), `PlayerControllerIsHltv` (`0x6D8`), `PlayerControllerPlayerId` (`0x910`), `PlayerControllerAssignedHero` (`0x914`), and `BaseModelEntityTeamVisibilityBitmask` (`0x788`).
2. **`DotaBuildCompatibilityGate`**: Updated `SupportedClientBuilds` to `dfec84e4e3b89518dcd78c308e27db35d04c7e286d67a01166eecd5a39ceefd6` (`2026-10-19 local-controller/replication profile`), keeping `DotaOffsets.Current` and the allowlist strictly paired.
3. **UI and documentation alignment**: Ensured `MainWindow.xaml` and `docs/design.md` consistently describe external read-only (`ReadProcessMemory`) access without write, injection, or hook claims.

## Live Arcade session trace (`dota2.exe` PID `1412`)

During the active Arcade session on `dota2.exe` (PID `1412`):

- **Native local controller (`array[0]`)**: `0x50120636000` (`.?AVC_DOTAPlayerController@@`), entity `#8`, handle `0x013C8008`, `m_bIsLocalPlayerController = 1`, `m_bIsHLTV = 0`, `m_nPlayerID = 9`, `m_iTeamNum = 3` (Dire), `m_hAssignedHero = 0x0143814D`.
- **Assigned local hero**: `0x500ED18C000` (`.?AVC_DOTA_Unit_Hero_PhantomLancer@@`), entity `#333` (`0x14D`), `m_iTeamNum = 3` (Dire), `m_iPlayerID = 9`, world origin `[1193.94, -198.69, 1664.00]`, projected viewport coordinates `[1435.68, 513.80]` on `1920x1080`.
- **Team-data handle table**:
  - Slot `3` (Dire, own team): handle `0x01C68054` -> `0x5012AE88000` (`.?AVC_DOTA_DataDire@@`, `m_iTeamNum = 3`, `m_bNPCVisibleState` word `5` = `0x02658C0000102000`, `localHeroBit = 1`).
  - Slot `2` (Radiant, opposing team): `0xFFFFFFFF` (unreplicated to this Dire client context).
- **End-of-match / dashboard transition**: Running `.scratch/CurrentContext` across the end of the Arcade match verified fail-closed transitions first to `"The local controller has no valid playing-team Dota PlayerID yet."` and then to `"The native local controller is unavailable; Dota may be at the dashboard or loading."` (`state=Unknown`, `hero=0x0`).

## Verification

- `dotnet test DotaUtility.slnx -c Release --nologo --tl:off`: **171/171 unit tests passed** (`DotaUtility.Memory.Tests`, `DotaUtility.Application.Tests`, `DotaUtility.Dota2.Tests`, `DotaUtility.App.Tests`) with **0 warnings and 0 errors**.
- `python -m unittest discover -s tests/research -v`: **18/18 tests passed**.

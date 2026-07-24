using System.Collections.Generic;
using ArachNOPE.Helpers;
using HarmonyLib;
using ProjectM;
using ProjectM.Hybrid;

using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace ArachNOPE.Patches;

[HarmonyPatch]
internal static class SpiderHidePatch
{
    struct TrackedSpider
    {
        public Entity SpiderEntity;
        public Entity FurnitureEntity;
        public bool FurnitureSpawned;
    }

    static bool _emReady;
    internal static World _clientWorld;
    internal static PrefabCollectionSystem _prefabCollectionSystem;
    static readonly Dictionary<int, TrackedSpider> _tracked = new();

    // Player-spider tracking (player entity -> furniture entity)
    internal static readonly Dictionary<Entity, Entity> _playerSpiders = new();

    // Tracks which tracked spider players are currently burrowed (furniture hidden)
    static readonly Dictionary<Entity, bool> _playerBurrowed = new();

    // Tracks buff entities we've already processed (to avoid duplicates in trigger)
    static readonly HashSet<Entity> _seenPlayerBuffs = new();

    // Maps buff entity -> player entity for cleanup on buff removal
    static readonly Dictionary<Entity, Entity> _buffToPlayer = new();

    // System access
    static HybridModelSystem _hybridModelSystem;

    // Track disabled player GameObjects for restoration
    static readonly Dictionary<Entity, GameObject> _disabledPlayerModels = new();
    // Players whose model hasn't been hidden yet (retry in SyncPositions)
    static readonly HashSet<Entity> _pendingHidePlayers = new();

    // Frame throttle counter for world-change stabilization
    static int _framesSinceWorldChange;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.OnDestroy))]
    static void OnClientDisconnect(ClientBootstrapSystem __instance)
    {
        ResetState();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HybridEquipmentSystem), nameof(HybridEquipmentSystem.OnUpdate))]
    static void OnUpdatePostfix(HybridEquipmentSystem __instance)
    {
        // Validate from the FRESH patch parameter every call
        try
        {
            if (__instance.World == null || !__instance.World.IsCreated)
            {
                if (_emReady) ResetState();
                return;
            }
        }
        catch
        {
            ResetState();
            return;
        }

        // World changed (reconnect/scene load) — reset everything and wait
        // for the new world to stabilize before re-initializing
        if (_emReady && __instance.World != _clientWorld)
        {
            ResetState();
            _framesSinceWorldChange = 0;
            return;
        }

        // Initialize on a new world after a 3-frame throttle to let the
        // world finish constructing before we query its systems
        if (!_emReady)
        {
            _framesSinceWorldChange++;
            if (_framesSinceWorldChange < 3) return;

            try
            {
                _clientWorld = __instance.World;
                TryGetPrefabCollectionSystem();
                TryGetSystems();
                _emReady = true;
            }
            catch
            {
                ResetState();
                _framesSinceWorldChange = 0;
                return;
            }
        }

        // Safety-net: throttled buff scan every 3 frames (catches server-replicated
        // spider buffs that bypass BuffSystem_Spawn_Client)
        _buffScanCounter++;
        if (_buffScanCounter >= 3)
        {
            _buffScanCounter = 0;
            try { SafetyScanBuffs(__instance.EntityManager); }
            catch { ResetState(); }
        }

        // Position sync for tracked furniture (runs every frame, early-exits if empty)
        SyncPositions(__instance.EntityManager);

        // NPC spider scan (every frame, narrow cached query)
        try
        {
            ScanSpiders(__instance.EntityManager);
        }
        catch
        {
            ResetState();
        }
    }

    static void SafetyScanBuffs(EntityManager em)
    {
        // Init cached query on first call
        if (!_buffQueryCreated)
        {
            _buffQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabGUID>(),
                ComponentType.ReadOnly<Buff>());
            _buffQueryCreated = true;
        }

        if (_clientWorld == null || !_clientWorld.IsCreated)
        {
            ResetState();
            return;
        }

        NativeArray<Entity> buffs = default;
        try
        {
            buffs = _buffQuery.ToEntityArray(Allocator.Temp);

            // Build set of currently-active spider buff entities and burrow buffs
            var activeBuffs = new HashSet<Entity>();
            var activeBurrows = new Dictionary<Entity, bool>();

            foreach (var buffEntity in buffs)
            {
                if (!em.HasComponent<PrefabGUID>(buffEntity)) continue;
                int guidHash = em.GetComponentData<PrefabGUID>(buffEntity).GuidHash;
                if (!SpiderLookup.IsSpiderBuff(guidHash) && !SpiderLookup.IsBurrowBuff(guidHash)) continue;

                Entity target = em.GetComponentData<Buff>(buffEntity).Target;
                if (!em.Exists(target)) continue;
                if (!em.HasComponent<PlayerCharacter>(target)) continue;

                if (SpiderLookup.IsBurrowBuff(guidHash))
                {
                    activeBurrows[target] = true;
                    continue;
                }

                activeBuffs.Add(buffEntity);

                if (!_seenPlayerBuffs.Add(buffEntity)) continue;

                if (_playerSpiders.ContainsKey(target)) continue;

                _buffToPlayer[buffEntity] = target;
                _pendingHidePlayers.Add(target);

                Entity furnitureEntity = Entity.Null;
                bool spawned = TrySpawnFurniture(em, target, guidHash, out furnitureEntity);
                if (spawned)
                    _playerSpiders[target] = furnitureEntity;
            }

            // Handle burrow state changes for tracked spider players.
            // Use separate lists to avoid modifying dictionaries during iteration.
            var burrowingPlayers = new List<Entity>();
            var unburrowingPlayers = new List<Entity>();

            foreach (var kvp in _playerSpiders)
            {
                if (activeBurrows.ContainsKey(kvp.Key))
                    burrowingPlayers.Add(kvp.Key);
            }

            foreach (var playerEntity in _playerBurrowed.Keys)
            {
                if (!activeBurrows.ContainsKey(playerEntity))
                    unburrowingPlayers.Add(playerEntity);
            }

            foreach (var playerEntity in burrowingPlayers)
            {
                if (_playerSpiders.TryGetValue(playerEntity, out var furnitureEntity))
                {
                    try
                    {
                        if (em.Exists(furnitureEntity))
                            em.DestroyEntity(furnitureEntity);
                    }
                    catch { }
                    _playerSpiders.Remove(playerEntity);
                }
                _playerBurrowed[playerEntity] = true;
            }

            foreach (var playerEntity in unburrowingPlayers)
            {
                _playerBurrowed.Remove(playerEntity);

                int spiderBuffHash = SpiderLookup.AB_Shapeshift_Spider_Buff;
                Entity newFurniture = Entity.Null;
                bool spawned = TrySpawnFurniture(em, playerEntity, spiderBuffHash, out newFurniture);
                if (spawned)
                {
                    _playerSpiders[playerEntity] = newFurniture;
                    _pendingHidePlayers.Add(playerEntity);
                }
            }

            // Check for removed spider buffs by comparing against active set
            if (_buffToPlayer.Count > 0)
            {
                var deadBuffs = new List<Entity>();
                foreach (var kvp in _buffToPlayer)
                {
                    if (!activeBuffs.Contains(kvp.Key))
                        deadBuffs.Add(kvp.Key);
                }

                foreach (var buffEntity in deadBuffs)
                {
                    var playerEntity = _buffToPlayer[buffEntity];

                    // Clean up burrow tracking if present
                    _playerBurrowed.Remove(playerEntity);

                    if (_playerSpiders.TryGetValue(playerEntity, out var furnitureEntity))
                    {
                        try
                        {
                            if (em.Exists(furnitureEntity))
                                em.DestroyEntity(furnitureEntity);
                        }
                        catch { }
                        _playerSpiders.Remove(playerEntity);
                    }

                    RestorePlayerVisual(playerEntity);
                    _buffToPlayer.Remove(buffEntity);
                    _seenPlayerBuffs.Remove(buffEntity);
                }
            }
        }
        catch { }
        finally
        {
            if (buffs.IsCreated) buffs.Dispose();
        }
    }

    static void ScanSpiders(EntityManager em)
    {
        // Init cached NPC query on first call (narrow: creatures only)
        if (!_npcQueryCreated)
        {
            _npcQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabGUID>(),
                ComponentType.ReadOnly<Health>(),
                ComponentType.ReadOnly<Team>());
            _npcQueryCreated = true;
        }

        // Safety guard: if the world was destroyed since our last check,
        // the query is stale and ToEntityArray will AV crash
        if (_clientWorld == null || !_clientWorld.IsCreated)
        {
            ResetState();
            return;
        }

        NativeArray<Entity> entities = default;
        try
        {
            entities = _npcQuery.ToEntityArray(Allocator.Temp);

            foreach (var entity in entities)
            {
                if (!em.HasComponent<PrefabGUID>(entity)) continue;
                int guidHash = em.GetComponentData<PrefabGUID>(entity).GuidHash;
                if (!SpiderLookup.IsSpider(guidHash)) continue;

                int idx = entity.Index;
                if (_tracked.TryGetValue(idx, out var existing))
                {
                    if (existing.SpiderEntity.Version == entity.Version) continue;
                    if (existing.FurnitureSpawned && _clientWorld != null && _clientWorld.IsCreated)
                    {
                        var cem = _clientWorld.EntityManager;
                        if (cem.Exists(existing.FurnitureEntity))
                            cem.DestroyEntity(existing.FurnitureEntity);
                    }
                    _tracked.Remove(idx);
                }

                // NPC spiders are not managed by HybridModelSystem (map has
                // only player equipment). The only working approach is to
                // scale hitbox to zero. This may affect bite targeting.
                if (em.HasComponent<LocalTransform>(entity))
                {
                    var t = em.GetComponentData<LocalTransform>(entity);
                    t.Scale = 0f;
                    em.SetComponentData(entity, t);
                }

                Entity furnitureEntity = Entity.Null;
                bool spawned = false;
                if (_prefabCollectionSystem != null)
                    spawned = TrySpawnFurniture(em, entity, guidHash, out furnitureEntity);

                _tracked[idx] = new TrackedSpider
                {
                    SpiderEntity = entity,
                    FurnitureEntity = furnitureEntity,
                    FurnitureSpawned = spawned
                };
            }

            var toRemove = new List<int>();
            foreach (var kvp in _tracked)
            {
                if (!IsSpiderAlive(em, kvp.Value.SpiderEntity))
                {
                    if (kvp.Value.FurnitureSpawned && _clientWorld != null && _clientWorld.IsCreated)
                    {
                        var cem = _clientWorld.EntityManager;
                        if (cem.Exists(kvp.Value.FurnitureEntity))
                            cem.DestroyEntity(kvp.Value.FurnitureEntity);
                    }
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
                _tracked.Remove(key);
        }
        finally
        {
            try { if (entities.IsCreated) entities.Dispose(); } catch { }
        }
    }

    static bool IsSpiderAlive(EntityManager em, Entity spider)
    {
        try
        {
            if (!em.Exists(spider)) return false;
            if (em.HasComponent<Health>(spider) && em.GetComponentData<Health>(spider).IsDead) return false;
            if (em.HasComponent<DestroyState>(spider) && em.GetComponentData<DestroyState>(spider).Value != DestroyStateEnum.NotDestroyed) return false;
            return true;
        }
        catch { return false; }
    }

    static bool HidePlayerVisual(EntityManager em, Entity playerEntity)
    {
        if (!em.Exists(playerEntity))
        {
            Plugin.Logger.LogWarning($"[ArachNOPE] HidePlayerVisual: entity doesn't exist");
            return false;
        }

        if (_hybridModelSystem == null)
        {
            Plugin.Logger.LogError($"[ArachNOPE] HidePlayerVisual: HybridModelSystem is null");
            return false;
        }

        try
        {
            // Get the Entity → GameObject map
            var entityToGameObject = _hybridModelSystem.GetEntityToGameObjectMap();
            if (entityToGameObject == null || entityToGameObject.Count == 0)
            {
                Plugin.Logger.LogInfo($"[ArachNOPE] HidePlayerVisual: map empty (may not be populated yet), will retry");
                return false;
            }

            // Try to find the player's GameObject
            if (!entityToGameObject.TryGetValue(playerEntity, out var playerGo) || playerGo == null)
            {
                return false;
            }

            var renderers = playerGo.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].enabled = false;

            _disabledPlayerModels[playerEntity] = playerGo;
            return true;
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE] HidePlayerVisual error: {e.Message}");
            return false;
        }
    }

    internal static void RestorePlayerVisual(Entity playerEntity)
    {
        if (!_disabledPlayerModels.TryGetValue(playerEntity, out var playerGo) || playerGo == null)
        {
            _disabledPlayerModels.Remove(playerEntity);
            return;
        }

        try
        {
            var renderers = playerGo.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].enabled = true;
            _disabledPlayerModels.Remove(playerEntity);
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE] RestorePlayerVisual error: {e.Message}");
        }
    }

    // Cached query for buff detection (created once, reused)
    static EntityQuery _buffQuery;
    static bool _buffQueryCreated;
    static int _buffScanCounter; // throttle for safety-net scan

    // Cached query for NPC spider detection (narrower: creatures only)
    static EntityQuery _npcQuery;
    static bool _npcQueryCreated;
    static int _rehideCounter;

    // ── Trigger: buff applied (client-side) ──────────────────────────────
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuffSystem_Spawn_Client), nameof(BuffSystem_Spawn_Client.OnUpdate))]
    static void OnBuffApply(BuffSystem_Spawn_Client __instance)
    {
        if (!_emReady) return;

        EntityManager em = __instance.EntityManager;

        // Init cached query on first call
        if (!_buffQueryCreated)
        {
            _buffQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabGUID>(),
                ComponentType.ReadOnly<Buff>());
            _buffQueryCreated = true;
        }

        NativeArray<Entity> newBuffs = default;
        try
        {
            newBuffs = _buffQuery.ToEntityArray(Allocator.Temp);

            foreach (var buffEntity in newBuffs)
            {
                if (!em.HasComponent<PrefabGUID>(buffEntity)) continue;
                int guidHash = em.GetComponentData<PrefabGUID>(buffEntity).GuidHash;

                if (SpiderLookup.IsBurrowBuff(guidHash))
                {
                    Entity burrowTarget = em.GetComponentData<Buff>(buffEntity).Target;
                    if (!em.Exists(burrowTarget)) continue;
                    if (!em.HasComponent<PlayerCharacter>(burrowTarget)) continue;

                    if (_playerBurrowed.ContainsKey(burrowTarget)) continue;

                    if (_playerSpiders.TryGetValue(burrowTarget, out var furnEntity))
                    {
                        try
                        {
                            if (em.Exists(furnEntity))
                                em.DestroyEntity(furnEntity);
                        }
                        catch { }
                        _playerSpiders.Remove(burrowTarget);
                    }
                    _playerBurrowed[burrowTarget] = true;
                    continue;
                }

                if (!SpiderLookup.IsSpiderBuff(guidHash)) continue;

                Entity target = em.GetComponentData<Buff>(buffEntity).Target;
                if (!em.Exists(target)) continue;
                if (!em.HasComponent<PlayerCharacter>(target)) continue;

                if (!_seenPlayerBuffs.Add(buffEntity)) continue;

                _buffToPlayer[buffEntity] = target;

                if (_playerSpiders.ContainsKey(target))
                {
                    _pendingHidePlayers.Add(target);
                    continue;
                }

                _pendingHidePlayers.Add(target);

                Entity furnitureEntity = Entity.Null;
                bool spawned = TrySpawnFurniture(em, target, guidHash, out furnitureEntity);

                if (spawned)
                    _playerSpiders[target] = furnitureEntity;
            }
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE] OnBuffApply error: {e.Message}");
        }
        finally
        {
            if (newBuffs.IsCreated) newBuffs.Dispose();
        }
    }

    static void SyncPositions(EntityManager em)
    {
        // Early exit — no tracked entities at all
        if (_pendingHidePlayers.Count == 0 && _tracked.Count == 0 && _playerSpiders.Count == 0)
            return;
        // Retry hiding player models that failed on first attempt
        if (_pendingHidePlayers.Count > 0)
        {
            var retryToRemove = new List<Entity>();
            foreach (var playerEntity in _pendingHidePlayers)
            {
                try
                {
                    if (!em.Exists(playerEntity))
                    {
                        retryToRemove.Add(playerEntity);
                        continue;
                    }

                    bool hidden = HidePlayerVisual(em, playerEntity);
                    if (hidden)
                        retryToRemove.Add(playerEntity);
                }
                catch { retryToRemove.Add(playerEntity); }
            }
            foreach (var e in retryToRemove)
                _pendingHidePlayers.Remove(e);
        }

        // Periodic re-hide every 5 frames (catches burrow unburrow, equipment
        // changes, or any game-side renderer re-enable)
        _rehideCounter++;
        if (_rehideCounter >= 5)
        {
            _rehideCounter = 0;
            foreach (var kvp in _disabledPlayerModels)
            {
                try
                {
                    var playerGo = kvp.Value;
                    if (playerGo == null) continue;
                    var renderers = playerGo.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].enabled = false;
                }
                catch { }
            }
        }

        foreach (var kvp in _tracked)
        {
            var tracked = kvp.Value;
            if (!tracked.FurnitureSpawned) continue;

            try
            {
                if (!em.Exists(tracked.SpiderEntity) || !em.Exists(tracked.FurnitureEntity)) continue;

                if (em.HasComponent<Translation>(tracked.SpiderEntity) && em.HasComponent<Translation>(tracked.FurnitureEntity))
                {
                    var pos = em.GetComponentData<Translation>(tracked.SpiderEntity);
                    em.SetComponentData(tracked.FurnitureEntity, pos);
                }

                if (em.HasComponent<Rotation>(tracked.SpiderEntity) && em.HasComponent<Rotation>(tracked.FurnitureEntity))
                {
                    var rot = em.GetComponentData<Rotation>(tracked.SpiderEntity);
                    em.SetComponentData(tracked.FurnitureEntity, rot);
                }
            }
            catch { break; }
        }

        // Sync player furniture with offset (diagnostic: separated from player)
        foreach (var kvp in _playerSpiders)
        {
            try
            {
                var playerEntity = kvp.Key;
                var furnitureEntity = kvp.Value;
                if (!em.Exists(playerEntity) || !em.Exists(furnitureEntity)) continue;

                if (em.HasComponent<Translation>(playerEntity) && em.HasComponent<Translation>(furnitureEntity))
                {
                    var pos = em.GetComponentData<Translation>(playerEntity);
                    em.SetComponentData(furnitureEntity, pos);
                }

                if (em.HasComponent<Rotation>(playerEntity) && em.HasComponent<Rotation>(furnitureEntity))
                {
                    var rot = em.GetComponentData<Rotation>(playerEntity);
                    em.SetComponentData(furnitureEntity, rot);
                }
            }
            catch { break; }
        }
    }

    static void TryGetPrefabCollectionSystem()
    {
        try
        {
            _prefabCollectionSystem = _clientWorld.GetExistingSystemManaged<PrefabCollectionSystem>();
        }
        catch
        {
            _prefabCollectionSystem = null;
        }
    }

    static void TryGetSystems()
    {
        Plugin.Logger.LogInfo($"[ArachNOPE] TryGetSystems: scanning all worlds...");

        int bestMapCount = -1;
        HybridModelSystem bestHms = null;
        World bestWorld = null;

        foreach (var w in World.All)
        {
            if (w == null || !w.IsCreated) continue;
            try
            {
                var hms = w.GetExistingSystemManaged<HybridModelSystem>();
                if (hms == null)
                {
                    Plugin.Logger.LogInfo($"[ArachNOPE]   World '{w.Name}': no HybridModelSystem");
                    continue;
                }

                int methodCount = 0;
                try
                {
                    var map = hms.GetEntityToGameObjectMap();
                    methodCount = map?.Count ?? -1;
                }
                catch { }

                int directCount = 0;
                try
                {
                    directCount = hms._EntityToGameObject?.Count ?? -1;
                }
                catch { }

                Plugin.Logger.LogInfo(
                    $"[ArachNOPE]   World '{w.Name}': HybridModelSystem " +
                    $"GetEntityToGameObjectMap={methodCount} _EntityToGameObject={directCount}");

                int mapCount = methodCount > directCount ? methodCount : directCount;
                if (mapCount > bestMapCount)
                {
                    bestMapCount = mapCount;
                    bestHms = hms;
                    bestWorld = w;
                }
            }
            catch { }
        }

        if (bestHms != null)
        {
            _hybridModelSystem = bestHms;
            _clientWorld = bestWorld;
            Plugin.Logger.LogInfo(
                $"[ArachNOPE]   => Using HybridModelSystem from world '{bestWorld.Name}' " +
                $"(map count: {bestMapCount})");
        }
        else
        {
            Plugin.Logger.LogError($"[ArachNOPE] HybridModelSystem not found in any world!");
        }
    }

    static void TryRemoveComponent<T>(EntityManager em, Entity e) where T : struct
    {
        if (em.HasComponent<T>(e))
            em.RemoveComponent<T>(e);
    }

    static void TryParentToEntity(EntityManager em, Entity parent, Entity child)
    {
        // No-op for now — Child hierarchy doesn't work with tile prefabs
    }

    internal static void TryUnparentEntity(EntityManager em, Entity parent, Entity child)
    {
        try
        {
            // Remove from parent's Child buffer
            if (em.HasBuffer<Child>(parent))
            {
                var childBuffer = em.GetBuffer<Child>(parent);
                for (int i = 0; i < childBuffer.Length; i++)
                {
                    if (childBuffer[i].Value == child)
                    {
                        childBuffer.RemoveAt(i);
                        break;
                    }
                }
            }

            // Remove Parent component from child
            if (em.HasComponent<Parent>(child))
                em.RemoveComponent<Parent>(child);
        }
        catch { }
    }

    static bool TrySpawnFurniture(EntityManager em, Entity sourceEntity, int guidHash, out Entity furnitureEntity)
    {
        furnitureEntity = Entity.Null;
        if (_prefabCollectionSystem == null) return false;

        try
        {
            int furnitureHash = SpiderLookup.IsSpider(guidHash)
                ? SpiderLookup.GetFurnitureGuidHash(guidHash)
                : SpiderLookup.GetFurnitureGuidHashForBuff(guidHash);
            var furnitureGuid = new PrefabGUID(furnitureHash);
            Entity prefab = Entity.Null;

            if (!_prefabCollectionSystem._PrefabLookupMap.TryGetValue(furnitureGuid, out prefab))
            {
                if (!_prefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue(furnitureGuid, out prefab))
                    return false;
            }

            furnitureEntity = em.Instantiate(prefab);

            // Strip collision/avoidance components so furniture doesn't
            // interfere with the player's movement or steering system
            TryRemoveComponent<Unity.Physics.PhysicsCollider>(em, furnitureEntity);
            TryRemoveComponent<Unity.Physics.PhysicsWorldIndex>(em, furnitureEntity);
            TryRemoveComponent<Unity.Physics.Systems.StaticPhysicsWorldBodyIndex>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.CollisionRadius>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.MoveStopTrigger>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TileCollisionTag>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TilePathfindingTag>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TileLineOfSightTag>(em, furnitureEntity);

            // Strip tile grid components so the furniture doesn't block the
            // player's tile in the client-side tile collision system
            TryRemoveComponent<ProjectM.TileModelSpatialData>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TilePosition>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TileBounds>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TileData>(em, furnitureEntity);
            TryRemoveComponent<ProjectM.TileModelRegistrationState>(em, furnitureEntity);

            // Set initial position/rotation
            if (em.HasComponent<Translation>(sourceEntity) && em.HasComponent<Translation>(furnitureEntity))
            {
                var pos = em.GetComponentData<Translation>(sourceEntity);
                em.SetComponentData(furnitureEntity, pos);
            }

            if (em.HasComponent<Rotation>(sourceEntity) && em.HasComponent<Rotation>(furnitureEntity))
            {
                var rot = em.GetComponentData<Rotation>(sourceEntity);
                em.SetComponentData(furnitureEntity, rot);
            }

            return true;
        }
        catch
        {
            furnitureEntity = Entity.Null;
            return false;
        }
    }

    static void ResetState()
    {
        _emReady = false;
        _prefabCollectionSystem = null;
        _clientWorld = null;
        _rehideCounter = 0;
        _buffScanCounter = 0;
        _framesSinceWorldChange = 0;
        _hybridModelSystem = null;

        // Dispose stale entity queries so they aren't reused with a new
        // world's EntityManager (native ECS access violation if mismatched)
        if (_buffQueryCreated)
        {
            try { _buffQuery.Dispose(); } catch { }
            _buffQueryCreated = false;
        }
        if (_npcQueryCreated)
        {
            try { _npcQuery.Dispose(); } catch { }
            _npcQueryCreated = false;
        }

        _tracked.Clear();
        _playerSpiders.Clear();
        _playerBurrowed.Clear();
        _seenPlayerBuffs.Clear();
        _buffToPlayer.Clear();
        _pendingHidePlayers.Clear();

        // Restore any disabled player renderers
        foreach (var kvp in _disabledPlayerModels)
        {
            try
            {
                if (kvp.Value != null)
                {
                    var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].enabled = true;
                }
            }
            catch { }
        }
        _disabledPlayerModels.Clear();
    }

    internal static void Cleanup()
    {
        foreach (var kvp in _tracked)
        {
            if (!kvp.Value.FurnitureSpawned) continue;
            try
            {
                if (_clientWorld != null && _clientWorld.IsCreated)
                {
                    var em = _clientWorld.EntityManager;
                    if (em.Exists(kvp.Value.FurnitureEntity))
                        em.DestroyEntity(kvp.Value.FurnitureEntity);
                }
            }
            catch { }
        }

        foreach (var kvp in _playerSpiders)
        {
            try
            {
                if (_clientWorld != null && _clientWorld.IsCreated)
                {
                    var em = _clientWorld.EntityManager;
                    if (em.Exists(kvp.Value))
                        em.DestroyEntity(kvp.Value);
                }
            }
            catch { }
        }

        // Restore any disabled player renderers
        foreach (var kvp in _disabledPlayerModels)
        {
            try
            {
                if (kvp.Value != null)
                {
                    var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].enabled = true;
                }
            }
            catch { }
        }

        _tracked.Clear();
        _playerSpiders.Clear();
        _playerBurrowed.Clear();
        _seenPlayerBuffs.Clear();
        _buffToPlayer.Clear();

        // ResetState handles query disposal + all flag resets
        ResetState();
    }
}

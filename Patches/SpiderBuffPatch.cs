using System.Collections.Generic;
using ArachNOPE.Helpers;
using HarmonyLib;
using ProjectM;
using ProjectM.Gameplay.Systems;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

namespace ArachNOPE.Patches;

[HarmonyPatch]
internal static class SpiderBuffPatch
{
    static bool _initialized;
    static int _applyFireCount;
    static int _removeFireCount;
    static readonly Dictionary<Entity, Entity> _buffToPlayer = new(); // buff entity -> player entity

    // OBSOLETE: replaced by BuffSystem_Spawn_Client.OnUpdate in SpiderHidePatch.cs
    // [HarmonyPrefix]
    // [HarmonyPatch(typeof(CreateGameplayEventOnSpawnSystem), nameof(CreateGameplayEventOnSpawnSystem.OnUpdate))]
    static void OnBuffApplyPrefix(CreateGameplayEventOnSpawnSystem __instance)
    {
        _applyFireCount++;
        Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] CreateGameplayEventOnSpawnSystem.OnUpdate fired ({_applyFireCount})");

        try
        {
            if (__instance.World == null || !__instance.World.IsCreated)
            {
                Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] World null or not created");
                return;
            }
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE][SysPatch] World check exception: {e.Message}");
            return;
        }

        if (!_initialized)
        {
            Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] Not initialized, attempting init...");
            if (!TryInitialize(__instance.World))
            {
                Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] Init failed");
                return;
            }
            Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] Init succeeded");
        }

        var em = __instance.EntityManager;
        EntityQuery query = default;
        NativeArray<Entity> buffEntities = default;

        try
        {
            query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabGUID>(),
                ComponentType.ReadOnly<Buff>());
            buffEntities = query.ToEntityArray(Allocator.Temp);

            Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] Query found {buffEntities.Length} entities with Buff+PrefabGUID");

            foreach (var buffEntity in buffEntities)
            {
                if (!em.HasComponent<PrefabGUID>(buffEntity)) continue;
                int guidHash = em.GetComponentData<PrefabGUID>(buffEntity).GuidHash;
                Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch]   buff entity {buffEntity.Index}:{buffEntity.Version} PrefabGUID hash={guidHash}");

                if (!SpiderLookup.IsSpiderBuff(guidHash)) continue;
                Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch]   *** SPIDER BUFF MATCH! ***");

                Entity target = em.GetComponentData<Buff>(buffEntity).Target;
                bool exists = em.Exists(target);
                bool hasPlayerChar = em.HasComponent<PlayerCharacter>(target);
                Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch]   Target={target.Index}:{target.Version}, Exists={exists}, HasPlayerCharacter={hasPlayerChar}");

                if (!exists) continue;
                if (!hasPlayerChar) continue;

                if (_buffToPlayer.ContainsKey(buffEntity))
                {
                    Plugin.Logger.LogInfo("[ArachNOPE][SysPatch]   Already tracked, skipping");
                    continue;
                }
                if (SpiderHidePatch._playerSpiders.ContainsKey(target))
                {
                    Plugin.Logger.LogInfo("[ArachNOPE][SysPatch]   Player already has furniture, skipping");
                    continue;
                }

                _buffToPlayer[buffEntity] = target;
                Plugin.Logger.LogInfo("[ArachNOPE][SysPatch]   Added to tracking");

                if (em.HasComponent<LocalTransform>(target))
                {
                    var t = em.GetComponentData<LocalTransform>(target);
                    t.Scale = 0f;
                    em.SetComponentData(target, t);
                    Plugin.Logger.LogInfo("[ArachNOPE][SysPatch]   Set player scale to 0");
                }

                Entity furnitureEntity = Entity.Null;
                bool spawned = TrySpawnFurniture(em, target, out furnitureEntity);
                Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch]   Spawned furniture={spawned}, entity={furnitureEntity.Index}:{furnitureEntity.Version}");
                if (spawned)
                {
                    SpiderHidePatch._playerSpiders[target] = furnitureEntity;
                }
            }
        }
        finally
        {
            if (buffEntities.IsCreated) buffEntities.Dispose();
            try { query.Dispose(); } catch { }
        }
    }

    // REDUNDANT: replaced by SafetyScanBuffs(active-set comparison in SpiderHidePatch.cs)
    // [HarmonyPostfix]
    // [HarmonyPatch(typeof(UpdateBuffsBuffer_Destroy), nameof(UpdateBuffsBuffer_Destroy.OnUpdate))]
    // static void OnBuffRemovePostfix(UpdateBuffsBuffer_Destroy __instance)
    // {
    //     if (!_initialized)
    //     {
    //         Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] Buff remove patch: not initialized yet");
    //         return;
    //     }

    //     _removeFireCount++;
    //     Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] UpdateBuffsBuffer_Destroy.OnUpdate fired ({_removeFireCount}), tracking {_buffToPlayer.Count} buffs");

    //     if (_buffToPlayer.Count == 0) return;

    //     var em = __instance.EntityManager;
    //     EntityQuery query = default;
    //     NativeArray<Entity> destroyedEntities = default;

    //     try
    //     {
    //         query = em.CreateEntityQuery(
    //             ComponentType.ReadOnly<PrefabGUID>(),
    //             ComponentType.ReadOnly<Buff>());
    //         destroyedEntities = query.ToEntityArray(Allocator.Temp);

    //         Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] Destroy query found {destroyedEntities.Length} entities");

    //         var destroyedSet = new HashSet<Entity>();
    //         foreach (var entity in destroyedEntities)
    //             destroyedSet.Add(entity);

    //         var toRemove = new List<Entity>();
    //         foreach (var kvp in _buffToPlayer)
    //         {
    //             var buffEntity = kvp.Key;
    //             var playerEntity = kvp.Value;

    //             if (destroyedSet.Contains(buffEntity))
    //             {
    //                 Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] Buff removed for player {playerEntity.Index}:{playerEntity.Version}");
    //                 HandleBuffRemoval(em, playerEntity);
    //                 toRemove.Add(buffEntity);
    //             }
    //         }

    //         foreach (var buffEntity in toRemove)
    //             _buffToPlayer.Remove(buffEntity);
    //     }
    //     finally
    //     {
    //         if (destroyedEntities.IsCreated) destroyedEntities.Dispose();
    //         try { query.Dispose(); } catch { }
    //     }
    // }

    static void HandleBuffRemoval(EntityManager em, Entity playerEntity)
    {
        if (SpiderHidePatch._playerSpiders.TryGetValue(playerEntity, out var furnitureEntity))
        {
            try
            {
                if (em.Exists(furnitureEntity))
                {
                    SpiderHidePatch.TryUnparentEntity(em, playerEntity, furnitureEntity);
                    em.DestroyEntity(furnitureEntity);
                }
                Plugin.Logger.LogInfo("[ArachNOPE][SysPatch] Destroyed furniture");
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError($"[ArachNOPE][SysPatch] Error destroying furniture: {e.Message}");
            }

            SpiderHidePatch._playerSpiders.Remove(playerEntity);
        }

        // Also restore client-side player visual
        SpiderHidePatch.RestorePlayerVisual(playerEntity);
    }

    static bool TryInitialize(World world)
    {
        try
        {
            SpiderHidePatch._clientWorld = world;
            SpiderHidePatch._prefabCollectionSystem = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] PrefabCollectionSystem found: {SpiderHidePatch._prefabCollectionSystem != null}");
            _initialized = true;
            return true;
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE][SysPatch] Init error: {e.Message}");
            return false;
        }
    }

    static bool TrySpawnFurniture(EntityManager em, Entity playerEntity, out Entity furnitureEntity)
    {
        furnitureEntity = Entity.Null;
        var prefabSystem = SpiderHidePatch._prefabCollectionSystem;
        if (prefabSystem == null) return false;

        try
        {
            int furnitureHash = SpiderLookup.GetFurnitureGuidHashForBuff(SpiderLookup.AB_Shapeshift_Spider_Buff);
            var furnitureGuid = new PrefabGUID(furnitureHash);
            Entity prefab = Entity.Null;

            if (!prefabSystem._PrefabLookupMap.TryGetValue(furnitureGuid, out prefab))
            {
                if (!prefabSystem._PrefabGuidToEntityMap.TryGetValue(furnitureGuid, out prefab))
                {
                    Plugin.Logger.LogInfo($"[ArachNOPE][SysPatch] Prefab not found for hash {furnitureHash}");
                    return false;
                }
            }

            furnitureEntity = em.Instantiate(prefab);

            // Remove collision so furniture doesn't push the hidden player
            if (em.HasComponent<Unity.Physics.PhysicsCollider>(furnitureEntity))
                em.RemoveComponent<Unity.Physics.PhysicsCollider>(furnitureEntity);

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

            return true;
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"[ArachNOPE][SysPatch] Spawn furniture error: {e.Message}");
            furnitureEntity = Entity.Null;
            return false;
        }
    }

    internal static void Cleanup()
    {
        _buffToPlayer.Clear();
        _initialized = false;
    }
}

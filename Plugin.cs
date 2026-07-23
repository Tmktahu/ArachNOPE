using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Reflection;
using ArachNOPE.Patches;

namespace ArachNOPE;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
internal class Plugin : BasePlugin
{
    internal static Plugin Instance { get; private set; }

    internal static ManualLogSource Logger => Instance.Log;

    Harmony _harmony;

    public override void Load()
    {
        Instance = this;

        _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded!");
    }

    public override bool Unload()
    {
        _harmony.UnpatchSelf();
        SpiderHidePatch.Cleanup();
        SpiderBuffPatch.Cleanup();
        return true;
    }
}

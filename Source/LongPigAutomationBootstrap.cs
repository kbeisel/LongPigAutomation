using HarmonyLib;
using Verse;

namespace LongPigAutomation
{
    /// <summary>
    /// Entry point for Long Pig Automation. Applies all Harmony patches.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LongPigAutomationBootstrap
    {
        static LongPigAutomationBootstrap()
        {
            var harmony = new Harmony("LongPigAutomation");
            harmony.PatchAll();
            Log.Message("[Long Pig Automation] Initialized Harmony patches.");
        }
    }
}
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LongPigAutomation
{
    /// <summary>
    /// Intercepts certain letters:
    /// - Baby birth: rename baby to ThingID and suppress letter/dialog.
    /// - Growth moment: try to invoke the first choice option and suppress the letter.
    ///   If we can't safely do that, falls back to vanilla behavior.
    /// </summary>
    [HarmonyPatch(typeof(LetterStack))]
    [HarmonyPatch("ReceiveLetter")]
    [HarmonyPatch(new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class LetterInterceptionPatches
    {
        public static bool Prefix(Letter let, string debugInfo, int delayTicks, bool playSound)
        {
            if (let == null)
                return true;

            try
            {
                // Baby birth letter
                if (IsBabyBirthLetter(let))
                {
                    TryHandleBabyBirthLetter(let);
                    // Swallow the letter – no icon, no naming dialog.
                    return false;
                }

                // Growth moment letter
                if (IsGrowthMomentLetter(let))
                {
                    bool handled = TryHandleGrowthMomentLetter(let);
                    if (handled)
                    {
                        // We successfully invoked the first choice; hide the letter.
                        return false;
                    }

                    // If we couldn't safely handle it, let vanilla proceed.
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[LPA] Exception in LetterInterceptionPatches.Prefix: {ex}");
                // On any error, fall back to vanilla behavior.
                return true;
            }

            // All other letters: vanilla behavior.
            return true;
        }

        // --- Baby birth handling ---

        private static bool IsBabyBirthLetter(Letter let)
        {
            var defName = let.def?.defName ?? string.Empty;
            var typeName = let.GetType().Name;

            // Biotech baby birth letter is usually "BabyBirth" and/or ChoiceLetter_BabyBirth.
            return defName.IndexOf("BabyBirth", StringComparison.OrdinalIgnoreCase) >= 0
                   || typeName.IndexOf("BabyBirth", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TryHandleBabyBirthLetter(Letter let)
        {
            Pawn baby = FindBabyPawnOnLetter(let);
            if (baby == null)
            {
                Log.Warning("[LPA] Baby birth letter intercepted but no baby pawn found.");
                return;
            }

            // Rename to pawn's ThingID and skip any naming UI.
            string idName = baby.ThingID;
            baby.Name = new NameSingle(idName, false);

            Log.Message($"[LPA] Auto-named baby {baby.ThingID} to \"{idName}\" and suppressed naming dialog.");
        }

        private static Pawn FindBabyPawnOnLetter(Letter let)
        {
            // First, try lookTargets.targets (List<GlobalTargetInfo>)
            if (let.lookTargets != null && let.lookTargets.IsValid())
            {
                try
                {
                    var lt = let.lookTargets;
                    if (lt.targets != null)
                    {
                        foreach (var tgt in lt.targets)
                        {
                            if (tgt.Thing is Pawn p && p.DevelopmentalStage == DevelopmentalStage.Baby)
                                return p;
                        }
                    }
                }
                catch
                {
                    // Ignore lookTargets issues and fall back to reflection below.
                }
            }

            // Fallback: reflect over fields to find a Pawn at baby stage.
            var tLetter = let.GetType();
            var fields = tLetter.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var f in fields)
            {
                try
                {
                    if (typeof(Pawn).IsAssignableFrom(f.FieldType))
                    {
                        var p = f.GetValue(let) as Pawn;
                        if (p != null && p.DevelopmentalStage == DevelopmentalStage.Baby)
                            return p;
                    }

                    // Collections of pawns (e.g. List<Pawn>)
                    if (typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType))
                    {
                        var en = f.GetValue(let) as System.Collections.IEnumerable;
                        if (en == null) continue;

                        foreach (var obj in en)
                        {
                            if (obj is Pawn p2 && p2.DevelopmentalStage == DevelopmentalStage.Baby)
                                return p2;
                        }
                    }
                }
                catch
                {
                    // Ignore field-specific issues and keep scanning.
                }
            }

            return null;
        }

        // --- Growth moment handling ---

        private static bool IsGrowthMomentLetter(Letter let)
        {
            var defName = let.def?.defName ?? string.Empty;
            var typeName = let.GetType().Name;

            // Biotech growth letter is typically something like "GrowthMoment"
            // and/or ChoiceLetter_GrowthMoment.
            return defName.IndexOf("GrowthMoment", StringComparison.OrdinalIgnoreCase) >= 0
                   || typeName.IndexOf("GrowthMoment", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Try to auto-select the first trait/passion option on a growth moment letter.
        /// Returns true if we found and invoked an appropriate method, false to let vanilla run.
        /// </summary>
        private static bool TryHandleGrowthMomentLetter(Letter let)
        {
            var tLetter = let.GetType();

            // Strategy:
            // 1. Find an instance method with signature void Method(int index)
            // 2. Prefer methods whose name contains "Choose" or "Select"
            // 3. Invoke with index 0 to pick the first option.
            MethodInfo chosenMethod = null;

            var methods = tLetter.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            // First pass: methods with "Choose" in name
            chosenMethod = methods.FirstOrDefault(m =>
                m.ReturnType == typeof(void) &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(int) &&
                m.Name.IndexOf("Choose", StringComparison.OrdinalIgnoreCase) >= 0);

            // Second pass: methods with "Select" in name
            if (chosenMethod == null)
            {
                chosenMethod = methods.FirstOrDefault(m =>
                    m.ReturnType == typeof(void) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.Name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Last fallback: any void(int) method at all
            if (chosenMethod == null)
            {
                chosenMethod = methods.FirstOrDefault(m =>
                    m.ReturnType == typeof(void) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(int));
            }

            if (chosenMethod == null)
            {
                Log.Warning("[LPA] Growth moment letter intercepted, but no suitable choice method (void(int)) was found. Falling back to vanilla.");
                return false;
            }

            try
            {
                chosenMethod.Invoke(let, new object[] { 0 });
                Log.Message($"[LPA] Auto-selected first growth option via {tLetter.Name}.{chosenMethod.Name}(0) and suppressed growth letter.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[LPA] Exception while auto-choosing growth moment option: {ex}");
                return false;
            }
        }
    }
}
/*
 * OLD VERSION:
 * 
 * using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LongPigAutomation
{
    /// <summary>
    /// Intercepts letters to:
    ///  - auto-name newborns with their ThingID
    ///  - auto-resolve growth moments and suppress the dialog
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter",
        new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class LetterInterceptionPatches
    {
        // RimWorld 1.6 signature:
        // public void ReceiveLetter(Letter let, string debugInfo = null, int delayTicks = 0, bool playSound = true)
        [HarmonyPrefix]
        public static bool ReceiveLetter_Prefix(Letter let, string debugInfo, int delayTicks, bool playSound)
        {
            if (let == null)
                return true;

            try
            {
                string typeName = let.GetType().Name;

                // If the letter is delayed, ignore it (we only care about immediate popup letters).
                if (delayTicks > 0)
                    return true;

                Pawn targetPawn = ExtractPrimaryPawnFromLetter(let);

                //
                // 1. Baby birth → auto-assign ThingID name & suppress dialog
                //
                if (typeName.IndexOf("Baby", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    targetPawn != null &&
                    targetPawn.RaceProps != null &&
                    targetPawn.RaceProps.Humanlike &&
                    targetPawn.DevelopmentalStage == DevelopmentalStage.Baby)
                {
                    AssignIdNameToPawn(targetPawn);
                    return false; // suppress the naming dialog
                }

                //
                // 2. Growth Moment → auto-select first option & suppress dialog
                //
                if (typeName.IndexOf("GrowthMoment", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    typeof(ChoiceLetter).IsAssignableFrom(let.GetType()) &&
                    targetPawn != null &&
                    targetPawn.RaceProps != null &&
                    targetPawn.RaceProps.Humanlike)
                {
                    TryAutoResolveGrowthMoment(let.GetType(), let, targetPawn);
                    return false; // suppress popup
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Long Pig Automation] Error intercepting letter {let}: {ex}");
            }

            return true; // fallback: let vanilla handle everything else
        }

        // --------------------------------------------------------------------
        // HELPERS
        // --------------------------------------------------------------------

        private static Pawn ExtractPrimaryPawnFromLetter(Letter let)
        {
            try
            {
                var targetInfo = let.lookTargets.TryGetPrimaryTarget();
                if (targetInfo.Thing is Pawn pawn)
                    return pawn;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Assigns a utilitarian ID-based name (ThingID) to the newborn pawn.
        /// Prevents the normal naming dialog from appearing.
        /// </summary>
        private static void AssignIdNameToPawn(Pawn pawn)
        {
            NameSingle current = pawn.Name as NameSingle;

            if (current != null && current.Name == pawn.ThingID)
                return; // already named

            pawn.Name = new NameSingle(pawn.ThingID, false);
        }

        /// <summary>
        /// Attempts to auto-resolve a Growth Moment letter by invoking any private
        /// choice-creation and choice-application methods via reflection.
        ///
        /// The logic is defensive: if something fails, we suppress the letter anyway
        /// and rely on vanilla fallback behavior.
        /// </summary>
        private static void TryAutoResolveGrowthMoment(Type letterType, Letter letter, Pawn pawn)
        {
            try
            {
                //
                // 1. Generate the choices
                //
                var generateMethods = letterType
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m =>
                        m.Name.StartsWith("MakeChoices", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.IndexOf("TrySetChoices", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                foreach (var m in generateMethods)
                {
                    var parameters = m.GetParameters();

                    if (parameters.Length == 0)
                        m.Invoke(letter, new object[0]);
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Pawn))
                        m.Invoke(letter, new object[] { pawn });
                }

                //
                // 2. Apply the first choice (index 0)
                //
                var applyMethods = letterType
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m =>
                        m.Name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Choose", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                bool applied = false;

                foreach (var m in applyMethods)
                {
                    var parameters = m.GetParameters();

                    // Method with an index → apply index 0
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        m.Invoke(letter, new object[] { 0 });
                        applied = true;
                        break;
                    }

                    // No-arg choice applier
                    if (parameters.Length == 0)
                    {
                        m.Invoke(letter, Array.Empty<object>());
                        applied = true;
                        break;
                    }
                }

                if (!applied)
                    Log.Warning("[Long Pig Automation] Could not locate apply-choice method; relying on fallback behavior.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Long Pig Automation] Failed to auto-resolve Growth Moment letter: {ex}");
            }
        }
    }
}
*/
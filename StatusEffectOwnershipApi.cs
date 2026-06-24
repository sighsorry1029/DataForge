using System;

namespace DataForge;

public static class DataForgeStatusEffectOwnership
{
    public static event Action? StatusEffectOverridesWillApply;
    public static event Action? StatusEffectOverridesApplied;

    public static bool HasActiveStatusEffectOverride(string effectName)
    {
        return StatusEffectOverrideManager.HasActiveStatusEffectOverride(effectName);
    }

    internal static void NotifyStatusEffectOverridesWillApply()
    {
        Notify(StatusEffectOverridesWillApply, "will-apply");
    }

    internal static void NotifyStatusEffectOverridesApplied()
    {
        Notify(StatusEffectOverridesApplied, "applied");
    }

    private static void Notify(Action? handlers, string phase)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning($"Status effect ownership {phase} subscriber failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

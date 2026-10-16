using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;

namespace V1Bridge.Ror
{
    /// <summary>The bridge's settings in Risk of Rain 2's own settings screen (Settings → Mod Options → V1 Bridge), through
    /// Risk of Options. Only touched when that mod is loaded.</summary>
    internal static class SettingsMenu
    {
        public const string RiskOfOptionsGuid = "com.rune580.riskofoptions";

        public static void Register()
        {
            ModSettingsManager.SetModDescription("Play as V1 with the real ULTRAKILL running beside Risk of Rain 2.");
            ModSettingsManager.AddOption(new IntSliderOption(Plugin.UkHeight, new IntSliderConfig { min = 360, max = 1440 }));
            ModSettingsManager.AddOption(new IntSliderOption(Plugin.UkFps, new IntSliderConfig { min = 30, max = 240 }));
            ModSettingsManager.AddOption(new StepSliderOption(Plugin.MouseScale, new StepSliderConfig { min = 0.1f, max = 5f, increment = 0.05f }));
            ModSettingsManager.AddOption(new StepSliderOption(Plugin.DamageCoefficient, new StepSliderConfig { min = 0.5f, max = 10f, increment = 0.25f }));
            ModSettingsManager.AddOption(new StepSliderOption(Plugin.IncomingDamage, new StepSliderConfig { min = 0.25f, max = 3f, increment = 0.05f }));
            ModSettingsManager.AddOption(new StepSliderOption(Plugin.V1Health, new StepSliderConfig { min = 10f, max = 500f, increment = 5f }));
            ModSettingsManager.AddOption(new CheckBoxOption(Plugin.HideRorHud));
            ModSettingsManager.AddOption(new CheckBoxOption(Plugin.UseRealSave, new CheckBoxConfig { restartRequired = true }));
        }
    }
}

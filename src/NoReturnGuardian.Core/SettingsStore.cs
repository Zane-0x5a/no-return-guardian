using System;
using System.IO;

namespace NoReturnGuardian.Core
{
    public sealed class SettingsStore
    {
        private readonly string _settingsPath;

        public SettingsStore(string settingsPath)
        {
            _settingsPath = settingsPath;
        }

        public GuardianSettings Load()
        {
            GuardianSettings settings = null;
            try
            {
                if (File.Exists(_settingsPath))
                {
                    settings = FileTools.ReadJson<GuardianSettings>(_settingsPath);
                }
            }
            catch
            {
                settings = null;
            }

            if (settings == null)
            {
                settings = new GuardianSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.StoragePath))
            {
                settings.StoragePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NoReturnGuardian");
            }

            settings.PollIntervalMs = Math.Max(750, Math.Min(10000, settings.PollIntervalMs));
            settings.StabilityDelayMs = Math.Max(1000, Math.Min(15000, settings.StabilityDelayMs));
            settings.AutomaticRetention = Math.Max(10, Math.Min(500, settings.AutomaticRetention));
            settings.PanelTransparency = Math.Max(0, Math.Min(100, settings.PanelTransparency));
            // File coherence cannot distinguish the hideout from an encounter.
            // Keep the legacy setting disabled until a real hideout classifier exists.
            settings.AutoMonitor = false;
            return settings;
        }

        public Result<bool> Save(GuardianSettings settings)
        {
            try
            {
                FileTools.WriteJsonAtomic(_settingsPath, settings);
                return Result<bool>.Ok(true, "settings_saved", "Settings were saved.");
            }
            catch (Exception exception)
            {
                return Result<bool>.Fail("settings_save_failed", exception.Message);
            }
        }
    }
}

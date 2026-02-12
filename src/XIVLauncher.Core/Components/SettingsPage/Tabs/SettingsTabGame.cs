using Hexa.NET.ImGui;

using XIVLauncher.Common;
using XIVLauncher.Core.Resources.Localization;

namespace XIVLauncher.Core.Components.SettingsPage.Tabs;

public class SettingsTabGame : SettingsTab
{
    public override SettingsEntry[] Entries { get; } =
    {
        new SettingsEntry<DirectoryInfo>(Strings.GamePathSetting, Strings.GamePathSettingDescription, () => Program.Config.GamePath, x => Program.Config.GamePath = x)
        {
            CheckValidity = x =>
            {
                if (string.IsNullOrWhiteSpace(x?.FullName))
                    return Strings.GamePathSettingNotSetValidation;

                if (x.Name is "game" or "boot")
                    return Strings.GamePathSettingInvalidValidationj;

                return null;
            }
        },

        new SettingsEntry<DirectoryInfo>(Strings.GameConfigurationPathSetting, Strings.GameConfigurationPathSettingDescription, () => Program.Config.GameConfigPath, x => Program.Config.GameConfigPath = x)
        {
            CheckValidity = x => string.IsNullOrWhiteSpace(x?.FullName) ? Strings.GameConfigurationPathNotSetValidation : null,

            // TODO: We should also support this on Windows
            CheckVisibility = () => Environment.OSVersion.Platform == PlatformID.Unix,
        },

        new SettingsEntry<string>(Strings.AdditionalGameArgsSetting, Strings.AdditionalGameArgsSettingDescription, () => Program.Config.AdditionalArgs, x => Program.Config.AdditionalArgs = x),
        new SettingsEntry<DpiAwareness>(Strings.GameDPIAwarenessSetting, Strings.GameDPIAwarenessSettingDescription, () => Program.Config.DpiAwareness ?? DpiAwareness.Unaware, x => Program.Config.DpiAwareness = x),
        new SettingsEntry<bool>(Strings.UseXLAuthMacrosSetting, Strings.UseXLAuthMacrosSettingDescription, () => Program.Config.IsOtpServer ?? false, x => Program.Config.IsOtpServer = x),
    };

    public override string Title => Strings.GameTitle;

    public override void Draw()
    {
        base.Draw();

        // UID cache not used for Taiwan version
    }
}

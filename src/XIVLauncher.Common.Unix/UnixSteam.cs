using System;
using System.Threading.Tasks;

using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Unix
{
    // Steam integration removed for Taiwan version.
    // This stub exists only to satisfy the ISteam interface requirement.
    public class UnixSteam : ISteam
    {
        public UnixSteam()
        {
        }

        public void Initialize(uint appId)
        {
        }

        public bool IsValid => false;

        public bool BLoggedOn => false;

        public bool BOverlayNeedsPresent => false;

        public void Shutdown()
        {
        }

        public Task<byte[]?> GetAuthSessionTicketAsync()
        {
            return Task.FromResult<byte[]?>(null);
        }

        public bool IsAppInstalled(uint appId)
        {
            return false;
        }

        public string GetAppInstallDir(uint appId)
        {
            return string.Empty;
        }

        public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "")
        {
            return false;
        }

        public string GetEnteredGamepadText()
        {
            return string.Empty;
        }

        public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height)
        {
            return false;
        }

        public bool IsRunningOnSteamDeck() => false;

        public uint GetServerRealTime() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public void ActivateGameOverlayToWebPage(string url, bool modal = false)
        {
        }

        public event Action<bool> OnGamepadTextInputDismissed;
    }
}

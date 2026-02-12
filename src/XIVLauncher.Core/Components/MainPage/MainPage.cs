using System.Diagnostics;
using System.Numerics;
using System.Threading;

using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

using Serilog;

using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition.Aria;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix;
using XIVLauncher.Common.Unix.Compatibility.Wine;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Resources.Localization;
using XIVLauncher.Core.Support;

namespace XIVLauncher.Core.Components.MainPage;

public class MainPage : Page
{
    private readonly LoginFrame loginFrame;
    private readonly NewsFrame newsFrame;
    private readonly ActionButtons actionButtons;

    public bool IsLoggingIn { get; private set; }

    public MainPage(LauncherApp app)
        : base(app)
    {
        this.loginFrame = new LoginFrame(this);
        this.newsFrame = new NewsFrame(app);

        this.actionButtons = new ActionButtons();

        this.AccountSwitcher = new AccountSwitcher(app.Accounts);
        this.AccountSwitcher.AccountChanged += this.AccountSwitcherOnAccountChanged;

        this.loginFrame.OnLogin += this.ProcessLogin;
        this.actionButtons.OnSettingsButtonClicked += () => this.App.State = LauncherApp.LauncherState.Settings;
        this.actionButtons.OnStatusButtonClicked += () => AppUtil.OpenBrowser("https://is.xivup.com/");
        this.actionButtons.OnAccountButtonClicked += () => AppUtil.OpenBrowser("https://user.ffxiv.com.tw");

        this.Padding = new Vector2(32f, 32f);

        var savedAccount = App.Accounts.CurrentAccount;

        if (savedAccount != null) this.SwitchAccount(savedAccount, false);

        if (PlatformHelpers.IsElevated())
            App.ShowMessage(Strings.XLElevatedWarning, "XIVTCLauncher");

        Troubleshooting.LogTroubleshooting();
    }

    public AccountSwitcher AccountSwitcher { get; private set; }

    public void DoAutoLoginIfApplicable()
    {
        Debug.Assert(App.State == LauncherApp.LauncherState.Main);

        if ((App.Settings.IsAutologin ?? false) && !string.IsNullOrEmpty(this.loginFrame.Username) && !string.IsNullOrEmpty(this.loginFrame.Password))
            ProcessLogin(LoginAction.Game);
    }

    public override void Draw()
    {
        base.Draw();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(32f, 32f));
        this.newsFrame.Draw();

        ImGui.SameLine();

        this.loginFrame.Draw();
        this.AccountSwitcher.Draw();

        this.actionButtons.Draw();
        ImGui.PopStyleVar();
    }

    public void ReloadNews() => this.newsFrame.ReloadNews();

    private void SwitchAccount(XivAccount account, bool saveAsCurrent)
    {
        this.loginFrame.Username = account.UserName;
        this.loginFrame.IsOtp = account.UseOtp;
        this.loginFrame.IsAutoLogin = App.Settings.IsAutologin ?? false;

        if (account.SavePassword)
            this.loginFrame.Password = account.Password;

        if (saveAsCurrent)
        {
            App.Accounts.CurrentAccount = account;
        }
    }

    private void AccountSwitcherOnAccountChanged(object? sender, XivAccount e)
    {
        SwitchAccount(e, true);
    }

    private void ProcessLogin(LoginAction action)
    {
        if (this.IsLoggingIn)
            return;

        this.App.StartLoading(Strings.LoggingIn, canDisableAutoLogin: true);

        Task.Run(async () =>
        {
            if (GameHelpers.CheckIsGameOpen() && action == LoginAction.Repair)
            {
                App.ShowMessageBlocking(Strings.RepairOfficialLauncherOpenError, Strings.XIVLauncherError);

                Reactivate();
                return;
            }

            IsLoggingIn = true;

            App.Settings.IsAutologin = this.loginFrame.IsAutoLogin;

            var result = await Login(loginFrame.Username, loginFrame.Password, loginFrame.IsOtp, false, action).ConfigureAwait(false);

            if (result)
            {
                var sdlEvent = new SDLEvent
                {
                    Type = (int)SDLEventType.Quit
                };
                if (SDL.PushEvent(ref sdlEvent))
                {
                    Log.Error($"Failed to push event to SDL queue: {SDL.GetErrorS()}");
                }
            }
            else
            {
                Log.Verbose("Reactivated after Login() != true");
                this.Reactivate();
            }
        }).ContinueWith(t =>
        {
            if (!App.HandleContinuationBlocking(t))
                this.Reactivate();
        });
    }

    public async Task<bool> Login(string username, string password, bool isOtp, bool doingAutoLogin, LoginAction action)
    {
        if (action == LoginAction.Fake)
        {
            IGameRunner gameRunner;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                gameRunner = new WindowsGameRunner(null, false);
            else
                gameRunner = new UnixGameRunner(Program.CompatibilityTools, null, false);

            App.TaiwanLauncher.LaunchGame(gameRunner, "0", App.Settings.GamePath!, DpiAwareness.Unaware);

            return false;
        }

        // Taiwan: No boot check needed (body starts with \n to skip boot)

        var otp = string.Empty;

        if (isOtp)
        {
            App.AskForOtp();
            otp = App.WaitForOtp();

            // Make sure we are loading again
            App.State = LauncherApp.LauncherState.Loading;
        }

        if (otp == null)
            return false;

        PersistAccount(username, password, isOtp);

        // Step 1: Obtain reCAPTCHA token via browser
        App.StartLoading("Opening browser for verification...");

        string? captchaToken;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var captchaService = new Util.CaptchaService();
            captchaToken = await captchaService.GetCaptchaTokenAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Captcha token exception");
            captchaToken = null;
        }

        if (string.IsNullOrEmpty(captchaToken))
        {
            App.ShowMessageBlocking("Failed to obtain verification token. Please ensure a browser is available.", "Verification Error");
            return false;
        }

        Log.Information("Got reCAPTCHA token ({Length} chars), proceeding with login", captchaToken.Length);

        // Step 2: Login to Taiwan server
        App.StartLoading(Strings.LoggingIn);

        TaiwanLauncher.TwLoginResult loginResult;
        try
        {
            loginResult = await App.TaiwanLauncher.LoginAsync(username, password, string.IsNullOrEmpty(otp) ? null : otp, captchaToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Taiwan login exception");
            App.ShowMessageBlocking($"Login failed: {ex.Message}", "Login Error");
            return false;
        }

        if (!loginResult.Success || loginResult.SessionId == null)
        {
            App.ShowMessageBlocking(loginResult.ErrorMessage ?? "Login failed", "Login Error");
            return false;
        }

        // Step 2: Check game version
        App.StartLoading("Checking game version...");

        TaiwanLauncher.TwGameCheckResult gameCheck;
        try
        {
            gameCheck = await App.TaiwanLauncher.CheckGameVersionAsync(App.Settings.GamePath!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Taiwan version check exception");
            App.ShowMessageBlocking($"Version check failed: {ex.Message}", "Error");
            return false;
        }

        if (action == LoginAction.Repair)
        {
            if (gameCheck.State == TaiwanLauncher.TwLoginState.NeedsPatchGame)
            {
                if (!await InstallGamePatch(gameCheck.PendingPatches).ConfigureAwait(false))
                    return false;
            }
            else
            {
                App.ShowMessageBlocking("Game files are up to date. No repair needed.", "Repair");
            }

            return false;
        }

        if (gameCheck.State == TaiwanLauncher.TwLoginState.NeedsPatchGame)
        {
            if (!await InstallGamePatch(gameCheck.PendingPatches).ConfigureAwait(false))
            {
                Log.Error("Patch installation failed");
                return false;
            }
        }

        if (action == LoginAction.GameNoLaunch)
        {
            App.ShowMessageBlocking(Strings.UpdateCheckFinished, "XIVTCLauncher");
            return false;
        }

        // Step 3: Launch game
        try
        {
            using var process = await StartGameAndAddon(loginResult.SessionId, action == LoginAction.GameNoDalamud, action == LoginAction.GameNoPlugins, action == LoginAction.GameNoThirdparty).ConfigureAwait(false);

            if (process is null)
                throw new InvalidOperationException("Could not obtain Process Handle");

            if (process.ExitCode != 0 && (App.Settings.TreatNonZeroExitCodeAsFailure ?? false))
                throw new InvalidOperationException("Game exited with non-zero exit code");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "StartGameAndAddon resulted in an exception.");
            throw;
        }
    }

    public async Task<Process> StartGameAndAddon(string sessionId, bool forceNoDalamud, bool noPlugins, bool noThird)
    {
        var dalamudOk = false;

        IDalamudRunner dalamudRunner;
        IDalamudCompatibilityCheck dalamudCompatCheck;

        switch (Environment.OSVersion.Platform)
        {
            case PlatformID.Win32NT:
                dalamudRunner = new WindowsDalamudRunner(Program.DalamudUpdater.Runtime);
                dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();
                break;

            case PlatformID.Unix:
                dalamudRunner = new UnixDalamudRunner(Program.CompatibilityTools, Program.DotnetRuntime);
                dalamudCompatCheck = new UnixDalamudCompatibilityCheck();
                break;

            default:
                throw new NotImplementedException();
        }

        Troubleshooting.LogTroubleshooting();

        var dalamudLauncher = new DalamudLauncher(dalamudRunner, Program.DalamudUpdater,
            App.Settings.DalamudLoadMethod.GetValueOrDefault(DalamudLoadMethod.DllInject), App.Settings.GamePath,
            App.Storage.Root, App.Storage.GetFolder("logs"), ClientLanguage.ChineseTraditional,
            App.Settings.DalamudLoadDelay, false, noPlugins, noThird, Troubleshooting.GetTroubleshootingJson());

        try
        {
            dalamudCompatCheck.EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "No Dalamud Redists found");
            throw;
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "Architecture not supported");
            throw;
        }

        if (App.Settings.DalamudEnabled.GetValueOrDefault(true) && !forceNoDalamud)
        {
            try
            {
                App.StartLoading(Strings.WaitingForDalamud, Strings.PleaseBePatient);
                dalamudOk = dalamudLauncher.HoldForUpdate(App.Settings.GamePath) == DalamudLauncher.DalamudInstallState.Ok;
            }
            catch (DalamudRunnerException ex)
            {
                Log.Error(ex, "Couldn't ensure Dalamud runner");
                throw;
            }
        }

        IGameRunner runner;

        // Set LD_PRELOAD to value of XL_PRELOAD if we're running as a steam compatibility tool.
        // This check must be done before the FixLDP check so that it will still work.
        if (CoreEnvironmentSettings.IsSteamCompatTool)
        {
            var ldpreload = System.Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "";
            var xlpreload = System.Environment.GetEnvironmentVariable("XL_PRELOAD") ?? "";
            ldpreload = (ldpreload + ":" + xlpreload).Trim(':');
            if (!string.IsNullOrEmpty(ldpreload))
                System.Environment.SetEnvironmentVariable("LD_PRELOAD", ldpreload);
        }

        // Hack: Force C.utf8 to fix incorrect unicode paths
        if (App.Settings.FixLocale == true && !string.IsNullOrEmpty(Program.CType))
        {
            System.Environment.SetEnvironmentVariable("LC_ALL", Program.CType);
            System.Environment.SetEnvironmentVariable("LC_CTYPE", Program.CType);
        }

        // Hack: Strip out gameoverlayrenderer.so entries from LD_PRELOAD
        if (App.Settings.FixLDP == true)
        {
            var ldpreload = CoreEnvironmentSettings.GetCleanEnvironmentVariable("LD_PRELOAD", "gameoverlayrenderer.so");
            System.Environment.SetEnvironmentVariable("LD_PRELOAD", ldpreload);
        }

        // Hack: XMODIFIERS=@im=null
        if (App.Settings.FixIM == true)
        {
            System.Environment.SetEnvironmentVariable("XMODIFIERS", "@im=null");
        }

        // Hack: Fix libicuuc dalamud crashes
        if (App.Settings.FixError127 == true)
        {
            System.Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_USENLS", "true");
        }

        // The Timezone environment on Unix platforms tends to cause issues with in-game time display.
        // For now the best workaround is to unset it, although it can be specified with AdditionalArgs
        // again if the user really wants to.
        if (Environment.OSVersion.Platform == PlatformID.Unix && App.Settings.DontUseSystemTz == true)
        {
            System.Environment.SetEnvironmentVariable("TZ", string.Empty);
        }

        // Deal with "Additional Arguments". VAR=value %command% -args
        var launchOptions = (App.Settings.AdditionalArgs ?? string.Empty).Split("%command%", 2);
        var launchEnv = "";
        var gameArgs = "";

        // If there's only one launch option (no %command%) figure out whether it's args or env variables.
        if (launchOptions.Length == 1)
        {
            if (launchOptions[0].StartsWith('-'))
                gameArgs = launchOptions[0];
            else
                launchEnv = launchOptions[0];
        }
        else
        {
            launchEnv = launchOptions[0] ?? "";
            gameArgs = launchOptions[1] ?? "";
        }

        if (!string.IsNullOrEmpty(launchEnv))
        {
            foreach (var envvar in launchEnv.Split(null))
            {
                if (!envvar.Contains('=')) continue;    // ignore entries without an '='
                var kvp = envvar.Split('=', 2);
                if (kvp[0].EndsWith('+'))               // if key ends with +, then it's actually key+=value
                {
                    kvp[0] = kvp[0].TrimEnd('+');
                    kvp[1] = (System.Environment.GetEnvironmentVariable(kvp[0]) ?? "") + kvp[1];
                }
                System.Environment.SetEnvironmentVariable(kvp[0], kvp[1]);
            }
        }

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            runner = new WindowsGameRunner(dalamudLauncher, dalamudOk);
        }
        else if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            if (App.Settings.WineStartupType == WineStartupType.Custom)
            {
                if (App.Settings.WineBinaryPath == null)
                    throw new InvalidOperationException("Custom wine binary path wasn't set.");
                else if (!Directory.Exists(App.Settings.WineBinaryPath))
                    throw new InvalidOperationException("Custom wine binary path is invalid: no such directory.\n" +
                                                        "Check path carefully for typos: " + App.Settings.WineBinaryPath);
                else if (!File.Exists(Path.Combine(App.Settings.WineBinaryPath, "wine64")))
                    throw new InvalidOperationException("Custom wine binary path is invalid: no wine64 found at that location.\n" +
                                                        "Check path carefully for typos: " + App.Settings.WineBinaryPath);

                Log.Information("Using Custom Wine: " + App.Settings.WineBinaryPath);
            }
            else
            {
                Log.Information("Using Managed Wine: " + App.Settings.WineManagedVersion.ToString());
            }
            Log.Information("Using Dxvk Version: " + App.Settings.DxvkVersion.ToString());

            var signal = new ManualResetEvent(false);
            var isFailed = false;

            var _ = Task.Run(async () =>
            {
                var tempPath = App.Storage.GetFolder("temp");
                await Program.CompatibilityTools.EnsureTool(Program.HttpClient, tempPath).ConfigureAwait(false);
            }).ContinueWith(t =>
            {
                isFailed = t.IsFaulted || t.IsCanceled;

                if (isFailed)
                    Log.Error(t.Exception, "Couldn't ensure compatibility tool");

                signal.Set();
            });

            App.StartLoading(Strings.PreparingForCompatTool, Strings.PleaseBePatient);
            signal.WaitOne();
            signal.Dispose();

            if (isFailed)
                return null!;

            App.StartLoading(Strings.StartingGame, Strings.HaveFun);

            runner = new UnixGameRunner(Program.CompatibilityTools, dalamudLauncher, dalamudOk);

            // Taiwan uses unencrypted args, so paths must be quoted
            var userPath = Program.CompatibilityTools.UnixToWinePath(App.Settings.GameConfigPath!.FullName);
            gameArgs += $" UserPath=\"{userPath}\"";

            gameArgs = gameArgs.Trim();
        }
        else
        {
            throw new NotImplementedException();
        }

        // Taiwan: Launch with unencrypted session args
        var launchedProcess = App.TaiwanLauncher.LaunchGame(runner,
            sessionId,
            App.Settings.GamePath!,
            App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware),
            gameArgs);

        // Hide the launcher if not Steam Deck or if using as a compatibility tool (XLM)
        // Show the Steam Deck prompt if on steam deck and not using as a compatibility tool
        if (!Program.IsSteamDeckHardware || CoreEnvironmentSettings.IsSteamCompatTool)
        {
            Hide();
        }
        else
        {
            App.State = LauncherApp.LauncherState.SteamDeckPrompt;
        }

        if (launchedProcess == null)
        {
            Log.Information("GameProcess was null...");
            IsLoggingIn = false;
            return null!;
        }

        var addonMgr = new AddonManager();

        try
        {
            App.Settings.Addons ??= new List<AddonEntry>();

            var addons = App.Settings.Addons.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();

            addonMgr.RunAddons(launchedProcess.Id, addons);
        }
        catch (Exception)
        {
            IsLoggingIn = false;
            addonMgr.StopAddons();
            throw;
        }

        Log.Debug("Waiting for game to exit");

        await Task.Run(() => launchedProcess!.WaitForExit()).ConfigureAwait(false);

        Log.Verbose("Game has exited");

        if (addonMgr.IsRunning)
            addonMgr.StopAddons();

        return launchedProcess!;
    }

    private void PersistAccount(string username, string password, bool isOtp)
    {
        // Update account password.
        if (App.Accounts.CurrentAccount != null && App.Accounts.CurrentAccount.UserName.Equals(username, StringComparison.Ordinal) &&
            App.Accounts.CurrentAccount.Password != password &&
            App.Accounts.CurrentAccount.SavePassword)
            App.Accounts.UpdatePassword(App.Accounts.CurrentAccount, password);

        if (App.Accounts.CurrentAccount is null || App.Accounts.CurrentAccount.Id != $"{username}-{isOtp}-False")
        {
            var accountToSave = new XivAccount(username)
            {
                Password = password,
                SavePassword = true,
                UseOtp = isOtp,
                IsFreeTrial = false,
                UseSteamServiceAccount = false
            };
            App.Accounts.AddAccount(accountToSave);
            App.Accounts.CurrentAccount = accountToSave;
        }
    }

    private Task<bool> InstallGamePatch(PatchListEntry[] pendingPatches)
    {
        Debug.Assert(pendingPatches != null, "pendingPatches != null ASSERTION FAILED");

        return TryHandlePatchAsync(Repository.Ffxiv, pendingPatches, "");
    }

    private async Task<bool> TryHandlePatchAsync(Repository repository, PatchListEntry[] pendingPatches, string sid)
    {
        // BUG(goat): This check only behaves correctly on Windows - the mutex doesn't seem to disappear on Linux, .NET issue?
#if WIN32
        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
        {
            App.ShowMessageBlocking( "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open.", "XIVLauncher");
            Environment.Exit(0);
            return false; // This line will not be run.
        }
#endif

        if (GameHelpers.CheckIsGameOpen())
        {
            App.ShowMessageBlocking(Strings.CannotPatchGameOpenError, "XIVLauncher");

            return false;
        }

        using var installer = new PatchInstaller(App.Settings.GamePath, App.Settings.KeepPatches ?? false);
        using var acquisition = new AriaPatchAcquisition(new FileInfo(Path.Combine(App.Storage.GetFolder("logs").FullName, "aria2.log")));
        Program.Patcher = new PatchManager(acquisition, App.Settings.PatchSpeedLimit, repository, pendingPatches, App.Settings.GamePath,
                                           App.Settings.PatchPath, installer, App.Launcher, sid);
        Program.Patcher.OnFail += PatcherOnFail;
        installer.OnFail += this.InstallerOnFail;

        this.App.StartLoading(string.Format(Strings.NowPatching, repository.ToString().ToLowerInvariant()), canCancel: false, isIndeterminate: false);

        try
        {
            var token = new CancellationTokenSource();
            var statusThread = new Thread(UpdatePatchStatus);

            statusThread.Start();

            void UpdatePatchStatus()
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(30);

                    App.LoadingPage.Line2 = string.Format(Strings.WorkingOnStatus, Program.Patcher.CurrentInstallIndex, Program.Patcher.Downloads.Count);
                    App.LoadingPage.Line3 = string.Format(Strings.LeftToDownloadStatus, MathHelpers.BytesToString(Program.Patcher.AllDownloadsLength < 0 ? 0 : Program.Patcher.AllDownloadsLength),
                        MathHelpers.BytesToString(Program.Patcher.Speeds.Sum()));

                    App.LoadingPage.Progress = Program.Patcher.CurrentInstallIndex / (float)Program.Patcher.Downloads.Count;
                }
            }

            try
            {
                await Program.Patcher.PatchAsync(false).ConfigureAwait(false);
            }
            finally
            {
                token.Cancel();
                statusThread.Join(TimeSpan.FromMilliseconds(1000));
            }

            return true;
        }
        catch (PatchInstallerException ex)
        {
            App.ShowMessageBlocking(string.Format(Strings.PatchInstallerStartFailError, ex.Message), Strings.XIVLauncherError);
        }
        catch (NotEnoughSpaceException sex)
        {
            switch (sex.Kind)
            {
                case NotEnoughSpaceException.SpaceKind.Patches:
                    App.ShowMessageBlocking(
                        string.Format(Strings.NotEnoughSpacePatchesError,
                            MathHelpers.BytesToString(sex.BytesRequired), MathHelpers.BytesToString(sex.BytesFree)), Strings.XIVLauncherError);
                    break;

                case NotEnoughSpaceException.SpaceKind.AllPatches:
                    App.ShowMessageBlocking(
                        string.Format(Strings.NotEnoughSpaceAllPatchesError,
                            MathHelpers.BytesToString(sex.BytesRequired), MathHelpers.BytesToString(sex.BytesFree)), Strings.XIVLauncherError);
                    break;

                case NotEnoughSpaceException.SpaceKind.Game:
                    App.ShowMessageBlocking(
                        string.Format(Strings.NotEnoughSpaceGameError,
                            MathHelpers.BytesToString(sex.BytesRequired), MathHelpers.BytesToString(sex.BytesFree)), Strings.XIVLauncherError);
                    break;

                default:
                    Debug.Assert(false, "HandlePatchAsync:Invalid NotEnoughSpaceException.SpaceKind value.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during patching");
            App.ShowExceptionBlocking(ex, "HandlePatchAsync");
        }
        finally
        {
            App.State = LauncherApp.LauncherState.Main;
        }

        return false;
    }

    private void PatcherOnFail(PatchListEntry patch, string context)
    {
        App.ShowMessageBlocking(string.Format(Strings.CannotVerifyGameFilesError, context, patch.VersionId), Strings.XIVLauncherError);
        Environment.Exit(0);
    }

    private void InstallerOnFail()
    {
        App.ShowMessageBlocking(Strings.PatchInstallerGenericError, Strings.XIVLauncherError);
        Environment.Exit(0);
    }

    private void Hide()
    {
        Program.HideWindow();
    }

    private void Reactivate()
    {
        IsLoggingIn = false;
        this.App.State = LauncherApp.LauncherState.Main;

        Program.ShowWindow();
    }
}

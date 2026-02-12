using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

namespace XIVLauncher.Core.Util;

/// <summary>
/// Obtains a reCAPTCHA Enterprise token by spawning a Python3/PyGObject/WebKitGTK script.
/// The Taiwan login API requires this token (domain-locked to launcher.ffxiv.com.tw).
/// </summary>
public class CaptchaService : IDisposable
{
    private const string RECAPTCHA_SITE_KEY = "6Ld6VmorAAAAANQdQeqkaOeScR42qHC7Hyalq00r";
    private const string BASE_URI = "https://launcher.ffxiv.com.tw/";

    private Process? helperProcess;
    private string? tempScriptPath;

    public async Task<string?> GetCaptchaTokenAsync(CancellationToken ct = default)
    {
        var python = FindPython();
        if (python == null)
        {
            Log.Error("CaptchaService: python3 not found in PATH");
            return null;
        }

        Log.Information("CaptchaService: Using python at {Path}", python);

        try
        {
            this.tempScriptPath = Path.Combine(Path.GetTempPath(), $"xivtc-captcha-{Guid.NewGuid():N}.py");
            await File.WriteAllTextAsync(this.tempScriptPath, GetPythonScript(), ct).ConfigureAwait(false);

            this.helperProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    ArgumentList = { this.tempScriptPath },
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            this.helperProcess.Start();

            // Send HTML on stdin, then close
            var html = GetCaptchaHtml();
            await this.helperProcess.StandardInput.WriteAsync(html).ConfigureAwait(false);
            this.helperProcess.StandardInput.Close();

            var tokenTask = this.helperProcess.StandardOutput.ReadLineAsync();
            var errorTask = this.helperProcess.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));

            var completedTask = await Task.WhenAny(
                tokenTask,
                Task.Delay(Timeout.Infinite, cts.Token)
            ).ConfigureAwait(false);

            if (completedTask != tokenTask)
            {
                Log.Warning("CaptchaService: Timed out waiting for token");
                return null;
            }

            var token = await tokenTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrEmpty(stderr))
                Log.Debug("CaptchaService helper stderr: {Stderr}", stderr);

            if (!string.IsNullOrEmpty(token))
            {
                Log.Information("CaptchaService: Got reCAPTCHA token ({Length} chars)", token.Length);
                return token;
            }

            Log.Warning("CaptchaService: No token received from helper");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "CaptchaService: Helper process error");
            return null;
        }
        finally
        {
            CleanupTempScript();
        }
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string GetPythonScript()
    {
        // Python script reads HTML from stdin, displays it in WebKitGTK with spoofed origin,
        // and prints the token to stdout when received via JS message handler.
        return @"import sys, gi
gi.require_version('Gtk', '3.0')
gi.require_version('WebKit2', '4.1')
from gi.repository import Gtk, WebKit2

BASE_URI = '" + BASE_URI + @"'
html = sys.stdin.read()

def on_message(manager, js_result, *args):
    value = js_result.get_js_value()
    token = value.to_string()
    if token:
        print(token, flush=True)
    Gtk.main_quit()

def on_destroy(widget, *args):
    Gtk.main_quit()

Gtk.init(None)
ucm = WebKit2.UserContentManager()
ucm.connect('script-message-received::tokenHandler', on_message)
ucm.register_script_message_handler('tokenHandler')
webview = WebKit2.WebView.new_with_user_content_manager(ucm)
webview.load_html(html, BASE_URI)
window = Gtk.Window()
window.set_title('FFXIV Verification')
window.set_default_size(480, 360)
window.connect('destroy', on_destroy)
window.add(webview)
window.show_all()
Gtk.main()
";
    }

    private static string GetCaptchaHtml()
    {
        return @"<!DOCTYPE html>
<html><head>
    <meta charset='utf-8'>
    <title>FFXIV Login Verification</title>
    <script src='https://www.google.com/recaptcha/enterprise.js?render=" + RECAPTCHA_SITE_KEY + @"'></script>
    <style>
        body { font-family: sans-serif; background: #1a1a2e; color: #fff;
               display: flex; justify-content: center; align-items: center;
               min-height: 100vh; margin: 0; }
        .box { text-align: center; padding: 40px; }
        .spinner { border: 4px solid rgba(255,255,255,0.3); border-top: 4px solid #fff;
                   border-radius: 50%; width: 40px; height: 40px;
                   animation: spin 1s linear infinite; margin: 20px auto; }
        @keyframes spin { to { transform: rotate(360deg); } }
        .success { color: #6bff6b; }
        .error { color: #ff6b6b; }
    </style>
</head><body>
    <div class='box'>
        <h2 id='title'>Verifying...</h2>
        <div class='spinner' id='spinner'></div>
        <p id='status'>Obtaining reCAPTCHA verification...</p>
    </div>
    <script>
        grecaptcha.enterprise.ready(async function() {
            try {
                const token = await grecaptcha.enterprise.execute('" + RECAPTCHA_SITE_KEY + @"', {action: 'LOGIN'});
                document.getElementById('status').textContent = 'Verification complete!';
                document.getElementById('spinner').style.display = 'none';
                document.getElementById('title').textContent = 'Done!';
                document.getElementById('title').className = 'success';
                document.getElementById('status').textContent = 'This window will close automatically.';

                if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.tokenHandler) {
                    window.webkit.messageHandlers.tokenHandler.postMessage(token);
                } else {
                    document.getElementById('status').textContent = 'Error: Message handler not available';
                    document.getElementById('title').className = 'error';
                }
            } catch (err) {
                document.getElementById('spinner').style.display = 'none';
                document.getElementById('title').textContent = 'Error';
                document.getElementById('title').className = 'error';
                document.getElementById('status').textContent = err.message;
            }
        });
    </script>
</body></html>";
    }

    private void CleanupTempScript()
    {
        if (this.tempScriptPath != null)
        {
            try { File.Delete(this.tempScriptPath); }
            catch { }
            this.tempScriptPath = null;
        }
    }

    public void Dispose()
    {
        if (this.helperProcess is { HasExited: false })
        {
            try { this.helperProcess.Kill(); }
            catch { }
        }

        this.helperProcess?.Dispose();
        this.helperProcess = null;
        CleanupTempScript();
    }
}

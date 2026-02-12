using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using Serilog;

using XIVLauncher.Core.Accounts.Secrets;

namespace XIVLauncher.Core.Util;

/// <summary>
/// TOTP (Time-based One-Time Password) service for Taiwan OTP.
/// Uses HMAC-SHA1 per RFC 4226/6238.
/// Stores secrets via ISecretProvider (libsecret on Linux).
/// </summary>
public class TotpService : IDisposable
{
    private const string OTP_SECRET_PREFIX = "XIVTC-OTP-";
    private const int TIME_STEP = 30;
    private const int CODE_DIGITS = 6;

    private byte[]? secretKey;
    private System.Timers.Timer? refreshTimer;

    /// <summary>Current OTP code.</summary>
    public string CurrentCode { get; private set; } = string.Empty;

    /// <summary>Seconds remaining until next code.</summary>
    public int SecondsRemaining { get; private set; }

    /// <summary>Whether an OTP secret is configured.</summary>
    public bool IsConfigured => this.secretKey != null && this.secretKey.Length > 0;

    /// <summary>Event fired when a new OTP code is generated.</summary>
    public event Action<string>? OtpCodeChanged;

    /// <summary>Event fired when the remaining seconds changes.</summary>
    public event Action<int>? SecondsRemainingChanged;

    /// <summary>
    /// Initialize OTP for a specific account by loading the stored secret.
    /// </summary>
    public void InitializeForAccount(string accountId, ISecretProvider secrets)
    {
        StopAutoRefresh();
        this.secretKey = null;
        CurrentCode = string.Empty;

        var key = OTP_SECRET_PREFIX + accountId;
        var base32Secret = secrets.GetPassword(key);

        if (!string.IsNullOrEmpty(base32Secret))
        {
            this.secretKey = Base32Decode(base32Secret.Replace(" ", "").ToUpperInvariant());

            if (IsConfigured)
            {
                StartAutoRefresh();
            }
        }
    }

    /// <summary>
    /// Set (store) an OTP secret from a base32-encoded string.
    /// </summary>
    public bool SetSecret(string accountId, string base32Secret, ISecretProvider secrets)
    {
        try
        {
            base32Secret = base32Secret.Replace(" ", "").ToUpperInvariant();
            var decoded = Base32Decode(base32Secret);

            if (decoded == null || decoded.Length == 0)
                return false;

            this.secretKey = decoded;

            var key = OTP_SECRET_PREFIX + accountId;
            secrets.SavePassword(key, base32Secret);

            StartAutoRefresh();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TotpService: Failed to set secret");
            return false;
        }
    }

    /// <summary>
    /// Clear the stored OTP secret for an account.
    /// </summary>
    public void ClearSecret(string accountId, ISecretProvider secrets)
    {
        this.secretKey = null;
        CurrentCode = string.Empty;
        StopAutoRefresh();

        var key = OTP_SECRET_PREFIX + accountId;
        secrets.DeletePassword(key);
    }

    /// <summary>
    /// Check if an account has an OTP secret stored.
    /// </summary>
    public static bool HasSecret(string accountId, ISecretProvider secrets)
    {
        var key = OTP_SECRET_PREFIX + accountId;
        return !string.IsNullOrEmpty(secrets.GetPassword(key));
    }

    /// <summary>
    /// Generate the current OTP code.
    /// </summary>
    public string GenerateCode()
    {
        if (this.secretKey == null || this.secretKey.Length == 0)
            return string.Empty;

        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = unixTime / TIME_STEP;

        return GenerateTotp(this.secretKey, counter);
    }

    /// <summary>
    /// Get seconds remaining until the next code.
    /// </summary>
    public int GetSecondsRemaining()
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return TIME_STEP - (int)(unixTime % TIME_STEP);
    }

    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        UpdateCode();

        this.refreshTimer = new System.Timers.Timer(1000);
        this.refreshTimer.Elapsed += (s, e) => UpdateCode();
        this.refreshTimer.AutoReset = true;
        this.refreshTimer.Start();
    }

    private void StopAutoRefresh()
    {
        this.refreshTimer?.Stop();
        this.refreshTimer?.Dispose();
        this.refreshTimer = null;
    }

    private void UpdateCode()
    {
        var newCode = GenerateCode();
        var seconds = GetSecondsRemaining();

        if (newCode != CurrentCode)
        {
            CurrentCode = newCode;
            OtpCodeChanged?.Invoke(CurrentCode);
        }

        if (seconds != SecondsRemaining)
        {
            SecondsRemaining = seconds;
            SecondsRemainingChanged?.Invoke(SecondsRemaining);
        }
    }

    /// <summary>
    /// Generate TOTP code using HMAC-SHA1 (RFC 4226).
    /// </summary>
    private static string GenerateTotp(byte[] secret, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation
        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var otp = binaryCode % (int)Math.Pow(10, CODE_DIGITS);
        return otp.ToString().PadLeft(CODE_DIGITS, '0');
    }

    /// <summary>
    /// Decode a base32-encoded string to bytes.
    /// </summary>
    private static byte[]? Base32Decode(string base32)
    {
        if (string.IsNullOrEmpty(base32))
            return null;

        base32 = base32.TrimEnd('=');

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in base32)
        {
            var charIndex = alphabet.IndexOf(c);
            if (charIndex < 0)
                return null;

            buffer = (buffer << 5) | charIndex;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
            }
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        StopAutoRefresh();
        GC.SuppressFinalize(this);
    }
}

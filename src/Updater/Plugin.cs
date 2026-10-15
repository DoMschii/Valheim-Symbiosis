using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Valheim_Symbiosis_Updater
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "domschii.valheimsymbiosis.updater";
        public const string PluginName = "Valheim Symbiosis Updater";
        public const string PluginVersion = "4.5.1";

        private const string ConfigFileName = "Valheim_Symbiosis_Update.json";
        private ManualLogSource _log;
        private UpdateSettings _settings;

        private void Awake()
        {
            _log = Logger;
            try
            {
                var pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
                var cfg = Path.Combine(pluginDir, ConfigFileName);
                _settings = LoadSettings(cfg);
                if (_settings == null || !_settings.Enabled || string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repository))
                {
                    _log.LogInfo("Updater disabled or not configured.");
                    return;
                }

                StartCoroutine(CheckForUpdate(pluginDir));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Updater startup failed: " + ex.Message);
            }
        }

        private IEnumerator CheckForUpdate(string pluginDir)
        {
            var api = $"https://api.github.com/repos/{_settings.Owner}/{_settings.Repository}/releases/latest";
            using (var req = UnityWebRequest.Get(api))
            {
                req.SetRequestHeader("User-Agent", "Valheim-Symbiosis-Updater/" + PluginVersion);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _log.LogWarning("Update check failed: " + req.error);
                    yield break;
                }

                var json = req.downloadHandler.text ?? string.Empty;
                var tag = MatchValue(json, "tag_name");
                if (string.IsNullOrWhiteSpace(tag)) yield break;

                var remote = ParseVersion(tag);
                var local = ParseVersion(PluginVersion);
                if (remote == null || local == null || remote <= local)
                {
                    _log.LogInfo($"Valheim Symbiosis is current ({PluginVersion}).");
                    yield break;
                }

                var zipUrl = FindAssetUrl(json, ".zip", "Standalone_Installer");
                var shaUrl = FindAssetUrl(json, ".sha256", null);
                if (string.IsNullOrWhiteSpace(zipUrl) || string.IsNullOrWhiteSpace(shaUrl))
                {
                    _log.LogWarning("New release found, but updater assets are incomplete.");
                    yield break;
                }

                var tempRoot = Path.Combine(Path.GetTempPath(), "Valheim_Symbiosis_Update");
                Directory.CreateDirectory(tempRoot);
                var zipPath = Path.Combine(tempRoot, "update.zip");
                var shaPath = Path.Combine(tempRoot, "update.sha256");

                yield return Download(zipUrl, zipPath);
                if (!File.Exists(zipPath)) yield break;
                yield return Download(shaUrl, shaPath);
                if (!File.Exists(shaPath)) yield break;

                QueueInstall(pluginDir, zipPath, shaPath, tag);
            }
        }

        private IEnumerator Download(string url, string path)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "Valheim-Symbiosis-Updater/" + PluginVersion);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _log.LogWarning("Update download failed: " + req.error);
                    yield break;
                }
                File.WriteAllBytes(path, req.downloadHandler.data);
            }
        }

        private void QueueInstall(string pluginDir, string zipPath, string shaPath, string tag)
        {
            try
            {
                var helper = Path.Combine(Path.GetTempPath(), "Valheim_Symbiosis_ApplyUpdate.ps1");
                var pid = Process.GetCurrentProcess().Id;
                var script = @"
param([int]$Pid,[string]$Zip,[string]$Sha,[string]$PluginDir)
$ErrorActionPreference='Stop'
try { Wait-Process -Id $Pid -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 2
$expected=((Get-Content -LiteralPath $Sha -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
$actual=(Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash.ToUpperInvariant()
if($expected -ne $actual){ throw "SHA256 mismatch" }
$stage=Join-Path $env:TEMP ('Valheim_Symbiosis_Apply_'+[guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Zip -DestinationPath $stage -Force
$payload=Join-Path $stage 'Payload'
if(!(Test-Path $payload)){ throw 'Payload folder missing in update package.' }
New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
Get-ChildItem -LiteralPath $payload -Filter '*.dll' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $PluginDir $_.Name) -Force }
$cfg=Join-Path $payload 'Valheim_Symbiosis_Update.json'
if(Test-Path $cfg){ Copy-Item -LiteralPath $cfg -Destination (Join-Path $PluginDir 'Valheim_Symbiosis_Update.json') -Force }
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Zip,$Sha -Force -ErrorAction SilentlyContinue
";
                File.WriteAllText(helper, script);
                var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" -Pid {pid} -Zip \"{zipPath}\" -Sha \"{shaPath}\" -PluginDir \"{pluginDir}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _log.LogInfo($"Update {tag} downloaded. It will be installed automatically after Valheim closes.");
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not queue update: " + ex.Message);
            }
        }

        private static UpdateSettings LoadSettings(string path)
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return new UpdateSettings
            {
                Enabled = !Regex.IsMatch(json, "\\\"enabled\\\"\\s*:\\s*false", RegexOptions.IgnoreCase),
                Owner = MatchValue(json, "owner"),
                Repository = MatchValue(json, "repository")
            };
        }

        private static string MatchValue(string json, string key)
        {
            var m = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FindAssetUrl(string json, string suffix, string contains)
        {
            var matches = Regex.Matches(json, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"[\\s\\S]*?\\\"browser_download_url\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var name = m.Groups[1].Value;
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(contains) && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return m.Groups[2].Value.Replace("\\/", "/");
            }
            return null;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var m = Regex.Match(value, @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        private sealed class UpdateSettings
        {
            public bool Enabled;
            public string Owner;
            public string Repository;
        }
    }
}

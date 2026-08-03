using System;
using System.IO;
using UnityEngine;

namespace ObjectSpawning
{
    // The dev PC's LAN IP can change any time it reconnects to WiFi (sleep/wake, router
    // reboot, DHCP lease renewal), which would otherwise require a full rebuild+reinstall to
    // fix. If a one-line override file exists on-device, it wins over the baked-in default --
    // update it with a single fast `adb push`, no Unity rebuild needed. Shared by every client
    // that talks to the backend so they all pick up an override the same way.
    public static class BackendUrlResolver
    {
        const string OverrideFileName = "backend_url.txt";

        public static string Resolve(string fallback)
        {
            try
            {
                var overridePath = Path.Combine(Application.persistentDataPath, OverrideFileName);
                if (File.Exists(overridePath))
                {
                    var overrideUrl = File.ReadAllText(overridePath).Trim();
                    if (!string.IsNullOrEmpty(overrideUrl))
                        return overrideUrl;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BackendUrlResolver] Failed to read backend URL override: {e.Message}");
            }

            return fallback;
        }
    }
}

using System;
using UnityEngine;

namespace Manimal.Terminal
{
    // EFT already ships a DXGI video-memory wrapper and attaches its command
    // buffer to EFT.CameraControl.CameraManager. Reuse that authoritative reading instead of trying
    // to infer residency pressure from Unity object counts.
    internal static class TerminalVramProbe
    {
        private const ulong Mib = 1024UL * 1024UL;
        private static float _nextRead;
        private static bool _valid, _warned;
        private static long _totalMb, _budgetMb, _usedMb;

        internal static void ResetForRaid()
        {
            _nextRead = 0f;
            _valid = false;
            _warned = false;
            _totalMb = _budgetMb = _usedMb = -1;
        }

        internal static bool Read(out long totalMb, out long budgetMb, out long usedMb)
        {
            if (Time.realtimeSinceStartup >= _nextRead)
            {
                _nextRead = Time.realtimeSinceStartup + 0.25f;
                try
                {
                    var cc = EFT.CameraControl.CameraManager.Instance;
                    if (cc != null)
                    {
                        cc.GetVRamUsage(out ulong total, out ulong budget, out ulong used);
                        _totalMb = (long)(total / Mib);
                        _budgetMb = (long)(budget / Mib);
                        _usedMb = (long)(used / Mib);
                        _valid = total > 0 && budget > 0;
                    }
                }
                catch (Exception e)
                {
                    _valid = false;
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning($"[VRAM] EFT memory query unavailable: {e.Message}");
                    }
                }
            }

            totalMb = _totalMb;
            budgetMb = _budgetMb;
            usedMb = _usedMb;
            return _valid;
        }
    }
}

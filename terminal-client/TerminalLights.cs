// RECOVERED FROM THE COMPILED DLL 2026-08-16: a botched git checkout discarded this
// file's working copy (an entire session of changes); logic was restored by
// decompiling our own last-good build, so the original comments are gone. the
// session's design notes live in docs/memory/terminal-backport.md — key systems in
// here: authored lamp restore (terminal_lights.json, position-keyed, culling-max
// intensities), TOD sky self-drive + probes + hour offset, sky-tint ambient via
// LevelSettings (NVG hemisphere preserved), night-sky substitute (stands down when
// WeatherController lives), hitch logger, weapon/loot light filters, lamp-system
// prefix skips, cutscene holds.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Weather;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{


    internal static class TerminalLights
    {
    	[HarmonyPatch]
    	internal static class Patch_LampsDead
    	{
    		private static IEnumerable<MethodBase> TargetMethods()
    		{
    			string[] array = new string[4] { "Awake", "OnEnable", "ManualUpdate", "TurnLights" };
    			foreach (string m in array)
    			{
    				MethodInfo mi = AccessTools.Method(typeof(LampController), m, (Type[])null, (Type[])null);
    				if (mi != null)
    				{
    					yield return mi;
    				}
    			}
    		}

    		[HarmonyPrefix]
		private static bool Prefix(LampController __instance)
		{
			// This prefix includes LampController.ManualUpdate, a per-lamp hot path.
			// The former off-map branch called TerminalLoaded.Check(), which scanned
			// every loaded scene for every lamp every frame. Streets has thousands of
			// lamp objects, turning an innocent map check into catastrophic overhead.
			// Component scene ownership is authoritative during early load and O(1).
			try
			{
				var sceneName = __instance != null ? __instance.gameObject.scene.name : null;
				return string.IsNullOrEmpty(sceneName)
					|| !sceneName.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase);
			}
			catch { return !TerminalGate.On; }
		}
    	}

    	[HarmonyPatch(typeof(GameWorld), "OnGameStarted")]
    	internal static class Patch_LightsAtRaidStart
    	{
    		[HarmonyPostfix]
    		private static void Postfix()
    		{
    			//IL_0014: Unknown result type (might be due to invalid IL or missing references)
    			//IL_001a: Expected O, but got Unknown
    			if (TerminalGate.On)
    			{
    				GameObject val = new GameObject("Terminal_Lights");
    				val.AddComponent<Host>();
    			}
    		}
    	}

    	internal class Host : MonoBehaviour
    	{
    		private void Start()
    		{
    			((MonoBehaviour)this).StartCoroutine(Init());
    		}

    		private IEnumerator Init()
    		{
    			DiscoverLamps();
    			yield return null;
    			ApplyLamps();
    			yield return null;
    			ApplyAmbient();
    			yield return null;
    			TerminalVolumetricLights.Restore();
    			yield return null;
    			try
    			{
    				TerminalFlares.TryBuild();
    			}
    			catch (Exception ex)
    			{
    				Exception e = ex;
    				Plugin.Log.LogWarning((object)("[Flares] build failed: " + e.Message));
    			}
    		}

    		private void Update()
    		{
    			//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
    			//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
    			//IL_0203: Unknown result type (might be due to invalid IL or missing references)
    			//IL_0208: Unknown result type (might be due to invalid IL or missing references)
    			float unscaledDeltaTime = Time.unscaledDeltaTime;
    			_frameAvg = Mathf.Lerp((_frameAvg <= 0f) ? unscaledDeltaTime : _frameAvg, unscaledDeltaTime, 0.05f);
    			bool flag = _frameAvg > 1.5f / Mathf.Max(30f, (Application.targetFrameRate > 0) ? ((float)Application.targetFrameRate) : 60f);
    			if (flag != _inChop && Time.time - _lastChopEdge > 2f)
    			{
    				_inChop = flag;
    				_lastChopEdge = Time.time;
    				int num = GC.CollectionCount(0);
    				Plugin.Log.LogWarning((object)(string.Format("[Perf] {0} t={1:0.0}s ", flag ? "CHOPPY window start" : "smooth again", Time.time) + $"avgFrame={_frameAvg * 1000f:0.0}ms gc0={num} (delta {num - _lastGc})"));
    				_lastGc = num;
    			}
    			if (Time.frameCount % 10 == 7)
    			{
    				long __t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    				TickSkyTime();
    				TerminalTickProfiler.Add("SkyTime", System.Diagnostics.Stopwatch.GetTimestamp() - __t0);
    			}
    			// 2026-08-21: removed the periodic _lastAmbient=-1f force-resample
    			// while hunting a cyclic FPS/lighting chop. it re-ran ApplyAmbient
    			// every 300 frames and produced identical values every time (log
    			// spam confirms), so at best it wasted work; at worst it was the
    			// source of the periodic re-apply. config-diff check at line 30
    			// still re-runs it when the user tweaks the slider.
    			if (Time.frameCount % 120 == 23)
    			{
    				long __t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    				TerminalWeather.TryStage();
    				TerminalWeather.TickProbe();
    				TerminalTickProfiler.Add("WeatherTk", System.Diagnostics.Stopwatch.GetTimestamp() - __t0);
    			}
			{ long __t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickNightSky(); TerminalTickProfiler.Add("NightSky", System.Diagnostics.Stopwatch.GetTimestamp() - __t0); }
			// Terminal's retail CullingManager is absent in some SPT/Fika raids. In
			// that case ApplyLamps cannot leave every native light enabled: thousands
			// of LightFlicker/visibility components then run in Update on the visual
			// host. Maintain the same bounded bubble ourselves during gameplay.
			TrackManagerlessGameplayLights();
			if (Plugin.SpatialAudio.Value)
    			{
    				if (Time.frameCount % 120 == 7)
    				{
    					TerminalAcoustics.TickSpatialInit();
    					TerminalAcoustics.TickSpatialVerdict();
    					TerminalAcoustics.TryBuildEnvironmentTriggers();
    					if (Plugin.AmbientRetail.Value)
    					{
    						TerminalAcoustics.TryStageAmbient();
    					}
    					TerminalAcoustics.TryApplyAmbientAuthoring();
    				}
				if ((Time.frameCount & 1) == 0)
				{
					long __t0 = System.Diagnostics.Stopwatch.GetTimestamp();
					TerminalAcoustics.DriveEnvironment();
					TerminalTickProfiler.Add("EnvDrive", System.Diagnostics.Stopwatch.GetTimestamp() - __t0);
				}
    			}
    			KeyboardShortcut value = Plugin.LightProbeKey.Value;
    			if (value.IsDown() || (Plugin.DevMode.Value && Time.frameCount % 300 == 33))
    			{
    				DumpNearPlayerLights();
    			}
    			value = Plugin.SoundProbeKey.Value;
    			if (value.IsDown())
    			{
    				DumpNearPlayerSounds();
    			}
    			if ((UnityEngine.Object)(object)_opticCam == (UnityEngine.Object)null)
    			{
    				Camera[] allCameras = Camera.allCameras;
    				for (int i = 0; i < allCameras.Length; i++)
    				{
    					if ((UnityEngine.Object)(object)allCameras[i] != (UnityEngine.Object)null && ((UnityEngine.Object)allCameras[i]).name == "BaseOpticCamera(Clone)")
    					{
    						_opticCam = allCameras[i];
    						break;
    					}
    				}
    			}
    			if ((UnityEngine.Object)(object)_opticCam != (UnityEngine.Object)null)
    			{
    				TOD_Scattering component = ((Component)_opticCam).GetComponent<TOD_Scattering>();
    				if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null && ((Behaviour)component).enabled)
    				{
    					((Behaviour)component).enabled = false;
    					Plugin.Log.LogInfo((object)"[Lights] optic camera TOD_Scattering disabled (dark magnified scopes)");
    				}
    			}
    			if (Time.frameCount % 30 == 0)
    			{
    				if (Plugin.LampIntensity.Value != _lastLamp || Plugin.LampShadows.Value != _lastShadows || Plugin.LightCullDistance.Value != _lastLightCull)
    				{
    					ApplyLamps();
    				}
    				if (Plugin.AmbientIntensity.Value != _lastAmbient
    					|| Plugin.AmbientColorOverride.Value != _lastColorOverride
    					|| Plugin.AmbientColorR.Value != _lastColorR
    					|| Plugin.AmbientColorG.Value != _lastColorG
    					|| Plugin.AmbientColorB.Value != _lastColorB)
    				{
    					ApplyAmbient();
    				}
    			}
    		}
    	}

    	private static readonly List<Light> _lamps = new List<Light>();

    	private static float _lastLamp = -1f;

    	private static float _lastAmbient = -1f;

    	private static bool _lastColorOverride;

    	private static float _lastColorR = -1f;

    	private static float _lastColorG = -1f;

    	private static float _lastColorB = -1f;

    	private static float _lastLightCull = -1f;

    	private static Camera _opticCam;

    	private static bool _lastShadows;

    	internal static bool CutsceneHold;

    	private static TOD_Sky _sky;

    	private static bool _skyTimeLogged;

    	private static int _skyProbes;

    	private static int _skyTicks;

    	private static bool _ambientAuthorityTaken;

    	private static float _frameAvg;

    	private static bool _inChop;

    	private static float _lastChopEdge;

    	private static int _lastGc;

    	private static float _authoredBrightness = -1f;

    	private static float _authoredScatter = -1f;

    	private static Dictionary<(int, int, int), List<float[]>> _authored;

    	private static bool _authoredTried;

	private static FieldInfo _cloMaxIntensity;

	// Cutscene cameras fly far away from the player's body. The old workaround
	// locked Tarkov's light culler and enabled every CullingLightObject on the map
	// (roughly 1,800 realtime lights). Besides making the cutscenes GPU-bound, the
	// release path left every light enabled until the player crossed another culling
	// cell. Keep a cached map-light list and expose only a bounded bubble around the
	// animated camera instead.
	private const float CutsceneLightRadius = 80f;
	private const float CutsceneLightRefreshSeconds = 0.20f;
	private const float GameplayLightRefreshSeconds = 0.50f;
	private static readonly List<CullingLightObject> _nativeCutsceneLights = new List<CullingLightObject>();
	private static readonly HashSet<CullingLightObject> _cutsceneVisibleLights = new HashSet<CullingLightObject>();
	private static bool _cutsceneLightStateKnown;
	private static float _nextCutsceneLightRefresh;
	private static float _nextManagerlessLightRefresh;

	internal static void ResetForNewRaid()
	{
		_lamps.Clear();
		_nativeCutsceneLights.Clear();
		_cutsceneVisibleLights.Clear();
		_cutsceneLightStateKnown = false;
		_lastLamp = (_lastAmbient = (_lastLightCull = -1f));
		CutsceneHold = false;
		_nextCutsceneLightRefresh = 0f;
		_nextManagerlessLightRefresh = 0f;
    		_sky = null;
    		_skyTimeLogged = false;
    		_ambientAuthorityTaken = false;
    	}

    	internal static void TickSkyTime()
    	{
    		//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01fc: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
    		//IL_023d: Unknown result type (might be due to invalid IL or missing references)
    		//IL_03d0: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0407: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0413: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0429: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0435: Unknown result type (might be due to invalid IL or missing references)
    		//IL_043c: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0441: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0446: Unknown result type (might be due to invalid IL or missing references)
    		try
    		{
    			if (!_sky)
    			{
    				_sky = UnityEngine.Object.FindObjectOfType<TOD_Sky>();
    			}
    			if (!_sky || !_sky.Initialized)
    			{
    				return;
    			}
                ITODSky instance = TODSkyProvider.Instance;
    			GameDateTime val = ((instance == null) ? null : instance.CurrentTime?.GameDateTime) ?? Singleton<GameWorld>.Instance?.GameDateTime;
    			if (val == null)
    			{
    				return;
    			}
    			DateTime dateTime = val.Calculate();
    			_sky.Cycle.DateTime = dateTime.AddHours(Plugin.SkyHourOffset.Value);
    			if (!((Behaviour)_sky).enabled)
    			{
    				((Behaviour)_sky).enabled = true;
    				Plugin.Log.LogWarning((object)"[Sky] TOD_Sky component was DISABLED - re-enabled (this is why the solver froze)");
    			}
    			if (!_ambientAuthorityTaken)
    			{
    				_ambientAuthorityTaken = true;
    				try
    				{
    					_sky.Ambient.UpdateInterval = float.MaxValue;
    					_lastAmbient = -1f;
    					Plugin.Log.LogWarning((object)"[Sky] TOD ambient module muzzled (UpdateInterval=max) - flat fill + NvgAmbient own RenderSettings ambient again");
    				}
    				catch (Exception ex)
    				{
    					Plugin.Log.LogWarning((object)("[Sky] ambient muzzle failed: " + ex.Message));
    				}
    			}
    			try
    			{
                _sky.UpdateCelestials();
    			}
    			catch (Exception ex2)
    			{
    				if (_skyProbes < 3)
    				{
    					Plugin.Log.LogWarning((object)("[Sky] self-driven solve threw: " + ex2.Message));
    				}
    			}
    			if (!_skyTimeLogged)
    			{
    				_skyTimeLogged = true;
    				Plugin.Log.LogWarning((object)$"[Sky] TOD hour synced to raid time: {dateTime:HH:mm} (re-synced ~1/s)");
    			}
    			_skyTicks++;
    			if (_skyProbes >= 3 || _skyTicks % 90 != 5)
    			{
    				return;
    			}
    			_skyProbes++;
    			try
    			{
    				Vector3 sunDirection = _sky.SunDirection;
    				Plugin.Log.LogWarning((object)($"[Sky] TOD state: cycleHour={_sky.Cycle.Hour:0.00} " + string.Format("sunDir.y={0:0.000} ({1}) ", sunDirection.y, (sunDirection.y > 0f) ? "SUN IS UP - solver disagrees with the clock" : "sun is down - solver agrees") + $"IsDay={_sky.IsDay} IsNight={_sky.IsNight} LerpValue={_sky.LerpValue:0.000} " + $"atmoBrightness={_sky.Atmosphere.Brightness:0.000} " + $"lat={_sky.World.Latitude:0.0} long={_sky.World.Longitude:0.0} UTC={_sky.World.UTC:0.00} " + $"date={_sky.Cycle.Year}-{_sky.Cycle.Month:00}-{_sky.Cycle.Day:00} zenith={_sky.SunZenith:0.0}"));
    				TOD_Components component = ((Component)_sky).GetComponent<TOD_Components>();
    				if ((bool)component && (bool)component.SunTransform && (bool)component.DomeTransform)
    				{
    					Transform sunTransform = component.SunTransform;
    					Transform domeTransform = component.DomeTransform;
    					ManualLogSource log = Plugin.Log;
    					string text = string.Format("[Sky] sun rig: sunLocalPos={0} sunParent='{1}' ", sunTransform.localPosition, (bool)sunTransform.parent ? ((UnityEngine.Object)sunTransform.parent).name : "NULL");
    					string text2 = $"domeScale={domeTransform.localScale} domePos={domeTransform.position} ";
    					object arg = sunTransform.position;
    					Vector3 val2 = domeTransform.position - sunTransform.position;
    					log.LogWarning((object)(text + text2 + $"sunWorld={arg} sunToDome={val2.magnitude:0.###}"));
    				}
    			}
    			catch (Exception ex3)
    			{
    				Plugin.Log.LogWarning((object)("[Sky] state probe failed: " + ex3.Message));
    			}
    			DumpSkyRig();
    		}
    		catch
    		{
    		}
    	}

    	// black sky with a visible sun means the Sun mesh draws but the Atmosphere
    	// dome doesn't. that's either (a) brightness driven to ~0, or (b) the dome
    	// renderer disabled / its material dead / it got occlusion-culled. dump both
    	// sides so we stop guessing which.
    	internal static void DumpSkyRig()
    	{
    		try
    		{
    			if (!_sky) { Plugin.Log.LogWarning((object)"[SkyRig] no TOD_Sky"); return; }
    			var sb = new StringBuilder();
				sb.Append($"init={_sky.Initialized} atmoBrightness={_sky.Atmosphere.Brightness:0.####} ")
				  .Append($"scattering={_sky.Atmosphere.ScatteringBrightness:0.####} fog={_sky.Atmosphere.Fogginess:0.###} ")
				  .Append($"sunSky={_sky.SunSkyColor} moonSky={_sky.MoonSkyColor} sunMesh={_sky.SunMeshColor} ")
				  .Append($"stars={_sky.Stars.Brightness:0.##}x tile={_sky.Stars.Tiling:0.##} "
				    + $"effectiveStars={(_sky.Stars.Brightness * (1f - _sky.Atmosphere.Fogginess) * (1f - _sky.LerpValue)):0.###} ")
				  .Append($"authoredCapture={_authoredBrightness:0.####} nightStrength={Plugin.SkyNightStrength.Value:0.##} ")
    			  .Append($"weatherCtrl={(bool)WeatherController.Instance} skybox={(RenderSettings.skybox ? RenderSettings.skybox.name : "NULL")} ")
    			  .Append($"ambientMode={RenderSettings.ambientMode}");

    			foreach (var r in ((Component)_sky).GetComponentsInChildren<Renderer>(true))
    			{
    				if (r == null) continue;
    				var m = r.sharedMaterial;
    				sb.Append($"\n    '{((UnityEngine.Object)r).name}' enabled={((Renderer)r).enabled} "
    					+ $"forceOff={r.forceRenderingOff} activeGO={r.gameObject.activeInHierarchy} "
    					+ $"layer={LayerMask.LayerToName(r.gameObject.layer)} "
    					+ $"mat='{(m ? ((UnityEngine.Object)m).name : "NULL")}' "
    					+ $"shader='{(m && m.shader ? m.shader.name : "NULL")}' "
    					+ $"queue={(m ? m.renderQueue : -1)}");
    			}
    			Plugin.Log.LogWarning((object)("[SkyRig] " + sb.ToString()));
    		}
    		catch (Exception e) { Plugin.Log.LogWarning((object)("[SkyRig] dump failed: " + e.Message)); }
    	}

    	internal static void TickNightSky()
    	{
    		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
    		try
    		{
    			if (!_sky || !_sky.Initialized || (bool)WeatherController.Instance)
    			{
    				return;
    			}
    			float value = Plugin.SkyNightStrength.Value;
    			if (!(value <= 0f))
    			{
    				// CAPTURE RACE (2026-08-22, black-sky-at-14:00 hunt): this samples
    				// the LIVE brightness once and lerps toward it forever. if the first
    				// call lands while the sky is dark — mid-load, pre-Initialize, or
    				// after something else zeroed it — we latch near-zero as "authored"
    				// and the sky can never recover. only accept a plausible value, and
    				// keep looking until we get one.
    				if (_authoredBrightness < 0f)
    				{
    					float b = _sky.Atmosphere.Brightness;
    					float s = _sky.Atmosphere.ScatteringBrightness;
    					if (b > 0.02f)
    					{
    						_authoredBrightness = b;
    						_authoredScatter = s;
    						Plugin.Log.LogWarning((object)$"[Sky] authored atmosphere captured: brightness={b:0.###} scattering={s:0.###}");
    					}
    					else
    					{
    						// nothing sane to work from yet — leave the sky alone rather
    						// than lerping it toward a bogus baseline
    						return;
    					}
    				}
    				float y = _sky.SunDirection.y;
    				float hour = _sky.Cycle.Hour;
    				bool flag = hour >= 21f || hour < 6f;
    				float num = Mathf.Clamp01(y * 4f + 0.15f);
    				if (flag)
    				{
    					num = Mathf.Min(num, 1f - value);
    				}
    				_sky.Atmosphere.Brightness = Mathf.Lerp(_authoredBrightness * 0.06f, _authoredBrightness, num);
    				_sky.Atmosphere.ScatteringBrightness = Mathf.Lerp(_authoredScatter * 0.08f, _authoredScatter, num);
    			}
    		}
    		catch
    		{
    		}
    	}

    	internal static void DumpNearPlayerLights()
    	{
    		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
    		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0076: Invalid comparison between Unknown and I4
    		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
    		try
    		{
    			Player val = Singleton<GameWorld>.Instance?.MainPlayer;
    			if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
    			{
    				Plugin.Log.LogWarning((object)"[LightProbe] no main player");
    				return;
    			}
    			int num = 0;
    			Light[] array = UnityEngine.Object.FindObjectsOfType<Light>();
    			foreach (Light val2 in array)
    			{
    				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || !((Behaviour)val2).enabled || val2.intensity <= 0.01f || (int)val2.type == 1)
    				{
    					continue;
    				}
    				Vector3 val3 = ((Component)val2).transform.position - val.Position;
    				if (!(val3.sqrMagnitude > 16f))
    				{
    					string text = ((UnityEngine.Object)((Component)val2).transform).name;
    					Transform parent = ((Component)val2).transform.parent;
    					while ((UnityEngine.Object)(object)parent != (UnityEngine.Object)null)
    					{
    						text = ((UnityEngine.Object)parent).name + "/" + text;
    						parent = parent.parent;
    					}
    					ManualLogSource log = Plugin.Log;
    					string[] obj = new string[6] { "[LightProbe] near-player light: '", text, "' scene='", null, null, null };
    					Scene scene = ((Component)val2).gameObject.scene;
    					obj[3] = scene.name;
    					obj[4] = "' ";
    					obj[5] = $"type={val2.type} intensity={val2.intensity:0.00} range={val2.range:0.0} culled={(UnityEngine.Object)(object)((Component)val2).GetComponent<CullingLightObject>() != (UnityEngine.Object)null}";
    					log.LogWarning((object)string.Concat(obj));
    					num++;
    				}
    			}
    			if (num == 0)
    			{
    				Plugin.Log.LogWarning((object)"[LightProbe] no lit lights within 4m right now");
    			}
    		}
    		catch
    		{
    		}
    	}

    	internal static void DumpNearPlayerSounds()
    	{
    		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
    		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0208: Unknown result type (might be due to invalid IL or missing references)
    		//IL_020d: Unknown result type (might be due to invalid IL or missing references)
    		try
    		{
    			Player val = Singleton<GameWorld>.Instance?.MainPlayer;
    			if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
    			{
    				Plugin.Log.LogWarning((object)"[SoundProbe] no main player");
    				return;
    			}
    			List<(float, string)> list = new List<(float, string)>();
    			AudioSource[] array = UnityEngine.Object.FindObjectsOfType<AudioSource>();
    			foreach (AudioSource val2 in array)
    			{
    				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || !val2.isPlaying || val2.volume <= 0.001f)
    				{
    					continue;
    				}
    				Vector3 val3 = ((Component)val2).transform.position - val.Position;
    				float magnitude = val3.magnitude;
    				bool flag = val2.spatialBlend < 0.5f;
    				if (flag || !(magnitude > 60f))
    				{
    					string text = ((UnityEngine.Object)((Component)val2).transform).name;
    					Transform parent = ((Component)val2).transform.parent;
    					while ((UnityEngine.Object)(object)parent != (UnityEngine.Object)null)
    					{
    						text = ((UnityEngine.Object)parent).name + "/" + text;
    						parent = parent.parent;
    					}
    					float item = (flag ? float.MaxValue : (val2.volume / Mathf.Max(magnitude, 1f)));
    					string[] obj = new string[9]
    					{
    						"[SoundProbe] ",
    						flag ? "GLOBAL/2D " : "",
    						"'",
    						null,
    						null,
    						null,
    						null,
    						null,
    						null
    					};
    					AudioClip clip = val2.clip;
    					obj[3] = ((clip != null) ? ((UnityEngine.Object)clip).name : null) ?? "<procedural>";
    					obj[4] = "' on '";
    					obj[5] = text;
    					obj[6] = "' ";
    					obj[7] = $"vol={val2.volume:0.00} dist={magnitude:0.0}m blend={val2.spatialBlend:0.0} loop={val2.loop} ";
    					object arg = val2.minDistance;
    					object arg2 = val2.maxDistance;
    					Scene scene = ((Component)val2).gameObject.scene;
    					obj[8] = $"min/max={arg:0.0}/{arg2:0.0} scene='{scene.name}'";
    					list.Add((item, string.Concat(obj)));
    				}
    			}
    			list.Sort(((float score, string line) a, (float score, string line) b) => b.score.CompareTo(a.score));
    			for (int num = 0; num < list.Count && num < 20; num++)
    			{
    				Plugin.Log.LogWarning((object)list[num].Item2);
    			}
    			Plugin.Log.LogWarning((object)$"[SoundProbe] {list.Count} playing source(s) within 60m ({Mathf.Max(list.Count - 20, 0)} not shown)");
    		}
    		catch
    		{
    		}
    	}

    	private static bool IsWeaponLight(Light l)
    	{
    		Transform val = ((Component)l).transform;
    		int num = 0;
    		while ((bool)val && num++ < 24)
    		{
    			string name = ((UnityEngine.Object)val).name;
    			if (name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("patron_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("shell_", StringComparison.OrdinalIgnoreCase))
    			{
    				return true;
    			}
    			val = val.parent;
    		}
    		return false;
    	}

    	internal static void DiscoverLamps()
    	{
    		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0079: Invalid comparison between Unknown and I4
    		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
    		HashSet<Light> hashSet = new HashSet<Light>();
		CullingLightObject[] array = UnityEngine.Object.FindObjectsOfType<CullingLightObject>();
		_nativeCutsceneLights.Clear();
		foreach (CullingLightObject val in array)
		{
    			Light light = val.GetLight();
    			if ((UnityEngine.Object)(object)light != (UnityEngine.Object)null)
    			{
    				hashSet.Add(light);
    			}
    		}
    		int num = 0;
    		int num2 = 0;
    		Light[] array2 = UnityEngine.Object.FindObjectsOfType<Light>();
    		foreach (Light val2 in array2)
    		{
    			if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || (int)val2.type == 1 || val2.intensity > 0.1f || _lamps.Contains(val2))
    			{
    				continue;
    			}
    			Scene scene = ((Component)val2).gameObject.scene;
    			string name = scene.name;
    			if (name != null && name.StartsWith("Terminal"))
    			{
    				if (hashSet.Contains(val2))
    				{
    					num++;
    				}
    				else if ((UnityEngine.Object)(object)((Component)val2).GetComponentInParent<LootItem>() != (UnityEngine.Object)null || IsWeaponLight(val2))
    				{
    					num2++;
    				}
    				else
    				{
    					_lamps.Add(val2);
    				}
    			}
    		}
    		if (num > 0)
    		{
    			Plugin.Log.LogInfo((object)$"[Lights] {num} lamps native-owned (CullingLightObject) - LampIntensity drives only the {_lamps.Count} unowned");
    		}
    		if (num2 > 0)
    		{
    			Plugin.Log.LogInfo((object)$"[Lights] {num2} light(s) on loot/weapon hierarchies skipped - guns are not map lamps");
    		}
    		int num3 = 0;
    		for (int num4 = _lamps.Count - 1; num4 >= 0; num4--)
    		{
    			Light val3 = _lamps[num4];
    			if (!val3)
    			{
    				_lamps.RemoveAt(num4);
    			}
    			else if ((UnityEngine.Object)(object)((Component)val3).GetComponentInParent<LootItem>() != (UnityEngine.Object)null || IsWeaponLight(val3))
    			{
    				val3.intensity = 0f;
    				((Behaviour)val3).enabled = false;
    				_lamps.RemoveAt(num4);
    				num3++;
    			}
    		}
    		if (num3 > 0)
    		{
    			Plugin.Log.LogInfo((object)$"[Lights] {num3} previously-revived weapon light(s) purged and darkened");
    		}
    	}

    	private static void LoadAuthoredLights()
    	{
    		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00aa: Expected O, but got Unknown
    		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00bd: Expected O, but got Unknown
    		//IL_01ef: Unknown result type (might be due to invalid IL or missing references)
    		if (_authoredTried)
    		{
    			return;
    		}
    		_authoredTried = true;
    		try
    		{
    			string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "plugin-data", "terminal_lights.json");
    			if (!File.Exists(path))
    			{
    				return;
    			}
    			JObject val = JObject.Parse(File.ReadAllText(path));
    			_authored = new Dictionary<(int, int, int), List<float[]>>();
    			foreach (JToken item in (JArray)val["lights"])
    			{
    				JArray val2 = (JArray)item["p"];
    				JArray val3 = (JArray)item["c"];
    				float[] array = new float[8]
    				{
    					(float)val2[0],
    					(float)val2[1],
    					(float)val2[2],
    					(float)item["i"],
    					(float)val3[0],
    					(float)val3[1],
    					(float)val3[2],
    					(float)(item["rng"] ?? (JToken)0f)
    				};
    				(int, int, int) key = ((int)Mathf.Floor(array[0]), (int)Mathf.Floor(array[1]), (int)Mathf.Floor(array[2]));
    				if (!_authored.TryGetValue(key, out var value))
    				{
    					value = (_authored[key] = new List<float[]>());
    				}
    				value.Add(array);
    			}
    			Plugin.Log.LogInfo((object)string.Format("[Lights] authored light table loaded: {0} rows", ((JContainer)(JArray)val["lights"]).Count));
    		}
    		catch (Exception ex)
    		{
    			Plugin.Log.LogWarning((object)("[Lights] authored light table failed to load: " + ex.Message));
    			_authored = null;
    		}
    	}

    	private static float[] AuthoredFor(Vector3 pos)
    	{
    		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
    		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
    		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
    		if (_authored == null)
    		{
    			return null;
    		}
    		float[] result = null;
    		float num = 0.5625f;
    		int num2 = (int)Mathf.Floor(pos.x);
    		int num3 = (int)Mathf.Floor(pos.y);
    		int num4 = (int)Mathf.Floor(pos.z);
    		for (int i = -1; i <= 1; i++)
    		{
    			for (int j = -1; j <= 1; j++)
    			{
    				for (int k = -1; k <= 1; k++)
    				{
    					if (!_authored.TryGetValue((num2 + i, num3 + j, num4 + k), out var value))
    					{
    						continue;
    					}
    					foreach (float[] item in value)
    					{
    						float num5 = (pos.x - item[0]) * (pos.x - item[0]) + (pos.y - item[1]) * (pos.y - item[1]) + (pos.z - item[2]) * (pos.z - item[2]);
    						if (num5 < num)
    						{
    							num = num5;
    							result = item;
    						}
    					}
    				}
    			}
    		}
    		return result;
    	}

    	private static void DriveLamp(Light l, float flatV, float scale, LightShadows shadows, ref int matched)
    	{
    		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
    		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
    		float[] array = (Plugin.LampAuthored.Value ? AuthoredFor(((Component)l).transform.position) : null);
    		if (array != null)
    		{
    			matched++;
    			float num = (l.intensity = array[3] * scale);
    			l.color = new Color(array[4], array[5], array[6], 1f);
    			if (array[7] > 0.1f)
    			{
    				l.range = array[7];
    			}
    			l.shadows = shadows;
    			((Behaviour)l).enabled = num > 0.01f;
    		}
    		else
    		{
    			l.intensity = flatV;
    			l.shadows = shadows;
    			if (flatV <= 0.01f)
    			{
    				((Behaviour)l).enabled = false;
    			}
    			else if (!((Behaviour)l).enabled)
    			{
    				((Behaviour)l).enabled = true;
    			}
    		}
    	}

    	internal static void ApplyLamps()
    	{
    		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
    		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0335: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0327: Unknown result type (might be due to invalid IL or missing references)
    		//IL_033a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0384: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0389: Unknown result type (might be due to invalid IL or missing references)
    		//IL_03fd: Unknown result type (might be due to invalid IL or missing references)
    		//IL_042a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0448: Unknown result type (might be due to invalid IL or missing references)
    		float value = Plugin.LampIntensity.Value;
    		float value2 = Plugin.LampAuthoredScale.Value;
    		LightShadows shadows = (LightShadows)(Plugin.LampShadows.Value ? 1 : 0);
    		LoadAuthoredLights();
    		int num = 0;
    		int matched = 0;
    		foreach (Light lamp in _lamps)
    		{
    			if ((bool)lamp)
    			{
    				DriveLamp(lamp, value, value2, shadows, ref matched);
    				num++;
    			}
    		}
    		int num2 = 0;
    		int num3 = 0;
    		FieldInfo fieldInfo = AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
    		FieldInfo fieldInfo2 = AccessTools.Field(typeof(CullingLightObject), "float_1");
    		FieldInfo fieldInfo3 = AccessTools.Field(typeof(CullingLightObject), "_fadeStartDistance");
    		FieldInfo fieldInfo4 = AccessTools.Field(typeof(CullingLightObject), "_fadeEndDistance");
    		float value3 = Plugin.LightCullDistance.Value;
    		bool flag = false;
    		try
    		{
    			flag = (UnityEngine.Object)(object)CullingManager.Instance != (UnityEngine.Object)null;
    		}
    		catch
    		{
    		}
		CullingLightObject[] array = UnityEngine.Object.FindObjectsOfType<CullingLightObject>();
		_nativeCutsceneLights.Clear();
		foreach (CullingLightObject val in array)
    		{
    			Light light = val.GetLight();
    			if (!light)
    			{
    				continue;
    			}
    			Scene scene = ((Component)light).gameObject.scene;
    			string name = scene.name;
			if (name == null || !name.StartsWith("Terminal"))
			{
				continue;
			}
			_nativeCutsceneLights.Add(val);
    			if (!flag)
    			{
    				DriveLamp(light, value, value2, shadows, ref matched);
    			}
    			fieldInfo?.SetValue(val, light.intensity);
    			fieldInfo2?.SetValue(val, light.intensity);
    			num2++;
    			try
    			{
    				if (fieldInfo3 != null && fieldInfo4 != null && value3 < (float)fieldInfo4.GetValue(val))
    				{
    					fieldInfo3.SetValue(val, Mathf.Min((float)fieldInfo3.GetValue(val), value3 * 0.6f));
    					fieldInfo4.SetValue(val, value3);
                val.CacheLightSqrDistances();
    					num3++;
    				}
    			}
    			catch
    			{
    			}
    		}
		if (num3 > 0)
    		{
    			Plugin.Log.LogDebug((object)$"[Lights] native fade window tightened to {value3:0}m on {num3} lights");
		}
		if (!flag)
		{
			Camera camera = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
			Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance?.MainPlayer : null;
			Vector3 position = player != null ? player.Position : camera != null ? camera.transform.position : Vector3.zero;
			_cutsceneVisibleLights.Clear();
			_cutsceneLightStateKnown = false;
			int retained = ApplyNativeLightBubble(position, value3);
			_nextManagerlessLightRefresh = Time.realtimeSinceStartup + GameplayLightRefreshSeconds;
			Plugin.Log.LogInfo((object)$"[Lights] managerless safety cull retained {retained}/{_nativeCutsceneLights.Count} native lights within {value3:0}m");
		}
		_lastLamp = value;
    		_lastShadows = Plugin.LampShadows.Value;
    		_lastLightCull = value3;
    		Plugin.Log.LogInfo((object)($"[Lights] drove {num} plain lamps + {num2} native culling lights - " + $"{matched} matched AUTHORED retail values (scale {value2:0.00}), rest flat {value:F2}" + (flag ? "" : " (MANAGER-LESS: Light components driven directly)")));
    		try
    		{
    			Camera val2 = (((UnityEngine.Object)(object)TerminalCullingDriver.CameraRef != (UnityEngine.Object)null) ? TerminalCullingDriver.CameraRef : Camera.main);
    			Vector3 val3 = (((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null) ? ((Component)val2).transform.position : Vector3.zero);
    			int num4 = 0;
    			int num5 = 0;
    			StringBuilder stringBuilder = new StringBuilder();
    			CullingLightObject[] array2 = UnityEngine.Object.FindObjectsOfType<CullingLightObject>();
    			foreach (CullingLightObject val4 in array2)
    			{
    				Light light2 = val4.GetLight();
    				if (!((UnityEngine.Object)(object)light2 == (UnityEngine.Object)null))
    				{
    					float num6 = Vector3.Distance(((Component)light2).transform.position, val3);
    					if (num6 < 30f)
    					{
    						num4++;
    					}
    					if (num5 < 5 && num6 < 60f)
    					{
    						num5++;
    						stringBuilder.Append($"\n  '{((UnityEngine.Object)light2).name}' d={num6:0}m enabled={((Behaviour)light2).enabled} type={light2.type} intensity={light2.intensity:0.##} range={light2.range:0.#} color={light2.color} mask=0x{light2.cullingMask:X} shadows={light2.shadows}");
    					}
    				}
    			}
    			Plugin.Log.LogWarning((object)$"[Lights] AUTOPSY: {num4} native lights within 30m of camera, samples:{stringBuilder}");
    		}
    		catch (Exception ex)
    		{
    			Plugin.Log.LogDebug((object)("[Lights] autopsy failed: " + ex.Message));
    		}
    	}

    	internal static void ApplyAmbient()
    	{
    		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01fc: Unknown result type (might be due to invalid IL or missing references)
    		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0141: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0148: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
    		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
    		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0196: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
    		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
    		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
    		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
    		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
    		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
    		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
    		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
    		float num = (_lastAmbient = Plugin.AmbientIntensity.Value);
    		_lastColorOverride = Plugin.AmbientColorOverride.Value;
    		_lastColorR = Plugin.AmbientColorR.Value;
    		_lastColorG = Plugin.AmbientColorG.Value;
    		_lastColorB = Plugin.AmbientColorB.Value;
    		float num2 = 0.16f * num;
    		Color val2 = default(Color);
    		if (_lastColorOverride)
    		{
    			// user-picked tint (F12 sliders), scaled by intensity like the sky path
    			val2 = new Color(_lastColorR * num, _lastColorG * num, _lastColorB * num, 1f);
    		}
    		else if ((bool)_sky && _sky.Initialized)
    		{
    			try
    			{
    				// sky HUE only, luminance normalized out — the sky-luminance
    				// variant was removed 2026-08-19 (double-counted the darkness)
    				Color val = _sky.SampleEquatorColor();
    				float num3 = Mathf.Max(val.r, Mathf.Max(val.g, val.b));
    				Color val3 = (Color)((num3 > 0.005f) ? (val / num3) : new Color(0.85f, 0.85f, 1f));
    				val2 = val3 * num2;
    				val2.a = 1f;
    			}
    			catch
    			{
    				val2 = new Color(0.15f * num, 0.15f * num, 0.18f * num, 1f);
    			}
    		}
    		else
    		{
    			val2 = new Color(0.15f * num, 0.15f * num, 0.18f * num, 1f);
    		}
    		LevelSettings instance = Singleton<LevelSettings>.Instance;
    		if ((UnityEngine.Object)(object)instance != (UnityEngine.Object)null)
    		{
    			instance.AmbientMode = (AmbientMode)3;
    			instance.SkyColor = val2;
    			instance.EquatorColor = val2;
    			instance.GroundColor = val2;
    			instance.AmbientIntensity = num;
    			float value = Plugin.NvgAmbient.Value;
    			Color val4 = default(Color);
    			val4 = new Color(val2.r * value, val2.g * value * 1.1f, val2.b * value, 1f);
    			instance.NightVisionSkyColor = val4;
    			instance.NightVisionEquatorColor = val4;
    			instance.NightVisionGroundColor = val4;
    			instance.NightVisionAmbientIntensity = num * value;
    			Plugin.Log.LogDebug((object)$"[Ambient] flat ambient -> {val2}, nvg -> {val4} (via LevelSettings, native per-frame apply)");
    		}
    		else
    		{
    			RenderSettings.ambientMode = (AmbientMode)3;
    			RenderSettings.ambientLight = val2;
    			RenderSettings.ambientIntensity = num;
    			Plugin.Log.LogDebug((object)$"[Ambient] flat ambient -> {val2} (RenderSettings fallback, no LevelSettings singleton)");
    		}
    	}

	private static void RefreshNativeCutsceneLightCache()
	{
		_nativeCutsceneLights.Clear();
		foreach (CullingLightObject lightObject in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
		{
			if (lightObject == null) continue;
			Light light = lightObject.GetLight();
			if (light == null) continue;
			string sceneName = light.gameObject.scene.name;
			if (sceneName != null && sceneName.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase))
				_nativeCutsceneLights.Add(lightObject);
		}
	}

	// Called from Camera.onPreCull after the real FPS camera has been moved to the
	// timeline pose. Throttling keeps the pass cheap while still following cuts.
	internal static void TrackCutsceneCamera(Vector3 cameraPosition, bool force = false)
	{
		if (!CutsceneHold && !force) return;
		if (!force && Time.realtimeSinceStartup < _nextCutsceneLightRefresh) return;
		_nextCutsceneLightRefresh = Time.realtimeSinceStartup + CutsceneLightRefreshSeconds;
		if (_nativeCutsceneLights.Count == 0) RefreshNativeCutsceneLightCache();
		ApplyNativeLightBubble(cameraPosition, CutsceneLightRadius);
	}

	private static int ApplyNativeLightBubble(Vector3 cameraPosition, float radius)
	{
		if (_cloMaxIntensity == null)
			_cloMaxIntensity = AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");

		float radiusSq = radius * radius;
		int visible = 0;
		for (int i = _nativeCutsceneLights.Count - 1; i >= 0; i--)
		{
			CullingLightObject lightObject = _nativeCutsceneLights[i];
			if (lightObject == null) { _nativeCutsceneLights.RemoveAt(i); continue; }
			Light light = lightObject.GetLight();
			if (light == null) continue;
			bool show = (light.transform.position - cameraPosition).sqrMagnitude <= radiusSq;
			bool wasVisible = _cutsceneVisibleLights.Contains(lightObject);
			if (!_cutsceneLightStateKnown || show != wasVisible)
			{
				try { ((CullingObject)lightObject).SetVisibility(show); } catch { }
				if (show) _cutsceneVisibleLights.Add(lightObject);
				else _cutsceneVisibleLights.Remove(lightObject);
			}
			if (!show) continue;
			visible++;
			if (!light.enabled) light.enabled = true;
			try
			{
				float max = (float)_cloMaxIntensity.GetValue(lightObject);
				if (max > 0f && light.intensity < max) light.intensity = max;
			}
			catch { }
		}
		_cutsceneLightStateKnown = true;
		return visible;
	}

	private static void TrackManagerlessGameplayLights()
	{
		if (CutsceneHold || Time.realtimeSinceStartup < _nextManagerlessLightRefresh) return;
		try
		{
			if (CullingManager.Instance != null) return;
		}
		catch { }
		_nextManagerlessLightRefresh = Time.realtimeSinceStartup + GameplayLightRefreshSeconds;
		if (_nativeCutsceneLights.Count == 0) RefreshNativeCutsceneLightCache();
		Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance?.MainPlayer : null;
		Camera camera = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
		if (player == null && camera == null) return;
		Vector3 position = player != null ? player.Position : camera.transform.position;
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		ApplyNativeLightBubble(position, Plugin.LightCullDistance.Value);
		TerminalTickProfiler.Add("LightBub", System.Diagnostics.Stopwatch.GetTimestamp() - started);
	}

	internal static void CutsceneBeginLightTracking()
	{
		CutsceneHold = true;
		_nextCutsceneLightRefresh = 0f;
		_cutsceneVisibleLights.Clear();
		_cutsceneLightStateKnown = false;
		try
    		{
    			CullingManager instance = CullingManager.Instance;
    			if ((UnityEngine.Object)(object)instance != (UnityEngine.Object)null)
    			{
    				instance.LockState(true);
    			}
    		}
    		catch (Exception ex)
    		{
    			Plugin.Log.LogWarning((object)("[CutsceneHold] native manager lock failed: " + ex.Message));
    		}
		// ApplyLamps captures the complete list before it hides distant objects.
		// Re-scanning here can omit GameObjects that SetVisibility just deactivated.
		if (_nativeCutsceneLights.Count == 0) RefreshNativeCutsceneLightCache();
		Camera camera = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
		int visible = camera != null ? ApplyNativeLightBubble(camera.transform.position, CutsceneLightRadius) : 0;
		Plugin.Log.LogDebug((object)$"[CutsceneHold] armed - bounded {visible}/{_nativeCutsceneLights.Count} native lights to {CutsceneLightRadius:0}m around the camera");
	}

	internal static void CutsceneRelease()
	{
		CutsceneHold = false;
		try
    		{
    			CullingManager instance = CullingManager.Instance;
    			if ((UnityEngine.Object)(object)instance != (UnityEngine.Object)null)
    			{
    				instance.LockState(false);
    			}
    		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("[CutsceneHold] release failed: " + ex.Message));
		}
		// LockState(false) does not necessarily re-evaluate a stationary camera. Cull
		// immediately at the normal gameplay distance so the cutscene bubble cannot
		// remain live in the starting room until the player crosses another cell.
		Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance?.MainPlayer : null;
		Camera camera = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
		Vector3 gameplayPosition = player != null ? player.Position : camera != null ? camera.transform.position : Vector3.zero;
		int visible = ApplyNativeLightBubble(gameplayPosition, Plugin.LightCullDistance.Value);
		Plugin.Log.LogDebug((object)$"[CutsceneHold] released - {visible}/{_nativeCutsceneLights.Count} native lights retained near gameplay camera, culling unlocked");
		_cutsceneVisibleLights.Clear();
		_cutsceneLightStateKnown = false;
	}
    }
}

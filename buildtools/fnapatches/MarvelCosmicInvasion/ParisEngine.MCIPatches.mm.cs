using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MonoMod;
using Paris.Engine;
using Paris.Engine.Helper;
using Paris.Engine.Input;
using Paris.Engine.Leaderboards;
using Paris.Engine.Localisation;
using Paris.Engine.Stats;

// Split asset preloading so GL work stays on the main thread
// Stock marshals it through workers, which deadlocks under GLES
namespace Paris.Engine.Context
{
	class patch_ContextManager : ContextManager
	{
		private volatile ContextManager.PreloadingState _preloadingState;
		private List<Thread> mGlobalLoadingThreads;

		private ContextManager.PreloadingState mci_preloaded;
		private static readonly object mci_preloadLock = new object();   // static: MonoMod drops instance field initializers

		public patch_ContextManager(Game game) : base(game) { }

		[MonoModReplace]
		public void StartPreLoadingGlobalAssets()
		{
			if (this.EditorMode || this._preloadingState != ContextManager.PreloadingState.Nothing)
			{
				return;
			}
			// No texture data, so these are safe to load in parallel on background threads
			Action[] actions = new Action[] { this.PreloadAudio, this.CacheJsonFiles, this.CacheReflectionInfo, this.PreloadPoolObjects };
			ContextManager.PreloadingState[] flags = new ContextManager.PreloadingState[]
			{
				ContextManager.PreloadingState.Audio,
				ContextManager.PreloadingState.JsonFiles,
				ContextManager.PreloadingState.Reflection,
				ContextManager.PreloadingState.PoolManager
			};
			for (int i = 0; i < actions.Length; i++)
			{
				Action action = actions[i];
				ContextManager.PreloadingState flag = flags[i];
				Thread thread = new Thread(() =>
				{
					action();
					this.MCI_MarkPreloaded(flag);
				});
				thread.Priority = ThreadPriority.BelowNormal;
				thread.IsBackground = true;
				thread.Name = string.Format("Preload : {0}", action.Method.Name);
				this.mGlobalLoadingThreads.Add(thread);
				thread.Start();
			}
			// Global content creates GL objects, so it loads here on the main thread
			this.LoadGlobalContent();
			this.MCI_MarkPreloaded(ContextManager.PreloadingState.GlobalAsset);
		}

		// Bridge for the Game.exe StartInit patch
		public void MCI_FinishPreload()
		{
			this.MCI_MarkPreloaded(ContextManager.PreloadingState.CustomPreloaders);
		}

		// The stock loaders set their flag with an unlocked |=, which can drop another thread's
		// flag and hang boot; each loader re-applies every finished flag once it returns
		private void MCI_MarkPreloaded(ContextManager.PreloadingState flag)
		{
			lock (mci_preloadLock)
			{
				this.mci_preloaded |= flag;
				this._preloadingState |= this.mci_preloaded;
			}
		}
	}
}

// Redirect the intro video to the compressed version
namespace Paris.Engine.Cutscenes
{
	class patch_VideoContext : VideoContext
	{
		private Microsoft.Xna.Framework.Media.VideoPlayer _videoPlayer;

		public extern void orig_Init();
		public override void Init()
		{
			string p = VideoContext.VideoPath;
			if (!string.IsNullOrEmpty(p))
			{
				int slash = p.LastIndexOfAny(new char[] { '\\', '/' });
				string patched = (slash < 0)
					? "PATCH_" + p
					: p.Substring(0, slash + 1) + "PATCH_" + p.Substring(slash + 1);
				string rel = patched.Replace('\\', System.IO.Path.DirectorySeparatorChar)
									 .Replace('/', System.IO.Path.DirectorySeparatorChar);
				string file = System.IO.Path.Combine(
					System.AppDomain.CurrentDomain.BaseDirectory, "Content", rel + ".ogv");
				if (System.IO.File.Exists(file))
				{
					VideoContext.VideoPath = patched;
				}
			}
			orig_Init();
		}

		// VideoPlayer throws without an SDL audio device after it already reports Playing;
		// stopping it keeps Render off the missing texture and the next tick ends the context
		public extern void orig_StatePlaying(FSM.StateStep stateStep, float deltaTime);
		public void StatePlaying(FSM.StateStep stateStep, float deltaTime)
		{
			try
			{
				orig_StatePlaying(stateStep, deltaTime);
			}
			catch (Microsoft.Xna.Framework.Audio.NoAudioHardwareException)
			{
				Console.WriteLine("[MCI] Video: SDL has no audio device, skipping '" + VideoContext.VideoPath + "'");
				this._videoPlayer.Stop();
			}
		}
	}
}

// Disable Steam Input so the game can see the controller
namespace Paris.Engine.Input
{
	class patch_InputManager : InputManager
	{
		private bool _disabled;
		public extern void orig_Init();
		public override void Init()
		{
			orig_Init();
			if (!SteamHelper.Initialized)
			{
				this._disabled = true;
			}
		}
	}

	// Force Nintendo-style button glyphs
	// This gets around some firmwares that use a PSX controller id
	class patch_InputManagerBase : InputManagerBase
	{
		public extern GamePadButtonType orig_GetButtonType(ParisInputType i_controllerType);
		public new GamePadButtonType GetButtonType(ParisInputType i_controllerType)
		{
			if (InputManagerBase.IsInputTypeController(i_controllerType))
			{
				return GamePadButtonType.SWITCH_PRO;
			}
			return orig_GetButtonType(i_controllerType);
		}
	}
}

namespace Paris.Engine.Audio
{
	// Release FMOD event instances left stopped, and recreate one on the next Play
	class patch_SoundEventInstance : SoundEventInstance
	{
		private FMOD.Studio.EventInstance _instance;
		private int _soundEventID;
		private FMOD.Studio.EVENT_CALLBACK _eventCallback;

		private bool mci_released;
		private float mci_idle;

		private const float MCI_GRACE = 10f; // seconds stopped before the instance is released

		public patch_SoundEventInstance(int soundEventID) : base(soundEventID) { }

		public extern void orig_Tick(float deltaTime, bool frameChanged);
		public new void Tick(float deltaTime, bool frameChanged)
		{
			orig_Tick(deltaTime, frameChanged);
			if (this.IsMusic || this.mci_released)
			{
				return;
			}
			FMOD.Studio.PLAYBACK_STATE st;
			if (this._instance.getPlaybackState(out st) == FMOD.RESULT.OK
				&& st == FMOD.Studio.PLAYBACK_STATE.STOPPED)
			{
				this.mci_idle += deltaTime;
				if (this.mci_idle >= MCI_GRACE)
				{
					this._instance.release();
					this.mci_released = true;
				}
			}
			else
			{
				this.mci_idle = 0f;
			}
		}

		public extern void orig_Play();
		public new void Play()
		{
			this.MCI_Revive();
			orig_Play();
		}

		// Settings made before Play must land on the instance that will play
		public extern void orig_SetParameter(string name, float value);
		public new void SetParameter(string name, float value)
		{
			this.MCI_Revive();
			orig_SetParameter(name, value);
		}

		public extern void orig_ResetParameter(string name);
		public new void ResetParameter(string name)
		{
			this.MCI_Revive();
			orig_ResetParameter(name);
		}

		public extern void orig_ResetParameters();
		public new void ResetParameters()
		{
			this.MCI_Revive();
			orig_ResetParameters();
		}

		public extern float orig_get_Volume();
		public extern void orig_set_Volume(float value);
		public new float Volume
		{
			get { return orig_get_Volume(); }
			set { this.MCI_Revive(); orig_set_Volume(value); }
		}

		public extern bool orig_get_IsPaused();
		public extern void orig_set_IsPaused(bool value);
		public new bool IsPaused
		{
			get { return orig_get_IsPaused(); }
			set { this.MCI_Revive(); orig_set_IsPaused(value); }
		}

		private void MCI_Revive()
		{
			if (this.mci_released)
			{
				this.Description.createInstance(out this._instance);
				this._instance.setUserData((IntPtr)this._soundEventID);
				this._instance.setCallback(this._eventCallback,
					FMOD.Studio.EVENT_CALLBACK_TYPE.STOPPED | FMOD.Studio.EVENT_CALLBACK_TYPE.TIMELINE_MARKER);
				this.mci_released = false;
			}
			this.mci_idle = 0f;
		}
	}
}

// Halt stats reporting since Steam is stubbed
namespace Paris.Engine.Stats
{
	public class patch_Stat : Stat
	{
		patch_Stat(string id, StatType type) : base(id, type) { }
		private bool StoreSteam()
		{
			return false;
		}
	}
	class patch_GameAnalytics : GameAnalytics
	{
		public new void Init()
		{
		}
	}
}

// Halt networking, EOSSDK is stubbed (the LAN build needs the real Disconnect and ChangeLobbyLocked)
#if !MCI_LAN
namespace Paris.Engine.Networking
{
	class patch_NetworkManager : NetworkManager
	{
		public new void Disconnect(bool transition = false)
		{
		}

		public new void ChangeLobbyLocked(bool isLocked)
		{
		}
	}
}
#endif

// Halt EpicHelper, EOSSDK is stubbed (the LAN build supplies its own patch_EpicHelper)
#if !MCI_LAN
namespace Paris.Engine
{
	[MonoModPatch("Paris.Engine.EpicHelper")]
	static class patch_EpicHelper
	{
		[MonoModReplace]
		public static void Init()
		{
		}
	}
}
#endif

// Mono 6.12 LLVM AOT miscompiles Vector3.CatmullRom/Hermite,
// so recompute in scalar double
namespace Paris.Engine.Effects
{
	class patch_SplinePositionTween : SplinePositionTween
	{
		[MonoModReplace]
		public new void Tick(float deltaTime)
		{
			this._timer += deltaTime;
			this._progress = (this._duration != 0f) ? MCIClamp(this._timer / this._duration, 0f, 1f) : 1f;
			float t = this._progress;
			if ((int)this._splineType == 0) // CatmullRom
			{
				this._position = new Vector3(
					MCICatmull(this._curveControl.X, this._curveStart.X, this._curveEnd.X, this._curveControl.X, t),
					MCICatmull(this._curveControl.Y, this._curveStart.Y, this._curveEnd.Y, this._curveControl.Y, t),
					MCICatmull(this._curveControl.Z, this._curveStart.Z, this._curveEnd.Z, this._curveControl.Z, t));
				return;
			}
			this._position = new Vector3(
				MCIHermite(this._curveStart.X, this._tangentStart.X, this._curveEnd.X, this._tangentEnd.X, t),
				MCIHermite(this._curveStart.Y, this._tangentStart.Y, this._curveEnd.Y, this._tangentEnd.Y, t),
				MCIHermite(this._curveStart.Z, this._tangentStart.Z, this._curveEnd.Z, this._tangentEnd.Z, t));
		}

		private static float MCIClamp(float v, float lo, float hi)
		{
			return (v < lo) ? lo : ((v > hi) ? hi : v);
		}

		private static float MCICatmull(float v1, float v2, float v3, float v4, float a)
		{
			double num = (double)a * a;
			double num2 = (double)a * num;
			return (float)(0.5 * (2.0 * v2 + (-(double)v1 + v3) * a + (2.0 * v1 - 5.0 * v2 + 4.0 * v3 - v4) * num + (-(double)v1 + 3.0 * v2 - 3.0 * v3 + v4) * num2));
		}

		private static float MCIHermite(float v1, float t1, float v2, float t2, float a)
		{
			double num2 = (double)a * a;
			double num3 = (double)a * num2;
			double n4 = 2.0 * num3 - 3.0 * num2 + 1.0;
			double n5 = -2.0 * num3 + 3.0 * num2;
			double n6 = num3 - 2.0 * num2 + a;
			double n7 = num3 - num2;
			return (float)((double)v1 * n4 + (double)v2 * n5 + (double)t1 * n6 + (double)t2 * n7);
		}
	}
}

// Leaderboards are Steam-only, so construct them empty
[MonoModPatch("Paris.Engine.Leaderboards.Leaderboard")]
public class patch_Leaderboard
{
	[MonoModConstructor]
	public patch_Leaderboard(LocID name, string key, LeaderboardBase.SortMode sortMode, LeaderboardBase.DisplayType displayType)
	{
	}
}

// The CRT filter's phosphor mask degenerates below 3x display scale
// Clamp it
namespace Paris.Engine.Graphics.Shaders
{
	class patch_CRTShader : CRTShader
	{
		public patch_CRTShader(string path) : base(path) { }

		public extern void orig_ApplyData(Renderer renderer, CRTShaderData data);
		public override void ApplyData(Renderer renderer, CRTShaderData data)
		{
			if (data.GameScale < 3f)
			{
				data.GameScale = 3f;
			}
			orig_ApplyData(renderer, data);
		}
	}
}

// GalactusDebris lists a texture the game doesn't ship, and stock stops there before loading the
// palette; stand texture 0 in for it, the one TryGetDrawArguments falls back to past the end
namespace Paris.Engine.Graphics.Animations
{
	class patch_Animator2dData : Animator2dData
	{
		public extern void orig_LoadTextures(string path, bool isReload);
		public new void LoadTextures(string path, bool isReload = false)
		{
			while (true)
			{
				try
				{
					orig_LoadTextures(path, isReload);
					return;
				}
				catch (Microsoft.Xna.Framework.Content.ContentLoadException) when (this.Textures.Count > 0 && this.Textures.Count < this.TextureFilenames.Count)
				{
					this.Textures.Add(this.Textures[0]);
					isReload = false;   // a reload clears Textures first, which would drop the stand-in
				}
			}
		}
	}
}

// Grow exhausted pools by one object on demand
// The stock path builds a 20% batch inline mid-frame, stalling heavy scenes
namespace Paris.Engine.Scene
{
	class patch_GameObjectPoolManager : GameObjectPoolManager
	{
		public extern GameObject2d orig_SpawnObject(string actorTemplate, Vector3 position, Guid forceGuid, bool sendToNetwork, bool reliable, bool global, int indexAuthority);
		public new GameObject2d SpawnObject(string actorTemplate, Vector3 position, Guid forceGuid, bool sendToNetwork, bool reliable, bool global = true, int indexAuthority = -1)
		{
			if (!Paris.Engine.Context.ContextManager.Singleton.EditorMode)
			{
				this.MCI_EnsureAvailable(actorTemplate, forceGuid, global, indexAuthority);
			}
			return this.orig_SpawnObject(actorTemplate, position, forceGuid, sendToNetwork, reliable, global, indexAuthority);
		}

		// An authority may only take slots where i % MaxPlayers == indexAuthority (stock SpawnObject)
		private void MCI_EnsureAvailable(string actorTemplate, Guid forceGuid, bool global, int indexAuthority)
		{
			int players = Math.Max(1, Paris.Engine.Data.EngineSettings.Singleton.MaxPlayers);
			bool needOne = true;
			int count = 0;
			Monitor.Enter(this._lock);
			List<Paris.Engine.Graphics.Playfield.GameObjectData> list;
			if (this._pools.TryGetValue(actorTemplate, out list))
			{
				count = list.Count;
				for (int i = 0; i < list.Count; i++)
				{
					// A live object already carrying the forced guid satisfies the spawn too (stock reuses it)
					Paris.Engine.Graphics.Playfield.GameObjectData d = list[i];
					if ((forceGuid != Guid.Empty && d.GameObject.Id == forceGuid)
						|| (d.GameObject.Disposed && (indexAuthority < 0 || i % players == indexAuthority)))
					{
						needOne = false;
						break;
					}
				}
			}
			Monitor.Exit(this._lock);
			if (!needOne)
			{
				return;
			}
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			int built = 0;
			do
			{
				this.MCI_ConstructOne(actorTemplate, global);
				built++;
			}
			while (indexAuthority >= 0 && built < players && (this.MCI_PoolCount(actorTemplate) - 1) % players != indexAuthority);
			Console.WriteLine("[MCI] Pool '" + actorTemplate + "' exhausted at " + count + "; built " + built + " in " + stopwatch.ElapsedMilliseconds + "ms.");
		}

		private int MCI_PoolCount(string actorTemplate)
		{
			Monitor.Enter(this._lock);
			List<Paris.Engine.Graphics.Playfield.GameObjectData> list;
			int count = this._pools.TryGetValue(actorTemplate, out list) ? list.Count : 0;
			Monitor.Exit(this._lock);
			return count;
		}

		// Mirrors AddPoolInternal's per-object steps, minus _poolStack so the stock
		// exhaustion branch can't recurse
		private void MCI_ConstructOne(string actorTemplate, bool global)
		{
			bool disableInvalidate;
			bool useGlobalContentManager;
			Monitor.Enter(this._lock);
			disableInvalidate = Paris.Engine.Physic.CollisionManager.DisableInvalidate;
			Paris.Engine.Physic.CollisionManager.DisableInvalidate = true;
			useGlobalContentManager = Paris.Engine.Context.ContextManager.Singleton.UseGlobalContentManager;
			Paris.Engine.Context.ContextManager.Singleton.UseGlobalContentManager = global;
			Monitor.Exit(this._lock);
			try
			{
				string text = actorTemplate.StartsWith("SceneInstance:")
					? actorTemplate
					: PathManager.NormalizePath("Actor2d\\" + actorTemplate);
				Paris.Engine.Graphics.Playfield.GameObjectData gameObjectData = new Paris.Engine.Graphics.Playfield.GameObjectData(text, false);
				Monitor.Enter(this._lock);
				List<Paris.Engine.Graphics.Playfield.GameObjectData> list;
				if (!this._pools.TryGetValue(actorTemplate, out list))
				{
					list = new List<Paris.Engine.Graphics.Playfield.GameObjectData>();
					this._pools[actorTemplate] = list;
				}
				int num = list.Count;
				Monitor.Exit(this._lock);
				gameObjectData.GameObject.Name = "POOLED_MCI_" + System.IO.Path.GetFileNameWithoutExtension(actorTemplate) + "_" + num.ToString("D3");
				gameObjectData.GameObject.PoolObject = true;
				gameObjectData.GameObject.Init();
				gameObjectData.GameObject.Disposed = true;
				gameObjectData.GameObject.Active = false;
				gameObjectData.PoolObject = true;
				gameObjectData.Saveable = false;
				Monitor.Enter(this._lock);
				list.Add(gameObjectData);
				this._poolMaxes[actorTemplate] = list.Count;
				Monitor.Exit(this._lock);
			}
			catch (Exception e)
			{
				Console.WriteLine("[MCI] Emergency pool construct failed for " + actorTemplate + ": " + e.Message);
			}
			finally
			{
				Monitor.Enter(this._lock);
				Paris.Engine.Context.ContextManager.Singleton.UseGlobalContentManager = useGlobalContentManager;
				Paris.Engine.Physic.CollisionManager.DisableInvalidate = disableInvalidate;
				Monitor.Exit(this._lock);
			}
		}
	}
}

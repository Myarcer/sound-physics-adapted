using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("ForestSymphony")]
[assembly: AssemblyConfiguration("Debug")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("ForestSymphony")]
[assembly: AssemblyTitle("ForestSymphony")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace ForestSymphony;

public class BirdCheck
{
	private readonly ICoreClientAPI capi;

	private readonly IPlayer player;

	private readonly BirdsSounds birdsSounds;

	private long birdListenerId = 0L;

	private ForestSeason lastSeason;

	private bool isProcessing = false;

	private Dictionary<BlockPos, Block> cachedLeaves = new Dictionary<BlockPos, Block>();

	private BlockPos lastCheckedPosition = new BlockPos();

	public BirdsSounds BirdsSounds => birdsSounds;

	public BirdCheck(ICoreClientAPI capi, IPlayer player)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Expected O, but got Unknown
		this.capi = capi ?? throw new ArgumentNullException("capi", "[BIRD CHECK] capi is null!");
		this.player = player ?? throw new ArgumentNullException("player", "[BIRD CHECK] player is null!");
		birdsSounds = new BirdsSounds(capi);
		DebugHelper.Debug("[BIRD CHECK] BirdCheck initialized successfully.");
		lastSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, ((Entity)player.Entity).Pos.AsBlockPos);
	}

	public void StartSeasonalListener()
	{
		RefreshInterval();
	}

	private void RefreshInterval()
	{
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Expected O, but got Unknown
		if (birdListenerId != 0)
		{
			((IEventAPI)capi.Event).UnregisterGameTickListener(birdListenerId);
			birdListenerId = 0L;
		}
		if (!ForestSymphonyConfigs.EnableBirdCheck)
		{
			return;
		}
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		float birdIntervalMultiplier = ForestSymphonyConfigs.GetBirdIntervalMultiplier((ICoreAPI)(object)capi, asBlockPos);
		if (birdIntervalMultiplier <= 0f)
		{
			DebugHelper.Debug($"[BIRD CHECK] Season multiplier is 0. Heartbeat every {5000} ms.");
			birdListenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnBirdHeartbeatTick, 5000, 0);
			return;
		}
		float num = ForestSymphonyConfigs.BirdCheckInterval;
		ICoreClientAPI obj = capi;
		object obj2;
		if (obj == null)
		{
			obj2 = null;
		}
		else
		{
			IClientWorldAccessor world = obj.World;
			obj2 = ((world != null) ? ((IWorldAccessor)world).BlockAccessor : null);
		}
		if (obj2 == null)
		{
			DebugHelper.Debug("[BIRD CHECK] BlockAccessor is null, skipping wind check.");
			return;
		}
		Vec3d val = new Vec3d(0.0, 0.0, 0.0);
		try
		{
			ICoreClientAPI obj3 = capi;
			object obj4;
			if (obj3 == null)
			{
				obj4 = null;
			}
			else
			{
				IClientWorldAccessor world2 = obj3.World;
				obj4 = ((world2 != null) ? ((IWorldAccessor)world2).BlockAccessor : null);
			}
			if (obj4 != null)
			{
				val = ((IWorldAccessor)capi.World).BlockAccessor.GetWindSpeedAt(asBlockPos);
			}
		}
		catch (Exception ex)
		{
			DebugHelper.Debug("[BIRD CHECK] Exception when getting wind speed: " + ex.Message);
		}
		float val2 = (float)Math.Sqrt(val.X * val.X + val.Z * val.Z);
		float num2 = Math.Min(val2, 1f);
		float num3 = ForestSymphonyConfigs.BirdCheckMultiplierWeak + (ForestSymphonyConfigs.BirdCheckMultiplierStrong - ForestSymphonyConfigs.BirdCheckMultiplierWeak) * num2;
		float num4 = num * birdIntervalMultiplier * num3;
		if (num4 < 50f)
		{
			num4 = 1000f;
		}
		int num5 = (int)num4;
		ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, asBlockPos);
		DebugHelper.Debug($"[BIRD CHECK] (Re)Registering. Season={currentSeason}, Wind Mult={num3:F2}, final interval={num5}ms");
		birdListenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnBirdCheckTick, num5, 0);
	}

	private void OnBirdHeartbeatTick(float dt)
	{
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, asBlockPos);
		if (currentSeason != lastSeason)
		{
			lastSeason = currentSeason;
			RefreshInterval();
		}
	}

	private void OnBirdCheckTick(float dt)
	{
		if (!ForestSymphonyConfigs.EnableBirdCheck)
		{
			return;
		}
		IPlayer obj = player;
		if (((obj != null) ? obj.Entity : null) != null)
		{
			RunBirdCheck();
			ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, ((Entity)player.Entity).Pos.AsBlockPos);
			if (currentSeason != lastSeason)
			{
				lastSeason = currentSeason;
				RefreshInterval();
			}
		}
	}

	private void RunBirdCheck()
	{
		if (!isProcessing && player != null && player.Entity != null)
		{
			BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
			float birdActivityMultiplier = ForestSymphonyConfigs.GetBirdActivityMultiplier((ICoreAPI)(object)capi, asBlockPos);
			if (asBlockPos.DistanceTo(lastCheckedPosition) >= 10f)
			{
				lastCheckedPosition.Set(asBlockPos);
				UpdateLeavesAsync(asBlockPos);
			}
			if (cachedLeaves.Count >= 80)
			{
				ManageSoundPlayback(new HashSet<BlockPos>(cachedLeaves.Keys), birdActivityMultiplier);
			}
		}
	}

	private async Task UpdateLeavesAsync(BlockPos playerPos)
	{
		isProcessing = true;
		Dictionary<BlockPos, Block> detectedLeaves = await Task.Run(() => DetectLeaves(playerPos));
		if (playerPos.DistanceTo(lastCheckedPosition) < 10f)
		{
			cachedLeaves = detectedLeaves;
		}
		isProcessing = false;
	}

	private Dictionary<BlockPos, Block> DetectLeaves(BlockPos playerPos)
	{
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Invalid comparison between Unknown and I4
		Dictionary<BlockPos, Block> dictionary = new Dictionary<BlockPos, Block>();
		IBlockAccessor blockAccessor = ((IWorldAccessor)capi.World).BlockAccessor;
		float num = 20f;
		for (int i = 10; i <= 15; i++)
		{
			for (int j = -30; j <= 30; j++)
			{
				for (int k = -30; k <= 30; k++)
				{
					double num2 = Math.Sqrt(j * j + k * k);
					if (!(num2 < (double)num))
					{
						BlockPos val = playerPos.AddCopy(j, i, k);
						Block block = blockAccessor.GetBlock(val);
						if (block != null && (int)block.BlockMaterial != 0 && ((RegistryObject)block).FirstCodePart(0) == "leaves")
						{
							dictionary[val] = block;
						}
					}
				}
			}
		}
		DebugHelper.Debug($"[BIRD CHECK] Leaves BFS done! Found {dictionary.Count} blocks.");
		return dictionary;
	}

	private void ManageSoundPlayback(HashSet<BlockPos> leafPositions, float birdActivityMult)
	{
		double num = ((IGameCalendar)capi.World.Calendar).TotalHours % 24.0;
		bool flag = num >= 5.0 && num < 18.0;
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		Vec3d windSpeedAt = ((IWorldAccessor)capi.World).BlockAccessor.GetWindSpeedAt(asBlockPos);
		float val = (float)Math.Sqrt(windSpeedAt.X * windSpeedAt.X + windSpeedAt.Z * windSpeedAt.Z);
		float num2 = Math.Min(val, 1f);
		int birdMinSounds = ForestSymphonyConfigs.BirdMinSounds;
		int birdMaxSounds = ForestSymphonyConfigs.BirdMaxSounds;
		float num3 = Math.Max(0.05f, (float)Math.Pow(1.0 - (double)num2, 1.0));
		int num4 = (int)Math.Ceiling((float)birdMinSounds * birdActivityMult * num3);
		int num5 = (int)Math.Ceiling((float)birdMaxSounds * birdActivityMult * num3);
		if (num4 < 1)
		{
			num4 = 0;
		}
		if (num5 < 1)
		{
			num5 = 0;
		}
		int num6 = ((((IWorldAccessor)capi.World).Rand.NextDouble() < (double)num3) ? ((IWorldAccessor)capi.World).Rand.Next(num4, num5 + 1) : 0);
		if (flag)
		{
			DebugHelper.Debug($"[BIRD CHECK] Day => {num6} bird calls, hour={num}, windEffect={num3:F2}");
			birdsSounds.EnqueueDaytimeSounds(leafPositions, num6, player);
		}
		else
		{
			DebugHelper.Debug($"[BIRD CHECK] Night => {num6} bird calls, hour={num}, windEffect={num3:F2}");
			birdsSounds.EnqueueNighttimeSounds(leafPositions, num6, player);
		}
	}

	public void UpdateCoverage(float volumeFactor, float pitchFactor, float dt)
	{
		birdsSounds.UpdateCoverage(volumeFactor, pitchFactor, dt);
	}
}
public class BirdsSounds
{
	private class ActiveBirdSound
	{
		public ILoadedSound Sound { get; }

		public Vec3f Position { get; }

		public string SoundPath { get; }

		public float OriginalVolume { get; set; }

		public float CurrentVolume { get; set; }

		public float OriginalPitch { get; set; }

		public float CurrentPitch { get; set; }

		public ActiveBirdSound(ILoadedSound sound, Vec3f position, string soundPath)
		{
			Sound = sound;
			Position = position;
			SoundPath = soundPath;
		}
	}

	private class SoundTask
	{
		public string SoundPath { get; set; } = string.Empty;

		public BlockPos? Position { get; set; }

		public int TargetMilliseconds { get; set; }
	}

	private readonly ICoreClientAPI capi;

	private readonly List<string> dayExoticSounds = new List<string>
	{
		"bird_2", "bird_5", "bird_6", "bird_7", "canary_1", "canary_2", "canary_3", "cuckoo_1", "cuckoo_2", "sparrow_1",
		"sparrow_2", "sparrow_3", "sparrow_4", "sparrow_5", "parrot_1", "parrot_2", "parrot_3", "parrot_4", "parrot_5", "sunbird_1",
		"sunbird_2"
	};

	private readonly List<string> dayTundraSounds = new List<string>
	{
		"bird_1", "bird_3", "bird_4", "robin_1", "robin_2", "robin_3", "woodpecker_1", "woodpecker_2", "woodpecker_3", "woodpecker_4",
		"chickadee_1", "chickadee_2", "chickadee_3", "crossbill_1", "crossbill_2"
	};

	private readonly List<string> nightExoticSounds = new List<string>
	{
		"bird_6", "bird_7", "hornbill_1", "nightjar_1", "nightjar_2", "potoo_1", "potoo_2", "tawny_1", "heron_1", "magpie_1",
		"raven_1", "raven_2"
	};

	private readonly List<string> nightTundraSounds = new List<string> { "bird_4", "owl_1", "owl_2", "owl_3", "owl_4", "owl_5", "owl_6" };

	private readonly List<ActiveBirdSound> activeBirdSounds = new List<ActiveBirdSound>();

	private readonly object activeSoundsLock = new object();

	private readonly Queue<string> recentlyPlayedSounds = new Queue<string>();

	private const int MaxRecentlyPlayed = 11;

	private readonly Dictionary<string, double> lastPlayedTimes = new Dictionary<string, double>();

	private const double SoundCooldownSeconds = 30.0;

	private float MinDistanceBetweenSounds => ForestSymphonyConfigs.BirdSoundMinDistance;

	public int ActiveSoundCount
	{
		get
		{
			lock (activeSoundsLock)
			{
				activeBirdSounds.RemoveAll((ActiveBirdSound s) => s.Sound == null || !s.Sound.IsPlaying);
				return activeBirdSounds.Count;
			}
		}
	}

	public BirdsSounds(ICoreClientAPI capi)
	{
		this.capi = capi ?? throw new ArgumentNullException("capi", "[BIRD DETECTION] capi is null!");
		((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnGameTick, 500, 0);
		DebugHelper.Debug("[BIRD DETECTION] BirdsSounds initialized successfully.");
	}

	public void EnqueueDaytimeSounds(HashSet<BlockPos> leafPositions, int numberOfSounds, IPlayer player)
	{
		if (numberOfSounds <= 0 || leafPositions.Count == 0)
		{
			return;
		}
		List<string> candidateListForDay = GetCandidateListForDay(GetCurrentTemperature());
		if (candidateListForDay.Count == 0)
		{
			return;
		}
		HashSet<BlockPos> availableLeafPositions = GetAvailableLeafPositions(leafPositions, numberOfSounds, MinDistanceBetweenSounds);
		if (availableLeafPositions.Count == 0)
		{
			return;
		}
		int num = Math.Min(numberOfSounds, availableLeafPositions.Count);
		foreach (BlockPos item in availableLeafPositions)
		{
			string text = SelectRandomSound(candidateListForDay);
			if (text == null)
			{
				break;
			}
			StartSoundWithDelay(new SoundTask
			{
				SoundPath = "forestsymphony:sounds/day/birds/" + text,
				Position = item
			}, player);
			num--;
			if (num <= 0)
			{
				break;
			}
		}
	}

	public void EnqueueNighttimeSounds(HashSet<BlockPos> leafPositions, int numberOfSounds, IPlayer player)
	{
		if (numberOfSounds <= 0 || leafPositions.Count == 0)
		{
			return;
		}
		List<string> candidateListForNight = GetCandidateListForNight(GetCurrentTemperature());
		if (candidateListForNight.Count == 0)
		{
			return;
		}
		HashSet<BlockPos> availableLeafPositions = GetAvailableLeafPositions(leafPositions, numberOfSounds, MinDistanceBetweenSounds);
		if (availableLeafPositions.Count == 0)
		{
			return;
		}
		int num = Math.Min(numberOfSounds, availableLeafPositions.Count);
		foreach (BlockPos item in availableLeafPositions)
		{
			string text = SelectRandomSound(candidateListForNight);
			if (text == null)
			{
				break;
			}
			StartSoundWithDelay(new SoundTask
			{
				SoundPath = "forestsymphony:sounds/night/birds/" + text,
				Position = item
			}, player);
			num--;
			if (num <= 0)
			{
				break;
			}
		}
	}

	private List<string> GetCandidateListForDay(float temperature)
	{
		List<string> list = new List<string>();
		float birdTundraTemperatureThreshold = ForestSymphonyConfigs.BirdTundraTemperatureThreshold;
		float birdExoticTemperatureThreshold = ForestSymphonyConfigs.BirdExoticTemperatureThreshold;
		if (temperature >= birdExoticTemperatureThreshold)
		{
			list.AddRange(dayExoticSounds);
		}
		else if (temperature < birdTundraTemperatureThreshold)
		{
			list.AddRange(dayTundraSounds);
		}
		else
		{
			list.AddRange(dayExoticSounds);
			list.AddRange(dayTundraSounds);
		}
		return list;
	}

	private List<string> GetCandidateListForNight(float temperature)
	{
		List<string> list = new List<string>();
		float birdTundraTemperatureThreshold = ForestSymphonyConfigs.BirdTundraTemperatureThreshold;
		float birdExoticTemperatureThreshold = ForestSymphonyConfigs.BirdExoticTemperatureThreshold;
		if (temperature >= birdExoticTemperatureThreshold)
		{
			list.AddRange(nightExoticSounds);
		}
		else if (temperature < birdTundraTemperatureThreshold)
		{
			list.AddRange(nightTundraSounds);
		}
		else
		{
			list.AddRange(nightExoticSounds);
			list.AddRange(nightTundraSounds);
		}
		return list;
	}

	private string? SelectRandomSound(List<string> soundList)
	{
		if (soundList.Count == 0)
		{
			return null;
		}
		double currentTime = (double)capi.ElapsedMilliseconds / 1000.0;
		List<string> list = soundList.FindAll((string s) => !recentlyPlayedSounds.Contains(s) && (!lastPlayedTimes.ContainsKey(s) || currentTime - lastPlayedTimes[s] > 30.0));
		if (list.Count == 0)
		{
			if (((IWorldAccessor)capi.World).Rand.NextDouble() < 0.05)
			{
				DebugHelper.Debug("[BIRD DETECTION] All candidate sounds on cooldown. Using override pick anyway.");
				return soundList[((IWorldAccessor)capi.World).Rand.Next(soundList.Count)];
			}
			DebugHelper.Debug("[BIRD DETECTION] Skipping bird sound to avoid repetition (all on cooldown).");
			return null;
		}
		string text = list[((IWorldAccessor)capi.World).Rand.Next(list.Count)];
		recentlyPlayedSounds.Enqueue(text);
		lastPlayedTimes[text] = currentTime;
		if (recentlyPlayedSounds.Count > 11)
		{
			recentlyPlayedSounds.Dequeue();
		}
		return text;
	}

	private HashSet<BlockPos> GetAvailableLeafPositions(HashSet<BlockPos> leafPositions, int desiredCount, float minDistance)
	{
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Expected O, but got Unknown
		float num = 15f;
		HashSet<BlockPos> hashSet = new HashSet<BlockPos>();
		List<BlockPos> list = new List<BlockPos>(leafPositions);
		Shuffle(list, ((IWorldAccessor)capi.World).Rand);
		Vec3d xYZ = ((Entity)((IPlayer)capi.World.Player).Entity).Pos.XYZ;
		Vec3f vec = new Vec3f((float)xYZ.X, (float)xYZ.Y, (float)xYZ.Z);
		Dictionary<string, List<BlockPos>> dictionary = new Dictionary<string, List<BlockPos>>
		{
			{
				"frontLeft",
				new List<BlockPos>()
			},
			{
				"frontRight",
				new List<BlockPos>()
			},
			{
				"backLeft",
				new List<BlockPos>()
			},
			{
				"backRight",
				new List<BlockPos>()
			}
		};
		foreach (BlockPos item in list)
		{
			double num2 = (double)item.X - xYZ.X;
			double num3 = (double)item.Z - xYZ.Z;
			if (num2 >= 0.0 && num3 >= 0.0)
			{
				dictionary["frontRight"].Add(item);
			}
			else if (num2 < 0.0 && num3 >= 0.0)
			{
				dictionary["frontLeft"].Add(item);
			}
			else if (num2 < 0.0 && num3 < 0.0)
			{
				dictionary["backLeft"].Add(item);
			}
			else
			{
				dictionary["backRight"].Add(item);
			}
		}
		foreach (List<BlockPos> value in dictionary.Values)
		{
			Shuffle(value, ((IWorldAccessor)capi.World).Rand);
		}
		List<string> list2 = new List<string>(dictionary.Keys);
		Shuffle(list2, ((IWorldAccessor)capi.World).Rand);
		lock (activeSoundsLock)
		{
			foreach (string item2 in list2)
			{
				List<BlockPos> list3 = dictionary[item2];
				foreach (BlockPos item3 in list3)
				{
					bool flag = false;
					foreach (ActiveBirdSound activeBirdSound in activeBirdSounds)
					{
						if (DistanceBetween(item3, activeBirdSound.Position) < (double)minDistance)
						{
							flag = true;
							break;
						}
					}
					if (!flag)
					{
						foreach (BlockPos item4 in hashSet)
						{
							if (DistanceBetween(item3, item4) < (double)minDistance)
							{
								flag = true;
								break;
							}
						}
					}
					if (!flag)
					{
						double num4 = DistanceBetweenBlockPosAndVec3f(item3, vec);
						if (num4 < (double)num)
						{
							flag = true;
						}
					}
					if (!flag)
					{
						hashSet.Add(item3);
						if (hashSet.Count >= desiredCount)
						{
							return hashSet;
						}
					}
				}
			}
		}
		return hashSet;
	}

	private double DistanceBetweenBlockPosAndVec3f(BlockPos pos, Vec3f vec)
	{
		float num = (float)pos.X + 0.5f - vec.X;
		float num2 = (float)pos.Y + 0.5f - vec.Y;
		float num3 = (float)pos.Z + 0.5f - vec.Z;
		return Math.Sqrt(num * num + num2 * num2 + num3 * num3);
	}

	private void StartSoundWithDelay(SoundTask task, IPlayer player)
	{
		if (task.SoundPath == null)
		{
			DebugHelper.Debug("[BIRD DETECTION] Skipping sound due to null path or cooldown.");
			return;
		}
		float elapsed = 0f;
		long listenerId = 0L;
		task.TargetMilliseconds = ((IWorldAccessor)capi.World).Rand.Next(0, 8000);
		listenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)delegate(float dt)
		{
			//IL_009f: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Expected O, but got Unknown
			//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d0: Expected O, but got Unknown
			//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00de: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
			//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
			//IL_0108: Expected O, but got Unknown
			elapsed += dt * 1000f;
			if (elapsed >= (float)task.TargetMilliseconds)
			{
				((IEventAPI)capi.Event).UnregisterGameTickListener(listenerId);
				if (!(task.Position == (BlockPos)null))
				{
					BlockPos position = task.Position;
					Vec3f position2 = new Vec3f((float)position.X + 0.5f, (float)position.Y + 0.5f, (float)position.Z + 0.5f);
					ILoadedSound val = capi.World.LoadSound(new SoundParams
					{
						Location = new AssetLocation(task.SoundPath),
						ShouldLoop = false,
						Position = position2,
						DisposeOnFinish = true,
						Volume = ForestSymphonyConfigs.BirdSoundVolume,
						Range = ForestSymphonyConfigs.BirdSoundRange,
						SoundType = (EnumSoundType)2
					});
					if (val != null)
					{
						lock (activeSoundsLock)
						{
							ActiveBirdSound item = new ActiveBirdSound(val, position2, task.SoundPath)
							{
								OriginalVolume = ForestSymphonyConfigs.BirdSoundVolume,
								CurrentVolume = ForestSymphonyConfigs.BirdSoundVolume,
								OriginalPitch = 1f,
								CurrentPitch = 1f
							};
							activeBirdSounds.Add(item);
						}
						val.Start();
					}
				}
			}
		}, 50, 0);
	}

	private void OnGameTick(float dt)
	{
		lock (activeSoundsLock)
		{
			for (int num = activeBirdSounds.Count - 1; num >= 0; num--)
			{
				ActiveBirdSound activeBirdSound = activeBirdSounds[num];
				if (activeBirdSound.Sound == null || !activeBirdSound.Sound.IsPlaying)
				{
					((IDisposable)activeBirdSound.Sound)?.Dispose();
					activeBirdSounds.RemoveAt(num);
				}
			}
		}
	}

	public bool HasAnyActiveSound()
	{
		lock (activeSoundsLock)
		{
			foreach (ActiveBirdSound activeBirdSound in activeBirdSounds)
			{
				if (activeBirdSound.Sound != null && activeBirdSound.Sound.IsPlaying)
				{
					return true;
				}
			}
		}
		return false;
	}

	public void StopAllSounds()
	{
		lock (activeSoundsLock)
		{
			for (int i = 0; i < activeBirdSounds.Count; i++)
			{
				ActiveBirdSound activeBirdSound = activeBirdSounds[i];
				if (activeBirdSound.Sound != null && activeBirdSound.Sound.IsPlaying)
				{
					DebugHelper.Debug($"[BIRD DETECTION] Stopping bird sound '{activeBirdSound.SoundPath}' at {activeBirdSound.Position}.");
					activeBirdSound.Sound.Stop();
					((IDisposable)activeBirdSound.Sound).Dispose();
				}
			}
			activeBirdSounds.Clear();
		}
	}

	public void UpdateCoverage(float volumeFactor, float pitchFactor, float dt)
	{
		lock (activeSoundsLock)
		{
			foreach (ActiveBirdSound activeBirdSound in activeBirdSounds)
			{
				if (activeBirdSound.Sound != null && activeBirdSound.Sound.IsPlaying)
				{
					float num = activeBirdSound.OriginalVolume * volumeFactor;
					float num2 = activeBirdSound.OriginalPitch * pitchFactor;
					float num3 = 5f * dt;
					activeBirdSound.CurrentVolume += (num - activeBirdSound.CurrentVolume) * num3;
					activeBirdSound.CurrentPitch += (num2 - activeBirdSound.CurrentPitch) * num3;
					activeBirdSound.Sound.SetVolume(activeBirdSound.CurrentVolume);
					activeBirdSound.Sound.SetPitch(activeBirdSound.CurrentPitch);
				}
			}
		}
	}

	private float GetCurrentTemperature()
	{
		BlockPos asBlockPos = ((Entity)((IPlayer)capi.World.Player).Entity).Pos.AsBlockPos;
		return ((IWorldAccessor)capi.World).BlockAccessor.GetClimateAt(asBlockPos, (EnumGetClimateMode)1, 0.0)?.Temperature ?? 0f;
	}

	private double DistanceBetween(BlockPos a, BlockPos b)
	{
		int num = a.X - b.X;
		int num2 = a.Y - b.Y;
		int num3 = a.Z - b.Z;
		return Math.Sqrt(num * num + num2 * num2 + num3 * num3);
	}

	private double DistanceBetween(BlockPos pos, Vec3f vec)
	{
		double num = (double)pos.X + 0.5 - (double)vec.X;
		double num2 = (double)pos.Y + 0.5 - (double)vec.Y;
		double num3 = (double)pos.Z + 0.5 - (double)vec.Z;
		return Math.Sqrt(num * num + num2 * num2 + num3 * num3);
	}

	private void Shuffle<T>(IList<T> list, Random rng)
	{
		int num = list.Count;
		while (num > 1)
		{
			num--;
			int index = rng.Next(num + 1);
			T value = list[index];
			list[index] = list[num];
			list[num] = value;
		}
	}
}
public class BlockPosComparer : IEqualityComparer<BlockPos>
{
	public bool Equals(BlockPos? x, BlockPos? y)
	{
		if (x == (BlockPos)null && y == (BlockPos)null)
		{
			return true;
		}
		if (x == (BlockPos)null || y == (BlockPos)null)
		{
			return false;
		}
		return x.X == y.X && x.Y == y.Y && x.Z == y.Z;
	}

	public int GetHashCode(BlockPos obj)
	{
		int num = 17;
		num = num * 23 + obj.X.GetHashCode();
		num = num * 23 + obj.Y.GetHashCode();
		return num * 23 + obj.Z.GetHashCode();
	}
}
public enum ForestSeason
{
	Winter,
	Spring,
	Summer,
	Autumn
}
public static class ForestSymphonyConfigs
{
	public static bool EnableDebugLogging { get; set; } = false;

	public static bool EnableBirdCheck { get; set; } = true;

	public static int BirdCheckInterval { get; set; } = 4000;

	public static int BirdMinSounds { get; set; } = 1;

	public static int BirdMaxSounds { get; set; } = 3;

	public static float BirdSoundMinDistance { get; set; } = 10f;

	public static float BirdSoundVolume { get; set; } = 10f;

	public static float BirdSoundRange { get; set; } = 20f;

	public static float BirdActivityWinter { get; set; } = 0.2f;

	public static float BirdActivitySpring { get; set; } = 1f;

	public static float BirdActivitySummer { get; set; } = 0.8f;

	public static float BirdActivityAutumn { get; set; } = 0.4f;

	public static float BirdCheckIntervalWinterMult { get; set; } = 15f;

	public static float BirdCheckIntervalSpringMult { get; set; } = 1f;

	public static float BirdCheckIntervalSummerMult { get; set; } = 1.5f;

	public static float BirdCheckIntervalAutumnMult { get; set; } = 10.5f;

	public static float BirdCheckMultiplierStrong { get; set; } = 0.1f;

	public static float BirdCheckMultiplierWeak { get; set; } = 1f;

	public static float BirdTundraTemperatureThreshold { get; set; } = 15f;

	public static float BirdExoticTemperatureThreshold { get; set; } = 30f;

	public static bool EnablePondCheck { get; set; } = true;

	public static int PondCheckInterval { get; set; } = 1000;

	public static float PondSoundVolume { get; set; } = 10f;

	public static float PondSoundRange { get; set; } = 20f;

	public static float PondActivityWinter { get; set; } = 0f;

	public static float PondActivitySpring { get; set; } = 1f;

	public static float PondActivitySummer { get; set; } = 1f;

	public static float PondActivityAutumn { get; set; } = 0.3f;

	public static float PondCheckIntervalWinterMult { get; set; } = 0f;

	public static float PondCheckIntervalSpringMult { get; set; } = 1f;

	public static float PondCheckIntervalSummerMult { get; set; } = 0.8f;

	public static float PondCheckIntervalAutumnMult { get; set; } = 2.5f;

	public static float PondWindMinChance { get; set; } = 1f;

	public static float PondWindMaxChance { get; set; } = 0f;

	public static bool EnableTreeLogCheck { get; set; } = true;

	public static int TreeLogCheckInterval { get; set; } = 10000;

	public static float TreeSoundVolume { get; set; } = 30f;

	public static float FallingTreeVolume { get; set; } = 1f;

	public static float TreeSoundRange { get; set; } = 20f;

	public static bool EnableFallingTreeCooldown { get; set; } = true;

	public static float FallingTreeCooldownSeconds { get; set; } = 15f;

	public static float TreeLogCheckMultiplierStrong { get; set; } = 0.05f;

	public static float TreeLogCheckMultiplierWeak { get; set; } = 3f;

	public static bool EnableInsectCheck { get; set; } = true;

	public static float InsectSoundVolume { get; set; } = 10f;

	public static float InsectSoundRange { get; set; } = 20f;

	public static float InsectTemperatureThreshold { get; set; } = 25f;

	public static float InsectVegetationChance { get; set; } = 0.5f;

	public static float InsectVegetationCooldownSeconds { get; set; } = 30f;

	public static void LoadOrCreate(ICoreAPI api)
	{
		string configPath = GetConfigPath(api);
		if (File.Exists(configPath))
		{
			try
			{
				string json = File.ReadAllText(configPath);
				ForestSymphonyConfigData forestSymphonyConfigData = JsonSerializer.Deserialize<ForestSymphonyConfigData>(json);
				if (forestSymphonyConfigData != null)
				{
					EnableDebugLogging = forestSymphonyConfigData.EnableDebugLogging;
					EnableBirdCheck = forestSymphonyConfigData.EnableBirdCheck;
					EnablePondCheck = forestSymphonyConfigData.EnablePondCheck;
					EnableTreeLogCheck = forestSymphonyConfigData.EnableTreeLogCheck;
					BirdCheckInterval = forestSymphonyConfigData.BirdCheckInterval;
					PondCheckInterval = forestSymphonyConfigData.PondCheckInterval;
					TreeLogCheckInterval = forestSymphonyConfigData.TreeLogCheckInterval;
					BirdSoundVolume = forestSymphonyConfigData.BirdSoundVolume;
					PondSoundVolume = forestSymphonyConfigData.PondSoundVolume;
					TreeSoundVolume = forestSymphonyConfigData.TreeSoundVolume;
					FallingTreeVolume = forestSymphonyConfigData.FallingTreeVolume;
					BirdSoundRange = forestSymphonyConfigData.BirdSoundRange;
					PondSoundRange = forestSymphonyConfigData.PondSoundRange;
					TreeSoundRange = forestSymphonyConfigData.TreeSoundRange;
					EnableFallingTreeCooldown = forestSymphonyConfigData.EnableFallingTreeCooldown;
					FallingTreeCooldownSeconds = forestSymphonyConfigData.FallingTreeCooldownSeconds;
					BirdActivityWinter = forestSymphonyConfigData.BirdActivityWinter;
					BirdActivitySpring = forestSymphonyConfigData.BirdActivitySpring;
					BirdActivitySummer = forestSymphonyConfigData.BirdActivitySummer;
					BirdActivityAutumn = forestSymphonyConfigData.BirdActivityAutumn;
					PondActivityWinter = forestSymphonyConfigData.PondActivityWinter;
					PondActivitySpring = forestSymphonyConfigData.PondActivitySpring;
					PondActivitySummer = forestSymphonyConfigData.PondActivitySummer;
					PondActivityAutumn = forestSymphonyConfigData.PondActivityAutumn;
					BirdCheckIntervalWinterMult = forestSymphonyConfigData.BirdCheckIntervalWinterMult;
					BirdCheckIntervalSpringMult = forestSymphonyConfigData.BirdCheckIntervalSpringMult;
					BirdCheckIntervalSummerMult = forestSymphonyConfigData.BirdCheckIntervalSummerMult;
					BirdCheckIntervalAutumnMult = forestSymphonyConfigData.BirdCheckIntervalAutumnMult;
					PondCheckIntervalWinterMult = forestSymphonyConfigData.PondCheckIntervalWinterMult;
					PondCheckIntervalSpringMult = forestSymphonyConfigData.PondCheckIntervalSpringMult;
					PondCheckIntervalSummerMult = forestSymphonyConfigData.PondCheckIntervalSummerMult;
					PondCheckIntervalAutumnMult = forestSymphonyConfigData.PondCheckIntervalAutumnMult;
					TreeLogCheckMultiplierStrong = forestSymphonyConfigData.TreeLogCheckMultiplierStrong;
					TreeLogCheckMultiplierWeak = forestSymphonyConfigData.TreeLogCheckMultiplierWeak;
					BirdCheckMultiplierStrong = forestSymphonyConfigData.BirdCheckMultiplierStrong;
					BirdCheckMultiplierWeak = forestSymphonyConfigData.BirdCheckMultiplierWeak;
					PondWindMinChance = forestSymphonyConfigData.PondWindMinChance;
					PondWindMaxChance = forestSymphonyConfigData.PondWindMaxChance;
					EnableInsectCheck = forestSymphonyConfigData.EnableInsectCheck;
					InsectTemperatureThreshold = forestSymphonyConfigData.InsectTemperatureThreshold;
					InsectSoundVolume = forestSymphonyConfigData.InsectSoundVolume;
					InsectSoundRange = forestSymphonyConfigData.InsectSoundRange;
					BirdMinSounds = forestSymphonyConfigData.BirdMinSounds;
					BirdMaxSounds = forestSymphonyConfigData.BirdMaxSounds;
					BirdSoundMinDistance = forestSymphonyConfigData.BirdSoundMinDistance;
					BirdTundraTemperatureThreshold = forestSymphonyConfigData.BirdTundraTemperatureThreshold;
					BirdExoticTemperatureThreshold = forestSymphonyConfigData.BirdExoticTemperatureThreshold;
					InsectVegetationChance = forestSymphonyConfigData.InsectVegetationChance;
					InsectVegetationCooldownSeconds = forestSymphonyConfigData.InsectVegetationCooldownSeconds;
				}
				return;
			}
			catch (Exception ex)
			{
				api.Logger.Error("[ForestSymphonyConfigs] Error loading config file: {0}", new object[1] { ex.Message });
				return;
			}
		}
		Save(api);
	}

	public static void Save(ICoreAPI api)
	{
		try
		{
			string configPath = GetConfigPath(api);
			ForestSymphonyConfigData value = new ForestSymphonyConfigData
			{
				EnableDebugLogging = EnableDebugLogging,
				EnableBirdCheck = EnableBirdCheck,
				EnablePondCheck = EnablePondCheck,
				EnableTreeLogCheck = EnableTreeLogCheck,
				BirdCheckInterval = BirdCheckInterval,
				PondCheckInterval = PondCheckInterval,
				TreeLogCheckInterval = TreeLogCheckInterval,
				BirdSoundVolume = BirdSoundVolume,
				PondSoundVolume = PondSoundVolume,
				TreeSoundVolume = TreeSoundVolume,
				FallingTreeVolume = FallingTreeVolume,
				BirdSoundRange = BirdSoundRange,
				PondSoundRange = PondSoundRange,
				TreeSoundRange = TreeSoundRange,
				EnableFallingTreeCooldown = EnableFallingTreeCooldown,
				FallingTreeCooldownSeconds = FallingTreeCooldownSeconds,
				BirdActivityWinter = BirdActivityWinter,
				BirdActivitySpring = BirdActivitySpring,
				BirdActivitySummer = BirdActivitySummer,
				BirdActivityAutumn = BirdActivityAutumn,
				PondActivityWinter = PondActivityWinter,
				PondActivitySpring = PondActivitySpring,
				PondActivitySummer = PondActivitySummer,
				PondActivityAutumn = PondActivityAutumn,
				BirdCheckIntervalWinterMult = BirdCheckIntervalWinterMult,
				BirdCheckIntervalSpringMult = BirdCheckIntervalSpringMult,
				BirdCheckIntervalSummerMult = BirdCheckIntervalSummerMult,
				BirdCheckIntervalAutumnMult = BirdCheckIntervalAutumnMult,
				PondCheckIntervalWinterMult = PondCheckIntervalWinterMult,
				PondCheckIntervalSpringMult = PondCheckIntervalSpringMult,
				PondCheckIntervalSummerMult = PondCheckIntervalSummerMult,
				PondCheckIntervalAutumnMult = PondCheckIntervalAutumnMult,
				TreeLogCheckMultiplierStrong = TreeLogCheckMultiplierStrong,
				TreeLogCheckMultiplierWeak = TreeLogCheckMultiplierWeak,
				BirdCheckMultiplierStrong = BirdCheckMultiplierStrong,
				BirdCheckMultiplierWeak = BirdCheckMultiplierWeak,
				PondWindMinChance = PondWindMinChance,
				PondWindMaxChance = PondWindMaxChance,
				EnableInsectCheck = EnableInsectCheck,
				InsectTemperatureThreshold = InsectTemperatureThreshold,
				InsectSoundVolume = InsectSoundVolume,
				InsectSoundRange = InsectSoundRange,
				BirdMinSounds = BirdMinSounds,
				BirdMaxSounds = BirdMaxSounds,
				BirdSoundMinDistance = BirdSoundMinDistance,
				BirdTundraTemperatureThreshold = BirdTundraTemperatureThreshold,
				BirdExoticTemperatureThreshold = BirdExoticTemperatureThreshold,
				InsectVegetationChance = InsectVegetationChance,
				InsectVegetationCooldownSeconds = InsectVegetationCooldownSeconds
			};
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			string contents = JsonSerializer.Serialize(value, options);
			File.WriteAllText(configPath, contents);
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ForestSymphonyConfigs] Error saving config file: {0}", new object[1] { ex.Message });
		}
	}

	private static string GetConfigPath(ICoreAPI api)
	{
		return Path.Combine(((ICoreAPICommon)api).GetOrCreateDataPath("ModConfig"), "ForestSymphonyConfigs.json");
	}

	public static ForestSeason GetCurrentSeason(ICoreAPI api, BlockPos pos)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Expected I4, but got Unknown
		object obj;
		if (api == null)
		{
			obj = null;
		}
		else
		{
			IWorldAccessor world = api.World;
			obj = ((world != null) ? world.Calendar : null);
		}
		if (obj == null)
		{
			return ForestSeason.Winter;
		}
		EnumSeason season = api.World.Calendar.GetSeason(pos);
		if (1 == 0)
		{
		}
		ForestSeason result = (int)season switch
		{
			0 => ForestSeason.Spring, 
			1 => ForestSeason.Summer, 
			2 => ForestSeason.Autumn, 
			_ => ForestSeason.Winter, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public static float GetBirdActivityMultiplier(ICoreAPI api, BlockPos pos)
	{
		return GetCurrentSeason(api, pos) switch
		{
			ForestSeason.Spring => BirdActivitySpring, 
			ForestSeason.Summer => BirdActivitySummer, 
			ForestSeason.Autumn => BirdActivityAutumn, 
			_ => BirdActivityWinter, 
		};
	}

	public static float GetPondActivityMultiplier(ICoreAPI api, BlockPos pos)
	{
		return GetCurrentSeason(api, pos) switch
		{
			ForestSeason.Spring => PondActivitySpring, 
			ForestSeason.Summer => PondActivitySummer, 
			ForestSeason.Autumn => PondActivityAutumn, 
			_ => PondActivityWinter, 
		};
	}

	public static float GetBirdIntervalMultiplier(ICoreAPI api, BlockPos pos)
	{
		return GetCurrentSeason(api, pos) switch
		{
			ForestSeason.Spring => BirdCheckIntervalSpringMult, 
			ForestSeason.Summer => BirdCheckIntervalSummerMult, 
			ForestSeason.Autumn => BirdCheckIntervalAutumnMult, 
			_ => BirdCheckIntervalWinterMult, 
		};
	}

	public static float GetPondIntervalMultiplier(ICoreAPI api, BlockPos pos)
	{
		return GetCurrentSeason(api, pos) switch
		{
			ForestSeason.Spring => PondCheckIntervalSpringMult, 
			ForestSeason.Summer => PondCheckIntervalSummerMult, 
			ForestSeason.Autumn => PondCheckIntervalAutumnMult, 
			_ => PondCheckIntervalWinterMult, 
		};
	}
}
internal class ForestSymphonyConfigData
{
	public string EnableDebugLoggingComment { get; set; } = "Enable logs, you shouldnt care too much about this unless youre really curious and wanna see how everything works.";

	public bool EnableDebugLogging { get; set; } = false;

	public string BirdsConfigs { get; set; } = "------------------------------------------BIRDS------------------------------------------";

	public string EnableBirdCheckConfig { get; set; } = "Set to false if you want to completely disable bird sounds.";

	public bool EnableBirdCheck { get; set; } = true;

	public string BirdCheckIntervalConfig { get; set; } = "Check intervals for bird sounds, this defines the interval in seconds at wich the mod will scan the area for leaves. Low values means faster response to environment but performance intensive, higher values means slower response to environment but can help with performance. all other multipliers (wind, seasons, temperature) rely on this variable.";

	public int BirdCheckInterval { get; set; } = 4000;

	public string BirdQueueRangeConfig { get; set; } = "This sets how many bird sounds are queued in each check.";

	public int BirdMinSounds { get; set; } = 1;

	public int BirdMaxSounds { get; set; } = 3;

	public string BirdQSoundDistanceConfig { get; set; } = "This set the distance in blocks that bird sounds are allowed to trigger for a leave block. Increase it if you want more spaced out sounds, decrease if you like clustering sounds more.";

	public float BirdSoundMinDistance { get; set; } = 10f;

	public string BirdSoundRangeConfig { get; set; } = "Adjust sound volume and range for bird sounds.";

	public float BirdSoundVolume { get; set; } = 10f;

	public float BirdSoundRange { get; set; } = 20f;

	public string SeasonalBirdActivityConfig { get; set; } = "Multipliers for how many sounds are queued each check (0.0 = none, 1.0 = normal, higher than 1 = more) based on the current season.";

	public float BirdActivityWinter { get; set; } = 0.2f;

	public float BirdActivitySpring { get; set; } = 1f;

	public float BirdActivitySummer { get; set; } = 0.8f;

	public float BirdActivityAutumn { get; set; } = 0.4f;

	public string BirdSeasonalIntervalsConfig { get; set; } = "Multipliers for check interval (0.5 = 2x faster, 2.0 = 2x slower). Based on the current season.";

	public float BirdCheckIntervalWinterMult { get; set; } = 15f;

	public float BirdCheckIntervalSpringMult { get; set; } = 1f;

	public float BirdCheckIntervalSummerMult { get; set; } = 1.5f;

	public float BirdCheckIntervalAutumnMult { get; set; } = 10.5f;

	public string WindMultiplierBirdConfig { get; set; } = "This multiplies how often bird checks happen based on wind speed.";

	public float BirdCheckMultiplierStrong { get; set; } = 0.1f;

	public float BirdCheckMultiplierWeak { get; set; } = 1f;

	public string BirdTemperatureConfig { get; set; } = "If temperature is += BirdExoticTemperatureThreshold then exotic birds only; if - BirdTundraTemperatureThreshold then tundra birds only; otherwise a mix of both.";

	public float BirdTundraTemperatureThreshold { get; set; } = 15f;

	public float BirdExoticTemperatureThreshold { get; set; } = 30f;

	public string PondsConfigs { get; set; } = "------------------------------------------PONDS------------------------------------------";

	public string EnablePondCheckConfig { get; set; } = "Set to false if you want to completely disable ponds sounds.";

	public bool EnablePondCheck { get; set; } = true;

	public string PondCheckIntervalConfig { get; set; } = "Check intervals for pond sounds, this defines the interval in seconds at wich the mod will scan the area for water. Low values means faster response to environment but performance intensive, higher values means slower response to environment but can help with performance. all other multipliers (wind, seasons, temperature) rely on this variable.";

	public int PondCheckInterval { get; set; } = 1000;

	public string PondSoundRangeConfig { get; set; } = "Adjust sound volume and range for pond sounds.";

	public float PondSoundVolume { get; set; } = 10f;

	public float PondSoundRange { get; set; } = 20f;

	public string SeasonalPondActivityConfig { get; set; } = "Multipliers for how many sounds are queued each check (0.0 = none, 1.0 = normal, higher than 1 = more) based on the current season.";

	public float PondActivityWinter { get; set; } = 0f;

	public float PondActivitySpring { get; set; } = 1f;

	public float PondActivitySummer { get; set; } = 1f;

	public float PondActivityAutumn { get; set; } = 0.3f;

	public string PondSeasonalIntervalsConfig { get; set; } = "Multipliers for check interval (0.5 = 2x faster, 2.0 = 2x slower). Based on the current season.";

	public float PondCheckIntervalWinterMult { get; set; } = 0f;

	public float PondCheckIntervalSpringMult { get; set; } = 1f;

	public float PondCheckIntervalSummerMult { get; set; } = 0.8f;

	public float PondCheckIntervalAutumnMult { get; set; } = 2.5f;

	public string WindMultiplierPondConfig { get; set; } = "This multiplies how often pond checks happen based on wind speed.";

	public float PondWindMinChance { get; set; } = 1f;

	public float PondWindMaxChance { get; set; } = 0f;

	public string TreeLogsConfigs { get; set; } = "------------------------------------------TREE LOGS------------------------------------------";

	public string EnableTreeLogsCheckConfig { get; set; } = "Set to false if you want to completely disable Tree Logs sounds.";

	public bool EnableTreeLogCheck { get; set; } = true;

	public string TreeLogCheckIntervalConfig { get; set; } = "Check intervals for Tree logs sounds, this defines the interval in seconds at wich the mod will scan the area for Logs. Low values means faster response to environment but performance intensive, higher values means slower response to environment but can help with performance. all other multipliers (wind, seasons, temperature) rely on this variable.";

	public int TreeLogCheckInterval { get; set; } = 10000;

	public string TreeLogSoundRangeConfig { get; set; } = "Adjust sound volume and range for tree logs sounds.";

	public float TreeSoundVolume { get; set; } = 30f;

	public float FallingTreeVolume { get; set; } = 1f;

	public float TreeSoundRange { get; set; } = 20f;

	public string FallingTreeSoundConfig { get; set; } = "This is for the sound that plays when chopping down trees, you can set the cooldown to false to make it play for every tree.";

	public bool EnableFallingTreeCooldown { get; set; } = true;

	public float FallingTreeCooldownSeconds { get; set; } = 15f;

	public string WindMultiplierConfig { get; set; } = "This multiplier is for the treeLog check. It multiplies or divides (if your custom number is less than 1) it, based on wind speed.";

	public float TreeLogCheckMultiplierStrong { get; set; } = 0.05f;

	public float TreeLogCheckMultiplierWeak { get; set; } = 3f;

	public string InsectsConfigs { get; set; } = "------------------------------------------INSECTS------------------------------------------";

	public string InsectCheckConfig { get; set; } = "Configure whether insects are checked, and the temperature threshold for insect sounds.";

	public bool EnableInsectCheck { get; set; } = true;

	public string InsectsSoundRangeConfig { get; set; } = "Adjust sound volume and range for insects sounds.";

	public float InsectSoundVolume { get; set; } = 1f;

	public float InsectSoundRange { get; set; } = 20f;

	public string InsectsTemperatureConfig { get; set; } = "Adjust outside temperature at wich insects will start buzzing.";

	public float InsectTemperatureThreshold { get; set; } = 25f;

	public string InsectVegetationChanceComment { get; set; } = "Chance (0.0 - 1.0) for vegetation insect sound trigger when standing inside leaves, tallgrass, or bush.";

	public float InsectVegetationChance { get; set; } = 0.5f;

	public string InsectVegetationCooldownSecondsComment { get; set; } = "Cooldown (in seconds) between vegetation insect sound triggers.";

	public float InsectVegetationCooldownSeconds { get; set; } = 30f;
}
public static class DebugHelper
{
	public static ICoreAPI? Api { get; set; }

	public static void Debug(string message)
	{
		if (Api != null && ForestSymphonyConfigs.EnableDebugLogging)
		{
			Api.Logger.Debug("[ForestSymphony] " + message);
		}
	}
}
public class FallingTree
{
	private readonly ICoreClientAPI capi;

	private readonly Dictionary<BlockPos, long> brokenLogs = new Dictionary<BlockPos, long>();

	private const int TreeChopThreshold = 300;

	private bool cooldownActive = false;

	public BirdsSounds? BirdSoundSystem { get; set; }

	public FallingTree(ICoreClientAPI capi)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected O, but got Unknown
		this.capi = capi;
		capi.Event.BlockChanged += new BlockChangedDelegate(OnBlockChanged);
	}

	private void OnBlockChanged(BlockPos pos, Block oldBlock)
	{
		if (oldBlock == null || ((RegistryObject)oldBlock).Code == (AssetLocation)null || ((RegistryObject)oldBlock).Code.Path == null || !((RegistryObject)oldBlock).Code.Path.StartsWith("log-grown-"))
		{
			return;
		}
		DebugHelper.Debug($"[TREE FALL] Natural log broken at {pos}.");
		if (ForestSymphonyConfigs.EnableFallingTreeCooldown && cooldownActive)
		{
			DebugHelper.Debug("[TREE FALL] Cooldown is active; skipping falling tree effect.");
		}
		else if (BirdSoundSystem != null && BirdSoundSystem.HasAnyActiveSound() && HasLeavesAbove(pos))
		{
			DebugHelper.Debug("[TREE FALL] Found at least one bird sound playing AND leaves above. Triggering flapping sound...");
			PlayStartleSound(pos);
			ScheduleStopAllBirdSounds();
			if (ForestSymphonyConfigs.EnableFallingTreeCooldown)
			{
				StartCooldown();
			}
		}
		else
		{
			DebugHelper.Debug("[TREE FALL] Conditions not met: either no bird sounds or no leaves above. Skipping flapping sound.");
		}
	}

	private bool HasLeavesAbove(BlockPos pos)
	{
		int num = 15;
		int num2 = 3;
		for (int i = 1; i <= num; i++)
		{
			for (int j = -num2; j <= num2; j++)
			{
				for (int k = -num2; k <= num2; k++)
				{
					BlockPos val = pos.AddCopy(j, i, k);
					Block block = ((IWorldAccessor)capi.World).BlockAccessor.GetBlock(val);
					if (block != null && ((RegistryObject)block).Code != (AssetLocation)null && ((RegistryObject)block).FirstCodePart(0) == "leaves")
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private void PlayStartleSound(BlockPos pos)
	{
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Expected O, but got Unknown
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Expected O, but got Unknown
		string[] array = new string[3] { "startled_1", "startled_2", "startled_3" };
		string text = array[((IWorldAccessor)capi.World).Rand.Next(array.Length)];
		Vec3f val = new Vec3f((float)pos.X + 0.5f, (float)pos.Y + 10.5f, (float)pos.Z + 0.5f);
		DebugHelper.Debug($"[TREE FALL] Playing flapping bird sound '{text}' at {pos}");
		float fallingTreeVolume = ForestSymphonyConfigs.FallingTreeVolume;
		((IWorldAccessor)capi.World).PlaySoundAt(new AssetLocation("forestsymphony:sounds/tree/" + text), (double)val.X, (double)val.Y, (double)val.Z, (IPlayer)null, true, 32f, fallingTreeVolume);
	}

	private void ScheduleStopAllBirdSounds()
	{
		float elapsed = 0f;
		long listenerId = 0L;
		listenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)delegate(float dt)
		{
			elapsed += dt;
			if (elapsed >= 1f)
			{
				BirdSoundSystem?.StopAllSounds();
				DebugHelper.Debug("[TREE FALL] All bird sounds stopped after 1 second delay.");
				((IEventAPI)capi.Event).UnregisterGameTickListener(listenerId);
			}
		}, 50, 0);
	}

	private void StartCooldown()
	{
		cooldownActive = true;
		float accumulated = 0f;
		long cooldownListenerId = 0L;
		float cdSecs = ForestSymphonyConfigs.FallingTreeCooldownSeconds;
		DebugHelper.Debug($"[TREE FALL] Starting cooldown for {cdSecs} seconds.");
		cooldownListenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)delegate(float dt)
		{
			accumulated += dt;
			if (accumulated >= cdSecs)
			{
				cooldownActive = false;
				DebugHelper.Debug("[TREE FALL] Falling tree cooldown ended; effect can trigger again.");
				((IEventAPI)capi.Event).UnregisterGameTickListener(cooldownListenerId);
			}
		}, 50, 0);
	}
}
public class ForestSymphonyClient : ModSystem
{
	private BirdCheck? birdCheck;

	private PondCheck? pondCheck;

	private TreeLogCheck? treeLogCheck;

	private FallingTree? FallingTree;

	private ICoreClientAPI? api;

	private bool searchComplete = true;

	private float coverageLoss = 0f;

	private readonly List<ILoadedSound> preloadedSounds = new List<ILoadedSound>();

	private const int FadeTickIntervalMs = 50;

	private const int CoverageTickIntervalMs = 250;

	public override void StartClientSide(ICoreClientAPI api)
	{
		((ModSystem)this).StartClientSide(api);
		this.api = api;
		ForestSymphonyConfigs.LoadOrCreate((ICoreAPI)(object)api);
		DebugHelper.Api = (ICoreAPI?)(object)api;
		DebugHelper.Debug("Forest Symphony client initialized.");
		api.Event.LevelFinalize += OnWorldLoaded;
		FallingTree = new FallingTree(api);
	}

	private void OnWorldLoaded()
	{
		if (api == null)
		{
			return;
		}
		PreloadAllModSounds(api);
		IPlayer player = (IPlayer)(object)api.World.Player;
		if (ForestSymphonyConfigs.EnableBirdCheck)
		{
			birdCheck = new BirdCheck(api, player);
			birdCheck.StartSeasonalListener();
		}
		if (ForestSymphonyConfigs.EnablePondCheck)
		{
			pondCheck = new PondCheck(api, player);
			pondCheck.StartSeasonalListener();
		}
		if (ForestSymphonyConfigs.EnableTreeLogCheck)
		{
			treeLogCheck = new TreeLogCheck(api, player);
			((IEventAPI)api.Event).RegisterGameTickListener((Action<float>)delegate(float dt)
			{
				treeLogCheck.Update(dt);
			}, 50, 0);
		}
		if (birdCheck != null && FallingTree != null)
		{
			FallingTree.BirdSoundSystem = birdCheck.BirdsSounds;
		}
		((IEventAPI)api.Event).RegisterGameTickListener((Action<float>)OnCoverageCheck, 250, 0);
		((IEventAPI)api.Event).RegisterGameTickListener((Action<float>)OnForestSymphonyFadeTick, 50, 0);
		api.Event.LevelFinalize -= OnWorldLoaded;
	}

	private void PreloadAllModSounds(ICoreClientAPI api)
	{
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Expected O, but got Unknown
		string modID = ((ModSystem)this).Mod.Info.ModID;
		List<IAsset> many = ((ICoreAPI)api).Assets.GetMany("", modID, false);
		IEnumerable<AssetLocation> enumerable = (from a in many
			select a.Location into loc
			where loc.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
			select loc).Distinct();
		foreach (AssetLocation item in enumerable)
		{
			SoundParams val = new SoundParams
			{
				Location = item,
				ShouldLoop = false,
				Volume = 0f,
				Range = 0f,
				SoundType = (EnumSoundType)2
			};
			ILoadedSound val2 = api.World.LoadSound(val);
			if (val2 != null)
			{
				preloadedSounds.Add(val2);
			}
		}
		((ICoreAPI)api).Logger.Event($"[ForestSymphony] Preloaded {preloadedSounds.Count} sound assets.");
	}

	private void OnCoverageCheck(float dt)
	{
		ICoreClientAPI? obj = api;
		object obj2;
		if (obj == null)
		{
			obj2 = null;
		}
		else
		{
			IClientWorldAccessor world = obj.World;
			obj2 = ((world != null) ? world.Player : null);
		}
		if (obj2 != null && searchComplete)
		{
			searchComplete = false;
			BlockPos asBlockPos = ((Entity)((IPlayer)api.World.Player).Entity).Pos.AsBlockPos;
			int distanceToRainFall = ((IWorldAccessor)api.World).BlockAccessor.GetDistanceToRainFall(asBlockPos, 12, 4);
			float num = ((float)distanceToRainFall - 2f) / 10f;
			num = GameMath.Max(num, 0f);
			float num2 = GameMath.Clamp((float)Math.Pow(num, 2.0), 0f, 1f);
			coverageLoss = num2;
			searchComplete = true;
		}
	}

	private void OnForestSymphonyFadeTick(float dt)
	{
		float num = coverageLoss * 0.95f;
		float volumeFactor = 1f - num;
		float pitchFactor = 1f - num * 0.05f;
		birdCheck?.UpdateCoverage(volumeFactor, pitchFactor, dt);
		pondCheck?.UpdateCoverage(volumeFactor, pitchFactor, dt);
	}

	public override void Dispose()
	{
		foreach (ILoadedSound preloadedSound in preloadedSounds)
		{
			preloadedSound.Stop();
			((IDisposable)preloadedSound).Dispose();
		}
		preloadedSounds.Clear();
		((ModSystem)this).Dispose();
	}
}
public class InsectsCheck
{
	private readonly ICoreClientAPI capi;

	private readonly IPlayer player;

	private readonly InsectsSounds insectsSounds;

	public InsectsCheck(ICoreClientAPI capi, IPlayer player)
	{
		this.capi = capi ?? throw new ArgumentNullException("capi", "[INSECTS CHECK] capi is null!");
		this.player = player ?? throw new ArgumentNullException("player", "[INSECTS CHECK] player is null!");
		((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)delegate
		{
			ProcessPlayerVegetation();
		}, 500, 0);
		insectsSounds = new InsectsSounds(this.capi);
		DebugHelper.Debug("[INSECTS CHECK] InsectsCheck initialized successfully.");
	}

	public void ProcessEnvironment(Dictionary<BlockPos, Block> waterPositions, Dictionary<BlockPos, Block> foliagePositions)
	{
		if (!ForestSymphonyConfigs.EnableInsectCheck)
		{
			DebugHelper.Debug("[INSECTS CHECK] InsectCheck is disabled in config.");
			return;
		}
		if (foliagePositions == null || foliagePositions.Count == 0)
		{
			DebugHelper.Debug("[INSECTS CHECK] No foliage found in BFS, skipping 'Insect_' sound from environment scan.");
			return;
		}
		float currentTemperature = GetCurrentTemperature();
		float insectTemperatureThreshold = ForestSymphonyConfigs.InsectTemperatureThreshold;
		DebugHelper.Debug($"[INSECTS CHECK] Current temperature = {currentTemperature:F1}°C");
		if (currentTemperature >= insectTemperatureThreshold)
		{
			if (((IWorldAccessor)capi.World).Rand.NextDouble() < 0.5)
			{
				BlockPos[] array = new List<BlockPos>(foliagePositions.Keys).ToArray();
				if (array.Length != 0)
				{
					int num = ((IWorldAccessor)capi.World).Rand.Next(array.Length);
					BlockPos val = array[num];
					insectsSounds.QueueNearWaterInsectSound(val);
					DebugHelper.Debug($"[INSECTS CHECK] Queued near-water Insect_ sound near {val}, T={currentTemperature:F1}°C");
				}
			}
			else
			{
				DebugHelper.Debug("[INSECTS CHECK] Random check skipped near-water insect sound this time.");
			}
		}
		else
		{
			DebugHelper.Debug($"[INSECTS CHECK] Temperature {currentTemperature:F1}°C is below threshold {insectTemperatureThreshold:F1}°C; no near-water insect sound played.");
		}
	}

	public void ProcessPlayerVegetation()
	{
		if (!ForestSymphonyConfigs.EnableInsectCheck)
		{
			return;
		}
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		Block block = ((IWorldAccessor)capi.World).BlockAccessor.GetBlock(asBlockPos);
		if (block == null)
		{
			return;
		}
		AssetLocation code = ((RegistryObject)block).Code;
		string text = ((code != null) ? code.Path : null) ?? "";
		if (text.Contains("leaves") || text.Contains("bush"))
		{
			float currentTemperature = GetCurrentTemperature();
			float insectTemperatureThreshold = ForestSymphonyConfigs.InsectTemperatureThreshold;
			DebugHelper.Debug($"[INSECTS CHECK] Player inside {text} at {asBlockPos}, Temp={currentTemperature:F1}°C");
			if (currentTemperature >= insectTemperatureThreshold)
			{
				insectsSounds.QueueVegetationInsectSound(asBlockPos);
				DebugHelper.Debug("[INSECTS CHECK] Queued vegetation insect sound.");
				return;
			}
			DebugHelper.Debug($"[INSECTS CHECK] Temperature {currentTemperature:F1}°C is below threshold.");
		}
	}

	public void TryPlayPondInsect(BlockPos pondCenterPos)
	{
		if (ForestSymphonyConfigs.EnableInsectCheck)
		{
			float currentTemperature = GetCurrentTemperature();
			float insectTemperatureThreshold = ForestSymphonyConfigs.InsectTemperatureThreshold;
			if (currentTemperature < insectTemperatureThreshold)
			{
				DebugHelper.Debug($"[INSECTS CHECK] Temperature {currentTemperature:F1}°C is below threshold {insectTemperatureThreshold:F1}°C; skipping PondInsect_ at {pondCenterPos}.");
			}
			else
			{
				insectsSounds.QueuePondInsectSound(pondCenterPos);
				DebugHelper.Debug($"[INSECTS CHECK] Queued 'PondInsect_' sound near {pondCenterPos}, T={currentTemperature:F1}°C.");
			}
		}
	}

	private float GetCurrentTemperature()
	{
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		return ((IWorldAccessor)capi.World).BlockAccessor.GetClimateAt(asBlockPos, (EnumGetClimateMode)1, 0.0)?.Temperature ?? 0f;
	}
}
public class InsectsSounds
{
	private class SoundRequest
	{
		public BlockPos Position { get; }

		public bool IsPondInsect { get; }

		public bool IsVegetation { get; }

		public SoundRequest(BlockPos position, bool isPondInsect, bool isVegetation = false)
		{
			Position = position;
			IsPondInsect = isPondInsect;
			IsVegetation = isVegetation;
		}
	}

	private class ActiveInsectSound
	{
		public ILoadedSound Sound { get; }

		public BlockPos Position { get; }

		public string SoundPath { get; }

		public ActiveInsectSound(ILoadedSound sound, BlockPos pos, string path)
		{
			Sound = sound;
			Position = pos;
			SoundPath = path;
		}
	}

	private readonly ICoreClientAPI capi;

	private readonly object insectSoundsLock = new object();

	private readonly List<ActiveInsectSound> activeInsectSounds = new List<ActiveInsectSound>();

	private readonly Queue<SoundRequest> pendingInsectSounds = new Queue<SoundRequest>();

	private readonly List<string> insectVegetationClips = new List<string> { "insect_1", "insect_2", "insect_3", "insect_5" };

	private readonly List<string> insectNearWaterClips = new List<string> { "insect_4", "insect_6", "insect_7", "insect_8" };

	private readonly List<string> pondInsectDayClips = new List<string> { "pondinsect_1", "pondinsect_2", "pondinsect_3", "pondinsect_4" };

	private readonly List<string> pondInsectNightClips = new List<string> { "pondinsectnight_1", "pondinsectnight_2", "pondinsectnight_3", "pondinsectnight_4" };

	private double nextAllowedVegetationInsectTime = 0.0;

	private double nextAllowedNearWaterInsectTime = 0.0;

	private string lastVegetationInsectClip = "";

	private string lastNearWaterInsectClip = "";

	private string lastPondInsectDayClip = "";

	private string lastPondInsectNightClip = "";

	public int MaxInsectSounds { get; private set; } = 5;

	public InsectsSounds(ICoreClientAPI capi)
	{
		this.capi = capi ?? throw new ArgumentNullException("capi", "[INSECTS SOUNDS] capi is null!");
		((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnGameTick, 1000, 0);
		DebugHelper.Debug("[INSECTS SOUNDS] InsectsSounds initialized successfully.");
	}

	public void QueueVegetationInsectSound(BlockPos pos)
	{
		lock (insectSoundsLock)
		{
			pendingInsectSounds.Enqueue(new SoundRequest(pos, isPondInsect: false, isVegetation: true));
		}
	}

	public void QueueNearWaterInsectSound(BlockPos pos)
	{
		lock (insectSoundsLock)
		{
			pendingInsectSounds.Enqueue(new SoundRequest(pos, isPondInsect: false));
		}
	}

	public void QueuePondInsectSound(BlockPos pos)
	{
		lock (insectSoundsLock)
		{
			pendingInsectSounds.Enqueue(new SoundRequest(pos, isPondInsect: true));
		}
	}

	private void OnGameTick(float dt)
	{
		lock (insectSoundsLock)
		{
			for (int num = activeInsectSounds.Count - 1; num >= 0; num--)
			{
				ActiveInsectSound activeInsectSound = activeInsectSounds[num];
				if (activeInsectSound.Sound == null || !activeInsectSound.Sound.IsPlaying)
				{
					((IDisposable)activeInsectSound.Sound)?.Dispose();
					activeInsectSounds.RemoveAt(num);
				}
			}
			if (pendingInsectSounds.Count > 0 && activeInsectSounds.Count < MaxInsectSounds)
			{
				SoundRequest req = pendingInsectSounds.Dequeue();
				TryPlaySound(req);
			}
		}
	}

	private void TryPlaySound(SoundRequest req)
	{
		//IL_03b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_03bd: Expected O, but got Unknown
		//IL_03c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_03cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03da: Expected O, but got Unknown
		//IL_03da: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0407: Unknown result type (might be due to invalid IL or missing references)
		//IL_0409: Unknown result type (might be due to invalid IL or missing references)
		//IL_0413: Expected O, but got Unknown
		if (!req.IsPondInsect)
		{
			double num = (double)((IWorldAccessor)capi.World).ElapsedMilliseconds / 1000.0;
			if (req.IsVegetation)
			{
				if (num < nextAllowedVegetationInsectTime)
				{
					DebugHelper.Debug("[INSECTS SOUNDS] Too soon for next vegetation insect sound, skipping.");
					return;
				}
				if (((IWorldAccessor)capi.World).Rand.NextDouble() > (double)ForestSymphonyConfigs.InsectVegetationChance)
				{
					DebugHelper.Debug("[INSECTS SOUNDS] Vegetation insect chance check failed, skipping.");
					return;
				}
				nextAllowedVegetationInsectTime = num + (double)ForestSymphonyConfigs.InsectVegetationCooldownSeconds;
				DebugHelper.Debug($"[INSECTS SOUNDS] Setting vegetation insect cooldown: {ForestSymphonyConfigs.InsectVegetationCooldownSeconds:F1}s");
			}
			else
			{
				if (num < nextAllowedNearWaterInsectTime)
				{
					DebugHelper.Debug("[INSECTS SOUNDS] Too soon for next near water insect sound, skipping.");
					return;
				}
				float currentTemperature = GetCurrentTemperature();
				float num2 = ComputeCooldown(currentTemperature);
				nextAllowedNearWaterInsectTime = num + (double)num2;
				DebugHelper.Debug($"[INSECTS SOUNDS] Setting near water insect cooldown: {num2:F1}s at temp={currentTemperature:F1}°C");
			}
		}
		bool flag = false;
		if (req.IsPondInsect)
		{
			double totalHours = ((IGameCalendar)capi.World.Calendar).TotalHours;
			double num3 = totalHours % 24.0;
			flag = num3 >= 5.0 && num3 < 18.0;
		}
		string text = "";
		List<string> list;
		if (req.IsPondInsect)
		{
			if (flag)
			{
				list = pondInsectDayClips;
				text = lastPondInsectDayClip;
			}
			else
			{
				list = pondInsectNightClips;
				text = lastPondInsectNightClip;
			}
		}
		else if (req.IsVegetation)
		{
			list = insectVegetationClips;
			text = lastVegetationInsectClip;
		}
		else
		{
			list = insectNearWaterClips;
			text = lastNearWaterInsectClip;
		}
		if (list.Count == 0)
		{
			DebugHelper.Debug("[INSECTS SOUNDS] No insect clips found for this type/time!");
			return;
		}
		string text2 = PickNonRepeatingClip(list, text);
		string text4;
		if (req.IsPondInsect)
		{
			string text3 = (flag ? "day" : "night");
			text4 = "forestsymphony:sounds/" + text3 + "/insect/" + text2;
		}
		else
		{
			text4 = "forestsymphony:sounds/insect/" + text2;
		}
		double num4 = 20.0;
		foreach (ActiveInsectSound activeInsectSound in activeInsectSounds)
		{
			double num5 = req.Position.DistanceTo(activeInsectSound.Position);
			if (num5 < num4 && activeInsectSound.Sound != null && activeInsectSound.Sound.IsPlaying)
			{
				DebugHelper.Debug($"[INSECTS SOUNDS] Another insect sound is too close at {activeInsectSound.Position}, skipping.");
				return;
			}
		}
		Vec3f position = new Vec3f((float)req.Position.X + 0.5f, (float)req.Position.Y + 0.5f, (float)req.Position.Z + 0.5f);
		ILoadedSound val = capi.World.LoadSound(new SoundParams
		{
			Location = new AssetLocation(text4),
			ShouldLoop = false,
			Position = position,
			DisposeOnFinish = true,
			Volume = ForestSymphonyConfigs.InsectSoundVolume,
			Range = ForestSymphonyConfigs.InsectSoundRange,
			SoundType = (EnumSoundType)2
		});
		if (val == null)
		{
			DebugHelper.Debug("[INSECTS SOUNDS] Failed to load insect sound '" + text4 + "'.");
			return;
		}
		val.Start();
		ActiveInsectSound item = new ActiveInsectSound(val, req.Position, text4);
		activeInsectSounds.Add(item);
		if (req.IsPondInsect)
		{
			if (flag)
			{
				lastPondInsectDayClip = text2;
			}
			else
			{
				lastPondInsectNightClip = text2;
			}
		}
		else if (req.IsVegetation)
		{
			lastVegetationInsectClip = text2;
		}
		else
		{
			lastNearWaterInsectClip = text2;
		}
		DebugHelper.Debug($"[INSECTS SOUNDS] Playing '{text4}' at {req.Position} (Pond? {req.IsPondInsect}, Vegetation? {req.IsVegetation}).");
	}

	private float GetCurrentTemperature()
	{
		BlockPos asBlockPos = ((Entity)((IPlayer)capi.World.Player).Entity).Pos.AsBlockPos;
		return ((IWorldAccessor)capi.World).BlockAccessor.GetClimateAt(asBlockPos, (EnumGetClimateMode)1, 0.0)?.Temperature ?? 0f;
	}

	private float ComputeCooldown(float temperature)
	{
		temperature = Math.Clamp(temperature, 20f, 35f);
		return 50f - temperature;
	}

	private string PickNonRepeatingClip(List<string> clipList, string lastClip)
	{
		if (clipList.Count < 2)
		{
			return clipList[((IWorldAccessor)capi.World).Rand.Next(clipList.Count)];
		}
		string text = lastClip;
		for (int i = 0; i < 3; i++)
		{
			text = clipList[((IWorldAccessor)capi.World).Rand.Next(clipList.Count)];
			if (text != lastClip)
			{
				break;
			}
		}
		return text;
	}
}
public class PondCheck
{
	private readonly ICoreClientAPI capi;

	private readonly IPlayer player;

	private readonly PondSounds pondSounds;

	private readonly InsectsCheck insectsCheck;

	private Dictionary<BlockPos, Block> cachedFoliage = new Dictionary<BlockPos, Block>();

	private Dictionary<BlockPos, Block> cachedWater = new Dictionary<BlockPos, Block>();

	private long pondListenerId = 0L;

	private ForestSeason lastSeason;

	private BlockPos lastCheckedPosition = new BlockPos();

	private readonly SemaphoreSlim waterUpdateLock = new SemaphoreSlim(1, 1);

	public PondCheck(ICoreClientAPI capi, IPlayer player)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Expected O, but got Unknown
		this.capi = capi ?? throw new ArgumentNullException("capi", "[POND CHECK] capi is null!");
		this.player = player ?? throw new ArgumentNullException("player", "[POND CHECK] player is null!");
		pondSounds = new PondSounds(capi, this);
		insectsCheck = new InsectsCheck(capi, player);
		DebugHelper.Debug("[POND CHECK] PondCheck initialized successfully.");
		lastSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, ((Entity)player.Entity).Pos.AsBlockPos);
	}

	public void StartSeasonalListener()
	{
		RefreshInterval();
	}

	private void RefreshInterval()
	{
		if (pondListenerId != 0)
		{
			((IEventAPI)capi.Event).UnregisterGameTickListener(pondListenerId);
			pondListenerId = 0L;
		}
		if (!ForestSymphonyConfigs.EnablePondCheck)
		{
			return;
		}
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		float pondIntervalMultiplier = ForestSymphonyConfigs.GetPondIntervalMultiplier((ICoreAPI)(object)capi, asBlockPos);
		if (pondIntervalMultiplier <= 0f)
		{
			DebugHelper.Debug($"[POND CHECK] Season multiplier is 0. Heartbeat every {5000} ms.");
			pondListenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnPondHeartbeatTick, 5000, 0);
			return;
		}
		float num = ForestSymphonyConfigs.PondCheckInterval;
		float num2 = num * pondIntervalMultiplier;
		if (num2 < 100f)
		{
			num2 = 100f;
		}
		int num3 = (int)num2;
		ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, asBlockPos);
		DebugHelper.Debug($"[POND CHECK] (Re)Registering. Season={currentSeason}, final interval={num3}ms");
		pondListenerId = ((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnPondCheckTick, num3, 0);
	}

	private void OnPondCheckTick(float dt)
	{
		if (!ForestSymphonyConfigs.EnablePondCheck)
		{
			return;
		}
		IPlayer obj = player;
		if (((obj != null) ? obj.Entity : null) != null)
		{
			RunPondCheck();
			ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, ((Entity)player.Entity).Pos.AsBlockPos);
			if (currentSeason != lastSeason)
			{
				lastSeason = currentSeason;
				RefreshInterval();
			}
		}
	}

	private void OnPondHeartbeatTick(float dt)
	{
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		ForestSeason currentSeason = ForestSymphonyConfigs.GetCurrentSeason((ICoreAPI)(object)capi, asBlockPos);
		if (currentSeason != lastSeason)
		{
			lastSeason = currentSeason;
			RefreshInterval();
		}
	}

	private bool IsPlayerInDeepWater(BlockPos playerPos)
	{
		int num = 3;
		int num2 = 0;
		int num3 = 9;
		for (int num4 = 0; num4 >= -2; num4--)
		{
			for (int i = -num; i <= num; i++)
			{
				for (int j = -num; j <= num; j++)
				{
					BlockPos val = playerPos.AddCopy(i, num4, j);
					Block block = ((IWorldAccessor)capi.World).BlockAccessor.GetBlock(val);
					if (block != null && ((RegistryObject)block).FirstCodePart(0) == "water")
					{
						num2++;
						if (num2 > num3)
						{
							return true;
						}
					}
				}
			}
		}
		return false;
	}

	private void RunPondCheck()
	{
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		if (IsStandingOverWater(asBlockPos) && IsPlayerInDeepWater(asBlockPos))
		{
			DebugHelper.Debug("[POND CHECK] Player is in deep water, skipping BFS.");
			return;
		}
		float pondActivityMultiplier = ForestSymphonyConfigs.GetPondActivityMultiplier((ICoreAPI)(object)capi, asBlockPos);
		if (pondActivityMultiplier <= 0.01f)
		{
			return;
		}
		Vec3d windSpeedAt = ((IWorldAccessor)capi.World).BlockAccessor.GetWindSpeedAt(asBlockPos);
		float val = (float)Math.Sqrt(windSpeedAt.X * windSpeedAt.X + windSpeedAt.Z * windSpeedAt.Z);
		float num = Math.Min(val, 1f);
		float value = ForestSymphonyConfigs.PondWindMinChance - num * (ForestSymphonyConfigs.PondWindMinChance - ForestSymphonyConfigs.PondWindMaxChance);
		value = Math.Clamp(value, 0f, 1f) * 100f;
		DebugHelper.Debug($"[POND CHECK] Wind={num:F2}, ConfigChance={value:F1}%");
		if (((IWorldAccessor)capi.World).Rand.NextDouble() * 100.0 > (double)value)
		{
			DebugHelper.Debug("[POND CHECK] Wind too strong, skipping pond sound.");
			return;
		}
		if (asBlockPos.DistanceTo(lastCheckedPosition) >= 2f)
		{
			lastCheckedPosition.Set(asBlockPos);
			UpdateWaterAsync(asBlockPos);
		}
		if (cachedWater.Count >= 9)
		{
			HandlePondSounds(new HashSet<BlockPos>(cachedWater.Keys), pondActivityMultiplier);
			if (ForestSymphonyConfigs.EnableInsectCheck && insectsCheck != null)
			{
				insectsCheck.ProcessEnvironment(cachedWater, cachedFoliage);
			}
		}
	}

	private bool IsStandingOverWater(BlockPos playerPos)
	{
		IBlockAccessor blockAccessor = ((IWorldAccessor)capi.World).BlockAccessor;
		for (int i = 0; i <= 2; i++)
		{
			Block block = blockAccessor.GetBlock(playerPos.DownCopy(i));
			if (((block != null) ? ((RegistryObject)block).FirstCodePart(0) : null) == "water")
			{
				return true;
			}
		}
		return false;
	}

	private async Task UpdateWaterAsync(BlockPos playerPos)
	{
		if (!(await waterUpdateLock.WaitAsync(0)))
		{
			return;
		}
		try
		{
			Dictionary<BlockPos, Block> detectedWater = await Task.Run(() => DetectWater(playerPos));
			Dictionary<BlockPos, Block> detectedFoliage = await Task.Run(() => DetectFoliage(playerPos));
			if (playerPos.DistanceTo(lastCheckedPosition) < 2f)
			{
				cachedWater = detectedWater;
				cachedFoliage = detectedFoliage;
			}
		}
		finally
		{
			waterUpdateLock.Release();
		}
	}

	private Dictionary<BlockPos, Block> DetectWater(BlockPos playerPos)
	{
		Dictionary<BlockPos, Block> dictionary = new Dictionary<BlockPos, Block>();
		IBlockAccessor blockAccessor = ((IWorldAccessor)capi.World).BlockAccessor;
		int num = 20;
		for (int i = -num; i <= num; i++)
		{
			for (int j = -num; j <= num; j++)
			{
				for (int k = -2; k <= 2; k++)
				{
					BlockPos val = playerPos.AddCopy(i, k, j);
					Block block = blockAccessor.GetBlock(val);
					if (block != null && ((RegistryObject)block).FirstCodePart(0) == "water")
					{
						BlockPos val2 = val.UpCopy(1);
						Block block2 = blockAccessor.GetBlock(val2);
						if (block2 == null || ((RegistryObject)block2).FirstCodePart(0) != "water")
						{
							dictionary[val] = block;
						}
					}
				}
			}
		}
		DebugHelper.Debug($"[POND CHECK] Surface Water BFS done! Found {dictionary.Count} surface water blocks.");
		return dictionary;
	}

	private Dictionary<BlockPos, Block> DetectFoliage(BlockPos playerPos)
	{
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Invalid comparison between Unknown and I4
		Dictionary<BlockPos, Block> dictionary = new Dictionary<BlockPos, Block>();
		IBlockAccessor blockAccessor = ((IWorldAccessor)capi.World).BlockAccessor;
		int num = 20;
		for (int i = -num; i <= num; i++)
		{
			for (int j = -num; j <= num; j++)
			{
				for (int k = -2; k <= 2; k++)
				{
					BlockPos val = playerPos.AddCopy(i, k, j);
					Block block = blockAccessor.GetBlock(val);
					if (block != null && (int)block.BlockMaterial != 0)
					{
						AssetLocation code = ((RegistryObject)block).Code;
						string text = ((code != null) ? code.Path : null) ?? "";
						if (text.Contains("tallgrass") || text.Contains("leaves") || text.Contains("bush"))
						{
							dictionary[val] = block;
						}
					}
				}
			}
		}
		DebugHelper.Debug($"[POND CHECK] Foliage BFS done! Found {dictionary.Count} foliage blocks.");
		return dictionary;
	}

	private void HandlePondSounds(HashSet<BlockPos> waterPositions, float pondActivityMult)
	{
		if (pondSounds == null)
		{
			return;
		}
		double minDistance = 25.0;
		if (pondSounds.IsPondSoundPlayingNear(((Entity)player.Entity).Pos.AsBlockPos, minDistance))
		{
			DebugHelper.Debug("[POND CHECK] Pond sound is already playing nearby. Skipping.");
			return;
		}
		HashSet<BlockPos> hashSet = FindPondCenters(waterPositions);
		if (hashSet.Count == 0)
		{
			DebugHelper.Debug("[POND CHECK] No valid 3x3 pond grids. Skipping.");
			return;
		}
		Shuffle(hashSet.ToList(), ((IWorldAccessor)capi.World).Rand);
		int num = 1;
		int num2 = (int)Math.Ceiling((float)num * pondActivityMult);
		if (num2 < 1)
		{
			num2 = 1;
		}
		int num3 = 0;
		foreach (BlockPos item in hashSet)
		{
			if (num3 >= num2)
			{
				break;
			}
			if (!pondSounds.IsPondSoundTooClose(item))
			{
				pondSounds.QueuePondSound(item);
				if (ForestSymphonyConfigs.EnableInsectCheck && insectsCheck != null)
				{
					insectsCheck.TryPlayPondInsect(item);
				}
				num3++;
			}
		}
		DebugHelper.Debug($"[POND CHECK] Pond sounds queued: {num3}/{num2}.");
	}

	private HashSet<BlockPos> FindPondCenters(HashSet<BlockPos> waterPositions)
	{
		//IL_0127: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Invalid comparison between Unknown and I4
		//IL_0308: Unknown result type (might be due to invalid IL or missing references)
		//IL_030e: Invalid comparison between Unknown and I4
		//IL_031e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0324: Invalid comparison between Unknown and I4
		//IL_0328: Unknown result type (might be due to invalid IL or missing references)
		//IL_032e: Invalid comparison between Unknown and I4
		//IL_0332: Unknown result type (might be due to invalid IL or missing references)
		//IL_0338: Invalid comparison between Unknown and I4
		HashSet<BlockPos> hashSet = new HashSet<BlockPos>(new BlockPosComparer());
		HashSet<BlockPos> hashSet2 = new HashSet<BlockPos>(waterPositions, new BlockPosComparer());
		int num = 0;
		bool flag = false;
		foreach (BlockPos waterPosition in waterPositions)
		{
			bool flag2 = true;
			HashSet<BlockPos> hashSet3 = new HashSet<BlockPos>(new BlockPosComparer());
			for (int i = -1; i <= 1 && flag2; i++)
			{
				for (int j = -1; j <= 1; j++)
				{
					BlockPos item = waterPosition.AddCopy(i, 0, j);
					hashSet3.Add(item);
					if (!hashSet2.Contains(item))
					{
						flag2 = false;
						break;
					}
				}
			}
			if (!flag2)
			{
				num++;
				continue;
			}
			bool flag3 = false;
			int mapSizeY = ((IWorldAccessor)capi.World).BlockAccessor.MapSizeY;
			foreach (BlockPos item3 in hashSet3)
			{
				for (int k = item3.Y + 1; k < mapSizeY; k++)
				{
					Block block = ((IWorldAccessor)capi.World).BlockAccessor.GetBlock(item3.X, k, item3.Z);
					if ((int)block.BlockMaterial == 6)
					{
						flag3 = true;
						break;
					}
				}
				if (flag3)
				{
					break;
				}
			}
			if (flag3)
			{
				num++;
				continue;
			}
			HashSet<BlockPos> hashSet4 = new HashSet<BlockPos>(new BlockPosComparer());
			int[,] array = new int[5, 3]
			{
				{ -1, 0, 0 },
				{ 1, 0, 0 },
				{ 0, 0, -1 },
				{ 0, 0, 1 },
				{ 0, -1, 0 }
			};
			foreach (BlockPos item4 in hashSet3)
			{
				int value = item4.X - waterPosition.X;
				int value2 = item4.Z - waterPosition.Z;
				if (Math.Abs(value) != 1 && Math.Abs(value2) != 1)
				{
					continue;
				}
				for (int l = 0; l < array.GetLength(0); l++)
				{
					BlockPos item2 = item4.AddCopy(array[l, 0], array[l, 1], array[l, 2]);
					if (!hashSet3.Contains(item2))
					{
						hashSet4.Add(item2);
					}
				}
			}
			foreach (BlockPos item5 in hashSet3)
			{
				hashSet4.Add(item5.DownCopy(1));
			}
			int num2 = 0;
			bool flag4 = false;
			foreach (BlockPos item6 in hashSet4)
			{
				Block block2 = ((IWorldAccessor)capi.World).BlockAccessor.GetBlock(item6);
				if (block2 != null)
				{
					if ((int)block2.BlockMaterial == 6)
					{
						flag4 = true;
						break;
					}
					if ((int)block2.BlockMaterial == 1 || (int)block2.BlockMaterial == 2 || (int)block2.BlockMaterial == 3)
					{
						num2++;
					}
				}
			}
			if (flag4)
			{
				num++;
				continue;
			}
			if (num2 >= 3)
			{
				hashSet.Add(waterPosition);
				DebugHelper.Debug($"[POND CHECK] Found 1 valid pond at {waterPosition}, skipping the rest of BFS.");
				flag = true;
				break;
			}
			num++;
		}
		if (!flag && num > 0)
		{
			DebugHelper.Debug($"[POND CHECK] Skipped {num} water blocks that didn't meet pond criteria.");
		}
		DebugHelper.Debug($"[POND CHECK] Pond detection completed. {hashSet.Count} ponds found.");
		return new HashSet<BlockPos>(hashSet.Distinct(new BlockPosComparer()));
	}

	private void Shuffle<T>(IList<T> list, Random rng)
	{
		int num = list.Count;
		while (num > 1)
		{
			num--;
			int index = rng.Next(num + 1);
			T value = list[index];
			list[index] = list[num];
			list[num] = value;
		}
	}

	public void UpdateCoverage(float volumeFactor, float pitchFactor, float dt)
	{
		pondSounds.UpdateCoverage(volumeFactor, pitchFactor, dt);
	}
}
public class PondSounds
{
	private class ActivePondSound
	{
		public ILoadedSound Sound { get; }

		public BlockPos Position { get; }

		public string SoundPath { get; }

		public float OriginalVolume { get; set; }

		public float CurrentVolume { get; set; }

		public float OriginalPitch { get; set; }

		public float CurrentPitch { get; set; }

		public ActivePondSound(ILoadedSound sound, BlockPos position, string soundPath)
		{
			Sound = sound;
			Position = position;
			SoundPath = soundPath;
		}
	}

	private readonly ICoreClientAPI capi;

	private readonly PondCheck pondCheck;

	private readonly List<string> pondDaySounds;

	private readonly List<string> pondNightSounds;

	private readonly List<ActivePondSound> activePondSounds = new List<ActivePondSound>();

	private readonly object activePondSoundsLock = new object();

	private readonly Queue<BlockPos> pendingPondSounds = new Queue<BlockPos>();

	public int MaxPondSounds { get; private set; } = 5;

	public PondSounds(ICoreClientAPI capi, PondCheck pondCheck)
	{
		this.capi = capi ?? throw new ArgumentNullException("capi", "[POND DETECTION] capi is null!");
		this.pondCheck = pondCheck ?? throw new ArgumentNullException("pondCheck", "[POND DETECTION] pondCheck is null!");
		pondDaySounds = new List<string> { "pond_1", "pond_2", "pond_3", "pond_4", "pond_5" };
		pondNightSounds = new List<string> { "pondnight_1", "pondnight_2", "pondnight_3", "pondnight_4", "pondnight_5" };
		((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnGameTick, 1000, 0);
		DebugHelper.Debug("[POND DETECTION] PondSounds initialized successfully.");
	}

	public bool PlayPondSound(BlockPos pondCenterPos, bool isDay)
	{
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Expected O, but got Unknown
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Expected O, but got Unknown
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0207: Expected O, but got Unknown
		if (capi == null || capi.World == null || pondCheck == null)
		{
			return false;
		}
		if (pondCenterPos == (BlockPos)null)
		{
			return false;
		}
		List<string> list = (isDay ? pondDaySounds : pondNightSounds);
		if (list.Count == 0)
		{
			return false;
		}
		string text = list[((IWorldAccessor)capi.World).Rand.Next(list.Count)];
		string text2 = (isDay ? ("forestsymphony:sounds/day/pond/" + text) : ("forestsymphony:sounds/night/pond/" + text));
		lock (activePondSoundsLock)
		{
			if (activePondSounds.Count >= MaxPondSounds)
			{
				DebugHelper.Debug("[POND DETECTION] Max pond sounds reached. Skipping.");
				return false;
			}
			foreach (ActivePondSound activePondSound in activePondSounds)
			{
				double num = pondCenterPos.DistanceTo(activePondSound.Position);
				if (num < 10.0)
				{
					DebugHelper.Debug($"[POND DETECTION] Too close to existing pond sound at {activePondSound.Position}");
					return false;
				}
			}
			Vec3f position = new Vec3f((float)pondCenterPos.X + 0.5f, (float)pondCenterPos.Y + 0.5f, (float)pondCenterPos.Z + 0.5f);
			ILoadedSound val = capi.World.LoadSound(new SoundParams
			{
				Location = new AssetLocation(text2),
				ShouldLoop = false,
				Position = position,
				DisposeOnFinish = true,
				Volume = ForestSymphonyConfigs.PondSoundVolume,
				Range = ForestSymphonyConfigs.PondSoundRange,
				SoundType = (EnumSoundType)2
			});
			if (val == null)
			{
				DebugHelper.Debug("[POND DETECTION] Failed to load sound '" + text2 + "'.");
				return false;
			}
			val.Start();
			ActivePondSound item = new ActivePondSound(val, pondCenterPos, text2)
			{
				OriginalVolume = ForestSymphonyConfigs.PondSoundVolume,
				CurrentVolume = ForestSymphonyConfigs.PondSoundVolume,
				OriginalPitch = 1f,
				CurrentPitch = 1f
			};
			activePondSounds.Add(item);
			return true;
		}
	}

	public bool IsPondSoundPlayingNear(BlockPos pos, double minDistance)
	{
		lock (activePondSoundsLock)
		{
			foreach (ActivePondSound activePondSound in activePondSounds)
			{
				double num = pos.DistanceTo(activePondSound.Position);
				if (num < minDistance && activePondSound.Sound != null && activePondSound.Sound.IsPlaying)
				{
					return true;
				}
			}
		}
		return false;
	}

	public bool IsPondSoundTooClose(BlockPos newPondPos)
	{
		double num = 20.0;
		lock (activePondSoundsLock)
		{
			foreach (ActivePondSound activePondSound in activePondSounds)
			{
				double num2 = newPondPos.DistanceTo(activePondSound.Position);
				if (num2 < num && activePondSound.Sound != null && activePondSound.Sound.IsPlaying)
				{
					return true;
				}
			}
		}
		return false;
	}

	private void OnGameTick(float dt)
	{
		lock (activePondSoundsLock)
		{
			for (int num = activePondSounds.Count - 1; num >= 0; num--)
			{
				ActivePondSound activePondSound = activePondSounds[num];
				if (activePondSound.Sound == null || !activePondSound.Sound.IsPlaying)
				{
					((IDisposable)activePondSound.Sound)?.Dispose();
					activePondSounds.RemoveAt(num);
				}
			}
			if (pendingPondSounds.Count > 0 && activePondSounds.Count < MaxPondSounds)
			{
				BlockPos pondCenterPos = pendingPondSounds.Dequeue();
				bool isDay = ((IGameCalendar)capi.World.Calendar).TotalHours % 24.0 >= 5.0 && ((IGameCalendar)capi.World.Calendar).TotalHours % 24.0 < 18.0;
				PlayPondSound(pondCenterPos, isDay);
			}
		}
	}

	public void UpdateCoverage(float volumeFactor, float pitchFactor, float dt)
	{
		lock (activePondSoundsLock)
		{
			foreach (ActivePondSound activePondSound in activePondSounds)
			{
				if (activePondSound.Sound != null && activePondSound.Sound.IsPlaying)
				{
					float num = activePondSound.OriginalVolume * volumeFactor;
					float num2 = activePondSound.OriginalPitch * pitchFactor;
					float num3 = 5f * dt;
					activePondSound.CurrentVolume += (num - activePondSound.CurrentVolume) * num3;
					activePondSound.CurrentPitch += (num2 - activePondSound.CurrentPitch) * num3;
					activePondSound.Sound.SetVolume(activePondSound.CurrentVolume);
					activePondSound.Sound.SetPitch(activePondSound.CurrentPitch);
				}
			}
		}
	}

	public void QueuePondSound(BlockPos pondCenterPos)
	{
		lock (activePondSoundsLock)
		{
			if (!pendingPondSounds.Contains(pondCenterPos))
			{
				pendingPondSounds.Enqueue(pondCenterPos);
			}
		}
	}
}
public class TreeLogCheck
{
	private readonly ICoreClientAPI capi;

	private readonly IPlayer player;

	private readonly TreeSounds treeSounds;

	private Dictionary<BlockPos, Block> cachedLogs = new Dictionary<BlockPos, Block>();

	private BlockPos lastCheckedPosition = new BlockPos();

	private readonly SemaphoreSlim logUpdateLock = new SemaphoreSlim(1, 1);

	private float elapsedTimeMs = 0f;

	public TreeLogCheck(ICoreClientAPI capi, IPlayer player)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		this.capi = capi ?? throw new ArgumentNullException("capi", "[TREE LOG CHECK] capi is null!");
		this.player = player ?? throw new ArgumentNullException("player", "[TREE LOG CHECK] player is null!");
		treeSounds = new TreeSounds(capi, this);
		DebugHelper.Debug("[TREE LOG CHECK] TreeLogCheck initialized successfully.");
	}

	public void Update(float dt)
	{
		elapsedTimeMs += dt * 1000f;
		BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
		Vec3d windSpeedAt = ((IWorldAccessor)capi.World).BlockAccessor.GetWindSpeedAt(asBlockPos);
		float num = (float)Math.Sqrt(windSpeedAt.X * windSpeedAt.X + windSpeedAt.Z * windSpeedAt.Z);
		float num2 = Math.Min(num, 1f);
		float num3 = ForestSymphonyConfigs.TreeLogCheckMultiplierWeak + (ForestSymphonyConfigs.TreeLogCheckMultiplierStrong - ForestSymphonyConfigs.TreeLogCheckMultiplierWeak) * num2;
		float num4 = (float)ForestSymphonyConfigs.TreeLogCheckInterval * num3;
		if (elapsedTimeMs >= num4)
		{
			DebugHelper.Debug($"[TREE LOG CHECK] Wind: {num:F2} (norm: {num2:F2}) => Multiplier: {num3:F2}, Dynamic Interval: {num4:F0} ms, Elapsed: {elapsedTimeMs:F0} ms");
			Check();
			elapsedTimeMs = 0f;
		}
	}

	public void Check()
	{
		if (player != null && player.Entity != null)
		{
			BlockPos asBlockPos = ((Entity)player.Entity).Pos.AsBlockPos;
			if (asBlockPos.DistanceTo(lastCheckedPosition) >= 2f)
			{
				lastCheckedPosition.Set(asBlockPos);
				UpdateLogsAsync(asBlockPos);
			}
			if (cachedLogs.Count != 0)
			{
				HandleLogSounds(new HashSet<BlockPos>(cachedLogs.Keys));
			}
		}
	}

	private async Task UpdateLogsAsync(BlockPos playerPos)
	{
		if (!(await logUpdateLock.WaitAsync(0)))
		{
			return;
		}
		try
		{
			Dictionary<BlockPos, Block> detectedLogs = await Task.Run(() => DetectLogs(playerPos));
			((IEventAPI)capi.Event).EnqueueMainThreadTask((Action)delegate
			{
				if (playerPos.DistanceTo(lastCheckedPosition) < 2f)
				{
					cachedLogs = detectedLogs;
					HandleLogSounds(new HashSet<BlockPos>(cachedLogs.Keys));
				}
			}, "UpdateLogsTask");
		}
		finally
		{
			logUpdateLock.Release();
		}
	}

	private Dictionary<BlockPos, Block> DetectLogs(BlockPos playerPos)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Invalid comparison between Unknown and I4
		Dictionary<BlockPos, Block> dictionary = new Dictionary<BlockPos, Block>();
		IBlockAccessor blockAccessor = ((IWorldAccessor)capi.World).BlockAccessor;
		int num = 15;
		for (int i = -num; i <= num; i++)
		{
			for (int j = -num; j <= num; j++)
			{
				for (int k = 0; k <= 6; k++)
				{
					BlockPos val = playerPos.AddCopy(i, k, j);
					Block block = blockAccessor.GetBlock(val);
					if (block != null && (int)block.BlockMaterial != 0 && !(((RegistryObject)block).FirstCodePart(0) != "log"))
					{
						int lightLevel = blockAccessor.GetLightLevel(val, (EnumLightLevelType)1);
						if (lightLevel < 12)
						{
							DebugHelper.Debug($"[TREE LOG CHECK] Skipping buried log at {val} (sunlight=0).");
						}
						else
						{
							dictionary[val] = block;
						}
					}
				}
			}
		}
		DebugHelper.Debug($"[TREE LOG CHECK] Surface log scan complete: found {dictionary.Count} logs.");
		return dictionary;
	}

	private void HandleLogSounds(HashSet<BlockPos> logPositions)
	{
		if (treeSounds == null)
		{
			DebugHelper.Debug("[TREE LOG CHECK] ERROR: treeSounds is null! Skipping tree sound.");
		}
		else if (logPositions.Count == 0)
		{
			DebugHelper.Debug("[TREE LOG CHECK] No log blocks detected within range.");
		}
		else if (treeSounds.PlayTreeSound(logPositions))
		{
			DebugHelper.Debug("[TREE LOG CHECK] Tree sound played.");
		}
		else
		{
			DebugHelper.Debug("[TREE LOG CHECK] Tree sound was not played.");
		}
	}
}
public class TreeSounds
{
	private readonly ICoreClientAPI capi;

	private readonly TreeLogCheck treeLogCheck;

	private readonly List<string> treeSounds;

	private bool isCooldownActive = false;

	private float cooldownRemainingMs = 0f;

	private readonly Random rand = new Random();

	public TreeSounds(ICoreClientAPI capi, TreeLogCheck treeLogCheck)
	{
		this.capi = capi ?? throw new ArgumentNullException("capi", "[LOG DETECTION] capi is null!");
		this.treeLogCheck = treeLogCheck ?? throw new ArgumentNullException("treeLogCheck", "[LOG DETECTION] treeLogCheck is null!");
		treeSounds = new List<string> { "creaky-tree_1", "creaky-tree_2", "creaky-tree_3", "creaky-tree_4", "creaky-tree_5" };
		if (treeSounds.Count == 0)
		{
			DebugHelper.Debug("[LOG DETECTION] No tree sound files found in 'sounds/tree' folder for Forest Symphony.");
		}
		((IEventAPI)capi.Event).RegisterGameTickListener((Action<float>)OnGameTick, 1000, 0);
	}

	public bool PlayTreeSound(HashSet<BlockPos> logPositions)
	{
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Expected O, but got Unknown
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Expected O, but got Unknown
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Expected O, but got Unknown
		if (isCooldownActive)
		{
			DebugHelper.Debug("[LOG DETECTION] Cooldown active. Skipping log sound playback.");
			return false;
		}
		if (logPositions == null || logPositions.Count == 0)
		{
			DebugHelper.Debug("[LOG DETECTION] No log positions provided. Skipping sound playback.");
			return false;
		}
		if (treeSounds.Count == 0)
		{
			DebugHelper.Debug("[LOG DETECTION] No tree sounds available to play.");
			return false;
		}
		BlockPos val = logPositions.ElementAt(rand.Next(logPositions.Count));
		string text = treeSounds[rand.Next(treeSounds.Count)];
		string text2 = "forestsymphony:sounds/tree/" + text;
		Vec3f val2 = new Vec3f((float)val.X + 0.5f, (float)val.Y + 0.5f, (float)val.Z + 0.5f);
		ILoadedSound val3 = capi.World.LoadSound(new SoundParams
		{
			Location = new AssetLocation(text2),
			ShouldLoop = false,
			Position = val2,
			DisposeOnFinish = true,
			Volume = ForestSymphonyConfigs.TreeSoundVolume,
			Range = ForestSymphonyConfigs.TreeSoundRange,
			SoundType = (EnumSoundType)2
		});
		if (val3 == null)
		{
			DebugHelper.Debug("[LOG DETECTION] ERROR: Failed to load sound '" + text2 + "'.");
			return false;
		}
		val3.Start();
		DebugHelper.Debug($"[LOG DETECTION] Sound '{text2}' started at {val2}.");
		ActivateCooldown();
		return true;
	}

	private void ActivateCooldown()
	{
		isCooldownActive = true;
		cooldownRemainingMs = rand.Next(10000, 20001);
		DebugHelper.Debug($"[LOG DETECTION] Cooldown activated for {cooldownRemainingMs / 1000f} seconds.");
	}

	private void OnGameTick(float dt)
	{
		if (isCooldownActive)
		{
			cooldownRemainingMs -= dt * 1000f;
			if (cooldownRemainingMs <= 0f)
			{
				isCooldownActive = false;
				cooldownRemainingMs = 0f;
				DebugHelper.Debug("[LOG DETECTION] Cooldown ended. Ready to play tree sounds again.");
			}
		}
	}

	public bool IsCooldownActive()
	{
		return isCooldownActive;
	}
}

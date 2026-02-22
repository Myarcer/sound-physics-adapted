using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using CarryOn.API.Common;
using CarryOn.API.Event;
using CarryOn.API.Event.Data;
using CarryOn.Client;
using CarryOn.Client.Logic;
using CarryOn.Common;
using CarryOn.Common.Network;
using CarryOn.Compatibility;
using CarryOn.Config;
using CarryOn.Server;
using CarryOn.Utility;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: ModInfo("Carry On", "carryon", Version = "1.12.1", Description = "Adds the capability to carry various things", Website = "https://github.com/NerdScurvy/CarryOn", Authors = new string[] { "copygirl", "NerdScurvy" })]
[assembly: ModDependency("game", "1.21.0")]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("copygirl,NerdScurvy")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyDescription("Vintage Story mod which adds the capability to carry various things")]
[assembly: AssemblyFileVersion("1.12.1.0")]
[assembly: AssemblyInformationalVersion("1.12.1+5eb25dbc8a2070f4d262e1dd18bd77ea4a40cdc8")]
[assembly: AssemblyProduct("CarryOn")]
[assembly: AssemblyTitle("Carry On")]
[assembly: AssemblyMetadata("RepositoryUrl", "https://github.com/NerdScurvy/CarryOn")]
[assembly: AssemblyVersion("1.12.1.0")]
[module: RefSafetyRules(11)]
namespace CarryOn
{
	public class CarrySystem : ModSystem
	{
		public static string ModId = "carryon";

		public static float PlaceSpeedDefault = 0.75f;

		public static float SwapSpeedDefault = 1.5f;

		public static float PickUpSpeedDefault = 0.8f;

		public static float InteractSpeedDefault = 0.8f;

		public static string PickupKeyCode = "carryonpickupkey";

		public static GlKeys PickupKeyDefault = (GlKeys)1;

		public static string SwapBackModifierKeyCode = "carryonswapbackmodifierkey";

		public static GlKeys SwapBackModifierDefault = (GlKeys)3;

		public static string ToggleKeyCode = "carryontogglekey";

		public static GlKeys ToggleDefault = (GlKeys)93;

		public static string QuickDropKeyCode = "carryonquickdropkey";

		public static GlKeys QuickDropDefault = (GlKeys)93;

		public static GlKeys ToggleDoubleTapDismountDefault = (GlKeys)93;

		public static string ToggleDoubleTapDismountKeyCode = "carryontoggledoubletapdismountkey";

		public static readonly string DoubleTapDismountEnabledAttributeKey = ModId + ":DoubleTapDismountEnabled";

		public static readonly string LastSneakTapMsKey = ModId + ":LastSneakTapMs";

		public static readonly int DoubleTapThresholdMs = 500;

		private Harmony _harmony;

		public ICoreAPI Api
		{
			get
			{
				ICoreAPI clientAPI = (ICoreAPI)(object)ClientAPI;
				return (ICoreAPI)(((object)clientAPI) ?? ((object)ServerAPI));
			}
		}

		public ICoreClientAPI ClientAPI { get; private set; }

		public IClientNetworkChannel ClientChannel { get; private set; }

		public EntityCarryRenderer EntityCarryRenderer { get; private set; }

		public HudOverlayRenderer HudOverlayRenderer { get; private set; }

		public HudCarried HudCarried { get; private set; }

		public ClientModConfig ClientConfig { get; private set; }

		public ICoreServerAPI ServerAPI { get; private set; }

		public IServerNetworkChannel ServerChannel { get; private set; }

		public DeathHandler DeathHandler { get; private set; }

		public CarryHandler CarryHandler { get; private set; }

		public CarryEvents CarryEvents { get; private set; }

		public override void StartPre(ICoreAPI api)
		{
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0038: Expected O, but got Unknown
			((ModSystem)this).StartPre(api);
			bool num = AutoConfigLib.HadPatches(api);
			ModConfig.ReadConfig(api);
			if (num)
			{
				((ICoreAPICommon)api).LoadModConfig<CarryOnConfig>(ModConfig.ConfigFile);
			}
			if (ModConfig.HarmonyPatchEnabled)
			{
				try
				{
					_harmony = new Harmony("CarryOn");
					_harmony.PatchAll();
					api.World.Logger.Notification("CarryOn: Harmony patches enabled.");
				}
				catch (Exception value)
				{
					api.World.Logger.Error($"CarryOn: Exception during Harmony patching: {value}");
				}
			}
			else
			{
				api.World.Logger.Notification("CarryOn: Harmony patches are disabled by config.");
			}
			api.World.Logger.Event("started 'CarryOn' mod");
		}

		public override void Start(ICoreAPI api)
		{
			((ICoreAPICommon)api).RegisterEntity("EntityBoatCarryOn", typeof(EntityBoat));
			api.Register<BlockBehaviorCarryable>();
			api.Register<BlockBehaviorCarryableInteract>();
			api.Register<EntityBehaviorAttachableCarryable>();
			CarryHandler = new CarryHandler(this);
			CarryEvents = new CarryEvents();
		}

		public override void StartClientSide(ICoreClientAPI api)
		{
			ClientAPI = api;
			ClientChannel = api.Network.RegisterChannel(ModId).RegisterMessageType<InteractMessage>().RegisterMessageType<LockSlotsMessage>()
				.RegisterMessageType<PickUpMessage>()
				.RegisterMessageType<PlaceDownMessage>()
				.RegisterMessageType<SwapSlotsMessage>()
				.RegisterMessageType<AttachMessage>()
				.RegisterMessageType<DetachMessage>()
				.RegisterMessageType<QuickDropMessage>()
				.RegisterMessageType<DismountMessage>()
				.RegisterMessageType<PlayerAttributeUpdateMessage>();
			EntityCarryRenderer = new EntityCarryRenderer(api);
			HudOverlayRenderer = new HudOverlayRenderer(api);
			HudCarried = new HudCarried(api);
			try
			{
				ClientConfig = new ClientModConfig();
				ClientConfig.Load(api);
				CarryOnClientConfig config = ClientConfig.Config;
				if (config != null)
				{
					if (!string.IsNullOrEmpty(config.HandsAnchor) && Enum.TryParse<HudCarried.Anchor>(config.HandsAnchor, ignoreCase: true, out var result))
					{
						HudCarried.HandsAnchor = result;
					}
					if (!string.IsNullOrEmpty(config.BackAnchor) && Enum.TryParse<HudCarried.Anchor>(config.BackAnchor, ignoreCase: true, out var result2))
					{
						HudCarried.BackAnchor = result2;
					}
					try
					{
						HudCarried.AnchorBackgroundEnabled = config.AnchorBackgroundEnabled;
						if (!string.IsNullOrEmpty(config.AnchorBackgroundColor))
						{
							HudCarried.AnchorBackgroundColor = config.AnchorBackgroundColor;
						}
						HudCarried.AnchorBackgroundAlpha = config.AnchorBackgroundAlpha;
						HudCarried.AnchorBorderEnabled = config.AnchorBorderEnabled;
						if (!string.IsNullOrEmpty(config.AnchorBorderColor))
						{
							HudCarried.AnchorBorderColor = config.AnchorBorderColor;
						}
						HudCarried.AnchorBorderAlpha = config.AnchorBorderAlpha;
						HudCarried.IconHighlightEnabled = config.IconHighlightEnabled;
						if (!string.IsNullOrEmpty(config.IconHighlightColor))
						{
							HudCarried.IconHighlightColor = config.IconHighlightColor;
						}
						HudCarried.IconHighlightAlpha = config.IconHighlightAlpha;
					}
					catch (Exception ex)
					{
						((ICoreAPI)api).Logger.Warning("CarryOn: Failed to apply anchor background settings: " + ex.Message);
					}
				}
			}
			catch (Exception ex2)
			{
				((ICoreAPI)api).Logger.Warning("CarryOn: Failed to apply client config: " + ex2.Message);
			}
			new Commands(this).Register();
			CarryHandler.InitClient();
			InitEvents();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			((ICoreAPI)(object)api).Register<EntityBehaviorDropCarriedOnDamage>();
			ServerAPI = api;
			ServerChannel = api.Network.RegisterChannel(ModId).RegisterMessageType<InteractMessage>().RegisterMessageType<LockSlotsMessage>()
				.RegisterMessageType<PickUpMessage>()
				.RegisterMessageType<PlaceDownMessage>()
				.RegisterMessageType<SwapSlotsMessage>()
				.RegisterMessageType<AttachMessage>()
				.RegisterMessageType<DetachMessage>()
				.RegisterMessageType<QuickDropMessage>()
				.RegisterMessageType<DismountMessage>()
				.RegisterMessageType<PlayerAttributeUpdateMessage>();
			DeathHandler = new DeathHandler(api);
			CarryHandler.InitServer();
			InitEvents();
		}

		public override void AssetsFinalize(ICoreAPI api)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Invalid comparison between Unknown and I4
			if ((int)api.Side == 1)
			{
				ManuallyAddCarryableBehaviors(api);
				ResolveMultipleCarryableBehaviors(api);
				AutoMapSimilarCarryables(api);
				AutoMapSimilarCarryableInteract(api);
				RemoveExcludedCarryableBehaviours(api);
			}
			((ModSystem)this).AssetsFinalize(api);
		}

		private void AddCarryableBehavior(Block block, ref BlockBehavior[] blockBehaviors, ref CollectibleBehavior[] collectibleBehaviors, JsonObject properties)
		{
			BlockBehaviorCarryable blockBehaviorCarryable = new BlockBehaviorCarryable(block);
			blockBehaviors = ArrayExtensions.Append<BlockBehavior>(blockBehaviors, (BlockBehavior)(object)blockBehaviorCarryable);
			((CollectibleBehavior)blockBehaviorCarryable).Initialize(properties);
			collectibleBehaviors = ArrayExtensions.Append<CollectibleBehavior>(collectibleBehaviors, (CollectibleBehavior)(object)blockBehaviorCarryable);
		}

		private void ManuallyAddCarryableBehaviors(ICoreAPI api)
		{
			if (ModConfig.HenboxEnabled)
			{
				Block block = api.World.BlockAccessor.GetBlock(AssetLocation.op_Implicit("henbox"));
				if (block != null)
				{
					JsonObject properties = JsonObject.FromJson("{slots:{Hands:{}}}");
					AddCarryableBehavior(block, ref block.BlockBehaviors, ref ((CollectibleObject)block).CollectibleBehaviors, properties);
				}
			}
		}

		private void RemoveExcludedCarryableBehaviours(ICoreAPI api)
		{
			bool loggingEnabled = ModConfig.ServerConfig.DebuggingOptions.LoggingEnabled;
			string[] removeCarryableBehaviour = ModConfig.ServerConfig.CarryablesFilters.RemoveCarryableBehaviour;
			if (removeCarryableBehaviour == null || removeCarryableBehaviour.Length == 0)
			{
				return;
			}
			foreach (Block item in api.World.Blocks.Where((Block b) => ((RegistryObject)b).Code != (AssetLocation)null))
			{
				string[] array = removeCarryableBehaviour;
				foreach (string value in array)
				{
					if (((object)((RegistryObject)item).Code).ToString().StartsWith(value))
					{
						int num2 = item.BlockBehaviors.Length;
						item.BlockBehaviors = RemoveCarryableBehaviours(item.BlockBehaviors.OfType<CollectibleBehavior>().ToArray()).OfType<BlockBehavior>().ToArray();
						((CollectibleObject)item).CollectibleBehaviors = RemoveCarryableBehaviours(((CollectibleObject)item).CollectibleBehaviors);
						if (num2 != item.BlockBehaviors.Length && loggingEnabled)
						{
							api.Logger.Debug($"CarryOn Removed Carryable Behaviour: {((RegistryObject)item).Code}");
						}
					}
				}
			}
		}

		private void ResolveMultipleCarryableBehaviors(ICoreAPI api)
		{
			CarryablesFiltersConfig carryablesFilters = ModConfig.ServerConfig.CarryablesFilters;
			foreach (Block block in api.World.Blocks)
			{
				bool removeBaseBehavior = false;
				if (((RegistryObject)block).Code == (AssetLocation)null || ((CollectibleObject)block).Id == 0)
				{
					continue;
				}
				string[] removeBaseCarryableBehaviour = carryablesFilters.RemoveBaseCarryableBehaviour;
				foreach (string value in removeBaseCarryableBehaviour)
				{
					if (((object)((RegistryObject)block).Code).ToString().StartsWith(value))
					{
						removeBaseBehavior = true;
						break;
					}
				}
				block.BlockBehaviors = RemoveOverriddenCarryableBehaviours(block.BlockBehaviors.OfType<CollectibleBehavior>().ToArray(), removeBaseBehavior).OfType<BlockBehavior>().ToArray();
				((CollectibleObject)block).CollectibleBehaviors = RemoveOverriddenCarryableBehaviours(((CollectibleObject)block).CollectibleBehaviors, removeBaseBehavior);
			}
		}

		private CollectibleBehavior[] RemoveOverriddenCarryableBehaviours(CollectibleBehavior[] behaviours, bool removeBaseBehavior = false)
		{
			List<CollectibleBehavior> list = behaviours.ToList();
			List<BlockBehaviorCarryable> carryableList = FindCarryables(list);
			if (carryableList.Count > 1)
			{
				BlockBehaviorCarryable blockBehaviorCarryable = carryableList.First((BlockBehaviorCarryable p) => p.PatchPriority == carryableList.Max((BlockBehaviorCarryable m) => m.PatchPriority));
				if (blockBehaviorCarryable != null)
				{
					if (!removeBaseBehavior || blockBehaviorCarryable.PatchPriority != 0)
					{
						carryableList.Remove(blockBehaviorCarryable);
					}
					list.RemoveAll((CollectibleBehavior r) => ((IEnumerable<CollectibleBehavior>)carryableList).Contains(r));
				}
			}
			else if (removeBaseBehavior && carryableList.Count == 1 && carryableList[0].PatchPriority == 0)
			{
				list.RemoveAll((CollectibleBehavior r) => ((IEnumerable<CollectibleBehavior>)carryableList).Contains(r));
			}
			return list.ToArray();
		}

		private CollectibleBehavior[] RemoveCarryableBehaviours(CollectibleBehavior[] behaviours)
		{
			List<CollectibleBehavior> list = behaviours.ToList();
			List<BlockBehaviorCarryable> carryableList = FindCarryables(list);
			if (carryableList.Count == 0)
			{
				return behaviours;
			}
			list.RemoveAll((CollectibleBehavior r) => ((IEnumerable<CollectibleBehavior>)carryableList).Contains(r));
			return list.ToArray();
		}

		private List<BlockBehaviorCarryable> FindCarryables<T>(List<T> behaviors)
		{
			List<BlockBehaviorCarryable> list = new List<BlockBehaviorCarryable>();
			foreach (T behavior in behaviors)
			{
				if (behavior is BlockBehaviorCarryable item)
				{
					list.Add(item);
				}
			}
			return list;
		}

		private void AutoMapSimilarCarryableInteract(ICoreAPI api)
		{
			bool loggingEnabled = ModConfig.ServerConfig.DebuggingOptions.LoggingEnabled;
			CarryablesFiltersConfig filters = ModConfig.ServerConfig.CarryablesFilters;
			if (!filters.AutoMapSimilar)
			{
				return;
			}
			List<string> matchKeys = new List<string>();
			foreach (Block item in api.World.Blocks.Where((Block b) => b.IsCarryableInteract()))
			{
				if (item.EntityClass != null && !(item.EntityClass == "Generic") && !matchKeys.Contains(item.EntityClass))
				{
					matchKeys.Add(item.EntityClass);
				}
			}
			foreach (Block item2 in api.World.Blocks.Where(delegate(Block w)
			{
				if (!w.IsCarryableInteract() && matchKeys.Contains(w.EntityClass))
				{
					string[] autoMatchIgnoreMods = filters.AutoMatchIgnoreMods;
					object obj;
					if (w == null)
					{
						obj = null;
					}
					else
					{
						AssetLocation code = ((RegistryObject)w).Code;
						obj = ((code != null) ? code.Domain : null);
					}
					return !ArrayExtensions.Contains<string>(autoMatchIgnoreMods, (string)obj);
				}
				return false;
			}))
			{
				item2.BlockBehaviors = ArrayExtensions.Append<BlockBehavior>(item2.BlockBehaviors, (BlockBehavior)(object)new BlockBehaviorCarryableInteract(item2));
				((CollectibleObject)item2).CollectibleBehaviors = ArrayExtensions.Append<CollectibleBehavior>(((CollectibleObject)item2).CollectibleBehaviors, (CollectibleBehavior)(object)new BlockBehaviorCarryableInteract(item2));
				if (loggingEnabled)
				{
					api.Logger.Debug($"CarryOn AutoMatch Interact: {((RegistryObject)item2).Code} key: {item2.EntityClass}");
				}
			}
		}

		private void AutoMapSimilarCarryables(ICoreAPI api)
		{
			bool loggingEnabled = ModConfig.ServerConfig.DebuggingOptions.LoggingEnabled;
			CarryablesFiltersConfig filters = ModConfig.ServerConfig.CarryablesFilters;
			if (!filters.AutoMapSimilar)
			{
				return;
			}
			Dictionary<string, BlockBehaviorCarryable> dictionary = new Dictionary<string, BlockBehaviorCarryable>();
			foreach (Block item in api.World.Blocks.Where((Block b) => b.IsCarryable() && ((RegistryObject)b).Code.Domain == "game"))
			{
				object obj;
				if (item == null)
				{
					obj = null;
				}
				else
				{
					CompositeShape shapeInventory = item.ShapeInventory;
					if (shapeInventory == null)
					{
						obj = null;
					}
					else
					{
						AssetLocation obj2 = shapeInventory.Base;
						obj = ((obj2 != null) ? obj2.Path : null);
					}
				}
				if (obj == null)
				{
					if (item == null)
					{
						obj = null;
					}
					else
					{
						CompositeShape shape = item.Shape;
						if (shape == null)
						{
							obj = null;
						}
						else
						{
							AssetLocation obj3 = shape.Base;
							obj = ((obj3 != null) ? obj3.Path : null);
						}
					}
				}
				string text = (string)obj;
				string text2 = ((text != null && text != "block/basic/cube") ? ("Shape:" + text) : null);
				string text3 = null;
				if (item.EntityClass != null && item.EntityClass != "Generic" && item.EntityClass != "Transient")
				{
					text3 = "EntityClass:" + item.EntityClass;
					if (!dictionary.ContainsKey(text3))
					{
						dictionary[text3] = ((CollectibleObject)item).GetBehavior<BlockBehaviorCarryable>();
						if (loggingEnabled)
						{
							api.Logger.Debug($"CarryOn matchBehavior: {text3} carryableBlock: {((RegistryObject)item).Code}");
						}
					}
				}
				string text4 = null;
				if (((RegistryObject)item).Class != "Block")
				{
					text4 = "Class:" + ((RegistryObject)item).Class;
					if (!dictionary.ContainsKey(text4))
					{
						dictionary[text4] = ((CollectibleObject)item).GetBehavior<BlockBehaviorCarryable>();
						if (loggingEnabled)
						{
							api.Logger.Debug($"CarryOn matchBehavior: {text4} carryableBlock: {((RegistryObject)item).Code}");
						}
					}
				}
				if (text2 == null)
				{
					continue;
				}
				if (text3 != null)
				{
					string text5 = text3 + "|" + text2;
					if (!dictionary.ContainsKey(text5))
					{
						dictionary[text5] = ((CollectibleObject)item).GetBehavior<BlockBehaviorCarryable>();
						if (loggingEnabled)
						{
							api.Logger.Debug($"CarryOn matchBehavior: {text5} carryableBlock: {((RegistryObject)item).Code}");
						}
					}
				}
				if (text4 != null)
				{
					string text6 = text4 + "|" + text2;
					if (!dictionary.ContainsKey(text6))
					{
						dictionary[text6] = ((CollectibleObject)item).GetBehavior<BlockBehaviorCarryable>();
						if (loggingEnabled)
						{
							api.Logger.Debug($"CarryOn matchBehavior: {text6} carryableBlock: {((RegistryObject)item).Code}");
						}
					}
				}
				if (ArrayExtensions.Contains<string>(filters.AllowedShapeOnlyMatches, text) && !dictionary.ContainsKey(text2))
				{
					dictionary[text2] = ((CollectibleObject)item).GetBehavior<BlockBehaviorCarryable>();
					if (loggingEnabled)
					{
						api.Logger.Debug($"CarryOn matchBehavior: {text2} carryableBlock: {((RegistryObject)item).Code}");
					}
				}
			}
			foreach (Block item2 in api.World.Blocks.Where(delegate(Block w)
			{
				if (!w.IsCarryable())
				{
					string[] autoMatchIgnoreMods = filters.AutoMatchIgnoreMods;
					object obj7;
					if (w == null)
					{
						obj7 = null;
					}
					else
					{
						AssetLocation code = ((RegistryObject)w).Code;
						obj7 = ((code != null) ? code.Domain : null);
					}
					return !ArrayExtensions.Contains<string>(autoMatchIgnoreMods, (string)obj7);
				}
				return false;
			}))
			{
				if (item2.EntityClass == null)
				{
					continue;
				}
				string text7 = null;
				string text8 = "Class:" + ((RegistryObject)item2).Class;
				string text9 = "EntityClass:" + item2.EntityClass;
				object obj4;
				if (item2 == null)
				{
					obj4 = null;
				}
				else
				{
					CompositeShape shapeInventory2 = item2.ShapeInventory;
					if (shapeInventory2 == null)
					{
						obj4 = null;
					}
					else
					{
						AssetLocation obj5 = shapeInventory2.Base;
						obj4 = ((obj5 != null) ? obj5.Path : null);
					}
				}
				if (obj4 == null)
				{
					if (item2 == null)
					{
						obj4 = null;
					}
					else
					{
						CompositeShape shape2 = item2.Shape;
						if (shape2 == null)
						{
							obj4 = null;
						}
						else
						{
							AssetLocation obj6 = shape2.Base;
							obj4 = ((obj6 != null) ? obj6.Path : null);
						}
					}
				}
				string text10 = (string)obj4;
				string text11 = ((text10 != null) ? ("Shape:" + text10) : null);
				foreach (string item3 in new List<string>
				{
					text8 + "|" + text11,
					text9 + "|" + text11,
					text11,
					text8
				})
				{
					if (dictionary.ContainsKey(item3))
					{
						text7 = item3;
						if (loggingEnabled)
						{
							api.Logger.Debug($"CarryOn AutoMatch: {((RegistryObject)item2).Code} key: {text7}");
						}
						break;
					}
				}
				if (text7 != null)
				{
					BlockBehaviorCarryable blockBehaviorCarryable = dictionary[text7];
					BlockBehaviorCarryable blockBehaviorCarryable2 = new BlockBehaviorCarryable(item2);
					item2.BlockBehaviors = ArrayExtensions.Append<BlockBehavior>(item2.BlockBehaviors, (BlockBehavior)(object)blockBehaviorCarryable2);
					((CollectibleBehavior)blockBehaviorCarryable2).Initialize(blockBehaviorCarryable.Properties);
					blockBehaviorCarryable2 = new BlockBehaviorCarryable(item2);
					((CollectibleObject)item2).CollectibleBehaviors = ArrayExtensions.Append<CollectibleBehavior>(((CollectibleObject)item2).CollectibleBehaviors, (CollectibleBehavior)(object)blockBehaviorCarryable2);
					((CollectibleBehavior)blockBehaviorCarryable2).Initialize(blockBehaviorCarryable.Properties);
				}
			}
		}

		private void InitEvents()
		{
			string[] ignoreMods = new string[3] { "game", "creative", "survival" };
			foreach (Assembly item in (from t in (from s in Api.ModLoader.Mods
					where !ArrayExtensions.Contains<string>(ignoreMods, s.Info.ModID)
					select s.Systems).SelectMany((IReadOnlyCollection<ModSystem> o) => o.ToArray())
				select ((object)t).GetType().Assembly).Distinct())
			{
				foreach (Type item2 in from t in item.GetTypes()
					where ArrayExtensions.Contains<Type>(t.GetInterfaces(), typeof(ICarryEvent))
					select t)
				{
					try
					{
						(Activator.CreateInstance(item2) as ICarryEvent)?.Init(this);
					}
					catch (Exception ex)
					{
						Api.Logger.Error(ex.Message);
					}
				}
			}
		}
	}
}
namespace CarryOn.Utility
{
	public static class Extensions
	{
		public static void Register<T>(this ICoreAPI api)
		{
			string text = (string)typeof(T).GetProperty("Name").GetValue(null);
			if (typeof(BlockBehavior).IsAssignableFrom(typeof(T)))
			{
				((ICoreAPICommon)api).RegisterBlockBehaviorClass(text, typeof(T));
				return;
			}
			if (typeof(EntityBehavior).IsAssignableFrom(typeof(T)))
			{
				((ICoreAPICommon)api).RegisterEntityBehaviorClass(text, typeof(T));
				return;
			}
			throw new ArgumentException("T is not a block or entity behavior", "T");
		}

		public static bool HasBehavior<T>(this Block block) where T : BlockBehavior
		{
			return ((CollectibleObject)block).HasBehavior(typeof(T), false);
		}

		public static T GetBehaviorOrDefault<T>(this Block block, T @default) where T : BlockBehavior
		{
			return ((CollectibleObject)block).GetBehavior<T>() ?? @default;
		}

		public static IAttribute TryGet(this IAttribute attr, params string[] keys)
		{
			foreach (string text in keys)
			{
				ITreeAttribute val = (ITreeAttribute)(object)((attr is ITreeAttribute) ? attr : null);
				if (val == null)
				{
					return null;
				}
				attr = val[text];
			}
			return attr;
		}

		public static T TryGet<T>(this IAttribute attr, params string[] keys) where T : class, IAttribute
		{
			return attr.TryGet(keys) as T;
		}

		public static void Set(this IAttribute attr, IAttribute value, params string[] keys)
		{
			//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f5: Expected O, but got Unknown
			//IL_00fa: Expected O, but got Unknown
			if (attr == null)
			{
				throw new ArgumentNullException("attr");
			}
			for (int i = 0; i < keys.Length; i++)
			{
				string text = keys[i];
				ITreeAttribute val = (ITreeAttribute)(object)((attr is ITreeAttribute) ? attr : null);
				if (val == null)
				{
					if (attr == null && value == null)
					{
						break;
					}
					string obj = $"attr{from k in keys.Take(i)
						select "[\"" + k + "\"]"}";
					string text2 = ((attr == null) ? null : attr.GetType()?.ToString()) ?? "null";
					throw new ArgumentException(obj + " is " + text2 + ", not TreeAttribute.", "attr");
				}
				if (i == keys.Length - 1)
				{
					if (value != null)
					{
						val[text] = value;
					}
					else
					{
						val.RemoveAttribute(text);
					}
					continue;
				}
				ITreeAttribute val2 = val;
				string text3 = text;
				IAttribute obj2 = val2[text3];
				if (obj2 == null)
				{
					TreeAttribute val3 = new TreeAttribute();
					IAttribute val4 = (IAttribute)val3;
					val2[text3] = (IAttribute)val3;
					obj2 = val4;
				}
				attr = obj2;
			}
		}

		public static void Remove(this IAttribute attr, params string[] keys)
		{
			attr.Set((IAttribute)null, keys);
		}

		public static void Set(this IAttribute attr, ItemStack value, params string[] keys)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			attr.Set((IAttribute)((value == null) ? ((ItemstackAttribute)null) : new ItemstackAttribute(value)), keys);
		}
	}
	public static class JsonHelper
	{
		public static bool TryGetBool(JsonObject json, string key, out bool result)
		{
			if (!json.KeyExists(key))
			{
				result = false;
				return false;
			}
			result = json[key].AsBool(false);
			return true;
		}

		public static bool TryGetInt(JsonObject json, string key, out int result)
		{
			if (!json.KeyExists(key))
			{
				result = 0;
				return false;
			}
			result = json[key].AsInt(0);
			return true;
		}

		public static bool TryGetFloat(JsonObject json, string key, out float result)
		{
			result = json[key].AsFloat(float.NaN);
			return !float.IsNaN(result);
		}

		public static bool TryGetVec3f(JsonObject json, string key, out Vec3f result)
		{
			//IL_0024: Unknown result type (might be due to invalid IL or missing references)
			float[] array = json[key].AsArray<float>((float[])null, (string)null);
			bool flag = array != null && array.Length == 3;
			result = ((!flag) ? ((Vec3f)null) : new Vec3f(array));
			return flag;
		}

		public static bool TryGetVec3i(JsonObject json, string key, out Vec3i result)
		{
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			int[] array = json[key].AsArray<int>((int[])null, (string)null);
			bool flag = array != null && array.Length == 3;
			result = ((!flag) ? ((Vec3i)null) : new Vec3i(array[0], array[1], array[2]));
			return flag;
		}

		public static ModelTransform GetTransform(JsonObject json, ModelTransform baseTransform)
		{
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0038: Unknown result type (might be due to invalid IL or missing references)
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			//IL_006f: Unknown result type (might be due to invalid IL or missing references)
			//IL_008a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0094: Expected O, but got Unknown
			//IL_008f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0094: Unknown result type (might be due to invalid IL or missing references)
			ModelTransform val = baseTransform.Clone();
			if (TryGetVec3f(json, "translation", out var result))
			{
				((ModelTransformNoDefaults)val).Translation = Vec3f.op_Implicit(result);
			}
			if (TryGetVec3f(json, "rotation", out var result2))
			{
				((ModelTransformNoDefaults)val).Rotation = Vec3f.op_Implicit(result2);
			}
			if (TryGetVec3f(json, "origin", out var result3))
			{
				((ModelTransformNoDefaults)val).Origin = Vec3f.op_Implicit(result3);
			}
			if (TryGetVec3f(json, "scale", out var result4))
			{
				((ModelTransformNoDefaults)val).ScaleXYZ = Vec3f.op_Implicit(result4);
			}
			if (TryGetFloat(json, "scale", out var result5))
			{
				((ModelTransformNoDefaults)val).ScaleXYZ = Vec3f.op_Implicit(new Vec3f(result5, result5, result5));
			}
			return val;
		}
	}
	public class LockedItemSlot : ItemSlot
	{
		public ItemSlot Original { get; }

		public int SlotID { get; }

		public LockedItemSlot(ItemSlot original)
			: base(original.Inventory)
		{
			Original = original ?? throw new ArgumentNullException("original");
			((ItemSlot)this).Itemstack = original.Itemstack;
			base.BackgroundIcon = original.BackgroundIcon;
			((ItemSlot)this).StorageType = (EnumItemStorageFlags)0;
			SlotID = -1;
			for (int i = 0; i < original.Inventory.Count; i++)
			{
				if (original.Inventory[i] == original)
				{
					SlotID = i;
					break;
				}
			}
			if (SlotID == -1)
			{
				throw new Exception("Couldn't find original slot in its own inventory!");
			}
		}

		public static LockedItemSlot Lock(ItemSlot slot)
		{
			LockedItemSlot lockedItemSlot = slot as LockedItemSlot;
			if (lockedItemSlot == null)
			{
				lockedItemSlot = new LockedItemSlot(slot);
				slot.Inventory[lockedItemSlot.SlotID] = (ItemSlot)(object)lockedItemSlot;
			}
			return lockedItemSlot;
		}

		public static void Restore(ItemSlot slot)
		{
			if (slot is LockedItemSlot lockedItemSlot)
			{
				slot.Inventory[lockedItemSlot.SlotID] = lockedItemSlot.Original;
			}
		}

		public override bool CanHold(ItemSlot sourceSlot)
		{
			return false;
		}

		public override bool CanTake()
		{
			return false;
		}

		public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority)
		{
			return false;
		}

		public override ItemStack TakeOutWhole()
		{
			return null;
		}

		public override ItemStack TakeOut(int quantity)
		{
			return null;
		}

		protected override void ActivateSlotLeftClick(ItemSlot sourceSlot, ref ItemStackMoveOperation op)
		{
		}

		protected override void ActivateSlotRightClick(ItemSlot sourceSlot, ref ItemStackMoveOperation op)
		{
		}
	}
}
namespace CarryOn.Server
{
	public class DroppedBlockInfo
	{
		public DateTime DroppedDateTime { get; set; }

		public string OwnerUID { get; set; }

		public string OwnerName { get; set; }

		public string BlockCode { get; set; }

		public string Teleport { get; set; }

		public BlockPos Position { get; set; }

		public List<string> Inventory { get; set; }

		private static string GetFileLocation(BlockPos pos, ICoreAPI api)
		{
			string text = Path.Combine("ModData", api.World.SavegameIdentifier, CarrySystem.ModId);
			return Path.Combine(((ICoreAPICommon)api).GetOrCreateDataPath(text), $"dropped-{pos.X}.{pos.Y}.{pos.Z}");
		}

		public static DroppedBlockInfo Get(BlockPos pos, IPlayer player)
		{
			ICoreAPI api = ((Entity)player.Entity).Api;
			string fileLocation = GetFileLocation(pos, api);
			if (File.Exists(fileLocation))
			{
				try
				{
					return JsonUtil.FromString<DroppedBlockInfo>(File.ReadAllText(fileLocation));
				}
				catch (Exception value)
				{
					api.World.Logger.Error($"Failed loading file '{fileLocation}' with error '{value}'!");
				}
			}
			return null;
		}

		public static void Create(BlockPos pos, IPlayer player, ITreeAttribute blockEntityData)
		{
			ICoreAPI api = ((Entity)player.Entity).Api;
			string fileLocation = GetFileLocation(pos, api);
			Block block = api.World.BlockAccessor.GetBlock(pos);
			List<string> list = new List<string>();
			IAttribute obj = ((blockEntityData != null) ? blockEntityData["inventory"] : null);
			TreeAttribute val = (TreeAttribute)(object)((obj is TreeAttribute) ? obj : null);
			if (val != null)
			{
				foreach (KeyValuePair<string, IAttribute> item in (IEnumerable<KeyValuePair<string, IAttribute>>)val.GetTreeAttribute("slots"))
				{
					IAttribute value = item.Value;
					ItemstackAttribute val2 = (ItemstackAttribute)(object)((value is ItemstackAttribute) ? value : null);
					if (val2 != null)
					{
						list.Add(((object)val2.value).ToString());
					}
				}
			}
			DroppedBlockInfo droppedBlockInfo = new DroppedBlockInfo
			{
				DroppedDateTime = DateTime.Now,
				OwnerUID = player.PlayerUID,
				OwnerName = player.PlayerName,
				BlockCode = ((object)((RegistryObject)block).Code).ToString(),
				Position = pos,
				Inventory = list,
				Teleport = $"/tp ={pos.X} ={pos.Y} ={pos.Z}"
			};
			try
			{
				string contents = JsonUtil.ToString<DroppedBlockInfo>(droppedBlockInfo);
				File.WriteAllText(fileLocation, contents);
				api.World.Logger.Debug("Created file '" + fileLocation + "'");
			}
			catch (Exception value2)
			{
				api.World.Logger.Error($"Failed saving file '{fileLocation}' with error '{value2}'!");
			}
		}

		public static void Remove(BlockPos pos, IPlayer player)
		{
			Remove(pos, ((Entity)player.Entity).Api);
		}

		public static void Remove(BlockPos pos, ICoreAPI api)
		{
			string fileLocation = GetFileLocation(pos, api);
			if (File.Exists(fileLocation))
			{
				try
				{
					File.Delete(fileLocation);
					api.World.Logger.Debug("Removed file '" + fileLocation + "'");
				}
				catch (Exception value)
				{
					api.World.Logger.Error($"Failed to delete file '{fileLocation}' with error '{value}'!");
				}
			}
		}
	}
	public class EntityBehaviorDropCarriedOnDamage : EntityBehavior
	{
		private static readonly CarrySlot[] DropFrom = new CarrySlot[2]
		{
			CarrySlot.Hands,
			CarrySlot.Shoulder
		};

		public static string Name { get; } = CarrySystem.ModId + ":dropondamage";

		public override string PropertyName()
		{
			return Name;
		}

		public EntityBehaviorDropCarriedOnDamage(Entity entity)
			: base(entity)
		{
		}

		public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Invalid comparison between Unknown and I4
			if ((int)damageSource.Type != 6)
			{
				base.entity.DropCarried(DropFrom, 1, 2);
			}
		}
	}
}
namespace CarryOn.Patches
{
	[HarmonyPatch(typeof(EntitySeat), "onControls")]
	public class Patch_EntitySeat_onControls
	{
		[HarmonyPrefix]
		public static bool Prefix(EntitySeat __instance, EnumEntityAction action, bool on, ref EnumHandling handled)
		{
			//IL_0026: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Invalid comparison between Unknown and I4
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0038: Invalid comparison between Unknown and I4
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			//IL_003f: Invalid comparison between Unknown and I4
			Entity passenger = __instance.Passenger;
			EntityAgent val = (EntityAgent)(object)((passenger is EntityAgent) ? passenger : null);
			if (val == null)
			{
				return true;
			}
			if (!((TreeAttribute)((Entity)val).WatchedAttributes).GetBool(CarrySystem.DoubleTapDismountEnabledAttributeKey, false))
			{
				return true;
			}
			if ((int)action != 5)
			{
				return true;
			}
			if ((int)((Entity)val).Api.Side == 2 && (int)action == 5 && on)
			{
				long elapsedMilliseconds = ((Entity)val).World.ElapsedMilliseconds;
				long num = ((TreeAttribute)((Entity)val).Attributes).GetLong(CarrySystem.LastSneakTapMsKey, 0L);
				if (num < elapsedMilliseconds)
				{
					if (elapsedMilliseconds - num < CarrySystem.DoubleTapThresholdMs && elapsedMilliseconds - num > 50)
					{
						CarrySystem modSystem = ((Entity)val).Api.ModLoader.GetModSystem<CarrySystem>(true);
						if (modSystem?.ClientChannel == null)
						{
							((Entity)val).Api.Logger.Error("CarrySystem ClientChannel is null");
							return false;
						}
						long entityId = __instance.Entity.EntityId;
						string seatId = __instance.SeatId;
						val.TryUnmount();
						__instance.controls.StopAllMovement();
						modSystem.ClientChannel.SendPacket<DismountMessage>(new DismountMessage
						{
							EntityId = entityId,
							SeatId = seatId
						});
						((Entity)val).Api.Logger.Debug($"Entity {((Entity)val).GetName()} double-tapped to dismount from seat {seatId} on entity {entityId}.");
					}
					else
					{
						((TreeAttribute)((Entity)val).Attributes).SetLong(CarrySystem.LastSneakTapMsKey, elapsedMilliseconds);
					}
				}
				((TreeAttribute)((Entity)val).Attributes).SetLong(CarrySystem.LastSneakTapMsKey, elapsedMilliseconds);
			}
			return false;
		}
	}
}
namespace CarryOn.Events
{
	public class DroppedBlockTracker : ICarryEvent
	{
		public void Init(CarrySystem carrySystem)
		{
			//IL_000d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Invalid comparison between Unknown and I4
			CarryEvents carryEvents = carrySystem.CarryEvents;
			if ((int)carrySystem.Api.Side == 2)
			{
				carryEvents.OnCheckPermissionToCarry = (CheckPermissionToCarryDelegate)Delegate.Combine(carryEvents.OnCheckPermissionToCarry, new CheckPermissionToCarryDelegate(OnCheckPermissionToCarryClient));
				return;
			}
			carryEvents.OnCheckPermissionToCarry = (CheckPermissionToCarryDelegate)Delegate.Combine(carryEvents.OnCheckPermissionToCarry, new CheckPermissionToCarryDelegate(OnCheckPermissionToCarry));
			carryEvents.BlockDropped += OnCarriedBlockDropped;
			carryEvents.BlockRemoved += OnCarryableBlockRemoved;
		}

		public void OnCheckPermissionToCarryClient(EntityPlayer playerEntity, BlockPos pos, bool isReinforced, out bool? hasPermission)
		{
			hasPermission = (isReinforced ? ((bool?)null) : new bool?(true));
		}

		public void OnCheckPermissionToCarry(EntityPlayer playerEntity, BlockPos pos, bool isReinforced, out bool? hasPermission)
		{
			hasPermission = null;
			if (isReinforced)
			{
				return;
			}
			IWorldAccessor world = ((Entity)playerEntity).Api.World;
			bool loggingEnabled = ModConfig.ServerConfig.DebuggingOptions.LoggingEnabled;
			if (DroppedBlockInfo.Get(pos, playerEntity.Player) != null)
			{
				if (loggingEnabled)
				{
					world.Logger.Debug($"Dropped block found at '{pos}'");
				}
				hasPermission = true;
			}
			else if (loggingEnabled)
			{
				world.Logger.Debug($"No dropped block found at '{pos}'");
			}
		}

		public void OnCarriedBlockDropped(object sender, BlockDroppedEventArgs e)
		{
			Entity entity = e.Entity;
			EntityPlayer val = (EntityPlayer)(object)((entity is EntityPlayer) ? entity : null);
			if (val != null)
			{
				DroppedBlockInfo.Create(e.Position, val.Player, e.CarriedBlock.BlockEntityData);
			}
		}

		public void OnCarryableBlockRemoved(object sender, BlockRemovedEventArgs e)
		{
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0011: Invalid comparison between Unknown and I4
			if ((int)e.World.Api.Side == 1)
			{
				DroppedBlockInfo.Remove(e.Position, e.World.Api);
			}
		}
	}
	public class MeshAngleFix : ICarryEvent
	{
		public void Init(CarrySystem carrySystem)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Invalid comparison between Unknown and I4
			if ((int)carrySystem.Api.Side == 1)
			{
				CarryEvents carryEvents = carrySystem.CarryEvents;
				carryEvents.OnRestoreEntityBlockData = (BlockEntityDataDelegate)Delegate.Combine(carryEvents.OnRestoreEntityBlockData, new BlockEntityDataDelegate(OnRestoreEntityBlockData));
			}
		}

		public void OnRestoreEntityBlockData(BlockEntity blockEntity, ITreeAttribute blockEntityData, bool dropped)
		{
			BlockEntitySign val = (BlockEntitySign)(object)((blockEntity is BlockEntitySign) ? blockEntity : null);
			if (val != null)
			{
				blockEntityData.SetFloat("meshAngle", val.MeshAngleRad);
				return;
			}
			BlockEntityBookshelf val2 = (BlockEntityBookshelf)(object)((blockEntity is BlockEntityBookshelf) ? blockEntity : null);
			if (val2 != null)
			{
				blockEntityData.SetFloat("meshAngleRad", val2.MeshAngleRad);
				return;
			}
			BlockEntityGeneric val3 = (BlockEntityGeneric)(object)((blockEntity is BlockEntityGeneric) ? blockEntity : null);
			if (val3 != null)
			{
				if (((blockEntity == null) ? null : ((RegistryObject)(blockEntity.Block?)).Class) == "BlockClutterBookshelf")
				{
					BEBehaviorClutterBookshelf behavior = ((BlockEntity)val3).GetBehavior<BEBehaviorClutterBookshelf>();
					blockEntityData.SetFloat("meshAngle", ((BEBehaviorShapeFromAttributes)behavior).rotateY);
				}
				else if (((blockEntity == null) ? null : ((RegistryObject)(blockEntity.Block?)).Class) == "BlockClutter")
				{
					BEBehaviorShapeFromAttributes behavior2 = ((BlockEntity)val3).GetBehavior<BEBehaviorShapeFromAttributes>();
					blockEntityData.SetFloat("meshAngle", behavior2.rotateY);
				}
			}
		}
	}
	public class MessageOnBlockDropped : ICarryEvent
	{
		public void Init(CarrySystem carrySystem)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Invalid comparison between Unknown and I4
			if ((int)carrySystem.Api.Side == 1)
			{
				carrySystem.CarryEvents.BlockDropped += OnCarriedBlockDropped;
			}
		}

		public void OnCarriedBlockDropped(object sender, BlockDroppedEventArgs e)
		{
			string text = string.Format("{0}:drop-notice{1}{2}", CarrySystem.ModId, e.Destroyed ? "-destroyed" : null, e.HadContents ? "-spill-contents" : null);
			Entity entity = e.Entity;
			Entity obj = ((entity is EntityPlayer) ? entity : null);
			IPlayer obj2 = ((obj != null) ? ((EntityPlayer)obj).Player : null);
			IPlayer obj3 = ((obj2 is IServerPlayer) ? obj2 : null);
			ItemStack itemStack = e.CarriedBlock.ItemStack;
			string text2 = ((itemStack == null) ? null : itemStack.GetName()?.ToLower());
			string text3 = Lang.Get(CarrySystem.ModId + ":slot-" + e.CarriedBlock.Slot.ToString().ToLower(), Array.Empty<object>());
			((IServerPlayer)obj3).SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get(text, new object[2] { text2, text3 }), (EnumChatType)4, (string)null);
		}
	}
	public class TrunkFix : ICarryEvent
	{
		public void Init(CarrySystem carrySystem)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Invalid comparison between Unknown and I4
			if ((int)carrySystem.Api.Side == 1)
			{
				CarryEvents carryEvents = carrySystem.CarryEvents;
				carryEvents.OnRestoreEntityBlockData = (BlockEntityDataDelegate)Delegate.Combine(carryEvents.OnRestoreEntityBlockData, new BlockEntityDataDelegate(OnRestoreEntityBlockData));
			}
		}

		public void OnRestoreEntityBlockData(BlockEntity blockEntity, ITreeAttribute blockEntityData, bool dropped)
		{
			int num;
			if (blockEntity == null)
			{
				num = 0;
			}
			else
			{
				Block block = blockEntity.Block;
				bool? obj;
				if (block == null)
				{
					obj = null;
				}
				else
				{
					CompositeShape shape = block.Shape;
					if (shape == null)
					{
						obj = null;
					}
					else
					{
						AssetLocation obj2 = shape.Base;
						obj = ((obj2 == null) ? ((bool?)null) : obj2.Path?.StartsWith("block/wood/trunk/"));
					}
				}
				bool? flag = obj;
				num = ((flag == true) ? 1 : 0);
			}
			bool flag2 = (byte)num != 0;
			if (dropped && flag2)
			{
				blockEntityData.SetFloat("meshAngle", -(float)Math.PI / 2f);
			}
		}
	}
}
namespace CarryOn.Config
{
	public class CarryablesConfig
	{
		public bool Anvil { get; set; } = true;

		public bool Barrel { get; set; } = true;

		public bool Bookshelf { get; set; }

		public bool BunchOCandles { get; set; }

		public bool Chandelier { get; set; }

		public bool ChestLabeled { get; set; } = true;

		public bool ChestTrunk { get; set; }

		public bool Chest { get; set; } = true;

		public bool Clutter { get; set; }

		public bool Crate { get; set; } = true;

		public bool DisplayCase { get; set; }

		public bool Flowerpot { get; set; }

		public bool Forge { get; set; }

		public bool Henbox { get; set; }

		public bool LogWithResin { get; set; }

		public bool LootVessel { get; set; } = true;

		public bool MoldRack { get; set; }

		public bool Molds { get; set; }

		public bool Oven { get; set; }

		public bool Planter { get; set; } = true;

		public bool Quern { get; set; } = true;

		public bool ReedBasket { get; set; } = true;

		public bool Resonator { get; set; } = true;

		public bool Shelf { get; set; }

		public bool Sign { get; set; }

		public bool StorageVessel { get; set; } = true;

		public bool ToolRack { get; set; }

		public bool TorchHolder { get; set; }
	}
	public class InteractablesConfig
	{
		public bool Door { get; set; } = true;

		public bool Barrel { get; set; } = true;

		public bool Storage { get; set; } = true;
	}
	public class CarryablesFiltersConfig
	{
		public bool AutoMapSimilar { get; set; } = true;

		public string[] AutoMatchIgnoreMods { get; set; } = new string[1] { "mcrate" };

		public string[] AllowedShapeOnlyMatches { get; set; } = new string[4] { "block/clay/lootvessel", "block/wood/chest/normal", "block/wood/trunk/normal", "block/reed/basket-normal" };

		public string[] RemoveBaseCarryableBehaviour { get; set; } = new string[1] { "woodchests:wtrunk" };

		public string[] RemoveCarryableBehaviour { get; set; } = new string[1] { "game:banner" };
	}
	public class CarryOptionsConfig
	{
		public bool AllowSprintWhileCarrying { get; set; }

		public bool IgnoreCarrySpeedPenalty { get; set; }

		public bool RemoveInteractDelayWhileCarrying { get; set; }

		public float InteractSpeedMultiplier { get; set; } = 1f;

		public bool BackSlotEnabled { get; set; } = true;

		public bool AllowChestTrunksOnBack { get; set; }

		public bool AllowLargeChestsOnBack { get; set; }

		public bool AllowCratesOnBack { get; set; }

		public string[] PreventSwapFromBackOnTarget { get; set; } = new string[3] { "behavior::Container", "behavior::Door", "class::portals.portal" };
	}
	public class DroppedBlockOptionsConfig
	{
		public string[] NonGroundBlockClasses { get; set; } = new string[2] { "BlockWater", "BlockLava" };
	}
	public class DebuggingOptionsConfig
	{
		public bool LoggingEnabled { get; set; }

		public bool DisableHarmonyPatch { get; set; }
	}
	public class CarryOnConfig
	{
		public int ConfigVersion { get; set; } = 2;

		public CarryablesConfig Carryables { get; set; } = new CarryablesConfig();

		public InteractablesConfig Interactables { get; set; } = new InteractablesConfig();

		public CarryOptionsConfig CarryOptions { get; set; } = new CarryOptionsConfig();

		public CarryablesFiltersConfig CarryablesFilters { get; set; } = new CarryablesFiltersConfig();

		public DroppedBlockOptionsConfig DroppedBlockOptions { get; set; } = new DroppedBlockOptionsConfig();

		public DebuggingOptionsConfig DebuggingOptions { get; set; } = new DebuggingOptionsConfig();

		public CarryOnConfig()
		{
		}

		public CarryOnConfig(CarryOnConfig previousConfig)
		{
			if (previousConfig == null)
			{
				throw new ArgumentNullException("previousConfig");
			}
			ConfigVersion = previousConfig.ConfigVersion;
			if (previousConfig.Carryables != null)
			{
				Carryables.Anvil = previousConfig.Carryables.Anvil;
				Carryables.Barrel = previousConfig.Carryables.Barrel;
				Carryables.Bookshelf = previousConfig.Carryables.Bookshelf;
				Carryables.BunchOCandles = previousConfig.Carryables.BunchOCandles;
				Carryables.Chandelier = previousConfig.Carryables.Chandelier;
				Carryables.ChestLabeled = previousConfig.Carryables.ChestLabeled;
				Carryables.ChestTrunk = previousConfig.Carryables.ChestTrunk;
				Carryables.Chest = previousConfig.Carryables.Chest;
				Carryables.Clutter = previousConfig.Carryables.Clutter;
				Carryables.Crate = previousConfig.Carryables.Crate;
				Carryables.DisplayCase = previousConfig.Carryables.DisplayCase;
				Carryables.Flowerpot = previousConfig.Carryables.Flowerpot;
				Carryables.Forge = previousConfig.Carryables.Forge;
				Carryables.Henbox = previousConfig.Carryables.Henbox;
				Carryables.LogWithResin = previousConfig.Carryables.LogWithResin;
				Carryables.MoldRack = previousConfig.Carryables.MoldRack;
				Carryables.Molds = previousConfig.Carryables.Molds;
				Carryables.LootVessel = previousConfig.Carryables.LootVessel;
				Carryables.Oven = previousConfig.Carryables.Oven;
				Carryables.Planter = previousConfig.Carryables.Planter;
				Carryables.Quern = previousConfig.Carryables.Quern;
				Carryables.ReedBasket = previousConfig.Carryables.ReedBasket;
				Carryables.Resonator = previousConfig.Carryables.Resonator;
				Carryables.Shelf = previousConfig.Carryables.Shelf;
				Carryables.Sign = previousConfig.Carryables.Sign;
				Carryables.StorageVessel = previousConfig.Carryables.StorageVessel;
				Carryables.ToolRack = previousConfig.Carryables.ToolRack;
				Carryables.TorchHolder = previousConfig.Carryables.TorchHolder;
			}
			if (previousConfig.Interactables != null)
			{
				Interactables.Barrel = previousConfig.Interactables.Barrel;
				Interactables.Door = previousConfig.Interactables.Door;
				Interactables.Storage = previousConfig.Interactables.Storage;
			}
			if (previousConfig.CarryOptions != null)
			{
				CarryOptions.AllowSprintWhileCarrying = previousConfig.CarryOptions.AllowSprintWhileCarrying;
				CarryOptions.IgnoreCarrySpeedPenalty = previousConfig.CarryOptions.IgnoreCarrySpeedPenalty;
				CarryOptions.RemoveInteractDelayWhileCarrying = previousConfig.CarryOptions.RemoveInteractDelayWhileCarrying;
				CarryOptions.InteractSpeedMultiplier = previousConfig.CarryOptions.InteractSpeedMultiplier;
				CarryOptions.AllowChestTrunksOnBack = previousConfig.CarryOptions.AllowChestTrunksOnBack;
				CarryOptions.BackSlotEnabled = previousConfig.CarryOptions.BackSlotEnabled;
				CarryOptions.AllowLargeChestsOnBack = previousConfig.CarryOptions.AllowLargeChestsOnBack;
				CarryOptions.AllowCratesOnBack = previousConfig.CarryOptions.AllowCratesOnBack;
				CarryOptions.PreventSwapFromBackOnTarget = ((previousConfig.CarryOptions.PreventSwapFromBackOnTarget != null) ? ((string[])previousConfig.CarryOptions.PreventSwapFromBackOnTarget.Clone()) : Array.Empty<string>());
			}
			if (previousConfig.CarryablesFilters != null)
			{
				CarryablesFilters.AutoMapSimilar = previousConfig.CarryablesFilters.AutoMapSimilar;
				CarryablesFilters.AutoMatchIgnoreMods = ((previousConfig.CarryablesFilters.AutoMatchIgnoreMods != null) ? ((string[])previousConfig.CarryablesFilters.AutoMatchIgnoreMods.Clone()) : Array.Empty<string>());
				CarryablesFilters.AllowedShapeOnlyMatches = ((previousConfig.CarryablesFilters.AllowedShapeOnlyMatches != null) ? ((string[])previousConfig.CarryablesFilters.AllowedShapeOnlyMatches.Clone()) : Array.Empty<string>());
				CarryablesFilters.RemoveBaseCarryableBehaviour = ((previousConfig.CarryablesFilters.RemoveBaseCarryableBehaviour != null) ? ((string[])previousConfig.CarryablesFilters.RemoveBaseCarryableBehaviour.Clone()) : Array.Empty<string>());
				CarryablesFilters.RemoveCarryableBehaviour = ((previousConfig.CarryablesFilters.RemoveCarryableBehaviour != null) ? ((string[])previousConfig.CarryablesFilters.RemoveCarryableBehaviour.Clone()) : Array.Empty<string>());
			}
			if (previousConfig.DroppedBlockOptions != null)
			{
				DroppedBlockOptions.NonGroundBlockClasses = ((previousConfig.DroppedBlockOptions.NonGroundBlockClasses != null) ? ((string[])previousConfig.DroppedBlockOptions.NonGroundBlockClasses.Clone()) : Array.Empty<string>());
			}
			if (previousConfig.DebuggingOptions != null)
			{
				DebuggingOptions.LoggingEnabled = previousConfig.DebuggingOptions.LoggingEnabled;
				DebuggingOptions.DisableHarmonyPatch = previousConfig.DebuggingOptions.DisableHarmonyPatch;
			}
		}
	}
	internal class CarryOnConfigLegacy
	{
		public bool AnvilEnabled = true;

		public bool BarrelEnabled = true;

		public bool BookshelfEnabled;

		public bool BunchOCandlesEnabled;

		public bool ChandelierEnabled;

		public bool ChestLabeledEnabled = true;

		public bool ChestTrunkEnabled;

		public bool ChestEnabled = true;

		public bool ClutterEnabled;

		public bool CrateLegacyEnabled = true;

		public bool CrateEnabled = true;

		public bool DisplayCaseEnabled;

		public bool FlowerpotEnabled;

		public bool ForgeEnabled;

		public bool HenboxEnabled;

		public bool LogWithResinEnabled;

		public bool LootVesselEnabled = true;

		public bool MoldRackEnabled;

		public bool MoldsEnabled;

		public bool OvenEnabled;

		public bool PlanterEnabled = true;

		public bool QuernEnabled = true;

		public bool ShelfEnabled;

		public bool SignEnabled;

		public bool ReedBasketEnabled = true;

		public bool StorageVesselEnabled = true;

		public bool ToolRackEnabled;

		public bool TorchHolderEnabled;

		public bool BackSlotEnabled = true;

		public bool AllowChestTrunksOnBack;

		public bool AllowLargeChestsOnBack;

		public bool AllowCratesOnBack;

		public string[] NonGroundBlockClasses = new string[2] { "BlockWater", "BlockLava" };

		public string[] AutoMatchIgnoreMods = new string[1] { "mcrate" };

		public string[] AllowedShapeOnlyMatches = new string[4] { "block/clay/lootvessel", "block/wood/chest/normal", "block/wood/trunk/normal", "block/reed/basket-normal" };

		public string[] RemoveBaseCarryableBehaviour = new string[1] { "woodchests:wtrunk" };

		public string[] RemoveCarryableBehaviour = new string[1] { "game:banner" };

		public bool HarmonyPatchEnabled = true;

		public bool AllowSprintWhileCarrying;

		public bool IgnoreCarrySpeedPenalty;

		public bool RemoveInteractDelayWhileCarrying;

		public float InteractSpeedMultiplier = 1f;

		public bool InteractDoorEnabled { get; set; } = true;

		public bool InteractStorageEnabled { get; set; } = true;

		public bool LoggingEnabled { get; set; }

		public CarryOnConfigLegacy()
		{
		}

		public CarryOnConfigLegacy(CarryOnConfigLegacy previousConfig)
		{
			if (previousConfig == null)
			{
				throw new ArgumentNullException("previousConfig");
			}
			AnvilEnabled = previousConfig.AnvilEnabled;
			BarrelEnabled = previousConfig.BarrelEnabled;
			BookshelfEnabled = previousConfig.BookshelfEnabled;
			BunchOCandlesEnabled = previousConfig.BunchOCandlesEnabled;
			ChandelierEnabled = previousConfig.ChandelierEnabled;
			ChestLabeledEnabled = previousConfig.ChestLabeledEnabled;
			ChestTrunkEnabled = previousConfig.ChestTrunkEnabled;
			ChestEnabled = previousConfig.ChestEnabled;
			ClutterEnabled = previousConfig.ClutterEnabled;
			CrateLegacyEnabled = previousConfig.CrateLegacyEnabled;
			CrateEnabled = previousConfig.CrateEnabled;
			DisplayCaseEnabled = previousConfig.DisplayCaseEnabled;
			FlowerpotEnabled = previousConfig.FlowerpotEnabled;
			ForgeEnabled = previousConfig.ForgeEnabled;
			HenboxEnabled = previousConfig.HenboxEnabled;
			LogWithResinEnabled = previousConfig.LogWithResinEnabled;
			LootVesselEnabled = previousConfig.LootVesselEnabled;
			MoldRackEnabled = previousConfig.MoldRackEnabled;
			MoldsEnabled = previousConfig.MoldsEnabled;
			OvenEnabled = previousConfig.OvenEnabled;
			PlanterEnabled = previousConfig.PlanterEnabled;
			QuernEnabled = previousConfig.QuernEnabled;
			ShelfEnabled = previousConfig.ShelfEnabled;
			SignEnabled = previousConfig.SignEnabled;
			ReedBasketEnabled = previousConfig.ReedBasketEnabled;
			StorageVesselEnabled = previousConfig.StorageVesselEnabled;
			ToolRackEnabled = previousConfig.ToolRackEnabled;
			TorchHolderEnabled = previousConfig.TorchHolderEnabled;
			InteractDoorEnabled = previousConfig.InteractDoorEnabled;
			InteractStorageEnabled = previousConfig.InteractStorageEnabled;
			BackSlotEnabled = previousConfig.BackSlotEnabled;
			AllowChestTrunksOnBack = previousConfig.AllowChestTrunksOnBack;
			AllowLargeChestsOnBack = previousConfig.AllowLargeChestsOnBack;
			AllowCratesOnBack = previousConfig.AllowCratesOnBack;
			AllowSprintWhileCarrying = previousConfig.AllowSprintWhileCarrying;
			IgnoreCarrySpeedPenalty = previousConfig.IgnoreCarrySpeedPenalty;
			RemoveInteractDelayWhileCarrying = previousConfig.RemoveInteractDelayWhileCarrying;
			InteractSpeedMultiplier = ((previousConfig.InteractSpeedMultiplier > 0f) ? previousConfig.InteractSpeedMultiplier : 1f);
			AutoMatchIgnoreMods = ((previousConfig.AutoMatchIgnoreMods != null) ? ((string[])previousConfig.AutoMatchIgnoreMods.Clone()) : Array.Empty<string>());
			AllowedShapeOnlyMatches = ((previousConfig.AllowedShapeOnlyMatches != null) ? ((string[])previousConfig.AllowedShapeOnlyMatches.Clone()) : Array.Empty<string>());
			RemoveBaseCarryableBehaviour = ((previousConfig.RemoveBaseCarryableBehaviour != null) ? ((string[])previousConfig.RemoveBaseCarryableBehaviour.Clone()) : Array.Empty<string>());
			RemoveCarryableBehaviour = ((previousConfig.RemoveCarryableBehaviour != null) ? ((string[])previousConfig.RemoveCarryableBehaviour.Clone()) : Array.Empty<string>());
			NonGroundBlockClasses = ((previousConfig.NonGroundBlockClasses != null) ? ((string[])previousConfig.NonGroundBlockClasses.Clone()) : Array.Empty<string>());
			LoggingEnabled = previousConfig.LoggingEnabled;
			HarmonyPatchEnabled = previousConfig.HarmonyPatchEnabled;
		}

		public CarryOnConfig Convert()
		{
			return new CarryOnConfig
			{
				ConfigVersion = 2,
				Carryables = 
				{
					Anvil = AnvilEnabled,
					Barrel = BarrelEnabled,
					Bookshelf = BookshelfEnabled,
					BunchOCandles = BunchOCandlesEnabled,
					Chandelier = ChandelierEnabled,
					ChestLabeled = ChestLabeledEnabled,
					ChestTrunk = ChestTrunkEnabled,
					Chest = ChestEnabled,
					Clutter = ClutterEnabled,
					Crate = CrateEnabled,
					DisplayCase = DisplayCaseEnabled,
					Flowerpot = FlowerpotEnabled,
					Forge = ForgeEnabled,
					LogWithResin = LogWithResinEnabled,
					MoldRack = MoldRackEnabled,
					Molds = MoldsEnabled,
					LootVessel = LootVesselEnabled,
					Oven = OvenEnabled,
					Planter = PlanterEnabled,
					Quern = QuernEnabled,
					ReedBasket = ReedBasketEnabled,
					Shelf = ShelfEnabled,
					Sign = SignEnabled,
					StorageVessel = StorageVesselEnabled,
					ToolRack = ToolRackEnabled,
					TorchHolder = TorchHolderEnabled,
					Henbox = HenboxEnabled
				},
				Interactables = 
				{
					Door = InteractDoorEnabled,
					Storage = InteractStorageEnabled
				},
				CarryOptions = 
				{
					AllowChestTrunksOnBack = AllowChestTrunksOnBack,
					AllowLargeChestsOnBack = AllowLargeChestsOnBack,
					AllowCratesOnBack = AllowCratesOnBack,
					AllowSprintWhileCarrying = AllowSprintWhileCarrying,
					IgnoreCarrySpeedPenalty = IgnoreCarrySpeedPenalty,
					RemoveInteractDelayWhileCarrying = RemoveInteractDelayWhileCarrying,
					InteractSpeedMultiplier = InteractSpeedMultiplier,
					BackSlotEnabled = BackSlotEnabled
				},
				DebuggingOptions = 
				{
					DisableHarmonyPatch = !HarmonyPatchEnabled,
					LoggingEnabled = LoggingEnabled
				},
				DroppedBlockOptions = 
				{
					NonGroundBlockClasses = ((NonGroundBlockClasses != null) ? ((string[])NonGroundBlockClasses.Clone()) : Array.Empty<string>())
				},
				CarryablesFilters = 
				{
					AutoMatchIgnoreMods = ((AutoMatchIgnoreMods != null) ? ((string[])AutoMatchIgnoreMods.Clone()) : Array.Empty<string>()),
					AllowedShapeOnlyMatches = ((AllowedShapeOnlyMatches != null) ? ((string[])AllowedShapeOnlyMatches.Clone()) : Array.Empty<string>()),
					RemoveBaseCarryableBehaviour = ((RemoveBaseCarryableBehaviour != null) ? ((string[])RemoveBaseCarryableBehaviour.Clone()) : Array.Empty<string>()),
					RemoveCarryableBehaviour = ((RemoveCarryableBehaviour != null) ? ((string[])RemoveCarryableBehaviour.Clone()) : Array.Empty<string>())
				}
			};
		}
	}
	public class CarryOnConfigVersion
	{
		public int? ConfigVersion { get; set; }
	}
	internal static class ModConfig
	{
		private static readonly string allowSprintKey = GetConfigKey("AllowSprintWhileCarrying");

		private static readonly string ignoreSpeedPenaltyKey = GetConfigKey("IgnoreCarrySpeedPenalty");

		private static readonly string removeInteractDelayKey = GetConfigKey("RemoveInteractDelayWhileCarrying");

		private static readonly string interactSpeedMultiplierKey = GetConfigKey("InteractSpeedMultiplier");

		private static readonly string harmonyPatchEnabledKey = GetConfigKey("HarmonyPatchEnabled");

		private static readonly string backSlotEnabledKey = GetConfigKey("BackSlotEnabled");

		private static readonly string henboxEnabledKey = GetConfigKey("HenboxEnabled");

		public static string ConfigFile = "CarryOnConfig.json";

		public static CarryOnConfig ServerConfig { get; private set; }

		public static IWorldAccessor World { get; private set; }

		public static bool AllowSprintWhileCarrying
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(allowSprintKey, false)) : ((bool?)null));
				}
				bool? flag = obj;
				return flag == true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set AllowSprintWhileCarrying.");
				}
				World.Config.SetBool(allowSprintKey, value);
			}
		}

		public static bool IgnoreCarrySpeedPenalty
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(ignoreSpeedPenaltyKey, false)) : ((bool?)null));
				}
				bool? flag = obj;
				return flag == true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set IgnoreCarrySpeedPenalty.");
				}
				World.Config.SetBool(ignoreSpeedPenaltyKey, value);
			}
		}

		public static bool RemoveInteractDelayWhileCarrying
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(removeInteractDelayKey, false)) : ((bool?)null));
				}
				bool? flag = obj;
				return flag == true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set RemoveInteractDelayWhileCarrying.");
				}
				World.Config.SetBool(removeInteractDelayKey, value);
			}
		}

		public static float InteractSpeedMultiplier
		{
			get
			{
				IWorldAccessor world = World;
				float? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new float?(config.GetFloat(interactSpeedMultiplierKey, 1f)) : ((float?)null));
				}
				return obj ?? 1f;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set InteractSpeedMultiplier.");
				}
				if (value < 0.01f)
				{
					value = 0.01f;
				}
				else if (value > 20f)
				{
					value = 20f;
				}
				World.Config.SetFloat(interactSpeedMultiplierKey, value);
			}
		}

		public static bool HarmonyPatchEnabled
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(harmonyPatchEnabledKey, true)) : ((bool?)null));
				}
				return obj ?? true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set HarmonyPatchEnabled.");
				}
				World.Config.SetBool(harmonyPatchEnabledKey, value);
			}
		}

		public static bool BackSlotEnabled
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(backSlotEnabledKey, true)) : ((bool?)null));
				}
				return obj ?? true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set BackSlotEnabled.");
				}
				World.Config.SetBool(backSlotEnabledKey, value);
			}
		}

		public static bool HenboxEnabled
		{
			get
			{
				IWorldAccessor world = World;
				bool? obj;
				if (world == null)
				{
					obj = null;
				}
				else
				{
					ITreeAttribute config = world.Config;
					obj = ((config != null) ? new bool?(config.GetBool(henboxEnabledKey, true)) : ((bool?)null));
				}
				return obj ?? true;
			}
			set
			{
				IWorldAccessor world = World;
				if (((world != null) ? world.Config : null) == null)
				{
					throw new InvalidOperationException("World or World.Config is null. Cannot set HenboxEnabled.");
				}
				World.Config.SetBool(henboxEnabledKey, value);
			}
		}

		public static string GetConfigKey(string key)
		{
			return CarrySystem.ModId + ":" + key;
		}

		public static void ReadConfig(ICoreAPI api)
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Invalid comparison between Unknown and I4
			//IL_04eb: Unknown result type (might be due to invalid IL or missing references)
			//IL_04f1: Expected O, but got Unknown
			//IL_053a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0541: Expected O, but got Unknown
			World = api.World;
			if ((int)api.Side != 1)
			{
				return;
			}
			try
			{
				ServerConfig = LoadConfig(api);
				if (ServerConfig == null)
				{
					StoreConfig(api);
					ServerConfig = LoadConfig(api);
				}
				else
				{
					StoreConfig(api, ServerConfig);
				}
			}
			catch (Exception ex)
			{
				api.Logger.Error("CarryOn: Exception loading config: " + ex);
				ServerConfig = new CarryOnConfig();
			}
			object obj;
			if (api == null)
			{
				obj = null;
			}
			else
			{
				IWorldAccessor world = api.World;
				obj = ((world != null) ? world.Config : null);
			}
			ITreeAttribute val = (ITreeAttribute)obj;
			if (val == null)
			{
				api.Logger.Error("CarryOn: Unable to access world config. CarryOn features may not work correctly.");
				return;
			}
			val.SetBool(GetConfigKey("AnvilEnabled"), ServerConfig.Carryables.Anvil);
			val.SetBool(GetConfigKey("BarrelEnabled"), ServerConfig.Carryables.Barrel);
			val.SetBool(GetConfigKey("BookshelfEnabled"), ServerConfig.Carryables.Bookshelf);
			val.SetBool(GetConfigKey("BunchOCandlesEnabled"), ServerConfig.Carryables.BunchOCandles);
			val.SetBool(GetConfigKey("ChandelierEnabled"), ServerConfig.Carryables.Chandelier);
			val.SetBool(GetConfigKey("ChestLabeledEnabled"), ServerConfig.Carryables.ChestLabeled);
			val.SetBool(GetConfigKey("ChestTrunkEnabled"), ServerConfig.Carryables.ChestTrunk);
			val.SetBool(GetConfigKey("ChestEnabled"), ServerConfig.Carryables.Chest);
			val.SetBool(GetConfigKey("ClutterEnabled"), ServerConfig.Carryables.Clutter);
			val.SetBool(GetConfigKey("CrateEnabled"), ServerConfig.Carryables.Crate);
			val.SetBool(GetConfigKey("DisplayCaseEnabled"), ServerConfig.Carryables.DisplayCase);
			val.SetBool(GetConfigKey("FlowerpotEnabled"), ServerConfig.Carryables.Flowerpot);
			val.SetBool(GetConfigKey("ForgeEnabled"), ServerConfig.Carryables.Forge);
			val.SetBool(GetConfigKey("LogWithResinEnabled"), ServerConfig.Carryables.LogWithResin);
			val.SetBool(GetConfigKey("MoldRackEnabled"), ServerConfig.Carryables.MoldRack);
			val.SetBool(GetConfigKey("MoldsEnabled"), ServerConfig.Carryables.Molds);
			val.SetBool(GetConfigKey("LootVesselEnabled"), ServerConfig.Carryables.LootVessel);
			val.SetBool(GetConfigKey("OvenEnabled"), ServerConfig.Carryables.Oven);
			val.SetBool(GetConfigKey("PlanterEnabled"), ServerConfig.Carryables.Planter);
			val.SetBool(GetConfigKey("QuernEnabled"), ServerConfig.Carryables.Quern);
			val.SetBool(GetConfigKey("ReedBasketEnabled"), ServerConfig.Carryables.ReedBasket);
			val.SetBool(GetConfigKey("ResonatorEnabled"), ServerConfig.Carryables.Resonator);
			val.SetBool(GetConfigKey("ShelfEnabled"), ServerConfig.Carryables.Shelf);
			val.SetBool(GetConfigKey("SignEnabled"), ServerConfig.Carryables.Sign);
			val.SetBool(GetConfigKey("StorageVesselEnabled"), ServerConfig.Carryables.StorageVessel);
			val.SetBool(GetConfigKey("ToolRackEnabled"), ServerConfig.Carryables.ToolRack);
			val.SetBool(GetConfigKey("TorchHolderEnabled"), ServerConfig.Carryables.TorchHolder);
			val.SetBool(GetConfigKey("BookshelfAndClutterEnabled"), ServerConfig.Carryables.Bookshelf && ServerConfig.Carryables.Clutter);
			HenboxEnabled = ServerConfig.Carryables.Henbox;
			val.SetBool(GetConfigKey("InteractDoorEnabled"), ServerConfig.Interactables.Door);
			val.SetBool(GetConfigKey("InteractBarrelEnabled"), ServerConfig.Interactables.Barrel);
			val.SetBool(GetConfigKey("InteractStorageEnabled"), ServerConfig.Interactables.Storage);
			val.SetBool(GetConfigKey("AllowChestTrunksOnBack"), ServerConfig.CarryOptions.AllowChestTrunksOnBack);
			val.SetBool(GetConfigKey("AllowLargeChestsOnBack"), ServerConfig.CarryOptions.AllowLargeChestsOnBack);
			val.SetBool(GetConfigKey("AllowCratesOnBack"), ServerConfig.CarryOptions.AllowCratesOnBack);
			TreeArrayAttribute val2 = new TreeArrayAttribute();
			List<TreeAttribute> list = new List<TreeAttribute>(((ArrayAttribute<TreeAttribute>)(object)val2).value ?? Array.Empty<TreeAttribute>());
			string[] array = ServerConfig?.CarryOptions?.PreventSwapFromBackOnTarget ?? Array.Empty<string>();
			foreach (string text in array)
			{
				TreeAttribute val3 = new TreeAttribute();
				val3.SetString("value", text);
				list.Add(val3);
			}
			((ArrayAttribute<TreeAttribute>)(object)val2).value = list.ToArray();
			val[GetConfigKey("PreventSwapFromBackOnTarget")] = (IAttribute)(object)val2;
			AllowSprintWhileCarrying = ServerConfig.CarryOptions.AllowSprintWhileCarrying;
			IgnoreCarrySpeedPenalty = ServerConfig.CarryOptions.IgnoreCarrySpeedPenalty;
			BackSlotEnabled = ServerConfig.CarryOptions.BackSlotEnabled;
			InteractSpeedMultiplier = ServerConfig.CarryOptions.InteractSpeedMultiplier;
			RemoveInteractDelayWhileCarrying = ServerConfig.CarryOptions.RemoveInteractDelayWhileCarrying;
			HarmonyPatchEnabled = !ServerConfig.DebuggingOptions.DisableHarmonyPatch;
		}

		public static CarryOnConfig LoadConfig(ICoreAPI api)
		{
			CarryOnConfigVersion carryOnConfigVersion = ((ICoreAPICommon)api).LoadModConfig<CarryOnConfigVersion>(ConfigFile);
			if (carryOnConfigVersion != null && !carryOnConfigVersion.ConfigVersion.HasValue)
			{
				CarryOnConfigLegacy carryOnConfigLegacy = ((ICoreAPICommon)api).LoadModConfig<CarryOnConfigLegacy>(ConfigFile);
				if (carryOnConfigLegacy != null)
				{
					string text = DateTime.Now.ToString("yyyyMMdd_HHmmss");
					ILogger logger = api.Logger;
					if (logger != null)
					{
						logger.Debug($"Saving backup of {ConfigFile} to {ConfigFile}-{text}.bak");
					}
					((ICoreAPICommon)api).StoreModConfig<CarryOnConfigLegacy>(carryOnConfigLegacy, ConfigFile + "-" + text + ".bak");
					ILogger logger2 = api.Logger;
					if (logger2 != null)
					{
						logger2.Debug("Converting legacy config to newer format");
					}
					return carryOnConfigLegacy.Convert();
				}
			}
			return ((ICoreAPICommon)api).LoadModConfig<CarryOnConfig>(ConfigFile);
		}

		private static void StoreConfig(ICoreAPI api)
		{
			((ICoreAPICommon)api).StoreModConfig<CarryOnConfig>(new CarryOnConfig(), ConfigFile);
		}

		private static void StoreConfig(ICoreAPI api, CarryOnConfig previousConfig)
		{
			((ICoreAPICommon)api).StoreModConfig<CarryOnConfig>(new CarryOnConfig(previousConfig), ConfigFile);
		}
	}
}
namespace CarryOn.Compatibility
{
	public static class AutoConfigLib
	{
		public static bool HadPatches(ICoreAPI api)
		{
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_000f: Expected O, but got Unknown
			bool flag = false;
			try
			{
				string text = "autoconfiglib";
				Harmony val = new Harmony(text);
				MethodInfo methodInfo = AccessTools.DeclaredMethod(typeof(ModConfig), "ReadConfig", (Type[])null, (Type[])null);
				MethodInfo methodInfo2 = AccessTools.DeclaredMethod(typeof(ModConfig), "LoadConfig", (Type[])null, (Type[])null);
				if (methodInfo != null)
				{
					val.Unpatch((MethodBase)methodInfo, (HarmonyPatchType)0, text);
					flag = true;
				}
				if (methodInfo2 != null)
				{
					val.Unpatch((MethodBase)methodInfo2, (HarmonyPatchType)0, text);
					flag = true;
				}
				if (flag)
				{
					api.Logger.Notification("CarryOn: Disabled AutoConfigLib patches.");
				}
			}
			catch (Exception value)
			{
				api.Logger.Error($"CarryOn: Exception during disabling CarryOn AutoConfigLib patches: {value}");
			}
			return flag;
		}
	}
}
namespace CarryOn.Common
{
	public class BlockBehaviorCarryable : BlockBehavior
	{
		public class SlotSettings
		{
			public ModelTransform Transform { get; set; }

			public string Animation { get; set; }

			public float WalkSpeedModifier { get; set; }
		}

		public class SlotStorage
		{
			private readonly Dictionary<CarrySlot, SlotSettings> _dict = new Dictionary<CarrySlot, SlotSettings>();

			public SlotSettings this[CarrySlot slot]
			{
				get
				{
					if (!_dict.TryGetValue(slot, out var value))
					{
						return null;
					}
					return value;
				}
			}

			public void Initialize(JsonObject properties, ModelTransform defaultTansform)
			{
				_dict.Clear();
				if (properties == null || !properties.Exists)
				{
					if (!DefaultAnimation.TryGetValue(CarrySlot.Hands, out var value))
					{
						value = null;
					}
					_dict.Add(CarrySlot.Hands, new SlotSettings
					{
						Animation = value
					});
					return;
				}
				foreach (CarrySlot item in Enum.GetValues(typeof(CarrySlot)).Cast<CarrySlot>())
				{
					JsonObject val = properties[item.ToString()];
					if (val == null || !val.Exists)
					{
						continue;
					}
					JsonObject val2 = val["keepWhenTrue"];
					IWorldAccessor world = ModConfig.World;
					if (((world != null) ? world.Config : null) != null && val2.Exists && !ModConfig.World.Config.GetBool(val2.AsString((string)null), true))
					{
						continue;
					}
					if (!_dict.TryGetValue(item, out var value2))
					{
						if (!DefaultAnimation.TryGetValue(item, out var value3))
						{
							value3 = null;
						}
						_dict.Add(item, value2 = new SlotSettings
						{
							Animation = value3
						});
					}
					value2.Transform = JsonHelper.GetTransform(val, defaultTansform);
					value2.Animation = val["animation"].AsString(value2.Animation);
					if (!DefaultWalkSpeed.TryGetValue(item, out var value4))
					{
						value4 = 0f;
					}
					value2.WalkSpeedModifier = val["walkSpeedModifier"].AsFloat(value4);
				}
			}
		}

		public static readonly IReadOnlyDictionary<CarrySlot, float> DefaultWalkSpeed = new Dictionary<CarrySlot, float>
		{
			{
				CarrySlot.Hands,
				-0.25f
			},
			{
				CarrySlot.Back,
				-0.15f
			},
			{
				CarrySlot.Shoulder,
				-0.15f
			}
		};

		public static readonly IReadOnlyDictionary<CarrySlot, string> DefaultAnimation = new Dictionary<CarrySlot, string>
		{
			{
				CarrySlot.Hands,
				CarrySystem.ModId + ":holdheavy"
			},
			{
				CarrySlot.Shoulder,
				CarrySystem.ModId + ":shoulder"
			}
		};

		public static string Name { get; } = "Carryable";

		public static WorldInteraction[] Interactions { get; } = (WorldInteraction[])(object)new WorldInteraction[1]
		{
			new WorldInteraction
			{
				ActionLangCode = CarrySystem.ModId + ":blockhelp-pickup",
				HotKeyCode = "carryonpickupkey",
				MouseButton = (EnumMouseButton)2,
				RequireFreeHand = true
			}
		};

		public static BlockBehaviorCarryable Default { get; } = new BlockBehaviorCarryable(null);

		public static ModelTransform DefaultBlockTransform => new ModelTransform
		{
			Translation = Vec3f.op_Implicit(new Vec3f(0f, 0f, 0f)),
			Rotation = Vec3f.op_Implicit(new Vec3f(0f, 0f, 0f)),
			Origin = Vec3f.op_Implicit(new Vec3f(0.5f, 0.5f, 0.5f)),
			ScaleXYZ = Vec3f.op_Implicit(new Vec3f(0.5f, 0.5f, 0.5f))
		};

		public float InteractDelay { get; private set; } = CarrySystem.PickUpSpeedDefault;

		public ModelTransform DefaultTransform { get; private set; } = DefaultBlockTransform;

		public SlotStorage Slots { get; } = new SlotStorage();

		public Vec3i MultiblockOffset { get; private set; }

		public int PatchPriority { get; private set; }

		public bool PreventAttaching { get; private set; }

		public JsonObject Properties { get; set; }

		public BlockBehaviorCarryable(Block block)
			: base(block)
		{
		}

		public override void Initialize(JsonObject properties)
		{
			Properties = properties;
			((CollectibleBehavior)this).Initialize(properties);
			if (JsonHelper.TryGetInt(properties, "patchPriority", out var result))
			{
				PatchPriority = result;
			}
			if (JsonHelper.TryGetFloat(properties, "interactDelay", out var result2))
			{
				InteractDelay = result2;
			}
			if (JsonHelper.TryGetVec3i(properties, "multiblockOffset", out var result3))
			{
				MultiblockOffset = result3;
			}
			if (JsonHelper.TryGetBool(properties, "preventAttaching", out var result4))
			{
				PreventAttaching = result4;
			}
			DefaultTransform = JsonHelper.GetTransform(properties, DefaultBlockTransform);
			Slots.Initialize(properties["slots"], DefaultTransform);
		}

		public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref EnumHandling handled)
		{
			return Interactions;
		}

		public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Invalid comparison between Unknown and I4
			if ((int)world.Api.Side == 1)
			{
				DroppedBlockInfo.Remove(pos, world.Api);
			}
			((BlockBehavior)this).OnBlockRemoved(world, pos, ref handling);
		}
	}
	public class BlockBehaviorCarryableInteract : BlockBehavior
	{
		public class AllowedCarryable
		{
			public string Code { get; set; }

			public string Class { get; set; }

			public bool IsMatch(Block block)
			{
				if (Code != null && ((RegistryObject)block).Code.GetName() == Code)
				{
					return true;
				}
				if (Class != null && ((RegistryObject)block).Class == Class)
				{
					return true;
				}
				return false;
			}
		}

		public static string Name { get; } = "CarryableInteract";

		public static BlockBehaviorCarryableInteract Default { get; } = new BlockBehaviorCarryableInteract(null);

		public float InteractDelay { get; private set; } = CarrySystem.InteractSpeedDefault;

		public IList<AllowedCarryable> AllowedCarryables { get; } = new List<AllowedCarryable>();

		public BlockBehaviorCarryableInteract(Block block)
			: base(block)
		{
		}

		public override void Initialize(JsonObject properties)
		{
			((CollectibleBehavior)this).Initialize(properties);
			if (JsonHelper.TryGetFloat(properties, "interactDelay", out var result))
			{
				InteractDelay = result;
			}
			if (properties.KeyExists("allowedCarryables"))
			{
				JsonObject obj = properties["allowedCarryables"];
				JsonObject[] array = ((obj != null) ? obj.AsArray() : null);
				foreach (JsonObject val in array)
				{
					IList<AllowedCarryable> allowedCarryables = AllowedCarryables;
					AllowedCarryable allowedCarryable = new AllowedCarryable();
					JsonObject obj2 = val["code"];
					allowedCarryable.Code = ((obj2 != null) ? obj2.AsString((string)null) : null);
					JsonObject obj3 = val["class"];
					allowedCarryable.Class = ((obj3 != null) ? obj3.AsString((string)null) : null);
					allowedCarryables.Add(allowedCarryable);
				}
			}
		}

		public bool CanInteract(IPlayer player)
		{
			if (AllowedCarryables.Count == 0)
			{
				return true;
			}
			CarriedBlock carriedBlock = ((player == null) ? null : ((Entity)(object)player.Entity)?.GetCarried(CarrySlot.Hands));
			foreach (AllowedCarryable allowedCarryable in AllowedCarryables)
			{
				if (allowedCarryable.IsMatch(carriedBlock.Block))
				{
					return true;
				}
			}
			return false;
		}
	}
	public enum CarryAction
	{
		None,
		Done,
		PickUp,
		PlaceDown,
		SwapBack,
		Interact,
		Attach,
		Detach
	}
	public class CarryHandler
	{
		private Type[] preventSwapFromBackOnBehaviors = Array.Empty<Type>();

		private string[] preventSwapFromBackOnClasses = Array.Empty<string>();

		public CarryInteraction Interaction { get; set; } = new CarryInteraction();

		public bool IsCarryOnEnabled { get; set; } = true;

		private KeyCombination CarryKeyCombination => CarrySystem.ClientAPI.Input.HotKeys[CarrySystem.PickupKeyCode]?.CurrentMapping;

		private KeyCombination CarrySwapKeyCombination => CarrySystem.ClientAPI.Input.HotKeys[CarrySystem.SwapBackModifierKeyCode]?.CurrentMapping;

		private CarrySystem CarrySystem { get; }

		public int MaxInteractionDistance { get; set; }

		public CarryHandler(CarrySystem carrySystem)
		{
			CarrySystem = carrySystem;
		}

		public void InitClient()
		{
			//IL_0031: Unknown result type (might be due to invalid IL or missing references)
			//IL_005e: Unknown result type (might be due to invalid IL or missing references)
			//IL_008b: Unknown result type (might be due to invalid IL or missing references)
			//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0161: Unknown result type (might be due to invalid IL or missing references)
			//IL_016b: Expected O, but got Unknown
			ICoreClientAPI clientAPI = CarrySystem.ClientAPI;
			IInputAPI input = clientAPI.Input;
			input.RegisterHotKey(CarrySystem.PickupKeyCode, Lang.Get(CarrySystem.ModId + ":pickup-hotkey", Array.Empty<object>()), CarrySystem.PickupKeyDefault, (HotkeyType)4, false, false, false);
			input.RegisterHotKey(CarrySystem.SwapBackModifierKeyCode, Lang.Get(CarrySystem.ModId + ":swap-back-hotkey", Array.Empty<object>()), CarrySystem.SwapBackModifierDefault, (HotkeyType)4, false, false, false);
			input.RegisterHotKey(CarrySystem.ToggleKeyCode, Lang.Get(CarrySystem.ModId + ":toggle-hotkey", Array.Empty<object>()), CarrySystem.ToggleDefault, (HotkeyType)4, true, false, false);
			input.RegisterHotKey(CarrySystem.QuickDropKeyCode, Lang.Get(CarrySystem.ModId + ":quickdrop-hotkey", Array.Empty<object>()), CarrySystem.QuickDropDefault, (HotkeyType)4, true, true, false);
			input.RegisterHotKey(CarrySystem.ToggleDoubleTapDismountKeyCode, Lang.Get(CarrySystem.ModId + ":toggle-double-tap-dismount-hotkey", Array.Empty<object>()), CarrySystem.ToggleDoubleTapDismountDefault, (HotkeyType)4, false, true, false);
			input.SetHotKeyHandler(CarrySystem.ToggleKeyCode, (ActionConsumable<KeyCombination>)TriggerToggleKeyPressed);
			input.SetHotKeyHandler(CarrySystem.QuickDropKeyCode, (ActionConsumable<KeyCombination>)TriggerQuickDropKeyPressed);
			input.SetHotKeyHandler(CarrySystem.ToggleDoubleTapDismountKeyCode, (ActionConsumable<KeyCombination>)TriggerToggleDoubleTapDismountKeyPressed);
			CarrySystem.ClientChannel.SetMessageHandler<LockSlotsMessage>((NetworkServerMessageHandler<LockSlotsMessage>)OnLockSlotsMessage);
			clientAPI.Input.InWorldAction += new OnEntityAction(OnEntityAction);
			((IEventAPI)clientAPI.Event).RegisterGameTickListener((Action<float>)OnGameTick, 0, 0);
			clientAPI.Event.BeforeActiveSlotChanged += (ActiveSlotChangeEventArgs _) => OnBeforeActiveSlotChanged((EntityAgent)(object)((IPlayer)CarrySystem.ClientAPI.World.Player).Entity);
			object obj;
			if (clientAPI == null)
			{
				obj = null;
			}
			else
			{
				IClientWorldAccessor world = clientAPI.World;
				obj = ((world != null) ? ((IWorldAccessor)world).Config : null);
			}
			IAttribute val = ((ITreeAttribute)obj)[ModConfig.GetConfigKey("PreventSwapFromBackOnTarget")];
			if (val == null)
			{
				return;
			}
			IAttribute obj2 = ((val is TreeArrayAttribute) ? val : null);
			List<Type> list = new List<Type>();
			List<string> list2 = new List<string>();
			TreeAttribute[] value = ((ArrayAttribute<TreeAttribute>)(object)obj2).value;
			for (int num = 0; num < value.Length; num++)
			{
				string asString = value[num].GetAsString("value", (string)null);
				if (string.IsNullOrEmpty(asString))
				{
					continue;
				}
				if (asString.Contains("::"))
				{
					if (asString.StartsWith("class::"))
					{
						asString = asString.Substring("class::".Length);
						list2.Add(asString);
					}
					else if (asString.StartsWith("behavior::"))
					{
						asString = asString.Substring("behavior::".Length);
						Type blockBehaviorClass = ((ICoreAPI)clientAPI).ClassRegistry.GetBlockBehaviorClass(asString);
						if (blockBehaviorClass != null)
						{
							list.Add(blockBehaviorClass);
							continue;
						}
						((ICoreAPI)clientAPI).Logger.Warning("CarryOn: Block behavior class '{0}' not found for PreventSwapFromBackOnTarget config entry.", new object[1] { asString });
					}
					else
					{
						((ICoreAPI)clientAPI).Logger.Warning("CarryOn: Invalid format '{0}' for PreventSwapFromBackOnTarget config entry. Must start with 'class::' or 'behavior::'", new object[1] { asString });
					}
				}
				else
				{
					((ICoreAPI)clientAPI).Logger.Warning("CarryOn: Invalid format '{0}' for PreventSwapFromBackOnTarget config entry. Must start with 'class::' or 'behavior::'", new object[1] { asString });
				}
			}
			preventSwapFromBackOnBehaviors = list.ToArray();
			preventSwapFromBackOnClasses = list2.ToArray();
		}

		public void InitServer()
		{
			//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cd: Expected O, but got Unknown
			//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ee: Expected O, but got Unknown
			MaxInteractionDistance = 6;
			CarrySystem.ServerChannel.SetMessageHandler<InteractMessage>((NetworkClientMessageHandler<InteractMessage>)OnInteractMessage).SetMessageHandler<PickUpMessage>((NetworkClientMessageHandler<PickUpMessage>)OnPickUpMessage).SetMessageHandler<PlaceDownMessage>((NetworkClientMessageHandler<PlaceDownMessage>)OnPlaceDownMessage)
				.SetMessageHandler<SwapSlotsMessage>((NetworkClientMessageHandler<SwapSlotsMessage>)OnSwapSlotsMessage)
				.SetMessageHandler<AttachMessage>((NetworkClientMessageHandler<AttachMessage>)OnAttachMessage)
				.SetMessageHandler<DetachMessage>((NetworkClientMessageHandler<DetachMessage>)OnDetachMessage)
				.SetMessageHandler<QuickDropMessage>((NetworkClientMessageHandler<QuickDropMessage>)OnQuickDropMessage)
				.SetMessageHandler<DismountMessage>((NetworkClientMessageHandler<DismountMessage>)OnDismountMessage)
				.SetMessageHandler<PlayerAttributeUpdateMessage>((NetworkClientMessageHandler<PlayerAttributeUpdateMessage>)OnPlayerAttributeUpdateMessage);
			((IEventAPI)CarrySystem.ServerAPI.Event).OnEntitySpawn += new EntityDelegate(OnServerEntitySpawn);
			CarrySystem.ServerAPI.Event.PlayerNowPlaying += new PlayerDelegate(OnServerPlayerNowPlaying);
			CarrySystem.ServerAPI.Event.BeforeActiveSlotChanged += (IServerPlayer player, ActiveSlotChangeEventArgs _) => OnBeforeActiveSlotChanged((EntityAgent)(object)((IPlayer)player).Entity);
		}

		private void OnDismountMessage(IServerPlayer player, DismountMessage message)
		{
			((EntityAgent)((IPlayer)player).Entity).TryUnmount();
			Entity entityById = ((Entity)((IPlayer)player).Entity).World.GetEntityById(message.EntityId);
			if (entityById == null)
			{
				return;
			}
			EntityBehaviorCreatureCarrier behavior = entityById.GetBehavior<EntityBehaviorCreatureCarrier>();
			if (behavior == null)
			{
				return;
			}
			IMountableSeat[] seats = ((EntityBehaviorSeatable)behavior).Seats;
			if (seats == null)
			{
				return;
			}
			IMountableSeat? obj = ((IEnumerable<IMountableSeat>)seats).FirstOrDefault((Func<IMountableSeat, bool>)((IMountableSeat s) => s.SeatId == message.SeatId));
			if (obj != null)
			{
				EntityControls controls = obj.Controls;
				if (controls != null)
				{
					controls.StopAllMovement();
				}
			}
		}

		private void OnPlayerAttributeUpdateMessage(IServerPlayer player, PlayerAttributeUpdateMessage message)
		{
			EntityPlayer entity = ((IPlayer)player).Entity;
			if (message.AttributeKey == null)
			{
				return;
			}
			if (message.AttributeKey == CarrySystem.DoubleTapDismountEnabledAttributeKey && message.IsWatchedAttribute)
			{
				if (message.BoolValue.HasValue)
				{
					((TreeAttribute)((Entity)entity).WatchedAttributes).SetBool(message.AttributeKey, message.BoolValue.Value);
				}
				else
				{
					((TreeAttribute)((Entity)entity).WatchedAttributes).RemoveAttribute(message.AttributeKey);
				}
			}
			else
			{
				((Entity)entity).Api.Logger.Warning("Received PlayerAttributeUpdateMessage with unknown attribute key: " + message.AttributeKey);
			}
		}

		public bool TriggerToggleKeyPressed(KeyCombination keyCombination)
		{
			IsCarryOnEnabled = !IsCarryOnEnabled;
			CarrySystem.ClientAPI.ShowChatMessage(Lang.Get(CarrySystem.ModId + ":carryon-" + (IsCarryOnEnabled ? "enabled" : "disabled"), Array.Empty<object>()));
			return true;
		}

		public bool TriggerQuickDropKeyPressed(KeyCombination keyCombination)
		{
			CarrySystem.ClientChannel.SendPacket<QuickDropMessage>(new QuickDropMessage());
			return true;
		}

		private bool TriggerToggleDoubleTapDismountKeyPressed(KeyCombination keyCombination)
		{
			EntityPlayer entity = ((IPlayer)CarrySystem.ClientAPI.World.Player).Entity;
			bool flag = ((TreeAttribute)((Entity)entity).WatchedAttributes).GetBool(CarrySystem.DoubleTapDismountEnabledAttributeKey, false);
			((TreeAttribute)((Entity)entity).WatchedAttributes).SetBool(CarrySystem.DoubleTapDismountEnabledAttributeKey, !flag);
			CarrySystem.ClientChannel.SendPacket<PlayerAttributeUpdateMessage>(new PlayerAttributeUpdateMessage(CarrySystem.DoubleTapDismountEnabledAttributeKey, !flag, isWatchedAttribute: true));
			CarrySystem.ClientAPI.ShowChatMessage(Lang.Get(CarrySystem.ModId + ":double-tap-dismount-" + ((!flag) ? "enabled" : "disabled"), Array.Empty<object>()));
			return true;
		}

		public bool IsCarryKeyPressed(bool checkMouse = false)
		{
			IInputAPI input = CarrySystem.ClientAPI.Input;
			if (checkMouse && !input.InWorldMouseButton.Right)
			{
				return false;
			}
			return input.KeyboardKeyState[CarryKeyCombination.KeyCode];
		}

		public bool IsCarrySwapKeyPressed()
		{
			return CarrySystem.ClientAPI.Input.KeyboardKeyState[CarrySwapKeyCombination.KeyCode];
		}

		public void OnServerEntitySpawn(Entity entity)
		{
			if (entity is EntityPlayer)
			{
				return;
			}
			foreach (CarriedBlock item in entity.GetCarried())
			{
				item.Set(entity, item.Slot);
			}
		}

		public void OnServerPlayerNowPlaying(IServerPlayer player)
		{
			foreach (CarriedBlock item in ((Entity)(object)((IPlayer)player).Entity).GetCarried())
			{
				item.Set((Entity)(object)((IPlayer)player).Entity, item.Slot);
			}
		}

		private static CarrySlot? FindActionSlot(Func<CarrySlot, bool> func)
		{
			if (func(CarrySlot.Hands))
			{
				return CarrySlot.Hands;
			}
			if (func(CarrySlot.Shoulder))
			{
				return CarrySlot.Shoulder;
			}
			return null;
		}

		private bool BeginEntityCarryableInteraction(ref EnumHandling handled)
		{
			IClientPlayer player = CarrySystem.ClientAPI.World.Player;
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands);
			EntitySelection currentEntitySelection = ((IPlayer)player).CurrentEntitySelection;
			object obj;
			if (currentEntitySelection == null)
			{
				obj = null;
			}
			else
			{
				Entity entity = currentEntitySelection.Entity;
				obj = ((entity != null) ? entity.GetBehavior<EntityBehaviorAttachableCarryable>() : null);
			}
			EntityBehaviorAttachableCarryable entityBehaviorAttachableCarryable = (EntityBehaviorAttachableCarryable)obj;
			bool flag = ((IPlayer)player).CurrentEntitySelection != null;
			bool flag2 = entityBehaviorAttachableCarryable != null;
			bool flag3 = ((Entity)(object)((IPlayer)player).Entity).IsCarryKeyHeld();
			if ((flag && !flag2) || (flag2 && !flag3))
			{
				return true;
			}
			if (flag2)
			{
				EntitySelection currentEntitySelection2 = ((IPlayer)player).CurrentEntitySelection;
				int num = currentEntitySelection2?.SelectionBoxIndex ?? (-1);
				int slotIndex = entityBehaviorAttachableCarryable.GetSlotIndex(num);
				EntityBehaviorAttachable val = ((currentEntitySelection2 != null) ? currentEntitySelection2.Entity.GetBehavior<EntityBehaviorAttachable>() : null);
				if (val == null)
				{
					((ICoreAPI)CarrySystem.ClientAPI).Logger.Error("EntityBehaviorAttachable not found on entity {0}", new object[1] { ((RegistryObject)(currentEntitySelection2?.Entity?)).Code });
					CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "attachable-not-found", Lang.Get(CarrySystem.ModId + ":attachable-behavior-not-found", Array.Empty<object>()));
					return true;
				}
				Interaction.TargetSlotIndex = val.GetSlotIndexFromSelectionBoxIndex(num - 1);
				Interaction.TargetEntity = currentEntitySelection2?.Entity;
				Interaction.Slot = entityBehaviorAttachableCarryable.GetItemSlot(slotIndex);
				if (Interaction.Slot == null)
				{
					CompleteInteraction();
					return true;
				}
				if (carried != null)
				{
					if (!Interaction.Slot.Empty)
					{
						CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "slot-not-empty", Lang.Get(CarrySystem.ModId + ":slot-not-empty", Array.Empty<object>()));
						CompleteInteraction();
						handled = (EnumHandling)2;
						return true;
					}
					Interaction.CarryAction = CarryAction.Attach;
				}
				else
				{
					if (Interaction.Slot.Empty)
					{
						CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "slot-empty", Lang.Get(CarrySystem.ModId + ":slot-empty", Array.Empty<object>()));
						CompleteInteraction();
						return true;
					}
					ItemSlot slot = Interaction.Slot;
					object obj2;
					if (slot == null)
					{
						obj2 = null;
					}
					else
					{
						ItemStack itemstack = slot.Itemstack;
						if (itemstack == null)
						{
							obj2 = null;
						}
						else
						{
							Block block = itemstack.Block;
							obj2 = ((block != null) ? ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryable>() : null);
						}
					}
					if (obj2 == null)
					{
						CompleteInteraction();
						return true;
					}
					Interaction.CarryAction = CarryAction.Detach;
				}
				handled = (EnumHandling)2;
				return true;
			}
			return false;
		}

		private bool BeginSwapBackInteraction(ref EnumHandling handled)
		{
			if (!ModConfig.BackSlotEnabled)
			{
				return false;
			}
			IClientPlayer player = CarrySystem.ClientAPI.World.Player;
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands);
			CarriedBlock carried2 = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Back);
			if (!CanInteract((EntityAgent)(object)((IPlayer)player).Entity, requireEmptyHanded: true))
			{
				return false;
			}
			BlockSelection multiblockOriginSelection = GetMultiblockOriginSelection((player != null) ? ((IPlayer)player).CurrentBlockSelection : null);
			bool num = multiblockOriginSelection != null && multiblockOriginSelection.Block?.IsCarryable(CarrySlot.Hands) == true;
			bool flag = ((Entity)(object)((IPlayer)player).Entity).IsCarryKeyHeld();
			bool flag2 = IsCarrySwapKeyPressed();
			bool flag3 = multiblockOriginSelection == null;
			bool flag4 = !num && carried2 != null && carried == null;
			if (flag && (flag2 || flag3 || flag4))
			{
				if (carried == null && !flag3 && SelectionPreventsSwap(multiblockOriginSelection))
				{
					CompleteInteraction();
					return true;
				}
				if (carried != null && carried.Behavior.Slots[CarrySlot.Back] == null)
				{
					CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "cannot-swap-back", Lang.Get(CarrySystem.ModId + ":cannot-swap-back", Array.Empty<object>()));
					CompleteInteraction();
					return true;
				}
				if (carried == null && carried2 == null)
				{
					CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "nothing-carried", Lang.Get(CarrySystem.ModId + ":nothing-carried", Array.Empty<object>()));
					CompleteInteraction();
					return true;
				}
				Interaction.CarryAction = CarryAction.SwapBack;
				Interaction.CarrySlot = CarrySlot.Hands;
				handled = (EnumHandling)2;
				return true;
			}
			return false;
		}

		private bool BeginBlockEntityInteraction(ref EnumHandling handled)
		{
			IClientPlayer player = CarrySystem.ClientAPI.World.Player;
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands);
			if (carried == null)
			{
				return false;
			}
			if (!CanInteract((EntityAgent)(object)((IPlayer)player).Entity, carried == null))
			{
				BlockSelection currentBlockSelection = ((IPlayer)player).CurrentBlockSelection;
				currentBlockSelection = GetMultiblockOriginSelection(currentBlockSelection);
				if (currentBlockSelection != null)
				{
					Block block = currentBlockSelection.Block;
					if (((block != null) ? new bool?(((CollectibleObject)block).HasBehavior<BlockBehaviorCarryableInteract>(false)) : ((bool?)null)) == true && ((currentBlockSelection != null) ? ((CollectibleObject)currentBlockSelection.Block).GetBehavior<BlockBehaviorCarryableInteract>() : null).CanInteract((IPlayer)(object)player))
					{
						Interaction.CarryAction = CarryAction.Interact;
						Interaction.TargetBlockPos = currentBlockSelection.Position;
						handled = (EnumHandling)2;
						return true;
					}
				}
			}
			return false;
		}

		private bool BeginBlockCarryableInteraction(ref EnumHandling handled)
		{
			IClientWorldAccessor world = CarrySystem.ClientAPI.World;
			IClientPlayer player = world.Player;
			if (!((Entity)(object)((IPlayer)player).Entity).IsCarryKeyHeld())
			{
				return false;
			}
			BlockSelection selection = ((IPlayer)player).CurrentBlockSelection;
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands);
			if (carried != null)
			{
				if (selection != null)
				{
					if (!CanInteract((EntityAgent)(object)((IPlayer)player).Entity, carried == null))
					{
						handled = (EnumHandling)2;
						return true;
					}
					BlockPos placedPosition = GetPlacedPosition((IWorldAccessor)(object)world, selection, carried.Block);
					if (placedPosition == (BlockPos)null)
					{
						return true;
					}
					Interaction.TargetBlockPos = placedPosition;
					Interaction.CarryAction = CarryAction.PlaceDown;
					Interaction.CarrySlot = carried.Slot;
					handled = (EnumHandling)2;
					return true;
				}
			}
			else if (CanInteract((EntityAgent)(object)((IPlayer)player).Entity, requireEmptyHanded: true))
			{
				if (selection != null)
				{
					selection = GetMultiblockOriginSelection(selection);
				}
				if (selection?.Block != null && (Interaction.CarrySlot = FindActionSlot((CarrySlot slot) => selection.Block.IsCarryable(slot))).HasValue)
				{
					Interaction.CarryAction = CarryAction.PickUp;
					CarryInteraction interaction = Interaction;
					BlockPos position = selection.Position;
					interaction.TargetBlockPos = ((position != null) ? position.Copy() : null);
					handled = (EnumHandling)2;
					return true;
				}
			}
			return false;
		}

		public void OnEntityAction(EnumEntityAction action, bool on, ref EnumHandling handled)
		{
			//IL_0003: Unknown result type (might be due to invalid IL or missing references)
			//IL_0006: Invalid comparison between Unknown and I4
			//IL_0021: Unknown result type (might be due to invalid IL or missing references)
			//IL_0023: Invalid comparison between Unknown and I4
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Invalid comparison between Unknown and I4
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Invalid comparison between Unknown and I4
			if (!on && (int)action == 16)
			{
				Interaction.CarryAction = CarryAction.None;
			}
			else
			{
				if (!on || !IsCarryOnEnabled)
				{
					return;
				}
				bool flag;
				if ((int)action != 6)
				{
					if ((int)action != 15)
					{
						if ((int)action != 16)
						{
							return;
						}
						flag = true;
					}
					else
					{
						flag = false;
					}
				}
				else
				{
					if (ModConfig.AllowSprintWhileCarrying)
					{
						return;
					}
					flag = false;
				}
				if (Interaction.CarryAction != CarryAction.None)
				{
					handled = (EnumHandling)2;
					return;
				}
				IClientPlayer player = CarrySystem.ClientAPI.World.Player;
				if (((EntityAgent)(object)((IPlayer)player).Entity).CanDoCarryAction(requireEmptyHanded: true) && (!flag || (!BeginEntityCarryableInteraction(ref handled) && !BeginSwapBackInteraction(ref handled) && !BeginBlockEntityInteraction(ref handled) && !BeginBlockCarryableInteraction(ref handled))) && (((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands) != null || (flag && Interaction.TimeHeld > 0f)))
				{
					handled = (EnumHandling)2;
				}
			}
		}

		public void OnGameTick(float deltaTime)
		{
			if (!IsCarryOnEnabled)
			{
				return;
			}
			IClientWorldAccessor world = CarrySystem.ClientAPI.World;
			IClientPlayer player = world.Player;
			IInputAPI input = CarrySystem.ClientAPI.Input;
			if (!input.InWorldMouseButton.Right)
			{
				CancelInteraction(resetTimeHeld: true);
				return;
			}
			((Entity)(object)((IPlayer)player).Entity).SetCarryKeyHeld(input.IsHotKeyPressed(CarrySystem.PickupKeyCode));
			if (Interaction.CarryAction == CarryAction.None || Interaction.CarryAction == CarryAction.Done)
			{
				return;
			}
			if (Interaction.CarryAction != CarryAction.Interact && !CanInteract((EntityAgent)(object)((IPlayer)player).Entity, Interaction.CarryAction != CarryAction.PlaceDown || Interaction.CarrySlot != CarrySlot.Hands))
			{
				CancelInteraction();
				return;
			}
			CarriedBlock carriedBlock = (Interaction.CarrySlot.HasValue ? ((Entity)(object)((IPlayer)player).Entity).GetCarried(Interaction.CarrySlot.Value) : null);
			CarriedBlock carriedBlock2 = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands) ?? ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Shoulder);
			BlockSelection val = null;
			BlockBehaviorCarryable blockBehaviorCarryable = null;
			BlockBehaviorCarryableInteract blockBehaviorCarryableInteract = null;
			EntityBehaviorAttachableCarryable entityBehaviorAttachableCarryable = null;
			switch (Interaction.CarryAction)
			{
			default:
				return;
			case CarryAction.PickUp:
			case CarryAction.PlaceDown:
			case CarryAction.Interact:
			{
				if (Interaction.CarryAction == CarryAction.PickUp == (carriedBlock2 != null))
				{
					CancelInteraction();
					return;
				}
				val = ((Interaction.CarryAction == CarryAction.PlaceDown) ? ((IPlayer)player).CurrentBlockSelection : GetMultiblockOriginSelection(((IPlayer)player).CurrentBlockSelection));
				BlockPos val2 = ((Interaction.CarryAction == CarryAction.PlaceDown) ? GetPlacedPosition((IWorldAccessor)(object)world, (player != null) ? ((IPlayer)player).CurrentBlockSelection : null, carriedBlock.Block) : val?.Position);
				if (Interaction.TargetBlockPos != val2)
				{
					CancelInteraction();
					return;
				}
				if (Interaction.CarryAction == CarryAction.Interact)
				{
					blockBehaviorCarryableInteract = ((val != null) ? ((CollectibleObject)val.Block).GetBehavior<BlockBehaviorCarryableInteract>() : null);
				}
				else
				{
					blockBehaviorCarryable = ((Interaction.CarryAction != CarryAction.PickUp) ? carriedBlock?.Behavior : val?.Block?.GetBehaviorOrDefault(BlockBehaviorCarryable.Default));
				}
				break;
			}
			case CarryAction.SwapBack:
			{
				if (!ModConfig.BackSlotEnabled)
				{
					return;
				}
				CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Back);
				blockBehaviorCarryable = ((carriedBlock == null) ? carried?.Behavior : carriedBlock?.Behavior);
				if (blockBehaviorCarryable == null)
				{
					CarrySystem.Api.Logger.Debug("Nothing carried. Player may have dropped the block from being damaged");
					return;
				}
				if (blockBehaviorCarryable.Slots[Interaction.CarrySlot.Value] == null)
				{
					return;
				}
				break;
			}
			case CarryAction.Attach:
			case CarryAction.Detach:
			{
				Entity targetEntity = Interaction.TargetEntity;
				entityBehaviorAttachableCarryable = ((targetEntity != null) ? targetEntity.GetBehavior<EntityBehaviorAttachableCarryable>() : null);
				break;
			}
			}
			float num;
			if (Interaction.CarryAction == CarryAction.Interact)
			{
				num = ((!ModConfig.RemoveInteractDelayWhileCarrying) ? (blockBehaviorCarryableInteract?.InteractDelay ?? CarrySystem.InteractSpeedDefault) : 0f);
			}
			else
			{
				num = blockBehaviorCarryable?.InteractDelay ?? CarrySystem.PickUpSpeedDefault;
				switch (Interaction.CarryAction)
				{
				case CarryAction.PlaceDown:
					num *= CarrySystem.PlaceSpeedDefault;
					break;
				case CarryAction.SwapBack:
					num *= CarrySystem.SwapSpeedDefault;
					break;
				}
			}
			num /= ((ModConfig.InteractSpeedMultiplier > 0f) ? ModConfig.InteractSpeedMultiplier : 1f);
			Interaction.TimeHeld += deltaTime;
			float num2 = Interaction.TimeHeld / num;
			CarrySystem.HudOverlayRenderer.CircleProgress = num2;
			HudCarried.TriggerHandsHighlight();
			if (Interaction.CarryAction == CarryAction.SwapBack)
			{
				HudCarried.TriggerBackHighlight();
			}
			if (num2 <= 1f)
			{
				return;
			}
			switch (Interaction.CarryAction)
			{
			case CarryAction.Interact:
				if (val != null)
				{
					Block block = val.Block;
					if (((block != null) ? new bool?(block.OnBlockInteractStart((IWorldAccessor)(object)world, (IPlayer)(object)player, val)) : ((bool?)null)) == true)
					{
						CarrySystem.ClientChannel.SendPacket<InteractMessage>(new InteractMessage(val.Position));
					}
				}
				break;
			case CarryAction.PickUp:
				if (((Entity)(object)((IPlayer)player).Entity).Carry(val.Position, Interaction.CarrySlot.Value))
				{
					CarrySystem.ClientChannel.SendPacket<PickUpMessage>(new PickUpMessage(val.Position, Interaction.CarrySlot.Value));
				}
				break;
			case CarryAction.PlaceDown:
			{
				string failureCode = null;
				if (PlaceDown((IPlayer)(object)player, carriedBlock, val, out var placedAt, ref failureCode))
				{
					CarrySystem.ClientChannel.SendPacket<PlaceDownMessage>(new PlaceDownMessage(Interaction.CarrySlot.Value, val, placedAt));
				}
				else if (failureCode != null && failureCode != "__ignore__")
				{
					CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", failureCode, Lang.Get(CarrySystem.ModId + ":place-down-failed-" + failureCode, Array.Empty<object>()));
				}
				else
				{
					CarrySystem.ClientAPI.TriggerIngameError((object)"carryon", "place-down-failed", Lang.Get(CarrySystem.ModId + ":place-down-failed", Array.Empty<object>()));
				}
				break;
			}
			case CarryAction.SwapBack:
				if (((Entity)(object)((IPlayer)player).Entity).Swap(Interaction.CarrySlot.Value, CarrySlot.Back))
				{
					CarrySystem.ClientChannel.SendPacket<SwapSlotsMessage>(new SwapSlotsMessage(CarrySlot.Back, Interaction.CarrySlot.Value));
				}
				break;
			case CarryAction.Attach:
				if (Interaction.TargetEntity != null)
				{
					CarrySystem.ClientChannel.SendPacket<AttachMessage>(new AttachMessage(Interaction.TargetEntity.EntityId, Interaction.TargetSlotIndex.Value));
					entityBehaviorAttachableCarryable.OnAttachmentToggled(isAttached: true, (EntityAgent)(object)((IPlayer)player).Entity, Interaction.Slot, Interaction.TargetSlotIndex.Value);
				}
				break;
			case CarryAction.Detach:
				if (Interaction.TargetEntity != null)
				{
					CarrySystem.ClientChannel.SendPacket<DetachMessage>(new DetachMessage(Interaction.TargetEntity.EntityId, Interaction.TargetSlotIndex.Value));
					entityBehaviorAttachableCarryable.OnAttachmentToggled(isAttached: false, (EntityAgent)(object)((IPlayer)player).Entity, Interaction.Slot, Interaction.TargetSlotIndex.Value);
				}
				break;
			}
			CompleteInteraction();
		}

		public void CancelInteraction(bool resetTimeHeld = false)
		{
			Interaction.Clear(resetTimeHeld);
			CarrySystem.HudOverlayRenderer.CircleVisible = false;
		}

		public void CompleteInteraction()
		{
			Interaction.Complete();
			CarrySystem.HudOverlayRenderer.CircleVisible = false;
		}

		public EnumHandling OnBeforeActiveSlotChanged(EntityAgent entity)
		{
			if (((Entity)(object)entity).GetCarried(CarrySlot.Hands) != null)
			{
				return (EnumHandling)2;
			}
			return (EnumHandling)0;
		}

		public void OnLockSlotsMessage(LockSlotsMessage message)
		{
			IInventory hotbarInventory = ((IPlayer)CarrySystem.ClientAPI.World.Player).InventoryManager.GetHotbarInventory();
			for (int i = 0; i < ((IReadOnlyCollection<ItemSlot>)hotbarInventory).Count; i++)
			{
				List<int> hotbarSlots = message.HotbarSlots;
				if (hotbarSlots != null && hotbarSlots.Contains(i))
				{
					LockedItemSlot.Lock(hotbarInventory[i]);
				}
				else
				{
					LockedItemSlot.Restore(hotbarInventory[i]);
				}
			}
		}

		public void SendLockSlotsMessage(IServerPlayer player)
		{
			IInventory hotbar = ((IPlayer)player).InventoryManager.GetHotbarInventory();
			List<int> hotbarSlots = (from i in Enumerable.Range(0, ((IReadOnlyCollection<ItemSlot>)hotbar).Count)
				where hotbar[i] is LockedItemSlot
				select i).ToList();
			CarrySystem.ServerChannel.SendPacket<LockSlotsMessage>(new LockSlotsMessage(hotbarSlots), (IServerPlayer[])(object)new IServerPlayer[1] { player });
		}

		public static void SendLockSlotsMessage(EntityPlayer player)
		{
			if (player != null)
			{
				IPlayer obj = ((Entity)player).World.PlayerByUid(player.PlayerUID);
				IServerPlayer val = (IServerPlayer)(object)((obj is IServerPlayer) ? obj : null);
				if (val != null)
				{
					((Entity)player).World.Api.ModLoader.GetModSystem<CarrySystem>(true).CarryHandler.SendLockSlotsMessage(val);
				}
			}
		}

		private void OnInteractMessage(IServerPlayer player, InteractMessage message)
		{
			IWorldAccessor world = ((Entity)((IPlayer)player).Entity).World;
			Block block = world.BlockAccessor.GetBlock(message.Position);
			if (block != null && block.HasBlockBehavior<BlockBehaviorCarryableInteract>(false) && ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryableInteract>().CanInteract((IPlayer)(object)player))
			{
				BlockSelection val = ((IPlayer)player).CurrentBlockSelection.Clone();
				val.Position = message.Position;
				val.Block = block;
				if (block != null)
				{
					block.OnBlockInteractStart(world, (IPlayer)(object)player, val);
				}
			}
		}

		public void OnPickUpMessage(IServerPlayer player, PickUpMessage message)
		{
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(message.Slot);
			if (message.Slot == CarrySlot.Back || carried != null || !CanInteract((EntityAgent)(object)((IPlayer)player).Entity, requireEmptyHanded: true) || !((Entity)(object)((IPlayer)player).Entity).Carry(message.Position, message.Slot))
			{
				InvalidCarry(player, message.Position);
			}
		}

		public void OnPlaceDownMessage(IServerPlayer player, PlaceDownMessage message)
		{
			string failureCode = null;
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(message.Slot);
			if (message.Slot == CarrySlot.Back || carried == null || !CanInteract((EntityAgent)(object)((IPlayer)player).Entity, message.Slot != CarrySlot.Hands) || !PlaceDown((IPlayer)(object)player, carried, message.Selection, out var placedAt, ref failureCode))
			{
				InvalidCarry(player, message.PlacedAt);
				if (failureCode != null && failureCode != "__ignore__")
				{
					CarrySystem.ServerAPI.SendIngameError(player, failureCode, Lang.Get(CarrySystem.ModId + ":place-down-failed-" + failureCode, Array.Empty<object>()), Array.Empty<object>());
				}
				else
				{
					CarrySystem.ServerAPI.SendIngameError(player, "place-down-failed", Lang.Get(CarrySystem.ModId + ":place-down-failed", Array.Empty<object>()), Array.Empty<object>());
				}
			}
			else if (placedAt != message.PlacedAt)
			{
				((Entity)((IPlayer)player).Entity).World.BlockAccessor.MarkBlockDirty(message.PlacedAt, (IPlayer)null);
			}
		}

		public void OnSwapSlotsMessage(IServerPlayer player, SwapSlotsMessage message)
		{
			//IL_005c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0078: Expected O, but got Unknown
			if (ModConfig.BackSlotEnabled && ((message.First != message.Second && message.First == CarrySlot.Back) || CanInteract((EntityAgent)(object)((IPlayer)player).Entity, requireEmptyHanded: true)) && ((Entity)(object)((IPlayer)player).Entity).Swap(message.First, message.Second))
			{
				CarrySystem.Api.World.PlaySoundAt(new AssetLocation("sounds/player/throw"), (Entity)(object)((IPlayer)player).Entity, (IPlayer)null, true, 32f, 1f);
				((Entity)((IPlayer)player).Entity).WatchedAttributes.MarkPathDirty(CarriedBlock.AttributeId);
			}
		}

		public void OnAttachMessage(IServerPlayer player, AttachMessage message)
		{
			//IL_0336: Unknown result type (might be due to invalid IL or missing references)
			//IL_033d: Expected O, but got Unknown
			//IL_056a: Unknown result type (might be due to invalid IL or missing references)
			Entity entityById = CarrySystem.Api.World.GetEntityById(message.TargetEntityId);
			if (entityById == null)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "entity-not-found", Lang.Get(CarrySystem.ModId + ":entity-not-found", Array.Empty<object>()), Array.Empty<object>());
				CarrySystem.Api.Logger.Debug("Target entity does not exist!");
				return;
			}
			EntityPos sidedPos = entityById.SidedPos;
			if (((sidedPos != null) ? new double?(sidedPos.DistanceTo(((Entity)((IPlayer)player).Entity).Pos)) : ((double?)null)) > (double)MaxInteractionDistance)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "entity-out-of-reach", Lang.Get(CarrySystem.ModId + ":entity-out-of-reach", Array.Empty<object>()), Array.Empty<object>());
				CarrySystem.Api.Logger.Debug("Target entity is too far away!");
				return;
			}
			if (message.SlotIndex < 0)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-not-found", Lang.Get(CarrySystem.ModId + ":slot-not-found", Array.Empty<object>()), Array.Empty<object>());
				CarrySystem.Api.Logger.Debug("Invalid target slot index!");
				return;
			}
			EntityBehaviorAttachable behavior = entityById.GetBehavior<EntityBehaviorAttachable>();
			if (behavior == null)
			{
				return;
			}
			CarriedBlock carried = ((Entity)(object)((IPlayer)player).Entity).GetCarried(CarrySlot.Hands);
			if (carried == null)
			{
				return;
			}
			ITreeAttribute val = carried?.BlockEntityData;
			if (val == null)
			{
				CarrySystem.Api.Logger.Warning("Block entity data is null, cannot attach block");
				CarrySystem.ServerAPI.SendIngameError(player, "slot-data-missing", Lang.Get(CarrySystem.ModId + ":slot-data-missing", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			string text = val.GetString("type", (string)null);
			val.GetTreeAttribute("inventory");
			Block val2 = carried?.Block;
			ItemSlot slotFromSelectionBoxIndex = behavior.GetSlotFromSelectionBoxIndex(message.SlotIndex);
			EntityBehaviorSelectionBoxes behavior2 = entityById.GetBehavior<EntityBehaviorSelectionBoxes>();
			string apname = ((behavior2 == null) ? null : behavior2.selectionBoxes[message.SlotIndex]?.AttachPoint?.Code);
			EntityBehaviorSeatable behavior3 = entityById.GetBehavior<EntityBehaviorSeatable>();
			bool flag = false;
			if (behavior3 != null)
			{
				string seatId = behavior3.SeatConfigs.Where((SeatConfig s) => s.APName == apname).FirstOrDefault()?.SeatId;
				IMountableSeat? obj = behavior3.Seats.Where((IMountableSeat s) => s.SeatId == seatId).FirstOrDefault();
				flag = ((obj != null) ? obj.Passenger : null) != null;
			}
			if (slotFromSelectionBoxIndex == null || !slotFromSelectionBoxIndex.Empty || flag)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-not-empty", Lang.Get(CarrySystem.ModId + ":slot-not-empty", Array.Empty<object>()), Array.Empty<object>());
				CarrySystem.Api.Logger.Log((EnumLogType)5, "Target Slot is occupied!");
				return;
			}
			ItemSlot val3 = (ItemSlot)new DummySlot((ItemStack)null);
			val3.Itemstack = carried.ItemStack.Clone();
			ITreeAttribute attributes = val3.Itemstack.Attributes;
			TreeAttribute val4 = (TreeAttribute)(object)((attributes is TreeAttribute) ? attributes : null);
			if (val4 == null)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-data-missing", Lang.Get(CarrySystem.ModId + ":slot-data-missing", Array.Empty<object>()), Array.Empty<object>());
				CarrySystem.Api.Logger.Log((EnumLogType)5, "Source item is invalid!");
				return;
			}
			ITreeAttribute val5 = val.Clone();
			val5.RemoveAttribute("inventory");
			val4.SetString("type", text);
			ITreeAttribute val6 = ConvertBlockInventoryToBackpack(val.GetTreeAttribute("inventory"));
			val4.SetAttribute("backpack", (IAttribute)(object)val6);
			val4.SetAttribute("carryonbackup", (IAttribute)(object)val5);
			if (!slotFromSelectionBoxIndex.CanTakeFrom(val3, (EnumMergePriority)0))
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-incompatible-block", Lang.Get(CarrySystem.ModId + ":slot-incompatible-block", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			BlockBehaviorCarryable behavior4 = ((CollectibleObject)val3.Itemstack.Block).GetBehavior<BlockBehaviorCarryable>();
			if (behavior4 != null && behavior4.PreventAttaching)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-prevent-attaching", Lang.Get(CarrySystem.ModId + ":slot-incompatible-block", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			IAttachedInteractions collectibleInterface = val3.Itemstack.Collectible.GetCollectibleInterface<IAttachedInteractions>();
			if (collectibleInterface != null && !collectibleInterface.OnTryAttach(val3, message.SlotIndex, entityById))
			{
				CarrySystem.ServerAPI.SendIngameError(player, "attach-unavailable", Lang.Get(CarrySystem.ModId + ":attach-unavailable", Array.Empty<object>()), Array.Empty<object>());
			}
			else if (val3.TryPutInto(entityById.World, slotFromSelectionBoxIndex, 1) > 0)
			{
				((EntityBehaviorContainer)behavior).storeInv();
				entityById.MarkShapeModified();
				entityById.World.BlockAccessor.GetChunkAtBlockPos(entityById.ServerPos.AsBlockPos).MarkModified();
				CarriedBlock.Remove((Entity)(object)((IPlayer)player).Entity, CarrySlot.Hands);
				AssetLocation val7 = (AssetLocation)(((object)((val2 != null) ? val2.Sounds.Place : null)) ?? ((object)new AssetLocation("sounds/player/build")));
				CarrySystem.Api.World.PlaySoundAt(val7, entityById, (IPlayer)null, true, 16f, 1f);
			}
			else
			{
				CarrySystem.ServerAPI.SendIngameError(player, "attach-failed", Lang.Get(CarrySystem.ModId + ":attach-failed", Array.Empty<object>()), Array.Empty<object>());
			}
		}

		public static ITreeAttribute ConvertBlockInventoryToBackpack(ITreeAttribute blockInventory)
		{
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_000f: Expected O, but got Unknown
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002e: Expected O, but got Unknown
			//IL_0003: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Expected O, but got Unknown
			if (blockInventory == null)
			{
				return (ITreeAttribute)new TreeAttribute();
			}
			TreeAttribute val = new TreeAttribute();
			int asInt = blockInventory.GetAsInt("qslots", 0);
			ITreeAttribute treeAttribute = blockInventory.GetTreeAttribute("slots");
			TreeAttribute val2 = new TreeAttribute();
			for (int i = 0; i < asInt; i++)
			{
				string text = "slot-" + i;
				ItemStack itemstack = treeAttribute.GetItemstack(i.ToString(), (ItemStack)null);
				val2.SetItemstack(text, itemstack);
			}
			val.SetAttribute("slots", (IAttribute)(object)val2);
			return (ITreeAttribute)(object)val;
		}

		public static IAttribute ConvertBackpackToBlockInventory(ITreeAttribute backpack)
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0006: Expected O, but got Unknown
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
			//IL_003d: Expected O, but got Unknown
			TreeAttribute val = new TreeAttribute();
			if (backpack != null)
			{
				if (backpack.Count == 0)
				{
					return (IAttribute)(object)val;
				}
				IAttribute obj = backpack["slots"];
				ITreeAttribute val2 = (ITreeAttribute)(object)((obj is ITreeAttribute) ? obj : null);
				int num = ((IEnumerable<KeyValuePair<string, IAttribute>>)val2).Count();
				val.SetInt("qslots", num);
				TreeAttribute val3 = new TreeAttribute();
				for (int i = 0; i < num; i++)
				{
					IAttribute val4 = val2.Values[i];
					if (val4 != null && val4.GetValue() != null)
					{
						val3.SetAttribute(i.ToString(), val4.Clone());
					}
				}
				val.SetAttribute("slots", (IAttribute)(object)val3);
			}
			return (IAttribute)(object)val;
		}

		public void OnDetachMessage(IServerPlayer player, DetachMessage message)
		{
			//IL_0378: Unknown result type (might be due to invalid IL or missing references)
			//IL_037f: Expected O, but got Unknown
			//IL_043b: Unknown result type (might be due to invalid IL or missing references)
			Entity entityById = CarrySystem.Api.World.GetEntityById(message.TargetEntityId);
			if (entityById == null)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "entity-not-found", Lang.Get(CarrySystem.ModId + ":entity-not-found", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			EntityPos sidedPos = entityById.SidedPos;
			if (((sidedPos != null) ? new double?(sidedPos.DistanceTo(((Entity)((IPlayer)player).Entity).Pos)) : ((double?)null)) > (double)MaxInteractionDistance)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "entity-out-of-reach", Lang.Get(CarrySystem.ModId + ":entity-out-of-reach", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			EntityBehaviorAttachable behavior = entityById.GetBehavior<EntityBehaviorAttachable>();
			if (behavior == null)
			{
				return;
			}
			ItemSlot slotFromSelectionBoxIndex = behavior.GetSlotFromSelectionBoxIndex(message.SlotIndex);
			if (slotFromSelectionBoxIndex == null || slotFromSelectionBoxIndex.Empty)
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-empty", Lang.Get(CarrySystem.ModId + ":slot-empty", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			if (slotFromSelectionBoxIndex == null || !slotFromSelectionBoxIndex.CanTake())
			{
				CarrySystem.ServerAPI.SendIngameError(player, "detach-unavailable", Lang.Get(CarrySystem.ModId + ":detach-unavailable", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			object obj;
			if (slotFromSelectionBoxIndex == null)
			{
				obj = null;
			}
			else
			{
				ItemStack itemstack = slotFromSelectionBoxIndex.Itemstack;
				obj = ((itemstack != null) ? itemstack.Block : null);
			}
			Block val = (Block)obj;
			if (val == null)
			{
				return;
			}
			if (!((CollectibleObject)val).HasBehavior<BlockBehaviorCarryable>(false))
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-not-carryable", Lang.Get(CarrySystem.ModId + ":slot-not-carryable", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			string inventoryName = $"mountedbaginv-{message.SlotIndex}-{message.TargetEntityId}";
			if ((from serverPlayer in CarrySystem.Api.World.AllOnlinePlayers.OfType<IServerPlayer>()
				where ((IPlayer)serverPlayer).PlayerUID != ((IPlayer)player).PlayerUID
				select serverPlayer).SelectMany((IServerPlayer serverPlayer) => ((IPlayer)serverPlayer).InventoryManager.OpenedInventories).Any((IInventory inv) => inv.InventoryID.StartsWith(inventoryName)))
			{
				CarrySystem.ServerAPI.SendIngameError(player, "slot-inventory-open", Lang.Get(CarrySystem.ModId + ":slot-inventory-open", Array.Empty<object>()), Array.Empty<object>());
				return;
			}
			IServerPlayer obj2 = player;
			if (((obj2 == null) ? null : ((Entity)(object)((IPlayer)obj2).Entity)?.GetCarried(CarrySlot.Hands)) != null)
			{
				return;
			}
			ItemStack val2 = ((slotFromSelectionBoxIndex != null) ? slotFromSelectionBoxIndex.Itemstack : null);
			object obj3;
			if (val2 == null)
			{
				obj3 = null;
			}
			else
			{
				ITreeAttribute attributes = val2.Attributes;
				obj3 = ((attributes != null) ? attributes["backpack"] : null);
			}
			IAttribute val3 = ConvertBackpackToBlockInventory((ITreeAttribute)((obj3 is ITreeAttribute) ? obj3 : null));
			object obj4;
			if (val2 == null)
			{
				obj4 = null;
			}
			else
			{
				ITreeAttribute attributes2 = val2.Attributes;
				obj4 = ((attributes2 != null) ? attributes2["carryonbackup"] : null);
			}
			TreeAttribute val4 = (TreeAttribute)((obj4 is TreeAttribute) ? obj4 : null);
			TreeAttribute val5 = (TreeAttribute)((val4 != null) ? ((object)val4) : ((object)new TreeAttribute()));
			val5.SetString("blockCode", ((RegistryObject)val).Code.ToShortString());
			val5.SetAttribute("inventory", val3);
			val5.SetString("forBlockCode", ((RegistryObject)val).Code.ToShortString());
			val5.SetString("type", val2.Attributes.GetString("type", (string)null));
			ItemStack val6 = val2.Clone();
			((IAttribute)(object)val6.Attributes).Remove("backpack");
			new CarriedBlock(CarrySlot.Hands, val6, (ITreeAttribute)(object)val5).Set((Entity)(object)((IPlayer)player).Entity, CarrySlot.Hands);
			AssetLocation val7 = (AssetLocation)(((object)((val != null) ? val.Sounds.Place : null)) ?? ((object)new AssetLocation("sounds/player/build")));
			CarrySystem.Api.World.PlaySoundAt(val7, entityById, (IPlayer)null, true, 16f, 1f);
			if (val2 != null)
			{
				IAttachedListener collectibleInterface = val2.Collectible.GetCollectibleInterface<IAttachedListener>();
				if (collectibleInterface != null)
				{
					collectibleInterface.OnDetached(slotFromSelectionBoxIndex, message.SlotIndex, entityById, (EntityAgent)(object)((IPlayer)player).Entity);
				}
			}
			EntityBehaviorAttachableCarryable.ClearCachedSlotStorage(CarrySystem.Api, message.SlotIndex, slotFromSelectionBoxIndex, entityById);
			slotFromSelectionBoxIndex.Itemstack = null;
			((EntityBehaviorContainer)behavior).storeInv();
			entityById.MarkShapeModified();
			entityById.World.BlockAccessor.GetChunkAtBlockPos(entityById.ServerPos.AsBlockPos).MarkModified();
		}

		public void OnQuickDropMessage(IServerPlayer player, QuickDropMessage message)
		{
			CarrySlot[] slots = new CarrySlot[2]
			{
				CarrySlot.Hands,
				CarrySlot.Shoulder
			};
			((Entity)(object)((IPlayer)player).Entity).DropCarried(slots, 1, 2);
		}

		public bool CanDoCarryAction(EntityAgent entityAgent, bool requireEmptyHanded)
		{
			if ((!entityAgent.RightHandItemSlot.Empty || !entityAgent.LeftHandItemSlot.Empty) && requireEmptyHanded)
			{
				return false;
			}
			EntityPlayer val = (EntityPlayer)(object)((entityAgent is EntityPlayer) ? entityAgent : null);
			if (val == null)
			{
				return true;
			}
			int activeHotbarSlotNumber = val.Player.InventoryManager.ActiveHotbarSlotNumber;
			if (activeHotbarSlotNumber >= 0)
			{
				return activeHotbarSlotNumber < 10;
			}
			return false;
		}

		public bool CanInteract(EntityAgent entityAgent, bool requireEmptyHanded)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Invalid comparison between Unknown and I4
			if ((int)((Entity)entityAgent).Api.Side == 2 && !IsCarryKeyPressed(checkMouse: true))
			{
				return false;
			}
			return CanDoCarryAction(entityAgent, requireEmptyHanded);
		}

		public bool PlaceDown(IPlayer player, CarriedBlock carried, BlockSelection selection, out BlockPos placedAt, ref string failureCode)
		{
			Block block = ((Entity)player.Entity).World.BlockAccessor.GetBlock(selection.Position);
			selection = selection.Clone();
			if (block.IsReplacableBy(carried.Block))
			{
				selection.Face = BlockFacing.UP;
				selection.HitPosition.Y = 0.5;
			}
			else
			{
				selection.Position.Offset(selection.Face);
				selection.DidOffset = true;
			}
			placedAt = selection.Position;
			return player.PlaceCarried(selection, carried.Slot, ref failureCode);
		}

		private void InvalidCarry(IServerPlayer player, BlockPos pos)
		{
			((Entity)((IPlayer)player).Entity).World.BlockAccessor.MarkBlockDirty(pos, (IPlayer)null);
			((Entity)((IPlayer)player).Entity).WatchedAttributes.MarkPathDirty(CarriedBlock.AttributeId);
			((Entity)((IPlayer)player).Entity).WatchedAttributes.MarkPathDirty("stats/walkspeed");
			SendLockSlotsMessage(player);
		}

		private static BlockPos GetPlacedPosition(IWorldAccessor world, BlockSelection selection, Block block)
		{
			if (selection == null)
			{
				return null;
			}
			BlockPos val = selection.Position.Copy();
			if (!world.BlockAccessor.GetBlock(val).IsReplacableBy(block))
			{
				val.Offset(selection.Face);
				if (!world.BlockAccessor.GetBlock(val).IsReplacableBy(block))
				{
					return null;
				}
			}
			return val;
		}

		private BlockPos GetMultiblockOrigin(BlockPos position, BlockMultiblock multiblock)
		{
			if (position == (BlockPos)null)
			{
				return null;
			}
			if (multiblock != null)
			{
				BlockPos obj = position.Copy();
				obj.Add(multiblock.OffsetInv);
				return obj;
			}
			return position;
		}

		private BlockSelection GetMultiblockOriginSelection(BlockSelection blockSelection)
		{
			Block obj = blockSelection?.Block;
			BlockMultiblock val = (BlockMultiblock)(object)((obj is BlockMultiblock) ? obj : null);
			if (val != null)
			{
				IWorldAccessor world = CarrySystem.Api.World;
				BlockPos multiblockOrigin = GetMultiblockOrigin(blockSelection.Position, val);
				Block block = world.BlockAccessor.GetBlock(multiblockOrigin);
				BlockSelection obj2 = blockSelection.Clone();
				obj2.Position = multiblockOrigin;
				obj2.Block = block;
				return obj2;
			}
			return blockSelection;
		}

		private bool SelectionPreventsSwap(BlockSelection selection)
		{
			if (selection?.Block == null)
			{
				return false;
			}
			Block block = selection.Block;
			if (preventSwapFromBackOnClasses != null && preventSwapFromBackOnClasses.Length != 0)
			{
				string a = ((RegistryObject)block).Class ?? "";
				string[] array = preventSwapFromBackOnClasses;
				foreach (string text in array)
				{
					if (!string.IsNullOrEmpty(text) && string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			if (preventSwapFromBackOnBehaviors != null && preventSwapFromBackOnBehaviors.Length != 0)
			{
				Type[] array2 = preventSwapFromBackOnBehaviors;
				foreach (Type type in array2)
				{
					if (!(type == null) && ((CollectibleObject)block).HasBehavior(type, false))
					{
						return true;
					}
				}
			}
			return false;
		}
	}
	public class CarryInteraction
	{
		public CarryAction CarryAction { get; set; }

		public float TimeHeld { get; set; }

		public int? TargetSlotIndex { get; set; }

		public CarrySlot? CarrySlot { get; set; }

		public BlockPos TargetBlockPos { get; set; }

		public ItemSlot Slot { get; set; }

		public Entity TargetEntity { get; set; }

		public void Complete()
		{
			Clear();
			CarryAction = CarryAction.Done;
		}

		public void Clear(bool resetTimeHeld = false)
		{
			CarryAction = CarryAction.None;
			Slot = null;
			CarrySlot = null;
			TargetSlotIndex = null;
			TargetEntity = null;
			TargetBlockPos = null;
			if (resetTimeHeld)
			{
				TimeHeld = 0f;
			}
		}
	}
	public class DeathHandler
	{
		public DeathHandler(ICoreServerAPI api)
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Expected O, but got Unknown
			api.Event.PlayerDeath += new PlayerDeathDelegate(OnPlayerDeath);
		}

		private void OnPlayerDeath(IPlayer player, DamageSource source)
		{
			EntityServerProperties server = ((Entity)player.Entity).Properties.Server;
			if (server != null)
			{
				ITreeAttribute attributes = ((EntitySidedProperties)server).Attributes;
				if (((attributes != null) ? new bool?(attributes.GetBool("keepContents", false)) : ((bool?)null)) == true)
				{
					return;
				}
			}
			((Entity)(object)player.Entity).DropAllCarried();
		}
	}
	public class EntityBehaviorAttachableCarryable : EntityBehavior, ICustomInteractionHelpPositioning
	{
		public readonly ICoreAPI Api;

		private EntityBehaviorAttachable _behaviorAttachable;

		public static string Name { get; } = CarrySystem.ModId + ":attachablecarryable";

		public bool TransparentCenter => false;

		public EntityBehaviorAttachableCarryable(Entity entity)
			: base(entity)
		{
			Api = entity.World.Api;
		}

		public int GetSlotIndex(int selBoxIndex)
		{
			if (selBoxIndex <= 0)
			{
				return 0;
			}
			if (_behaviorAttachable == null)
			{
				_behaviorAttachable = base.entity.GetBehavior<EntityBehaviorAttachable>();
			}
			EntityBehaviorAttachable behaviorAttachable = _behaviorAttachable;
			if (behaviorAttachable == null)
			{
				return 0;
			}
			return behaviorAttachable.GetSlotIndexFromSelectionBoxIndex(selBoxIndex - 1);
		}

		public ItemSlot GetItemSlot(int slotIndex)
		{
			if (slotIndex >= 0)
			{
				EntityBehaviorAttachable behaviorAttachable = _behaviorAttachable;
				if (slotIndex < ((behaviorAttachable != null) ? new int?(((EntityBehaviorContainer)behaviorAttachable).Inventory.Count) : ((int?)null)))
				{
					EntityBehaviorAttachable behaviorAttachable2 = _behaviorAttachable;
					if (behaviorAttachable2 == null)
					{
						return null;
					}
					return ((EntityBehaviorContainer)behaviorAttachable2).Inventory[slotIndex];
				}
			}
			return null;
		}

		public bool IsItemSlotEmpty(ItemSlot itemSlot)
		{
			if (itemSlot != null)
			{
				return itemSlot.Empty;
			}
			return false;
		}

		public override string PropertyName()
		{
			return Name;
		}

		public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player, ref EnumHandling handled)
		{
			//IL_009b: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00df: Expected O, but got Unknown
			if (es.SelectionBoxIndex == 0)
			{
				return null;
			}
			EntityBehaviorAttachable behavior = es.Entity.GetBehavior<EntityBehaviorAttachable>();
			if (behavior == null)
			{
				return null;
			}
			int num = es.SelectionBoxIndex - 1;
			ItemSlot slotFromSelectionBoxIndex = behavior.GetSlotFromSelectionBoxIndex(num);
			if (slotFromSelectionBoxIndex == null)
			{
				return null;
			}
			List<ItemStack> interactionItemStacks = AttachableCarryableInteractionHelp.GetInteractionItemStacks(Api, es.Entity, num, slotFromSelectionBoxIndex);
			string text = null;
			if (!slotFromSelectionBoxIndex.Empty)
			{
				Block block = slotFromSelectionBoxIndex.Itemstack.Block;
				if (((block != null) ? ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryable>() : null) != null)
				{
					text = CarrySystem.ModId + ":blockhelp-detach";
				}
			}
			else
			{
				text = CarrySystem.ModId + ":blockhelp-attach";
			}
			if (text == null)
			{
				return null;
			}
			return (WorldInteraction[])(object)new WorldInteraction[1]
			{
				new WorldInteraction
				{
					ActionLangCode = text,
					Itemstacks = ((!slotFromSelectionBoxIndex.Empty) ? null : interactionItemStacks?.ToArray()),
					MouseButton = (EnumMouseButton)2,
					HotKeyCode = "carryonpickupkey",
					RequireFreeHand = true
				}
			};
		}

		public Vec3d GetInteractionHelpPosition()
		{
			ICoreAPI api = base.entity.Api;
			ICoreClientAPI val = (ICoreClientAPI)(object)((api is ICoreClientAPI) ? api : null);
			if (((IPlayer)val.World.Player).CurrentEntitySelection == null)
			{
				return null;
			}
			int num = ((IPlayer)val.World.Player).CurrentEntitySelection.SelectionBoxIndex - 1;
			if (num < 0)
			{
				return null;
			}
			Vec3d centerPosOfBox = base.entity.GetBehavior<EntityBehaviorSelectionBoxes>().GetCenterPosOfBox(num);
			if (centerPosOfBox == null)
			{
				return null;
			}
			return centerPosOfBox.Add(0.0, 0.5, 0.0);
		}

		public void OnAttachmentToggled(bool isAttached, EntityAgent byEntity, ItemSlot itemslot, int targetSlotIndex)
		{
			object obj;
			if (itemslot == null)
			{
				obj = null;
			}
			else
			{
				ItemStack itemstack = itemslot.Itemstack;
				if (itemstack == null)
				{
					obj = null;
				}
				else
				{
					CollectibleObject collectible = itemstack.Collectible;
					obj = ((collectible != null) ? collectible.GetCollectibleInterface<IAttachedListener>() : null);
				}
			}
			IAttachedListener val = (IAttachedListener)obj;
			if (val != null)
			{
				if (isAttached)
				{
					val.OnAttached(itemslot, targetSlotIndex, base.entity, byEntity);
				}
				else
				{
					val.OnDetached(itemslot, targetSlotIndex, base.entity, byEntity);
				}
			}
			base.entity.MarkShapeModified();
			base.entity.World.BlockAccessor.GetChunkAtBlockPos(base.entity.ServerPos.AsBlockPos).MarkModified();
			if (!isAttached)
			{
				ClearCachedSlotStorage(Api, targetSlotIndex, itemslot, base.entity);
			}
		}

		public static void ClearCachedSlotStorage(ICoreAPI api, int slotIndex, ItemSlot slot, Entity targetEntity)
		{
			if (slotIndex >= 0 && targetEntity != null && ((slot != null) ? slot.Itemstack : null) != null)
			{
				ObjectCacheUtil.Delete(api, "att-cont-workspace-" + slotIndex + "-" + targetEntity.EntityId + "-" + slot.Itemstack.Id);
			}
		}
	}
	public class AttachableCarryableInteractionHelp
	{
		public static List<ItemStack> GetInteractionItemStacks(ICoreAPI api, Entity entity, int slotIndex, ItemSlot slot)
		{
			string text = "carryable-stack-" + AssetLocation.op_Implicit(((RegistryObject)entity).Code) + "-" + slotIndex;
			List<ItemStack> list = ObjectCacheUtil.TryGet<List<ItemStack>>(api, text);
			if (list == null)
			{
				string text2 = "interactionhelp-attachable-" + AssetLocation.op_Implicit(((RegistryObject)entity).Code) + "-" + slotIndex;
				List<ItemStack> attachableStacks = ObjectCacheUtil.TryGet<List<ItemStack>>(api, text2);
				if (attachableStacks != null)
				{
					List<ItemStack> list2 = attachableStacks;
					if (list2 == null || list2.Count != 0)
					{
						list = ObjectCacheUtil.GetOrCreate<List<ItemStack>>(api, text, (CreateCachableObjectDelegate<List<ItemStack>>)delegate
						{
							List<ItemStack> list3 = new List<ItemStack>();
							foreach (ItemStack item in attachableStacks)
							{
								if (item.Block != null && item.Block.IsCarryable())
								{
									list3.Add(item.Clone());
								}
							}
							return list3;
						});
						goto IL_0097;
					}
				}
				return null;
			}
			goto IL_0097;
			IL_0097:
			return list;
		}
	}
}
namespace CarryOn.Common.Network
{
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class AttachMessage
	{
		public long TargetEntityId { get; }

		public int SlotIndex { get; }

		private AttachMessage()
		{
		}

		public AttachMessage(long targetEntityId, int slotIndex)
		{
			TargetEntityId = targetEntityId;
			SlotIndex = slotIndex;
		}
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class DetachMessage
	{
		public long TargetEntityId { get; }

		public int SlotIndex { get; }

		private DetachMessage()
		{
		}

		public DetachMessage(long targetEntityId, int slotIndex)
		{
			TargetEntityId = targetEntityId;
			SlotIndex = slotIndex;
		}
	}
	[ProtoContract]
	public class DismountMessage
	{
		[ProtoMember(1)]
		public long EntityId { get; set; }

		[ProtoMember(2)]
		public string SeatId { get; set; }
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class InteractMessage
	{
		public BlockPos Position { get; }

		private InteractMessage()
		{
		}

		public InteractMessage(BlockPos position)
		{
			Position = position;
		}
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class LockSlotsMessage
	{
		public List<int> HotbarSlots { get; }

		private LockSlotsMessage()
		{
		}

		public LockSlotsMessage(List<int> hotbarSlots)
		{
			HotbarSlots = hotbarSlots;
		}
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class PickUpMessage
	{
		public BlockPos Position { get; }

		public CarrySlot Slot { get; }

		private PickUpMessage()
		{
		}

		public PickUpMessage(BlockPos position, CarrySlot slot)
		{
			Position = position;
			Slot = slot;
		}
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class PlaceDownMessage
	{
		private readonly BlockPos _pos;

		private readonly byte _face;

		private readonly float _x;

		private readonly float _y;

		private readonly float _z;

		public CarrySlot Slot { get; }

		public BlockSelection Selection => new BlockSelection
		{
			Position = _pos,
			Face = BlockFacing.ALLFACES[_face],
			HitPosition = new Vec3d((double)_x, (double)_y, (double)_z)
		};

		public BlockPos PlacedAt { get; }

		private PlaceDownMessage()
		{
		}

		public PlaceDownMessage(CarrySlot slot, BlockSelection selection, BlockPos placedAt)
		{
			Slot = slot;
			_pos = selection.Position;
			_face = (byte)selection.Face.Index;
			_x = (float)selection.HitPosition.X;
			_y = (float)selection.HitPosition.Y;
			_z = (float)selection.HitPosition.Z;
			PlacedAt = placedAt;
		}
	}
	[ProtoContract]
	public class PlayerAttributeUpdateMessage
	{
		[ProtoMember(1)]
		public string AttributeKey { get; set; }

		[ProtoMember(2)]
		public bool? BoolValue { get; set; }

		[ProtoMember(3)]
		public bool IsWatchedAttribute { get; set; }

		public PlayerAttributeUpdateMessage()
		{
		}

		public PlayerAttributeUpdateMessage(string attributeKey, bool value, bool isWatchedAttribute = false)
		{
			AttributeKey = attributeKey;
			BoolValue = value;
			IsWatchedAttribute = isWatchedAttribute;
		}
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class QuickDropMessage
	{
	}
	[ProtoContract(/*Could not decode attribute arguments.*/)]
	public class SwapSlotsMessage
	{
		public CarrySlot First { get; }

		public CarrySlot Second { get; }

		private SwapSlotsMessage()
		{
		}

		public SwapSlotsMessage(CarrySlot first, CarrySlot second)
		{
			if (first == second)
			{
				throw new ArgumentException("Slots can't be the same");
			}
			First = first;
			Second = second;
		}
	}
}
namespace CarryOn.Client
{
	public class AnimationFixer
	{
		private HashSet<string> _previous = new HashSet<string>();

		public void Update(EntityPlayer player)
		{
			HashSet<string> hashSet = new HashSet<string>(from carried in ((Entity)(object)player).GetCarried()
				select carried.Behavior?.Slots[carried.Slot]?.Animation into animation
				where animation != null
				select animation);
			IEnumerable<string> enumerable = hashSet.Except(_previous);
			IEnumerable<string> enumerable2 = _previous.Except(hashSet);
			foreach (string item in enumerable)
			{
				((Entity)player).StartAnimation(item);
			}
			foreach (string item2 in enumerable2)
			{
				((Entity)player).StopAnimation(item2);
			}
			_previous = hashSet;
		}
	}
	public class Commands
	{
		private readonly CarrySystem carrySystem;

		private readonly ICoreClientAPI api;

		public Commands(CarrySystem carrySystem)
		{
			if (carrySystem == null)
			{
				throw new ArgumentNullException("carrySystem");
			}
			this.carrySystem = carrySystem;
			api = carrySystem.ClientAPI;
		}

		public void Register()
		{
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Expected O, but got Unknown
			//IL_0078: Unknown result type (might be due to invalid IL or missing references)
			//IL_0082: Expected O, but got Unknown
			//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d4: Expected O, but got Unknown
			//IL_011c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0126: Expected O, but got Unknown
			//IL_0146: Unknown result type (might be due to invalid IL or missing references)
			//IL_0150: Expected O, but got Unknown
			//IL_0170: Unknown result type (might be due to invalid IL or missing references)
			//IL_017a: Expected O, but got Unknown
			//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
			//IL_01bd: Expected O, but got Unknown
			//IL_01dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e7: Expected O, but got Unknown
			//IL_022f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0239: Expected O, but got Unknown
			//IL_0281: Unknown result type (might be due to invalid IL or missing references)
			//IL_028b: Expected O, but got Unknown
			//IL_02ab: Unknown result type (might be due to invalid IL or missing references)
			//IL_02b5: Expected O, but got Unknown
			//IL_02d5: Unknown result type (might be due to invalid IL or missing references)
			//IL_02df: Expected O, but got Unknown
			//IL_0318: Unknown result type (might be due to invalid IL or missing references)
			//IL_0322: Expected O, but got Unknown
			//IL_0342: Unknown result type (might be due to invalid IL or missing references)
			//IL_034c: Expected O, but got Unknown
			//IL_0394: Unknown result type (might be due to invalid IL or missing references)
			//IL_039e: Expected O, but got Unknown
			//IL_03e6: Unknown result type (might be due to invalid IL or missing references)
			//IL_03f0: Expected O, but got Unknown
			//IL_0410: Unknown result type (might be due to invalid IL or missing references)
			//IL_041a: Expected O, but got Unknown
			//IL_043a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0444: Expected O, but got Unknown
			//IL_0469: Unknown result type (might be due to invalid IL or missing references)
			//IL_0473: Expected O, but got Unknown
			//IL_0493: Unknown result type (might be due to invalid IL or missing references)
			//IL_049d: Expected O, but got Unknown
			//IL_0502: Unknown result type (might be due to invalid IL or missing references)
			//IL_050c: Expected O, but got Unknown
			try
			{
				((ICoreAPI)api).ChatCommands.Create("carryon").BeginSubCommand("gui").BeginSubCommand("bg")
					.WithDescription("Configure anchor background fill (enable/disable/color/alpha/show)")
					.BeginSubCommand("enable")
					.WithDescription("Enable anchor background fill")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgEnable))
					.EndSubCommand()
					.BeginSubCommand("disable")
					.WithDescription("Disable anchor background fill")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgDisable))
					.EndSubCommand()
					.BeginSubCommand("color")
					.WithDescription("Set anchor background fill color as hex (e.g. #e4c4a6)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Word("hex") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgColor))
					.EndSubCommand()
					.BeginSubCommand("alpha")
					.WithDescription("Set anchor background alpha (0.0 - 1.0)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Float("alpha") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgAlpha))
					.EndSubCommand()
					.BeginSubCommand("show")
					.WithDescription("Show current anchor background settings (runtime and saved)")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgShow))
					.EndSubCommand()
					.BeginSubCommand("reset")
					.WithDescription("Reset anchor background to defaults (enabled, color, alpha)")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBgReset))
					.EndSubCommand()
					.EndSubCommand()
					.BeginSubCommand("border")
					.WithDescription("Configure anchor border outline (enable/disable/color/alpha/show/reset)")
					.BeginSubCommand("enable")
					.WithDescription("Enable anchor border outline")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderEnable))
					.EndSubCommand()
					.BeginSubCommand("disable")
					.WithDescription("Disable anchor border outline")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderDisable))
					.EndSubCommand()
					.BeginSubCommand("color")
					.WithDescription("Set anchor border color as hex (e.g. #45372D)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Word("hex") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderColor))
					.EndSubCommand()
					.BeginSubCommand("alpha")
					.WithDescription("Set anchor border alpha (0.0 - 1.0)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Float("alpha") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderAlpha))
					.EndSubCommand()
					.BeginSubCommand("reset")
					.WithDescription("Reset anchor border to defaults")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderReset))
					.EndSubCommand()
					.BeginSubCommand("show")
					.WithDescription("Show current anchor border settings (runtime and saved)")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiBorderShow))
					.EndSubCommand()
					.EndSubCommand()
					.BeginSubCommand("highlight")
					.WithDescription("Configure icon highlight (enable/disable/color/alpha/show/reset)")
					.BeginSubCommand("enable")
					.WithDescription("Enable icon highlight")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightEnable))
					.EndSubCommand()
					.BeginSubCommand("disable")
					.WithDescription("Disable icon highlight")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightDisable))
					.EndSubCommand()
					.BeginSubCommand("color")
					.WithDescription("Set icon highlight color as hex (e.g. #FFFFFF)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Word("hex") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightColor))
					.EndSubCommand()
					.BeginSubCommand("alpha")
					.WithDescription("Set icon highlight alpha (0.0 - 1.0)")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[1] { (ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Float("alpha") })
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightAlpha))
					.EndSubCommand()
					.BeginSubCommand("reset")
					.WithDescription("Reset icon highlight to defaults")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightReset))
					.EndSubCommand()
					.BeginSubCommand("show")
					.WithDescription("Show current icon highlight settings (runtime and saved)")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiHighlightShow))
					.EndSubCommand()
					.EndSubCommand()
					.BeginSubCommand("show")
					.WithDescription("Show current CarryOn GUI anchor assignments")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiShow))
					.EndSubCommand()
					.BeginSubCommand("reset")
					.WithDescription("Reset CarryOn GUI anchors to defaults (Back -> R1, Hands -> empty)")
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiReset))
					.EndSubCommand()
					.BeginSubCommand("set")
					.WithDescription("Set or clear carry slot anchors. Usage: .carryon gui set L1 hands | R2 back | R1 clear")
					.WithArgs((ICommandArgumentParser[])(object)new ICommandArgumentParser[2]
					{
						(ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Word("anchor"),
						(ICommandArgumentParser)((ICoreAPI)api).ChatCommands.Parsers.Word("slot")
					})
					.HandleWith(new OnCommandDelegate(CmdCarryOnGuiSet))
					.EndSubCommand()
					.EndSubCommand();
			}
			catch (Exception ex)
			{
				((IWorldAccessor)api.World).Logger.Warning("CarryOn: Failed to register client chat command for GUI debug: " + ex.Message);
			}
		}

		protected TextCommandResult CmdCarryOnGuiToggle(TextCommandCallingArgs args)
		{
			HudCarried.ShowDebugIcons = !HudCarried.ShowDebugIcons;
			string text = (HudCarried.ShowDebugIcons ? "enabled" : "disabled");
			string text2 = "CarryOn GUI debug icons " + text;
			try
			{
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch
			{
			}
			return TextCommandResult.Success(text2, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiSet(TextCommandCallingArgs args)
		{
			string value = ((string)args[0])?.ToUpperInvariant();
			string text = ((string)args[1])?.ToLowerInvariant();
			if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(text))
			{
				return TextCommandResult.Error("Usage: .carryon gui set L1 hands | R2 back | R1 clear", "");
			}
			if (!Enum.TryParse<HudCarried.Anchor>(value, ignoreCase: true, out var result))
			{
				return TextCommandResult.Error("Invalid anchor. Use L1,L2,L3,R1,R2,R3", "");
			}
			switch (text)
			{
			case "hands":
				if (HudCarried.HandsAnchor != HudCarried.Anchor.None)
				{
					if (HudCarried.HandsAnchor == result)
					{
						return TextCommandResult.Success($"Hands already at {result}", (object)null);
					}
					HudCarried.HandsAnchor = HudCarried.Anchor.None;
				}
				if (HudCarried.BackAnchor == result)
				{
					HudCarried.BackAnchor = HudCarried.Anchor.None;
				}
				HudCarried.HandsAnchor = result;
				try
				{
					if (carrySystem?.ClientConfig != null)
					{
						carrySystem.ClientConfig.Config.HandsAnchor = HudCarried.HandsAnchor.ToString();
						carrySystem.ClientConfig.Save(api);
					}
				}
				catch (Exception ex3)
				{
					((ICoreAPI)api).Logger.Error("Error moving Hands anchor: " + ex3);
					return TextCommandResult.Error("Failed to move Hands anchor due to an error.", "");
				}
				return TextCommandResult.Success($"Hands moved to {result}", (object)null);
			case "back":
				if (HudCarried.BackAnchor != HudCarried.Anchor.None)
				{
					if (HudCarried.BackAnchor == result)
					{
						return TextCommandResult.Success($"Back already at {result}", (object)null);
					}
					HudCarried.BackAnchor = HudCarried.Anchor.None;
				}
				if (HudCarried.HandsAnchor == result)
				{
					HudCarried.HandsAnchor = HudCarried.Anchor.None;
				}
				HudCarried.BackAnchor = result;
				try
				{
					if (carrySystem?.ClientConfig != null)
					{
						carrySystem.ClientConfig.Config.BackAnchor = HudCarried.BackAnchor.ToString();
						carrySystem.ClientConfig.Save(api);
					}
				}
				catch (Exception ex2)
				{
					((ICoreAPI)api).Logger.Error("Error moving Back anchor: " + ex2);
					return TextCommandResult.Error("Failed to move Back anchor due to an error.", "");
				}
				return TextCommandResult.Success($"Back moved to {result}", (object)null);
			case "clear":
			{
				bool flag = false;
				if (HudCarried.HandsAnchor == result)
				{
					HudCarried.HandsAnchor = HudCarried.Anchor.None;
					flag = true;
				}
				if (HudCarried.BackAnchor == result)
				{
					HudCarried.BackAnchor = HudCarried.Anchor.None;
					flag = true;
				}
				if (flag)
				{
					try
					{
						if (carrySystem?.ClientConfig != null)
						{
							carrySystem.ClientConfig.Config.HandsAnchor = HudCarried.HandsAnchor.ToString();
							carrySystem.ClientConfig.Config.BackAnchor = HudCarried.BackAnchor.ToString();
							carrySystem.ClientConfig.Save(api);
						}
					}
					catch (Exception ex)
					{
						((ICoreAPI)api).Logger.Error("Error clearing anchor: " + ex);
						return TextCommandResult.Error("Failed to clear anchor due to an error.", "");
					}
					return TextCommandResult.Success($"Cleared anchor {result}", (object)null);
				}
				return TextCommandResult.Error($"Anchor {result} was already empty", "");
			}
			default:
				return TextCommandResult.Error("Invalid slot. Use 'hands', 'back', or 'clear'", "");
			}
		}

		protected TextCommandResult CmdCarryOnGuiReset(TextCommandCallingArgs args)
		{
			HudCarried.HandsAnchor = HudCarried.HandsAnchorDefault;
			HudCarried.BackAnchor = HudCarried.BackAnchorDefault;
			HudCarried.AnchorBackgroundEnabled = true;
			HudCarried.AnchorBackgroundColor = HudCarried.AnchorBackgroundColorDefault;
			HudCarried.AnchorBackgroundAlpha = HudCarried.AnchorBackgroundAlphaDefault;
			HudCarried.AnchorBorderEnabled = true;
			HudCarried.AnchorBorderColor = HudCarried.AnchorBorderColorDefault;
			HudCarried.AnchorBorderAlpha = HudCarried.AnchorBorderAlphaDefault;
			HudCarried.IconHighlightEnabled = true;
			HudCarried.IconHighlightColor = HudCarried.IconHighlightColorDefault;
			HudCarried.IconHighlightAlpha = HudCarried.IconHighlightAlphaDefault;
			try
			{
				CarryOnClientConfig carryOnClientConfig = carrySystem?.ClientConfig?.Config;
				if (carryOnClientConfig != null)
				{
					carryOnClientConfig.HandsAnchor = HudCarried.HandsAnchor.ToString();
					carryOnClientConfig.BackAnchor = HudCarried.BackAnchor.ToString();
					carryOnClientConfig.AnchorBackgroundEnabled = HudCarried.AnchorBackgroundEnabled;
					carryOnClientConfig.AnchorBackgroundColor = HudCarried.AnchorBackgroundColor;
					carryOnClientConfig.AnchorBackgroundAlpha = HudCarried.AnchorBackgroundAlpha;
					carryOnClientConfig.AnchorBorderEnabled = HudCarried.AnchorBorderEnabled;
					carryOnClientConfig.AnchorBorderColor = HudCarried.AnchorBorderColor;
					carryOnClientConfig.AnchorBorderAlpha = HudCarried.AnchorBorderAlpha;
					carryOnClientConfig.IconHighlightEnabled = HudCarried.IconHighlightEnabled;
					carryOnClientConfig.IconHighlightColor = HudCarried.IconHighlightColor;
					carryOnClientConfig.IconHighlightAlpha = HudCarried.IconHighlightAlpha;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error resetting CarryOn GUI anchors: " + ex);
				return TextCommandResult.Error("Failed to reset CarryOn GUI anchors due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn GUI anchors reset to defaults (Hands -> {HudCarried.HandsAnchor}, Back -> {HudCarried.BackAnchor})", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiShow(TextCommandCallingArgs args)
		{
			string text = HudCarried.HandsAnchor.ToString();
			string text2 = HudCarried.BackAnchor.ToString();
			string text3 = null;
			try
			{
				CarryOnClientConfig carryOnClientConfig = carrySystem?.ClientConfig?.Config;
				if (carryOnClientConfig != null)
				{
					text3 = "Saved: Hands=" + carryOnClientConfig.HandsAnchor + ", Back=" + carryOnClientConfig.BackAnchor;
				}
			}
			catch
			{
			}
			return TextCommandResult.Success("CarryOn GUI anchors — Runtime: Hands=" + text + ", Back=" + text2 + ((text3 != null) ? (" | " + text3) : ""), (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgEnable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBackgroundEnabled = true;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBackgroundEnabled = true;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error enabling CarryOn anchor background: " + ex);
				return TextCommandResult.Error("Failed to enable CarryOn anchor background due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor background: enabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgDisable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBackgroundEnabled = false;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBackgroundEnabled = false;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error disabling CarryOn anchor background: " + ex);
				return TextCommandResult.Error("Failed to disable CarryOn anchor background due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor background: disabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgColor(TextCommandCallingArgs args)
		{
			string text = ((string)args[0])?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return TextCommandResult.Error("Usage: .carryon gui bg color #rrggbb", "");
			}
			text = "#" + text.TrimStart('#').ToUpperInvariant();
			if (text.Length != 7)
			{
				return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb (6 hex digits)", "");
			}
			for (int i = 1; i < 7; i++)
			{
				char c = text[i];
				if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
				{
					return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb (6 hex digits)", "");
				}
			}
			try
			{
				HudCarried.AnchorBackgroundColor = text;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBackgroundColor = text;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn anchor background color: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn anchor background color due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor background color set to " + text, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgAlpha(TextCommandCallingArgs args)
		{
			float num = 0f;
			try
			{
				num = (float)args[0];
			}
			catch
			{
				return TextCommandResult.Error("Usage: .carryon gui bg alpha 0.0-1.0", "");
			}
			if (num < 0f || num > 1f)
			{
				return TextCommandResult.Error("Alpha must be between 0.0 and 1.0", "");
			}
			try
			{
				HudCarried.AnchorBackgroundAlpha = num;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBackgroundAlpha = num;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn anchor background alpha: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn anchor background alpha due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn anchor background alpha set to {num:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgShow(TextCommandCallingArgs args)
		{
			string text = $"Runtime: enabled={HudCarried.AnchorBackgroundEnabled}, color={HudCarried.AnchorBackgroundColor}, alpha={HudCarried.AnchorBackgroundAlpha:0.##}";
			string text2 = "Saved: (none)";
			try
			{
				CarryOnClientConfig carryOnClientConfig = carrySystem?.ClientConfig?.Config;
				if (carryOnClientConfig != null)
				{
					text2 = $"Saved: enabled={carryOnClientConfig.AnchorBackgroundEnabled}, color={carryOnClientConfig.AnchorBackgroundColor}, alpha={carryOnClientConfig.AnchorBackgroundAlpha:0.##}";
				}
			}
			catch
			{
			}
			return TextCommandResult.Success("CarryOn anchor background — " + text + " | " + text2, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBgReset(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBackgroundEnabled = true;
				HudCarried.AnchorBackgroundColor = HudCarried.AnchorBackgroundColorDefault;
				HudCarried.AnchorBackgroundAlpha = HudCarried.AnchorBackgroundAlphaDefault;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBackgroundEnabled = HudCarried.AnchorBackgroundEnabled;
					carrySystem.ClientConfig.Config.AnchorBackgroundColor = HudCarried.AnchorBackgroundColor;
					carrySystem.ClientConfig.Config.AnchorBackgroundAlpha = HudCarried.AnchorBackgroundAlpha;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error resetting CarryOn anchor background: " + ex);
				return TextCommandResult.Error("Failed to reset CarryOn anchor background due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn anchor background reset to defaults: enabled={HudCarried.AnchorBackgroundEnabled}, color={HudCarried.AnchorBackgroundColor}, alpha={HudCarried.AnchorBackgroundAlpha:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderEnable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBorderEnabled = true;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBorderEnabled = true;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error enabling CarryOn anchor border: " + ex);
				return TextCommandResult.Error("Failed to enable CarryOn anchor border due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor border: enabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderDisable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBorderEnabled = false;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBorderEnabled = false;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error disabling CarryOn anchor border: " + ex);
				return TextCommandResult.Error("Failed to disable CarryOn anchor border due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor border: disabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderColor(TextCommandCallingArgs args)
		{
			string text = ((string)args[0])?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return TextCommandResult.Error("Usage: .carryon gui border color #rrggbb", "");
			}
			text = "#" + text.TrimStart('#').ToUpperInvariant();
			if (text.Length != 7)
			{
				return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb", "");
			}
			for (int i = 1; i < 7; i++)
			{
				char c = text[i];
				if ((c < '0' || c > '9') && (c < 'A' || c > 'F'))
				{
					return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb", "");
				}
			}
			try
			{
				HudCarried.AnchorBorderColor = text;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBorderColor = text;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn anchor border color: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn anchor border color due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn anchor border color set to " + text, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderAlpha(TextCommandCallingArgs args)
		{
			float num = 0f;
			try
			{
				num = (float)args[0];
			}
			catch
			{
				return TextCommandResult.Error("Usage: .carryon gui border alpha 0.0-1.0", "");
			}
			if (num < 0f || num > 1f)
			{
				return TextCommandResult.Error("Alpha must be between 0.0 and 1.0", "");
			}
			try
			{
				HudCarried.AnchorBorderAlpha = num;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBorderAlpha = num;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn anchor border alpha: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn anchor border alpha due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn anchor border alpha set to {num:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderReset(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.AnchorBorderEnabled = true;
				HudCarried.AnchorBorderColor = HudCarried.AnchorBorderColorDefault;
				HudCarried.AnchorBorderAlpha = HudCarried.AnchorBorderAlphaDefault;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.AnchorBorderEnabled = HudCarried.AnchorBorderEnabled;
					carrySystem.ClientConfig.Config.AnchorBorderColor = HudCarried.AnchorBorderColor;
					carrySystem.ClientConfig.Config.AnchorBorderAlpha = HudCarried.AnchorBorderAlpha;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error resetting CarryOn anchor border: " + ex);
				return TextCommandResult.Error("Failed to reset CarryOn anchor border due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn anchor border reset to defaults: enabled={HudCarried.AnchorBorderEnabled}, color={HudCarried.AnchorBorderColor}, alpha={HudCarried.AnchorBorderAlpha:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiBorderShow(TextCommandCallingArgs args)
		{
			string text = $"Runtime: enabled={HudCarried.AnchorBorderEnabled}, color={HudCarried.AnchorBorderColor}, alpha={HudCarried.AnchorBorderAlpha:0.##}";
			string text2 = "Saved: (none)";
			try
			{
				CarryOnClientConfig carryOnClientConfig = carrySystem?.ClientConfig?.Config;
				if (carryOnClientConfig != null)
				{
					text2 = $"Saved: enabled={carryOnClientConfig.AnchorBorderEnabled}, color={carryOnClientConfig.AnchorBorderColor}, alpha={carryOnClientConfig.AnchorBorderAlpha:0.##}";
				}
			}
			catch
			{
			}
			return TextCommandResult.Success("CarryOn anchor border — " + text + " | " + text2, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightEnable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.IconHighlightEnabled = true;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.IconHighlightEnabled = true;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error enabling CarryOn icon highlight: " + ex);
				return TextCommandResult.Error("Failed to enable CarryOn icon highlight due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn icon highlight: enabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightDisable(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.IconHighlightEnabled = false;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.IconHighlightEnabled = false;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error disabling CarryOn icon highlight: " + ex);
				return TextCommandResult.Error("Failed to disable CarryOn icon highlight due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn icon highlight: disabled", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightColor(TextCommandCallingArgs args)
		{
			string text = ((string)args[0])?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return TextCommandResult.Error("Usage: .carryon gui highlight color #rrggbb", "");
			}
			text = "#" + text.TrimStart('#').ToUpperInvariant();
			if (text.Length != 7)
			{
				return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb", "");
			}
			for (int i = 1; i < 7; i++)
			{
				char c = text[i];
				if ((c < '0' || c > '9') && (c < 'A' || c > 'F'))
				{
					return TextCommandResult.Error("Invalid hex color. Expected format: #rrggbb", "");
				}
			}
			try
			{
				HudCarried.IconHighlightColor = text;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.IconHighlightColor = text;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn icon highlight color: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn icon highlight color due to an error.", "");
			}
			return TextCommandResult.Success("CarryOn icon highlight color set to " + text, (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightAlpha(TextCommandCallingArgs args)
		{
			float num = 0f;
			try
			{
				num = (float)args[0];
			}
			catch
			{
				return TextCommandResult.Error("Usage: .carryon gui highlight alpha 0.0-1.0", "");
			}
			if (num < 0f || num > 1f)
			{
				return TextCommandResult.Error("Alpha must be between 0.0 and 1.0", "");
			}
			try
			{
				HudCarried.IconHighlightAlpha = num;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.IconHighlightAlpha = num;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error setting CarryOn icon highlight alpha: " + ex);
				return TextCommandResult.Error("Failed to set CarryOn icon highlight alpha due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn icon highlight alpha set to {num:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightReset(TextCommandCallingArgs args)
		{
			try
			{
				HudCarried.IconHighlightEnabled = true;
				HudCarried.IconHighlightColor = HudCarried.IconHighlightColorDefault;
				HudCarried.IconHighlightAlpha = HudCarried.IconHighlightAlphaDefault;
				if (carrySystem?.ClientConfig != null)
				{
					carrySystem.ClientConfig.Config.IconHighlightEnabled = HudCarried.IconHighlightEnabled;
					carrySystem.ClientConfig.Config.IconHighlightColor = HudCarried.IconHighlightColor;
					carrySystem.ClientConfig.Config.IconHighlightAlpha = HudCarried.IconHighlightAlpha;
					carrySystem.ClientConfig.Save(api);
				}
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Error("Error resetting CarryOn icon highlight to defaults: " + ex);
				return TextCommandResult.Error("Failed to reset CarryOn icon highlight to defaults due to an error.", "");
			}
			return TextCommandResult.Success($"CarryOn icon highlight reset to defaults: enabled={HudCarried.IconHighlightEnabled}, color={HudCarried.IconHighlightColor}, alpha={HudCarried.IconHighlightAlpha:0.##}", (object)null);
		}

		protected TextCommandResult CmdCarryOnGuiHighlightShow(TextCommandCallingArgs args)
		{
			string text = $"Runtime: enabled={HudCarried.IconHighlightEnabled}, color={HudCarried.IconHighlightColor}, alpha={HudCarried.IconHighlightAlpha:0.##}";
			string text2 = "Saved: (none)";
			try
			{
				CarryOnClientConfig carryOnClientConfig = carrySystem?.ClientConfig?.Config;
				if (carryOnClientConfig != null)
				{
					text2 = $"Saved: enabled={carryOnClientConfig.IconHighlightEnabled}, color={carryOnClientConfig.IconHighlightColor}, alpha={carryOnClientConfig.IconHighlightAlpha:0.##}";
				}
			}
			catch
			{
			}
			return TextCommandResult.Success("CarryOn icon highlight — " + text + " | " + text2, (object)null);
		}
	}
	public class EntityCarryRenderer : IRenderer, IDisposable
	{
		private class SlotRenderSettings
		{
			public string AttachmentPoint { get; }

			public Vec3f Offset { get; }

			public SlotRenderSettings(string attachmentPoint, float xOffset, float yOffset, float zOffset)
			{
				//IL_0012: Unknown result type (might be due to invalid IL or missing references)
				//IL_001c: Expected O, but got Unknown
				AttachmentPoint = attachmentPoint;
				Offset = new Vec3f(xOffset, yOffset, zOffset);
			}
		}

		private static readonly Dictionary<CarrySlot, SlotRenderSettings> _renderSettings = new Dictionary<CarrySlot, SlotRenderSettings>
		{
			{
				CarrySlot.Hands,
				new SlotRenderSettings("carryon:FrontCarry", 0.05f, -0.5f, -0.5f)
			},
			{
				CarrySlot.Back,
				new SlotRenderSettings("Back", 0f, -0.6f, -0.5f)
			},
			{
				CarrySlot.Shoulder,
				new SlotRenderSettings("carryon:ShoulderL", -0.5f, 0f, -0.5f)
			}
		};

		private long _renderTick;

		private long _lastTickHandsRendered;

		private float _moveWobble;

		private float _lastYaw;

		private float _yawDifference;

		private ICoreClientAPI Api { get; }

		private AnimationFixer AnimationFixer { get; }

		public double RenderOrder => 1.0;

		public int RenderRange => 99;

		public EntityCarryRenderer(ICoreClientAPI api)
		{
			Api = api;
			Api.Event.RegisterRenderer((IRenderer)(object)this, (EnumRenderStage)1, (string)null);
			Api.Event.RegisterRenderer((IRenderer)(object)this, (EnumRenderStage)4, (string)null);
			Api.Event.RegisterRenderer((IRenderer)(object)this, (EnumRenderStage)6, (string)null);
			AnimationFixer = new AnimationFixer();
		}

		public void Dispose()
		{
		}

		private ItemRenderInfo GetRenderInfo(CarriedBlock carried)
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Expected O, but got Unknown
			DummySlot val = new DummySlot(carried.ItemStack);
			ItemRenderInfo itemStackRenderInfo = Api.Render.GetItemStackRenderInfo((ItemSlot)(object)val, (EnumItemRenderTarget)4, 0f);
			itemStackRenderInfo.Transform = carried.Behavior.Slots[carried.Slot]?.Transform ?? carried.Behavior.DefaultTransform;
			return itemStackRenderInfo;
		}

		public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
		{
			//IL_0034: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Invalid comparison between Unknown and I4
			IPlayer[] allPlayers = ((IWorldAccessor)Api.World).AllPlayers;
			foreach (IPlayer val in allPlayers)
			{
				if (val.Entity != null)
				{
					bool num = (object)val == Api.World.Player;
					bool flag = (int)stage != 1;
					if (num)
					{
						AnimationFixer.Update(val.Entity);
					}
					else if (flag ? (!((Entity)val.Entity).IsShadowRendered) : (!((Entity)val.Entity).IsRendered))
					{
						continue;
					}
					RenderAllCarried((EntityAgent)(object)val.Entity, deltaTime, flag);
				}
			}
			_renderTick++;
		}

		private void RenderAllCarried(EntityAgent entity, float deltaTime, bool isShadowPass)
		{
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0038: Invalid comparison between Unknown and I4
			//IL_0055: Unknown result type (might be due to invalid IL or missing references)
			//IL_005c: Expected O, but got Unknown
			List<CarriedBlock> list = ((Entity)(object)entity).GetCarried().ToList();
			if (list.Count == 0)
			{
				return;
			}
			IClientPlayer player = Api.World.Player;
			bool isFirstPerson = (object)entity == ((IPlayer)player).Entity && (int)player.CameraMode == 0;
			bool immersiveFpMode = ((IPlayer)player).ImmersiveFpMode;
			EntityShapeRenderer val = (EntityShapeRenderer)((Entity)entity).Properties.Client.Renderer;
			IAnimator animator = ((Entity)entity).AnimManager.Animator;
			if (val == null)
			{
				return;
			}
			foreach (CarriedBlock item in list)
			{
				RenderCarried(entity, item, deltaTime, isFirstPerson, immersiveFpMode, isShadowPass, val, animator);
			}
		}

		private void RenderCarried(EntityAgent entity, CarriedBlock carried, float deltaTime, bool isFirstPerson, bool isImmersiveFirstPerson, bool isShadowPass, EntityShapeRenderer renderer, IAnimator animator)
		{
			IRenderAPI render = Api.Render;
			bool flag = carried.Slot == CarrySlot.Hands;
			if (!flag && isFirstPerson && !isShadowPass)
			{
				return;
			}
			float[] array = Array.ConvertAll(render.CameraMatrixOrigin, (double i) => (float)i);
			SlotRenderSettings slotRenderSettings = _renderSettings[carried.Slot];
			ItemRenderInfo renderInfo = GetRenderInfo(carried);
			float[] array2;
			if (flag && isFirstPerson && !isImmersiveFirstPerson && !isShadowPass)
			{
				array2 = GetFirstPersonHandsMatrix(entity, array, deltaTime);
			}
			else
			{
				if (animator == null || slotRenderSettings == null)
				{
					return;
				}
				AttachmentPointAndPose attachmentPointPose = animator.GetAttachmentPointPose(slotRenderSettings.AttachmentPoint);
				if (attachmentPointPose == null)
				{
					return;
				}
				array2 = GetAttachmentPointMatrix(renderer, attachmentPointPose);
				if (isImmersiveFirstPerson)
				{
					Mat4f.Translate(array2, array2, 0f, -0.12f, 0f);
				}
			}
			ModelTransform transform = renderInfo.Transform;
			Mat4f.Scale(array2, array2, ((ModelTransformNoDefaults)transform).ScaleXYZ.X, ((ModelTransformNoDefaults)transform).ScaleXYZ.Y, ((ModelTransformNoDefaults)transform).ScaleXYZ.Z);
			Mat4f.Translate(array2, array2, slotRenderSettings.Offset.X, slotRenderSettings.Offset.Y, slotRenderSettings.Offset.Z);
			Mat4f.Translate(array2, array2, ((ModelTransformNoDefaults)transform).Origin.X, ((ModelTransformNoDefaults)transform).Origin.Y, ((ModelTransformNoDefaults)transform).Origin.Z);
			Mat4f.RotateX(array2, array2, ((ModelTransformNoDefaults)transform).Rotation.X * ((float)Math.PI / 180f));
			Mat4f.RotateZ(array2, array2, ((ModelTransformNoDefaults)transform).Rotation.Z * ((float)Math.PI / 180f));
			Mat4f.RotateY(array2, array2, ((ModelTransformNoDefaults)transform).Rotation.Y * ((float)Math.PI / 180f));
			Mat4f.Translate(array2, array2, 0f - ((ModelTransformNoDefaults)transform).Origin.X, 0f - ((ModelTransformNoDefaults)transform).Origin.Y, 0f - ((ModelTransformNoDefaults)transform).Origin.Z);
			Mat4f.Translate(array2, array2, ((ModelTransformNoDefaults)transform).Translation.X, ((ModelTransformNoDefaults)transform).Translation.Y, ((ModelTransformNoDefaults)transform).Translation.Z);
			if (isShadowPass)
			{
				IShaderProgram currentActiveShader = render.CurrentActiveShader;
				Mat4f.Mul(array2, render.CurrentShadowProjectionMatrix, array2);
				currentActiveShader.BindTexture2D("tex2d", renderInfo.TextureId, 0);
				currentActiveShader.UniformMatrix("mvpMatrix", array2);
				currentActiveShader.Uniform("origin", renderer.OriginPos);
				render.RenderMultiTextureMesh(renderInfo.ModelRef, "tex2d", 0);
			}
			else
			{
				IStandardShaderProgram obj = render.PreparedStandardShader((int)((Entity)entity).Pos.X, (int)((Entity)entity).Pos.Y, (int)((Entity)entity).Pos.Z, (Vec4f)null);
				obj.Tex2D = renderInfo.TextureId;
				obj.AlphaTest = 0.01f;
				obj.ViewMatrix = array;
				obj.ModelMatrix = array2;
				obj.DontWarpVertices = 1;
				render.RenderMultiTextureMesh(renderInfo.ModelRef, "tex", 0);
				((IShaderProgram)obj).Stop();
			}
		}

		private float[] GetAttachmentPointMatrix(EntityShapeRenderer renderer, AttachmentPointAndPose attachPointAndPose)
		{
			float[] obj = ((renderer?.ModelMat == null) ? null : Mat4f.CloneIt(renderer.ModelMat));
			float[] animModelMatrix = attachPointAndPose.AnimModelMatrix;
			Mat4f.Mul(obj, obj, animModelMatrix);
			AttachmentPoint attachPoint = attachPointAndPose.AttachPoint;
			Mat4f.Translate(obj, obj, (float)(attachPoint.PosX / 16.0), (float)(attachPoint.PosY / 16.0), (float)(attachPoint.PosZ / 16.0));
			Mat4f.RotateX(obj, obj, (float)attachPoint.RotationX * ((float)Math.PI / 180f));
			Mat4f.RotateY(obj, obj, (float)attachPoint.RotationY * ((float)Math.PI / 180f));
			Mat4f.RotateZ(obj, obj, (float)attachPoint.RotationZ * ((float)Math.PI / 180f));
			return obj;
		}

		private float[] GetFirstPersonHandsMatrix(EntityAgent entity, float[] viewMat, float deltaTime)
		{
			float[] array = Mat4f.Invert(Mat4f.Create(), viewMat);
			if (_renderTick - _lastTickHandsRendered > 10)
			{
				_moveWobble = 0f;
				_lastYaw = ((Entity)entity).Pos.Yaw;
				_yawDifference = 0f;
			}
			_lastTickHandsRendered = _renderTick;
			if (entity.Controls.TriesToMove)
			{
				float num = entity.Controls.MovespeedMultiplier * (float)entity.GetWalkSpeedMultiplier(0.3);
				_moveWobble += num * deltaTime * 5f;
			}
			else
			{
				float num2 = (float)(Math.Round((double)_moveWobble / Math.PI) * Math.PI);
				float num3 = deltaTime * (0.2f + Math.Abs(num2 - _moveWobble) * 4f);
				if (Math.Abs(num2 - _moveWobble) < num3)
				{
					_moveWobble = num2;
				}
				else
				{
					_moveWobble += (float)Math.Sign(num2 - _moveWobble) * num3;
				}
			}
			_moveWobble %= (float)Math.PI * 2f;
			float num4 = GameMath.Sin(_moveWobble + (float)Math.PI) * 0.03f;
			float num5 = GameMath.Sin(_moveWobble * 2f) * 0.02f;
			_yawDifference += GameMath.AngleRadDistance(_lastYaw, ((Entity)entity).Pos.Yaw);
			_yawDifference *= 0.925f;
			_lastYaw = ((Entity)entity).Pos.Yaw;
			float num6 = (0f - _yawDifference) / 2f;
			float num7 = (((Entity)entity).Pos.Pitch - (float)Math.PI) / 4f;
			Mat4f.RotateY(array, array, num6);
			Mat4f.Translate(array, array, 0f, -0.35f, -0.2f);
			Mat4f.RotateY(array, array, 0f - num6);
			Mat4f.RotateX(array, array, num7 / 2f);
			Mat4f.Translate(array, array, 0f, 0f, -0.2f);
			Mat4f.RotateX(array, array, num7);
			Mat4f.RotateY(array, array, num6);
			Mat4f.Translate(array, array, num4, num5, 0f);
			Mat4f.RotateY(array, array, (float)Math.PI / 2f);
			return array;
		}
	}
	public class HudCarried : IDisposable
	{
		public enum Anchor
		{
			None,
			L1,
			L2,
			L3,
			R1,
			R2,
			R3
		}

		private class HudCarriedRenderer : IRenderer, IDisposable
		{
			private readonly ICoreClientAPI api;

			private float cachedGUIScale = -1f;

			private float cachedFrameWidth = -1f;

			private float cachedFrameHeight = -1f;

			private float cachedSlotSize;

			private float cachedBackgroundSize;

			private float cachedHotbarWidth;

			private float cachedHotbarCenterX;

			private float cachedHotbarY;

			private readonly (int x, int y)[] cachedLeftPositions = new(int, int)[3];

			private readonly (int x, int y)[] cachedRightPositions = new(int, int)[3];

			private MeshRef highlightMesh;

			private MeshRef rectMesh;

			public double RenderOrder => 1.0;

			public int RenderRange => 10;

			public HudCarriedRenderer(ICoreClientAPI api)
			{
				this.api = api;
			}

			public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
			{
				//IL_0000: Unknown result type (might be due to invalid IL or missing references)
				//IL_0003: Invalid comparison between Unknown and I4
				if ((int)stage != 10)
				{
					return;
				}
				IClientPlayer player = api.World.Player;
				if (player == null)
				{
					return;
				}
				IRenderAPI render = api.Render;
				float gUIScale = RuntimeEnv.GUIScale;
				float num = render.FrameWidth;
				float num2 = render.FrameHeight;
				if (cachedGUIScale != gUIScale || cachedFrameWidth != num || cachedFrameHeight != num2)
				{
					UpdateCachedPositions(gUIScale, num, num2);
				}
				if (HandsHighlightSecondsRemaining > 0f)
				{
					HandsHighlightSecondsRemaining = Math.Max(0f, HandsHighlightSecondsRemaining - deltaTime);
				}
				if (BackHighlightSecondsRemaining > 0f)
				{
					BackHighlightSecondsRemaining = Math.Max(0f, BackHighlightSecondsRemaining - deltaTime);
				}
				Anchor backAnchor = BackAnchor;
				Anchor handsAnchor = HandsAnchor;
				bool flag = false;
				if (backAnchor != Anchor.None && handsAnchor != Anchor.None)
				{
					int num3 = (int)backAnchor;
					int num4 = (int)handsAnchor;
					bool num5 = num3 >= 1 && num3 <= 3 && num4 >= 1 && num4 <= 3;
					bool flag2 = num3 >= 4 && num3 <= 6 && num4 >= 4 && num4 <= 6;
					if ((num5 || flag2) && Math.Abs(num3 - num4) == 1)
					{
						(int x, int y) positionForAnchor = GetPositionForAnchor(backAnchor);
						(int, int) positionForAnchor2 = GetPositionForAnchor(handsAnchor);
						float centerX = (float)(positionForAnchor.x + positionForAnchor2.Item1) / 2f;
						float num6 = (float)(positionForAnchor.y + positionForAnchor2.Item2) / 2f + 1f;
						float width = (float)Math.Abs(positionForAnchor.x - positionForAnchor2.Item1) + cachedBackgroundSize;
						float height = Math.Max(cachedBackgroundSize, 2f * (cachedFrameHeight - num6));
						try
						{
							if (AnchorBackgroundEnabled)
							{
								string anchorBackgroundColor = AnchorBackgroundColor;
								float anchorBackgroundAlpha = AnchorBackgroundAlpha;
								DrawRectFilled(render, centerX, num6, width, height, ParseHexColor(anchorBackgroundColor, anchorBackgroundAlpha));
							}
						}
						catch
						{
						}
						if (AnchorBorderEnabled)
						{
							Vec4f color = ParseHexColor(AnchorBorderColor, AnchorBorderAlpha);
							DrawRectOutline(render, centerX, num6, width, height, Math.Max(0.5f, cachedSlotSize * 0.08f), color);
						}
						flag = true;
					}
				}
				if (!flag)
				{
					if (backAnchor != Anchor.None)
					{
						(int, int) positionForAnchor3 = GetPositionForAnchor(backAnchor);
						float num7 = (float)positionForAnchor3.Item2 + 1f;
						float height2 = Math.Max(cachedBackgroundSize, 2f * (cachedFrameHeight - num7));
						try
						{
							if (AnchorBackgroundEnabled)
							{
								string anchorBackgroundColor2 = AnchorBackgroundColor;
								float anchorBackgroundAlpha2 = AnchorBackgroundAlpha;
								DrawRectFilled(render, positionForAnchor3.Item1, num7, cachedBackgroundSize, height2, ParseHexColor(anchorBackgroundColor2, anchorBackgroundAlpha2));
							}
						}
						catch
						{
						}
						if (AnchorBorderEnabled)
						{
							Vec4f color2 = ParseHexColor(AnchorBorderColor, AnchorBorderAlpha);
							DrawRectOutline(render, positionForAnchor3.Item1, num7, cachedBackgroundSize, height2, Math.Max(0.5f, cachedSlotSize * 0.08f), color2);
						}
					}
					if (handsAnchor != Anchor.None)
					{
						(int, int) positionForAnchor4 = GetPositionForAnchor(handsAnchor);
						float num8 = (float)positionForAnchor4.Item2 + 1f;
						float height3 = Math.Max(cachedBackgroundSize, 2f * (cachedFrameHeight - num8));
						try
						{
							if (AnchorBackgroundEnabled)
							{
								string anchorBackgroundColor3 = AnchorBackgroundColor;
								float anchorBackgroundAlpha3 = AnchorBackgroundAlpha;
								DrawRectFilled(render, positionForAnchor4.Item1, num8, cachedBackgroundSize, height3, ParseHexColor(anchorBackgroundColor3, anchorBackgroundAlpha3));
							}
						}
						catch
						{
						}
						if (AnchorBorderEnabled)
						{
							Vec4f color3 = ParseHexColor(AnchorBorderColor, AnchorBorderAlpha);
							DrawRectOutline(render, positionForAnchor4.Item1, num8, cachedBackgroundSize, height3, Math.Max(0.5f, cachedSlotSize * 0.08f), color3);
						}
					}
				}
				CarriedBlock carriedBlock = ((Entity)(object)((IPlayer)player).Entity)?.GetCarried(CarrySlot.Hands);
				if (carriedBlock != null)
				{
					RenderCarriedBlock(render, carriedBlock, HandsAnchor, HandsHighlightSecondsRemaining);
				}
				CarriedBlock carriedBlock2 = ((Entity)(object)((IPlayer)player).Entity)?.GetCarried(CarrySlot.Back);
				if (carriedBlock2 != null)
				{
					RenderCarriedBlock(render, carriedBlock2, BackAnchor, BackHighlightSecondsRemaining);
				}
			}

			private void EnsureHighlightMesh(int steps = 32)
			{
				//IL_0017: Unknown result type (might be due to invalid IL or missing references)
				//IL_001d: Expected O, but got Unknown
				if (highlightMesh == null)
				{
					int num = 1 + steps;
					int num2 = steps * 3;
					MeshData val = new MeshData(num, num2, false, false, true, true);
					val.AddVertexSkipTex(0f, 0f, 0f, -1);
					for (int i = 0; i < steps; i++)
					{
						float num3 = (float)i / (float)steps * ((float)Math.PI * 2f);
						float num4 = (float)Math.Cos(num3);
						float num5 = (float)Math.Sin(num3);
						val.AddVertexSkipTex(num4, num5, 0f, -1);
					}
					for (int j = 0; j < steps; j++)
					{
						int num6 = 0;
						int num7 = 1 + j;
						int num8 = 1 + (j + 1) % steps;
						val.AddIndices(new int[3] { num6, num7, num8 });
					}
					float[] array = new float[num * 2];
					for (int k = 0; k < num; k++)
					{
						array[k * 2] = 0f;
						array[k * 2 + 1] = 0f;
					}
					val.Uv = array;
					byte[] array2 = new byte[num * 4];
					array2[0] = byte.MaxValue;
					array2[1] = byte.MaxValue;
					array2[2] = byte.MaxValue;
					array2[3] = byte.MaxValue;
					for (int l = 1; l < num; l++)
					{
						int num9 = l * 4;
						array2[num9] = byte.MaxValue;
						array2[num9 + 1] = byte.MaxValue;
						array2[num9 + 2] = byte.MaxValue;
						array2[num9 + 3] = 0;
					}
					val.Rgba = array2;
					highlightMesh = api.Render.UploadMesh(val);
				}
			}

			private void EnsureRectMesh()
			{
				//IL_000f: Unknown result type (might be due to invalid IL or missing references)
				//IL_0015: Expected O, but got Unknown
				if (rectMesh == null)
				{
					MeshData val = new MeshData(4, 6, false, false, true, true);
					val.AddVertexSkipTex(-0.5f, -0.5f, 0f, -1);
					val.AddVertexSkipTex(0.5f, -0.5f, 0f, -1);
					val.AddVertexSkipTex(0.5f, 0.5f, 0f, -1);
					val.AddVertexSkipTex(-0.5f, 0.5f, 0f, -1);
					val.AddIndices(new int[6] { 0, 1, 2, 0, 2, 3 });
					float[] uv = new float[8];
					val.Uv = uv;
					byte[] array = new byte[16];
					for (int i = 0; i < 4; i++)
					{
						array[i * 4] = byte.MaxValue;
						array[i * 4 + 1] = byte.MaxValue;
						array[i * 4 + 2] = byte.MaxValue;
						array[i * 4 + 3] = byte.MaxValue;
					}
					val.Rgba = array;
					rectMesh = api.Render.UploadMesh(val);
				}
			}

			private void DrawRectOutline(IRenderAPI rapi, float centerX, float centerY, float width, float height, float thickness, Vec4f color)
			{
				EnsureRectMesh();
				IShaderProgram currentActiveShader = rapi.CurrentActiveShader;
				try
				{
					rapi.GlPushMatrix();
					rapi.GlTranslate((float)(int)centerX, (float)(int)centerY, 0f);
					rapi.GlPushMatrix();
					rapi.GlTranslate(0f, 0f - (height / 2f - thickness / 2f), 0f);
					rapi.GlScale(width, thickness, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					currentActiveShader.Uniform("rgbaIn", color);
					currentActiveShader.Uniform("applyColor", 1);
					currentActiveShader.Uniform("noTexture", 1f);
					rapi.RenderMesh(rectMesh);
					rapi.GlPopMatrix();
					rapi.GlPushMatrix();
					rapi.GlTranslate(0f, height / 2f - thickness / 2f, 0f);
					rapi.GlScale(width, thickness, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					rapi.RenderMesh(rectMesh);
					rapi.GlPopMatrix();
					rapi.GlPushMatrix();
					rapi.GlTranslate(0f - (width / 2f - thickness / 2f), 0f, 0f);
					rapi.GlScale(thickness, height, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					rapi.RenderMesh(rectMesh);
					rapi.GlPopMatrix();
					rapi.GlPushMatrix();
					rapi.GlTranslate(width / 2f - thickness / 2f, 0f, 0f);
					rapi.GlScale(thickness, height, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					rapi.RenderMesh(rectMesh);
					rapi.GlPopMatrix();
					currentActiveShader.Uniform("applyColor", 0);
					currentActiveShader.Uniform("noTexture", 0f);
					rapi.GlPopMatrix();
				}
				catch (Exception ex)
				{
					((ICoreAPI)api).Logger.Debug("[HudCarried] Exception in DrawRectOutline: " + ex);
				}
			}

			private void DrawRectFilled(IRenderAPI rapi, float centerX, float centerY, float width, float height, Vec4f color)
			{
				EnsureRectMesh();
				IShaderProgram currentActiveShader = rapi.CurrentActiveShader;
				try
				{
					rapi.GlPushMatrix();
					rapi.GlTranslate((float)(int)centerX, (float)(int)centerY, 0f);
					rapi.GlScale(width, height, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					currentActiveShader.Uniform("rgbaIn", color);
					currentActiveShader.Uniform("applyColor", 1);
					currentActiveShader.Uniform("noTexture", 1f);
					rapi.RenderMesh(rectMesh);
					currentActiveShader.Uniform("applyColor", 0);
					currentActiveShader.Uniform("noTexture", 0f);
					rapi.GlPopMatrix();
				}
				catch (Exception ex)
				{
					((ICoreAPI)api).Logger.Debug("[HudCarried] Exception in DrawRectFilled: " + ex);
				}
			}

			private void DrawIconHighlight(IRenderAPI rapi, float secondsRemaining, float duration, float centerX, float centerY)
			{
				//IL_015f: Unknown result type (might be due to invalid IL or missing references)
				//IL_0166: Expected O, but got Unknown
				if (duration <= 0f || !IconHighlightEnabled)
				{
					return;
				}
				float num = Math.Max(0.0001f, Math.Max(0f, duration - 0.4f));
				float val = 0.4f;
				float num2 = duration - secondsRemaining;
				float num3 = Math.Max(0f, Math.Min(1f, num2 / num));
				float num4 = 1f - (1f - num3) * (1f - num3);
				float num5 = 1.35f - 0.35f * num4;
				float num6 = 0f;
				if (num2 >= num)
				{
					num6 = Math.Max(0f, Math.Min(1f, (num2 - num) / Math.Max(0.0001f, val)));
				}
				float num7 = IconHighlightAlpha * (1f - num6);
				EnsureHighlightMesh();
				IShaderProgram currentActiveShader = rapi.CurrentActiveShader;
				try
				{
					rapi.GlPushMatrix();
					rapi.GlTranslate((float)(int)centerX, (float)(int)centerY, 0f);
					float num8 = cachedSlotSize * 0.8f * num5;
					rapi.GlScale(num8, num8, 0f);
					currentActiveShader.UniformMatrix("modelViewMatrix", rapi.CurrentModelviewMatrix);
					Vec4f val2 = ParseHexColor(IconHighlightColor, 1f);
					float num9 = Math.Max(0f, Math.Min(1f, val2.W * num7));
					Vec4f val3 = new Vec4f(val2.X, val2.Y, val2.Z, num9);
					currentActiveShader.Uniform("rgbaIn", val3);
					currentActiveShader.Uniform("applyColor", 1);
					currentActiveShader.Uniform("noTexture", 1f);
					rapi.RenderMesh(highlightMesh);
					currentActiveShader.Uniform("applyColor", 0);
					currentActiveShader.Uniform("noTexture", 0f);
					rapi.GlPopMatrix();
				}
				catch (Exception ex)
				{
					((ICoreAPI)api).Logger.Debug("[HudCarried] Exception in DrawIconHighlight: " + ex);
				}
			}

			private Vec4f ParseHexColor(string hex, float alpha)
			{
				//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
				//IL_00cb: Expected O, but got Unknown
				//IL_0018: Unknown result type (might be due to invalid IL or missing references)
				//IL_001e: Expected O, but got Unknown
				//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
				//IL_00b2: Expected O, but got Unknown
				//IL_0054: Unknown result type (might be due to invalid IL or missing references)
				//IL_005a: Expected O, but got Unknown
				if (string.IsNullOrEmpty(hex))
				{
					return new Vec4f(76f / 85f, 0.76862746f, 0.6509804f, alpha);
				}
				try
				{
					string text = hex.Trim();
					if (text.StartsWith("#"))
					{
						text = text.Substring(1);
					}
					if (text.Length != 6)
					{
						return new Vec4f(76f / 85f, 0.76862746f, 0.6509804f, alpha);
					}
					int num = int.Parse(text.Substring(0, 2), NumberStyles.HexNumber);
					int num2 = int.Parse(text.Substring(2, 2), NumberStyles.HexNumber);
					int num3 = int.Parse(text.Substring(4, 2), NumberStyles.HexNumber);
					return new Vec4f((float)num / 255f, (float)num2 / 255f, (float)num3 / 255f, alpha);
				}
				catch
				{
					return new Vec4f(76f / 85f, 0.76862746f, 0.6509804f, alpha);
				}
			}

			private void RenderCarriedBlock(IRenderAPI rapi, CarriedBlock carriedBlock, Anchor anchor, float highlightSecondsRemaining)
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Expected O, but got Unknown
				DummySlot val = new DummySlot(carriedBlock.ItemStack);
				(int, int) positionForAnchor = GetPositionForAnchor(anchor);
				if (highlightSecondsRemaining > 0f)
				{
					DrawIconHighlight(rapi, highlightSecondsRemaining, 1.4f, positionForAnchor.Item1, positionForAnchor.Item2);
				}
				rapi.CurrentActiveShader.Uniform("noTexture", 0f);
				rapi.RenderItemstackToGui((ItemSlot)(object)val, (double)positionForAnchor.Item1, (double)positionForAnchor.Item2, 100.0, cachedSlotSize, -1, true, false, true);
			}

			private void UpdateCachedPositions(float guiScale, float frameWidth, float frameHeight)
			{
				cachedSlotSize = (float)GuiElement.scaled(32.0);
				int num = (int)GuiElement.scaled(16.0);
				int num2 = (int)GuiElement.scaled(16.0);
				float num3 = 850f;
				cachedHotbarWidth = (float)GuiElement.scaled((double)num3);
				cachedHotbarCenterX = frameWidth / 2f;
				cachedHotbarY = frameHeight - (float)GuiElement.scaled(36.0);
				cachedBackgroundSize = (float)GuiElement.scaled(64.0);
				float num4 = cachedHotbarCenterX - cachedHotbarWidth / 2f - (float)num - cachedSlotSize;
				for (int i = 0; i < 3; i++)
				{
					float num5 = num4 - (float)i * (cachedSlotSize + (float)num2);
					float val = cachedHotbarY;
					num5 += cachedSlotSize / 2f;
					num5 = Math.Max(cachedSlotSize / 2f, Math.Min(num5, frameWidth - cachedSlotSize / 2f));
					val = Math.Max(cachedSlotSize / 2f, Math.Min(val, frameHeight - cachedSlotSize / 2f));
					cachedLeftPositions[i] = (x: (int)num5, y: (int)val);
				}
				float num6 = cachedHotbarCenterX + cachedHotbarWidth / 2f + (float)num;
				for (int j = 0; j < 3; j++)
				{
					float num7 = num6 + (float)j * (cachedSlotSize + (float)num2);
					float val2 = cachedHotbarY;
					num7 += cachedSlotSize / 2f;
					num7 = Math.Max(cachedSlotSize / 2f, Math.Min(num7, frameWidth - cachedSlotSize / 2f));
					val2 = Math.Max(cachedSlotSize / 2f, Math.Min(val2, frameHeight - cachedSlotSize / 2f));
					cachedRightPositions[j] = (x: (int)num7, y: (int)val2);
				}
				cachedGUIScale = guiScale;
				cachedFrameWidth = frameWidth;
				cachedFrameHeight = frameHeight;
			}

			private (int x, int y) GetPositionForAnchor(Anchor anchor)
			{
				return anchor switch
				{
					Anchor.L1 => cachedLeftPositions[0], 
					Anchor.L2 => cachedLeftPositions[1], 
					Anchor.L3 => cachedLeftPositions[2], 
					Anchor.R1 => cachedRightPositions[0], 
					Anchor.R2 => cachedRightPositions[1], 
					Anchor.R3 => cachedRightPositions[2], 
					_ => (x: (int)cachedHotbarCenterX, y: (int)cachedHotbarY), 
				};
			}

			public void Dispose()
			{
				if (highlightMesh != null)
				{
					api.Render.DeleteMesh(highlightMesh);
					highlightMesh = null;
				}
				if (rectMesh != null)
				{
					api.Render.DeleteMesh(rectMesh);
					rectMesh = null;
				}
			}
		}

		private readonly ICoreClientAPI api;

		private IRenderer renderer;

		public static readonly Anchor HandsAnchorDefault = Anchor.L1;

		public static readonly Anchor BackAnchorDefault = Anchor.R1;

		public static readonly float AnchorBackgroundAlphaDefault = 0.4f;

		public static readonly string AnchorBackgroundColorDefault = "#E4C4A6";

		public static readonly float AnchorBorderAlphaDefault = 0.75f;

		public static readonly string AnchorBorderColorDefault = "#45372D";

		public static readonly string IconHighlightColorDefault = "#FFFFFF";

		public static readonly float IconHighlightAlphaDefault = 0.8f;

		public const float DefaultHighlightDuration = 1f;

		public const float HighlightFadeExtra = 0.4f;

		public static bool ShowDebugIcons { get; set; } = false;

		public static Anchor HandsAnchor { get; set; } = HandsAnchorDefault;

		public static Anchor BackAnchor { get; set; } = BackAnchorDefault;

		public static bool AnchorBackgroundEnabled { get; set; } = true;

		public static float AnchorBackgroundAlpha { get; set; } = AnchorBackgroundAlphaDefault;

		public static string AnchorBackgroundColor { get; set; } = AnchorBackgroundColorDefault;

		public static bool AnchorBorderEnabled { get; set; } = true;

		public static float AnchorBorderAlpha { get; set; } = AnchorBorderAlphaDefault;

		public static string AnchorBorderColor { get; set; } = AnchorBorderColorDefault;

		public static bool IconHighlightEnabled { get; set; } = true;

		public static string IconHighlightColor { get; set; } = IconHighlightColorDefault;

		public static float IconHighlightAlpha { get; set; } = IconHighlightAlphaDefault;

		public static float HandsHighlightSecondsRemaining { get; private set; } = 0f;

		public static float BackHighlightSecondsRemaining { get; private set; } = 0f;

		public static void TriggerHandsHighlight(float seconds = 1.4f)
		{
			HandsHighlightSecondsRemaining = Math.Max(HandsHighlightSecondsRemaining, seconds);
		}

		public static void TriggerBackHighlight(float seconds = 1.4f)
		{
			BackHighlightSecondsRemaining = Math.Max(BackHighlightSecondsRemaining, seconds);
		}

		public HudCarried(ICoreClientAPI api)
		{
			this.api = api ?? throw new ArgumentNullException("api");
			renderer = (IRenderer)(object)new HudCarriedRenderer(api);
			this.api.Event.RegisterRenderer(renderer, (EnumRenderStage)10, (string)null);
			((ICoreAPI)this.api).Logger.Debug("[HudCarried] HUD renderer registered successfully");
		}

		public void Dispose()
		{
			if (renderer != null)
			{
				api.Event.UnregisterRenderer(renderer, (EnumRenderStage)10);
				((HudCarriedRenderer)(object)renderer).Dispose();
				renderer = null;
			}
		}
	}
	public class HudOverlayRenderer : IRenderer, IDisposable
	{
		private const int CircleColor = 13421772;

		private const float CircleAlphaIn = 0.2f;

		private const float CircleAlphaOut = 0.4f;

		private const int CircleMaxSteps = 16;

		private const float OuterRadius = 24f;

		private const float InnerRadius = 18f;

		private MeshRef circleMesh;

		private ICoreClientAPI api;

		private float circleAlpha;

		private float circleProgress;

		public bool CircleVisible { get; set; }

		public float CircleProgress
		{
			get
			{
				return circleProgress;
			}
			set
			{
				circleProgress = GameMath.Clamp(value, 0f, 1f);
				CircleVisible = true;
			}
		}

		public double RenderOrder => 0.0;

		public int RenderRange => 10;

		public HudOverlayRenderer(ICoreClientAPI api)
		{
			this.api = api;
			this.api.Event.RegisterRenderer((IRenderer)(object)this, (EnumRenderStage)10, (string)null);
			UpdateCircleMesh(1f);
		}

		private void UpdateCircleMesh(float progress)
		{
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0021: Expected O, but got Unknown
			int num = 1 + (int)Math.Ceiling(16f * progress);
			MeshData val = new MeshData(num * 2, num * 6, false, false, true, false);
			for (int i = 0; i < num; i++)
			{
				double num2 = (double)Math.Min(progress, (float)i * 0.0625f) * Math.PI * 2.0;
				float num3 = (float)Math.Sin(num2);
				float num4 = 0f - (float)Math.Cos(num2);
				val.AddVertexSkipTex(num3, num4, 0f, -1);
				val.AddVertexSkipTex(num3 * 0.75f, num4 * 0.75f, 0f, -1);
				if (i > 0)
				{
					val.AddIndices(new int[3]
					{
						i * 2 - 2,
						i * 2 - 1,
						i * 2
					});
					val.AddIndices(new int[3]
					{
						i * 2,
						i * 2 - 1,
						i * 2 + 1
					});
				}
			}
			if (circleMesh != null)
			{
				api.Render.UpdateMesh(circleMesh, val);
			}
			else
			{
				circleMesh = api.Render.UploadMesh(val);
			}
		}

		public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
		{
			//IL_0086: Unknown result type (might be due to invalid IL or missing references)
			//IL_008c: Expected O, but got Unknown
			IRenderAPI render = api.Render;
			IShaderProgram currentActiveShader = render.CurrentActiveShader;
			circleAlpha = Math.Max(0f, Math.Min(1f, circleAlpha + deltaTime / (CircleVisible ? 0.2f : (-0.4f))));
			if (!(CircleProgress <= 0f) && !(circleAlpha <= 0f))
			{
				UpdateCircleMesh(CircleProgress);
				Vec4f val = new Vec4f(0.8f, 0.8f, 0.8f, circleAlpha);
				currentActiveShader.Uniform("rgbaIn", val);
				currentActiveShader.Uniform("extraGlow", 0);
				currentActiveShader.Uniform("applyColor", 0);
				currentActiveShader.Uniform("tex2d", 0);
				currentActiveShader.Uniform("noTexture", 1f);
				currentActiveShader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
				int num;
				int num2;
				if (api.Input.MouseGrabbed)
				{
					num = api.Render.FrameWidth / 2;
					num2 = api.Render.FrameHeight / 2;
				}
				else
				{
					num = api.Input.MouseX;
					num2 = api.Input.MouseY;
				}
				render.GlPushMatrix();
				render.GlTranslate((float)num, (float)num2, 0f);
				render.GlScale(24f, 24f, 0f);
				currentActiveShader.UniformMatrix("modelViewMatrix", render.CurrentModelviewMatrix);
				render.GlPopMatrix();
				render.RenderMesh(circleMesh);
				currentActiveShader.Uniform("noTexture", 0f);
			}
		}

		public void Dispose()
		{
			if (circleMesh != null)
			{
				api.Render.DeleteMesh(circleMesh);
			}
		}
	}
}
namespace CarryOn.Client.Logic
{
	public class CarryOnClientConfig
	{
		public int? ConfigVersion { get; set; }

		public string HandsAnchor { get; set; } = HudCarried.HandsAnchorDefault.ToString();

		public string BackAnchor { get; set; } = HudCarried.BackAnchorDefault.ToString();

		public bool AnchorBackgroundEnabled { get; set; } = true;

		public string AnchorBackgroundColor { get; set; } = HudCarried.AnchorBackgroundColorDefault;

		public float AnchorBackgroundAlpha { get; set; } = HudCarried.AnchorBackgroundAlphaDefault;

		public bool AnchorBorderEnabled { get; set; } = true;

		public string AnchorBorderColor { get; set; } = HudCarried.AnchorBorderColorDefault;

		public float AnchorBorderAlpha { get; set; } = HudCarried.AnchorBorderAlphaDefault;

		public bool IconHighlightEnabled { get; set; } = true;

		public string IconHighlightColor { get; set; } = HudCarried.IconHighlightColorDefault;

		public float IconHighlightAlpha { get; set; } = HudCarried.IconHighlightAlphaDefault;

		public CarryOnClientConfig()
		{
		}

		public CarryOnClientConfig(int version)
		{
			ConfigVersion = version;
		}
	}
	public class ClientModConfig
	{
		private const int CurrentVersion = 1;

		private const string ConfigFileName = "CarryOnClientConfig.json";

		public CarryOnClientConfig Config { get; private set; }

		public void Load(ICoreClientAPI api)
		{
			//IL_0004: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Invalid comparison between Unknown and I4
			if (api == null || (int)((ICoreAPI)api).Side != 2)
			{
				return;
			}
			try
			{
				CarryOnClientConfig carryOnClientConfig = ((ICoreAPICommon)api).LoadModConfig<CarryOnClientConfig>("CarryOnClientConfig.json");
				if (carryOnClientConfig == null)
				{
					carryOnClientConfig = new CarryOnClientConfig(1);
				}
				if (!carryOnClientConfig.ConfigVersion.HasValue)
				{
					carryOnClientConfig.ConfigVersion = 1;
				}
				Config = carryOnClientConfig;
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Warning("CarryOn: Failed to load client config: " + ex.Message);
				Config = new CarryOnClientConfig(1);
			}
		}

		public void Save(ICoreClientAPI api)
		{
			//IL_0004: Unknown result type (might be due to invalid IL or missing references)
			//IL_000a: Invalid comparison between Unknown and I4
			if (api == null || (int)((ICoreAPI)api).Side != 2 || Config == null)
			{
				return;
			}
			try
			{
				((ICoreAPICommon)api).StoreModConfig<CarryOnClientConfig>(Config, "CarryOnClientConfig.json");
			}
			catch (Exception ex)
			{
				((ICoreAPI)api).Logger.Warning("CarryOn: Failed to save client config: " + ex.Message);
			}
		}
	}
}
namespace CarryOn.API.Event
{
	public delegate void ActionDelegate(EnumEntityAction action, ref EnumHandling handled);
	public delegate void BlockEntityDataDelegate(BlockEntity blockEntity, ITreeAttribute blockEntityData, bool dropped = false);
	public class CarryEvents
	{
		public BlockEntityDataDelegate OnRestoreEntityBlockData;

		public CheckPermissionToCarryDelegate OnCheckPermissionToCarry;

		public event EventHandler<BlockDroppedEventArgs> BlockDropped;

		public event EventHandler<BlockRemovedEventArgs> BlockRemoved;

		public void TriggerBlockDropped(IWorldAccessor world, BlockPos position, Entity entity, CarriedBlock carriedBlock, bool destroyed = false, bool hadContents = false)
		{
			BlockDroppedEventArgs e = new BlockDroppedEventArgs
			{
				World = world,
				Entity = entity,
				Position = position,
				CarriedBlock = carriedBlock,
				Destroyed = destroyed,
				HadContents = hadContents
			};
			OnBlockDropped(e);
		}

		protected virtual void OnBlockDropped(BlockDroppedEventArgs e)
		{
			this.BlockDropped?.Invoke(this, e);
		}

		public void TriggerBlockRemoved(IWorldAccessor world, BlockPos position)
		{
			BlockRemovedEventArgs e = new BlockRemovedEventArgs
			{
				World = world,
				Position = position
			};
			OnBlockRemoved(e);
		}

		protected virtual void OnBlockRemoved(BlockRemovedEventArgs e)
		{
			this.BlockRemoved?.Invoke(this, e);
		}
	}
	public delegate void CheckPermissionToCarryDelegate(EntityPlayer playerEntity, BlockPos pos, bool isReinforced, out bool? hasPermission);
}
namespace CarryOn.API.Event.Data
{
	public class BlockDroppedEventArgs : EventArgs
	{
		public IWorldAccessor World { get; set; }

		public Entity Entity { get; set; }

		public BlockPos Position { get; set; }

		public CarriedBlock CarriedBlock { get; set; }

		public bool Destroyed { get; set; }

		public bool HadContents { get; set; }
	}
	public class BlockRemovedEventArgs : EventArgs
	{
		public IWorldAccessor World { get; set; }

		public BlockPos Position { get; set; }
	}
}
namespace CarryOn.API.Common
{
	public class CarriedBlock
	{
		public static string AttributeId { get; } = CarrySystem.ModId + ":Carried";

		public CarrySlot Slot { get; }

		public ItemStack ItemStack { get; }

		public Block Block => ItemStack.Block;

		public ITreeAttribute BlockEntityData { get; }

		public BlockBehaviorCarryable Behavior => Block.GetBehaviorOrDefault(BlockBehaviorCarryable.Default);

		public CarriedBlock(CarrySlot slot, ItemStack stack, ITreeAttribute blockEntityData)
		{
			Slot = slot;
			ItemStack = stack ?? throw new ArgumentNullException("stack");
			BlockEntityData = blockEntityData;
		}

		public static CarriedBlock Get(Entity entity, CarrySlot slot)
		{
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			//IL_0057: Invalid comparison between Unknown and I4
			//IL_0082: Unknown result type (might be due to invalid IL or missing references)
			//IL_0088: Invalid comparison between Unknown and I4
			if (entity == null)
			{
				throw new ArgumentNullException("entity");
			}
			ITreeAttribute val = ((IAttribute)(object)entity.WatchedAttributes).TryGet<ITreeAttribute>(new string[2]
			{
				AttributeId,
				slot.ToString()
			});
			if (val == null)
			{
				return null;
			}
			ItemStack itemstack = val.GetItemstack("Stack", (ItemStack)null);
			if (itemstack == null || (int)itemstack.Class > 0)
			{
				return null;
			}
			if (itemstack.Block == null)
			{
				itemstack.ResolveBlockOrItem(entity.World);
				if (itemstack.Block == null)
				{
					return null;
				}
			}
			ITreeAttribute blockEntityData = (((int)entity.World.Side == 1) ? ((IAttribute)(object)entity.Attributes).TryGet<ITreeAttribute>(new string[3]
			{
				AttributeId,
				slot.ToString(),
				"Data"
			}) : null);
			return new CarriedBlock(slot, itemstack, blockEntityData);
		}

		public static void Set(Entity entity, CarrySlot slot, ItemStack stack, ITreeAttribute blockEntityData)
		{
			//IL_0056: Unknown result type (might be due to invalid IL or missing references)
			//IL_005c: Invalid comparison between Unknown and I4
			if (entity == null)
			{
				throw new ArgumentNullException("entity");
			}
			((IAttribute)(object)entity.WatchedAttributes).Set(stack, AttributeId, slot.ToString(), "Stack");
			entity.WatchedAttributes.MarkPathDirty(AttributeId);
			if ((int)entity.World.Side == 1 && blockEntityData != null)
			{
				((IAttribute)(object)entity.Attributes).Set((IAttribute)(object)blockEntityData, AttributeId, slot.ToString(), "Data");
			}
			BlockBehaviorCarryable.SlotSettings slotSettings = stack.Block.GetBehaviorOrDefault(BlockBehaviorCarryable.Default).Slots[slot];
			if (slotSettings != null && slotSettings.Animation != null)
			{
				entity.StartAnimation(slotSettings.Animation);
			}
			EntityAgent val = (EntityAgent)(object)((entity is EntityAgent) ? entity : null);
			if (val != null)
			{
				float num = (ModConfig.IgnoreCarrySpeedPenalty ? 0f : (slotSettings?.WalkSpeedModifier ?? 0f));
				if (num != 0f && !ModConfig.AllowSprintWhileCarrying)
				{
					((Entity)val).Stats.Set("walkspeed", $"{CarrySystem.ModId}:{slot}", num, false);
				}
				if (slot == CarrySlot.Hands)
				{
					LockedItemSlot.Lock(val.RightHandItemSlot);
				}
				if (slot != CarrySlot.Back)
				{
					LockedItemSlot.Lock(val.LeftHandItemSlot);
				}
				CarryHandler.SendLockSlotsMessage((EntityPlayer)(object)((val is EntityPlayer) ? val : null));
			}
		}

		public void Set(Entity entity, CarrySlot slot)
		{
			Set(entity, slot, ItemStack, BlockEntityData);
		}

		public static void Remove(Entity entity, CarrySlot slot)
		{
			if (entity == null)
			{
				throw new ArgumentNullException("entity");
			}
			string text = entity.GetCarried(slot)?.Behavior?.Slots?[slot]?.Animation;
			if (text != null)
			{
				entity.StopAnimation(text);
			}
			EntityAgent val = (EntityAgent)(object)((entity is EntityAgent) ? entity : null);
			if (val != null)
			{
				((Entity)val).Stats.Remove("walkspeed", $"{CarrySystem.ModId}:{slot}");
				if (slot == CarrySlot.Hands)
				{
					LockedItemSlot.Restore(val.RightHandItemSlot);
				}
				if (slot != CarrySlot.Back)
				{
					LockedItemSlot.Restore(val.LeftHandItemSlot);
				}
				CarryHandler.SendLockSlotsMessage((EntityPlayer)(object)((val is EntityPlayer) ? val : null));
			}
			((IAttribute)(object)entity.WatchedAttributes).Remove(AttributeId, slot.ToString());
			entity.WatchedAttributes.MarkPathDirty(AttributeId);
			((IAttribute)(object)entity.Attributes).Remove(AttributeId, slot.ToString());
		}

		public static CarriedBlock CreateFromBlockPos(IWorldAccessor world, BlockPos pos, CarrySlot slot)
		{
			//IL_0050: Unknown result type (might be due to invalid IL or missing references)
			//IL_0056: Invalid comparison between Unknown and I4
			//IL_0047: Unknown result type (might be due to invalid IL or missing references)
			//IL_0068: Unknown result type (might be due to invalid IL or missing references)
			//IL_006e: Expected O, but got Unknown
			if (world == null)
			{
				throw new ArgumentNullException("world");
			}
			if (pos == (BlockPos)null)
			{
				throw new ArgumentNullException("pos");
			}
			Block block = world.BlockAccessor.GetBlock(pos);
			if (((CollectibleObject)block).Id == 0)
			{
				return null;
			}
			ItemStack stack = (ItemStack)(((object)block.OnPickBlock(world, pos)) ?? ((object)new ItemStack(block, 1)));
			ITreeAttribute val = null;
			if ((int)world.Side == 1)
			{
				BlockEntity blockEntity = world.BlockAccessor.GetBlockEntity(pos);
				if (blockEntity != null)
				{
					val = (ITreeAttribute)new TreeAttribute();
					blockEntity.ToTreeAttributes(val);
					val = val.Clone();
					val.RemoveAttribute("posx");
					val.RemoveAttribute("posy");
					val.RemoveAttribute("posz");
					val.RemoveAttribute("meshAngle");
				}
			}
			return new CarriedBlock(slot, stack, val);
		}

		public static CarriedBlock PickUp(IWorldAccessor world, BlockPos pos, CarrySlot slot, bool checkIsCarryable = false)
		{
			CarriedBlock carriedBlock = CreateFromBlockPos(world, pos, slot);
			if (carriedBlock == null)
			{
				return null;
			}
			if (checkIsCarryable && !carriedBlock.Block.IsCarryable(slot))
			{
				return null;
			}
			world.BlockAccessor.SetBlock(0, pos);
			ModSystemBlockReinforcement modSystem = world.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>(true);
			if (modSystem != null)
			{
				modSystem.ClearReinforcement(pos);
			}
			world.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
			return carriedBlock;
		}

		public bool PlaceDown(ref string failureCode, IWorldAccessor world, BlockSelection selection, Entity entity, bool dropped = false, bool playSound = true)
		{
			if (world == null)
			{
				throw new ArgumentNullException("world");
			}
			if (selection == null)
			{
				throw new ArgumentNullException("selection");
			}
			if (!world.BlockAccessor.IsValidPos(selection.Position))
			{
				return false;
			}
			EntityPlayer val = (EntityPlayer)(object)((entity is EntityPlayer) ? entity : null);
			if (val != null && !dropped)
			{
				if (failureCode == null)
				{
					failureCode = "__ignore__";
				}
				bool shiftKey = ((EntityAgent)val).Controls.ShiftKey;
				bool ctrlKey = ((EntityAgent)val).Controls.CtrlKey;
				IPlayer val2 = world.PlayerByUid(val.PlayerUID);
				try
				{
					val2.InventoryManager.ActiveHotbarSlot.Itemstack = ItemStack;
					((EntityAgent)val).Controls.ShiftKey = true;
					((EntityAgent)val).Controls.CtrlKey = false;
					if (!Block.TryPlaceBlock(world, val2, ItemStack, selection, ref failureCode))
					{
						val2.InventoryManager.ActiveHotbarSlot.Itemstack = null;
						return false;
					}
				}
				catch (NullReferenceException ex)
				{
					world.Logger.Error("Error occured while trying to place a carried block: " + ex.Message);
					world.BlockAccessor.SetBlock(((CollectibleObject)Block).Id, selection.Position, ItemStack);
				}
				finally
				{
					((EntityAgent)val).Controls.ShiftKey = shiftKey;
					((EntityAgent)val).Controls.CtrlKey = ctrlKey;
				}
			}
			else
			{
				world.BlockAccessor.SetBlock(((CollectibleObject)Block).Id, selection.Position, ItemStack);
			}
			RestoreBlockEntityData(world, selection.Position, dropped);
			world.BlockAccessor.MarkBlockDirty(selection.Position, (IPlayer)null);
			world.BlockAccessor.TriggerNeighbourBlockUpdate(selection.Position);
			if (entity != null)
			{
				Remove(entity, Slot);
			}
			if (playSound)
			{
				PlaySound(selection.Position, world, (EntityPlayer)(object)(dropped ? null : ((entity is EntityPlayer) ? entity : null)));
			}
			if (dropped)
			{
				world.GetCarryEvents()?.TriggerBlockDropped(world, selection.Position, entity, this);
			}
			return true;
		}

		public void RestoreBlockEntityData(IWorldAccessor world, BlockPos pos, bool dropped = false)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Invalid comparison between Unknown and I4
			if ((int)world.Side != 1 || BlockEntityData == null)
			{
				return;
			}
			ITreeAttribute blockEntityData = BlockEntityData;
			blockEntityData.SetInt("posx", pos.X);
			blockEntityData.SetInt("posy", pos.Y);
			blockEntityData.SetInt("posz", pos.Z);
			BlockEntity blockEntity = world.BlockAccessor.GetBlockEntity(pos);
			Delegate[] array = world.GetCarryEvents()?.OnRestoreEntityBlockData?.GetInvocationList();
			if (array != null)
			{
				foreach (BlockEntityDataDelegate item in array.Cast<BlockEntityDataDelegate>())
				{
					try
					{
						item(blockEntity, blockEntityData, dropped);
					}
					catch (Exception ex)
					{
						world.Logger.Error(ex.Message);
					}
				}
			}
			if (blockEntity != null)
			{
				blockEntity.FromTreeAttributes(blockEntityData, world);
			}
			if (blockEntity != null)
			{
				blockEntity.MarkDirty(true, (IPlayer)null);
			}
		}

		internal void PlaySound(BlockPos pos, IWorldAccessor world, EntityPlayer entityPlayer = null)
		{
			//IL_0024: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Invalid comparison between Unknown and I4
			BlockSounds sounds = Block.Sounds;
			if (!(((sounds != null) ? sounds.Place : null) == (AssetLocation)null))
			{
				IPlayer val = ((entityPlayer == null || (int)world.Side != 1) ? null : ((entityPlayer != null) ? entityPlayer.Player : null));
				world.PlaySoundAt(Block.Sounds.Place, (double)pos.X + 0.5, (double)pos.Y + 0.25, (double)pos.Z + 0.5, val, true, 16f, 1f);
			}
		}
	}
	public static class CarryableExtensions
	{
		public static bool IsCarryable(this Block block)
		{
			return ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryable>() != null;
		}

		public static bool IsCarryableInteract(this Block block)
		{
			return ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryableInteract>() != null;
		}

		public static bool IsCarryable(this Block block, CarrySlot slot)
		{
			return ((CollectibleObject)block).GetBehavior<BlockBehaviorCarryable>()?.Slots?[slot] != null;
		}

		public static CarriedBlock GetCarried(this Entity entity, CarrySlot slot)
		{
			return CarriedBlock.Get(entity, slot);
		}

		public static IEnumerable<CarriedBlock> GetCarried(this Entity entity)
		{
			foreach (CarrySlot item in Enum.GetValues(typeof(CarrySlot)).Cast<CarrySlot>())
			{
				CarriedBlock carried = entity.GetCarried(item);
				if (carried != null)
				{
					yield return carried;
				}
			}
		}

		public static bool Carry(this Entity entity, BlockPos pos, CarrySlot slot, bool checkIsCarryable = true, bool playSound = true)
		{
			if (!HasPermissionToCarry(entity, pos))
			{
				return false;
			}
			if (CarriedBlock.Get(entity, slot) != null)
			{
				return false;
			}
			CarriedBlock carriedBlock = CarriedBlock.PickUp(entity.World, pos, slot, checkIsCarryable);
			if (carriedBlock == null)
			{
				return false;
			}
			carriedBlock.Set(entity, slot);
			if (playSound)
			{
				carriedBlock.PlaySound(pos, entity.World, (EntityPlayer)(object)((entity is EntityPlayer) ? entity : null));
			}
			return true;
		}

		private static bool HasPermissionToCarry(Entity entity, BlockPos pos)
		{
			//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00cb: Invalid comparison between Unknown and I4
			ModSystemBlockReinforcement modSystem = entity.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>(true);
			bool flag = modSystem != null && modSystem.IsReinforced(pos);
			EntityPlayer val = (EntityPlayer)(object)((entity is EntityPlayer) ? entity : null);
			if (val != null)
			{
				Delegate[] array = entity.World.GetCarryEvents()?.OnCheckPermissionToCarry?.GetInvocationList();
				if (array != null)
				{
					foreach (CheckPermissionToCarryDelegate item in array.Cast<CheckPermissionToCarryDelegate>())
					{
						try
						{
							item(val, pos, flag, out var hasPermission);
							if (hasPermission.HasValue)
							{
								return hasPermission.Value;
							}
						}
						catch (Exception ex)
						{
							entity.World.Logger.Error(ex.Message);
						}
					}
				}
				if ((int)val.Player.WorldData.CurrentGameMode != 2 && flag)
				{
					return false;
				}
				return entity.World.Claims.TryAccess(val.Player, pos, (EnumBlockAccessFlags)1);
			}
			return !flag;
		}

		public static void DropCarried(this Entity entity, IEnumerable<CarrySlot> slots, int hSize = 4, int vSize = 4)
		{
			//IL_0069: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Expected O, but got Unknown
			//IL_0322: Unknown result type (might be due to invalid IL or missing references)
			//IL_0329: Expected O, but got Unknown
			//IL_04c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_03a5: Unknown result type (might be due to invalid IL or missing references)
			//IL_03ac: Expected O, but got Unknown
			if (entity == null)
			{
				throw new ArgumentNullException("entity");
			}
			if (slots == null)
			{
				throw new ArgumentNullException("slots");
			}
			if (hSize < 0)
			{
				throw new ArgumentOutOfRangeException("hSize");
			}
			if (vSize < 0)
			{
				throw new ArgumentOutOfRangeException("vSize");
			}
			IServerPlayer player = null;
			Entity obj = entity;
			EntityPlayer val = (EntityPlayer)(object)((obj is EntityPlayer) ? obj : null);
			if (val != null)
			{
				player = (IServerPlayer)val.Player;
			}
			ICoreAPI api = entity.Api;
			IWorldAccessor world = entity.World;
			IBlockAccessor blockAccessor = world.BlockAccessor;
			string[] array = ModConfig.ServerConfig?.DroppedBlockOptions?.NonGroundBlockClasses ?? Array.Empty<string>();
			HashSet<CarriedBlock> remaining = new HashSet<CarriedBlock>(from s in slots
				select entity.GetCarried(s) into t
				where t != null
				orderby t?.Behavior?.MultiblockOffset
				select t);
			if (remaining.Count == 0)
			{
				return;
			}
			BlockPos centerBlock = entity.Pos.AsBlockPos;
			BlockPos val2 = centerBlock.DownCopy(1);
			bool flag = false;
			while (!flag)
			{
				Block block = blockAccessor.GetBlock(val2);
				if (block.BlockId == 0 || (array != null && array.Contains(((RegistryObject)block).Class)))
				{
					centerBlock = val2;
					val2 = val2.DownCopy(1);
				}
				else
				{
					flag = true;
				}
			}
			List<BlockPos> list = new List<BlockPos>((hSize * 2 + 1) * (hSize * 2 + 1));
			for (int num = -hSize; num <= hSize; num++)
			{
				for (int num2 = -hSize; num2 <= hSize; num2++)
				{
					list.Add(centerBlock.AddCopy(num, 0, num2));
				}
			}
			List<BlockPos> airBlocks = new List<BlockPos>();
			List<BlockPos> list2 = new List<BlockPos>();
			list2.AddRange(list.OrderBy((BlockPos b) => b.DistanceTo(centerBlock)));
			list = list2;
			int num3 = 0;
			int num4 = 0;
			while (remaining.Count > 0)
			{
				if (num3 >= list.Count)
				{
					while (remaining.Count > 0)
					{
						CarriedBlock block2 = remaining.FirstOrDefault();
						BlockPos val3 = airBlocks.FirstOrDefault();
						if (val3 == (BlockPos)null)
						{
							break;
						}
						TryDrop(val3, block2);
					}
					if (remaining.Count <= 0)
					{
						break;
					}
					api.Logger.Warning($"Entity {entity.GetName()} could not drop carryable on or near {centerBlock}");
					bool destroyed = false;
					bool hadContents = false;
					Vec3d val4 = new Vec3d((double)centerBlock.X + 0.5, (double)centerBlock.Y + 0.5, (double)centerBlock.Z + 0.5);
					{
						foreach (CarriedBlock item in remaining)
						{
							ITreeAttribute blockEntityData = item.BlockEntityData;
							IAttribute obj2 = ((blockEntityData != null) ? blockEntityData["inventory"] : null);
							TreeAttribute val5 = (TreeAttribute)(object)((obj2 is TreeAttribute) ? obj2 : null);
							if (val5 != null)
							{
								IAttribute obj3 = val5["slots"];
								TreeAttribute val6 = (TreeAttribute)(object)((obj3 is TreeAttribute) ? obj3 : null);
								if (val6 != null)
								{
									foreach (ItemstackAttribute item2 in val6.Values.Cast<ItemstackAttribute>())
									{
										ItemStack val7 = (ItemStack)item2.GetValue();
										world.SpawnItemEntity(val7, val4, (Vec3d)null);
										hadContents = true;
									}
									ItemStack val8 = item.ItemStack.Clone();
									((IAttribute)(object)val8.Attributes).Remove("contents");
									world.SpawnItemEntity(val8, val4, (Vec3d)null);
									goto IL_04a5;
								}
							}
							ItemStack[] drops = item.Block.GetDrops(world, centerBlock, (IPlayer)(object)player, 1f);
							if (drops.Length == 1 && drops[0].Id == item.ItemStack.Id)
							{
								world.SpawnItemEntity(item.ItemStack, val4, (Vec3d)null);
							}
							else
							{
								destroyed = true;
								ItemStack[] array2 = drops;
								foreach (ItemStack val9 in array2)
								{
									world.SpawnItemEntity(val9, val4, (Vec3d)null);
									hadContents = true;
								}
							}
							goto IL_04a5;
							IL_04a5:
							AssetLocation val10 = (AssetLocation)(((object)item.Block.Sounds.GetBreakSound((IPlayer)(object)player)) ?? ((object)new AssetLocation("game:sounds/block/planks")));
							world.PlaySoundAt(val10, (double)centerBlock.X, (double)centerBlock.Y, (double)centerBlock.Z, (IPlayer)null, true, 32f, 1f);
							CarriedBlock.Remove(entity, item.Slot);
							world.GetCarryEvents()?.TriggerBlockDropped(world, centerBlock, entity, item, destroyed, hadContents);
						}
						break;
					}
				}
				BlockPos val11 = list[num3];
				if (Math.Abs(val11.Y - centerBlock.Y) <= vSize)
				{
					int num6 = Math.Sign(val11.Y - centerBlock.Y);
					Block testBlock = blockAccessor.GetBlock(val11);
					if (testBlock.BlockId == 0 || (array != null && array.Contains(((RegistryObject)testBlock).Class)))
					{
						airBlocks.Add(val11.Copy());
					}
					CarriedBlock carriedBlock = remaining.FirstOrDefault((CarriedBlock c) => testBlock.IsReplacableBy(c.Block));
					if (num6 == 0)
					{
						num6 = ((carriedBlock == null) ? 1 : (-1));
					}
					else if (num6 > 0)
					{
						TryDrop(val11, carriedBlock);
					}
					else if (carriedBlock == null)
					{
						BlockPos val12 = val11.UpCopy(1);
						testBlock = blockAccessor.GetBlock(val12);
						carriedBlock = remaining.FirstOrDefault((CarriedBlock c) => testBlock.IsReplacableBy(c.Block));
						TryDrop(val12, carriedBlock);
					}
					val11.Add(0, num6, 0);
				}
				if (++num4 > 3)
				{
					num4 = 0;
					num3++;
					if (num3 % 4 == 4 && ++num3 >= list.Count)
					{
						num3 = 0;
					}
				}
			}
			bool CanPlaceMultiblock(BlockPos position, CarriedBlock carriedBlock2)
			{
				if (carriedBlock2?.Behavior?.MultiblockOffset != (Vec3i)null)
				{
					BlockPos val13 = position.AddCopy(carriedBlock2.Behavior.MultiblockOffset);
					if (!blockAccessor.GetBlock(val13).IsReplacableBy(carriedBlock2.Block))
					{
						return false;
					}
				}
				return true;
			}
			bool Drop(BlockPos pos, CarriedBlock carriedBlock2)
			{
				//IL_0017: Unknown result type (might be due to invalid IL or missing references)
				//IL_001c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0035: Expected O, but got Unknown
				if (!CanPlaceMultiblock(pos, carriedBlock2))
				{
					return false;
				}
				string failureCode = null;
				if (!carriedBlock2.PlaceDown(ref failureCode, world, new BlockSelection
				{
					Position = pos
				}, (Entity)(object)((IPlayer)player).Entity, dropped: true))
				{
					return false;
				}
				CarriedBlock.Remove(entity, carriedBlock2.Slot);
				return true;
			}
			void TryDrop(BlockPos pos, CarriedBlock carriedBlock2)
			{
				if (carriedBlock2 != null)
				{
					if (Drop(pos, carriedBlock2))
					{
						remaining.Remove(carriedBlock2);
						airBlocks.Remove(pos);
					}
					else
					{
						airBlocks.Remove(pos);
					}
				}
			}
		}

		public static void DropAllCarried(this Entity entity, int hSize = 4, int vSize = 4)
		{
			entity.DropCarried(Enum.GetValues(typeof(CarrySlot)).Cast<CarrySlot>(), hSize, vSize);
		}

		public static bool Swap(this Entity entity, CarrySlot first, CarrySlot second)
		{
			if (first == second)
			{
				throw new ArgumentException("Slots can't be the same");
			}
			CarriedBlock carriedBlock = CarriedBlock.Get(entity, first);
			CarriedBlock carriedBlock2 = CarriedBlock.Get(entity, second);
			if (carriedBlock == null && carriedBlock2 == null)
			{
				return false;
			}
			CarriedBlock.Remove(entity, first);
			CarriedBlock.Remove(entity, second);
			carriedBlock?.Set(entity, second);
			carriedBlock2?.Set(entity, first);
			return true;
		}

		public static bool IsCarryKeyHeld(this Entity entity)
		{
			return ((TreeAttribute)entity.Attributes).GetBool("carryKeyHeld", false);
		}

		public static void SetCarryKeyHeld(this Entity entity, bool isHeld)
		{
			if (entity.IsCarryKeyHeld() != isHeld)
			{
				((TreeAttribute)entity.Attributes).SetBool("carryKeyHeld", isHeld);
			}
		}

		public static bool CanDoCarryAction(this EntityAgent entityAgent, bool requireEmptyHanded)
		{
			return ((Entity)entityAgent).World.GetCarrySystem().CarryHandler.CanDoCarryAction(entityAgent, requireEmptyHanded);
		}

		public static bool PlaceCarried(this IPlayer player, BlockSelection selection, CarrySlot slot, ref string failureCode)
		{
			if (player == null)
			{
				throw new ArgumentNullException("player");
			}
			if (selection == null)
			{
				throw new ArgumentNullException("selection");
			}
			if (!((Entity)player.Entity).World.Claims.TryAccess(player, selection.Position, (EnumBlockAccessFlags)1))
			{
				return false;
			}
			return CarriedBlock.Get((Entity)(object)player.Entity, slot)?.PlaceDown(ref failureCode, ((Entity)player.Entity).World, selection, (Entity)(object)player.Entity) ?? false;
		}

		public static CarrySystem GetCarrySystem(this IWorldAccessor world)
		{
			return world.Api.ModLoader.GetModSystem<CarrySystem>(true);
		}

		public static CarryEvents GetCarryEvents(this IWorldAccessor world)
		{
			return world.GetCarrySystem().CarryEvents;
		}
	}
	public enum CarrySlot
	{
		Hands,
		Back,
		Shoulder
	}
	public interface ICarryEvent
	{
		void Init(CarrySystem carrySystem);
	}
}

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Which generation of the Carry On API is loaded.
    /// </summary>
    public enum CarryOnApiGeneration
    {
        /// <summary>Carry On is not loaded, or its mod system could not be read.</summary>
        None,

        /// <summary>
        /// Carry On 1.x. It has no event before the block leaves the world, so the boombox
        /// must watch the "carryKeyHeld" entity attribute during the pickup animation.
        /// </summary>
        Legacy,

        /// <summary>
        /// Carry On 2.x (with CarryOnLib). It sends CarryEvents.BeforeRemoveBlockFromWorld
        /// on the line before the block is deleted, which is an exact pickup signal.
        /// </summary>
        Modern
    }

    /// <summary>
    /// Reflection bridge to whichever Carry On generation is loaded.
    ///
    /// Carry On 1.x kept the pickup state in the "carryKeyHeld" entity attribute. Carry On 2.x
    /// deleted that attribute and moved the state into an internal client state machine, but it
    /// added the public event CarryEvents.BeforeRemoveBlockFromWorld. The boombox uses the event
    /// when it is available and the attribute when it is not.
    ///
    /// All access is by reflection. The mod does not link to CarryOn.dll or CarryOnLib.dll, so a
    /// change in either mod cannot break the build or the load.
    /// </summary>
    public static class CarryOnApiBridge
    {
        private const string CARRY_SYSTEM_TYPE = "CarryOn.CarrySystem";
        private const string CARRY_EVENTS_PROPERTY = "CarryEvents";
        private const string BEFORE_REMOVE_EVENT = "BeforeRemoveBlockFromWorld";

        /// <summary>
        /// The generation detected on the last TryHookBeforeRemoveBlock call.
        /// </summary>
        public static CarryOnApiGeneration Generation { get; private set; } = CarryOnApiGeneration.None;

        // Kept so Unhook can detach the same delegate from the same event.
        private static EventInfo hookedEvent;
        private static object hookedEventOwner;
        private static Delegate hookedHandler;

        /// <summary>
        /// Subscribe to CarryEvents.BeforeRemoveBlockFromWorld.
        /// Returns true when the callback is attached, which means the caller can drop the
        /// legacy "carryKeyHeld" detection. Returns false for Carry On 1.x or for any
        /// reflection failure, and the caller must keep the legacy detection.
        /// </summary>
        /// <param name="api">Client API.</param>
        /// <param name="onBeforeRemove">Called with the block position, immediately before Carry On deletes the block.</param>
        public static bool TryHookBeforeRemoveBlock(ICoreClientAPI api, Action<BlockPos> onBeforeRemove)
        {
            Generation = CarryOnApiGeneration.None;

            if (api == null || onBeforeRemove == null) return false;

            try
            {
                var carrySystem = GetCarrySystem(api);
                if (carrySystem == null) return false;

                var eventsProperty = carrySystem.GetType().GetProperty(CARRY_EVENTS_PROPERTY);
                if (eventsProperty == null)
                {
                    Generation = CarryOnApiGeneration.Legacy;
                    return false;
                }

                // The event is declared on the property TYPE, so the generation is known even
                // when the instance is not built yet.
                var eventInfo = eventsProperty.PropertyType.GetEvent(BEFORE_REMOVE_EVENT);
                if (eventInfo == null)
                {
                    // Carry On 1.x: CarryEvents exists but has no pre-removal event.
                    Generation = CarryOnApiGeneration.Legacy;
                    return false;
                }

                Generation = CarryOnApiGeneration.Modern;

                var eventsInstance = eventsProperty.GetValue(carrySystem);
                if (eventsInstance == null)
                {
                    api.Logger.Warning($"[SoundPhysicsAdapted] Carry On {BEFORE_REMOVE_EVENT} found but CarryEvents is null - keeping legacy detection");
                    return false;
                }

                var handler = BuildHandler(eventInfo.EventHandlerType, onBeforeRemove);
                if (handler == null)
                {
                    api.Logger.Warning($"[SoundPhysicsAdapted] Carry On {BEFORE_REMOVE_EVENT} has an unexpected signature - keeping legacy detection");
                    return false;
                }

                eventInfo.AddEventHandler(eventsInstance, handler);

                hookedEvent = eventInfo;
                hookedEventOwner = eventsInstance;
                hookedHandler = handler;
                return true;
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[SoundPhysicsAdapted] Failed to hook Carry On {BEFORE_REMOVE_EVENT}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Detach the callback. Safe to call when nothing is attached.
        /// </summary>
        public static void Unhook()
        {
            try
            {
                if (hookedEvent != null && hookedEventOwner != null && hookedHandler != null)
                {
                    hookedEvent.RemoveEventHandler(hookedEventOwner, hookedHandler);
                }
            }
            catch { }

            hookedEvent = null;
            hookedEventOwner = null;
            hookedHandler = null;
            Generation = CarryOnApiGeneration.None;
        }

        /// <summary>
        /// Find the Carry On mod system. GetModSystem matches on the full type name, which both
        /// generations keep as "CarryOn.CarrySystem". The scan is a fallback for a renamed namespace.
        /// </summary>
        private static ModSystem GetCarrySystem(ICoreClientAPI api)
        {
            var system = api.ModLoader.GetModSystem(CARRY_SYSTEM_TYPE);
            if (system != null) return system;

            return api.ModLoader.Systems.FirstOrDefault(
                s => s.GetType().Name == "CarrySystem" && s.GetType().Namespace == "CarryOn");
        }

        /// <summary>
        /// Build a delegate of Carry On's own delegate type that forwards the BlockPos argument
        /// to our callback. The first parameter is Carry On's CarriedBlock, which this mod cannot
        /// name at compile time, so the delegate is compiled from an expression tree instead.
        /// </summary>
        private static Delegate BuildHandler(Type delegateType, Action<BlockPos> onBeforeRemove)
        {
            if (delegateType == null) return null;

            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null || invoke.ReturnType != typeof(void)) return null;

            var parameters = invoke.GetParameters();
            if (parameters.Length != 2 || parameters[1].ParameterType != typeof(BlockPos)) return null;

            var carriedBlockParam = Expression.Parameter(parameters[0].ParameterType, "carriedBlock");
            var posParam = Expression.Parameter(parameters[1].ParameterType, "pos");

            var body = Expression.Invoke(Expression.Constant(onBeforeRemove), posParam);

            return Expression.Lambda(delegateType, body, carriedBlockParam, posParam).Compile();
        }
    }
}

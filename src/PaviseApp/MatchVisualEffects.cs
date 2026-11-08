using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class MatchVisualEffects
	{
		internal sealed class Effect
		{
			internal readonly string Name;

			internal readonly uint Get;

			internal readonly uint Set;

			internal readonly bool UiParam;

			internal readonly bool Animation;

			internal Effect(string name, uint get, uint set, bool uiParam = false, bool animation = false)
			{
				Name = name;
				Get = get;
				Set = set;
				UiParam = uiParam;
				Animation = animation;
			}
		}

		private struct AnimationInfo
		{
			internal uint Size;

			internal int MinAnimate;
		}

		internal static readonly Effect[] Effects = new Effect[10]
		{
			new Effect("WindowAnimation", 72u, 73u, false, true),
			new Effect("ClientAnimation", 4162u, 4163u),
			new Effect("MenuAnimation", 4098u, 4099u),
			new Effect("ComboAnimation", 4100u, 4101u),
			new Effect("ListSmoothScroll", 4102u, 4103u),
			new Effect("SelectionFade", 4116u, 4117u),
			new Effect("TooltipAnimation", 4118u, 4119u),
			new Effect("CursorShadow", 4122u, 4123u),
			new Effect("DropShadow", 4132u, 4133u),
			new Effect("DragFullWindows", 38u, 37u, true)
		};

		internal static bool Enabled
		{
			get
			{
                // Retired. Primitive readers/writers remain only for owned-record recovery.
                return false;
			}
		}

		[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
		private static extern bool AnimationParameters(uint action, uint param, ref AnimationInfo value, uint flags);

		private static Effect Find(string target)
		{
			Effect[] effects = Effects;
			foreach (Effect effect in effects)
			{
				if (effect.Name == target)
				{
					return effect;
				}
			}
			throw new FormatException("Unknown desktop visual effect");
		}

		internal static string Read(string target)
		{
			SharedSpiManual.RequirePrimitive(target, false);
			Effect effect = Find(target);
			int value = 0;
			bool animationParametersSucceeded;
			if (effect.Animation)
			{
				AnimationInfo animationInfo = default(AnimationInfo);
				animationInfo.Size = (uint)Marshal.SizeOf(typeof(AnimationInfo));
				AnimationInfo value2 = animationInfo;
				animationParametersSucceeded = AnimationParameters(effect.Get, value2.Size, ref value2, 0u);
				value = value2.MinAnimate;
			}
			else
			{
				animationParametersSucceeded = Native.SystemParametersInfoGet(effect.Get, 0u, ref value, 0u);
			}
			if (!animationParametersSucceeded)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read desktop effect: " + target);
			}
			if (value != 0)
			{
				return "1";
			}
			return "0";
		}

		internal static bool Write(string target, string value)
		{
			SharedSpiManual.RequirePrimitive(target, true, value);
			Effect effect = Find(target);
			if (value != "0" && value != "1")
			{
				throw new FormatException("Invalid desktop effect value");
			}
			int minAnimateValue = ((value == "1") ? 1 : 0);
			if (effect.Animation)
			{
				AnimationInfo animationInfo = default(AnimationInfo);
				animationInfo.Size = (uint)Marshal.SizeOf(typeof(AnimationInfo));
				animationInfo.MinAnimate = minAnimateValue;
				AnimationInfo value2 = animationInfo;
				return AnimationParameters(effect.Set, value2.Size, ref value2, 2u);
			}
			return Native.SystemParametersInfoSet(effect.Set, effect.UiParam ? ((uint)minAnimateValue) : 0u, effect.UiParam ? IntPtr.Zero : new IntPtr(minAnimateValue), 2u);
		}
	}
}

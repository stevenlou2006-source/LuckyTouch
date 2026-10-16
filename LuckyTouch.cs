using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using AdvancedREPO.Config;

namespace LuckyTouch
{
	[BepInPlugin("dyxc666.LuckyTouch", "LuckyTouch", "1.2.1")]
	[BepInProcess("REPO.exe")]
	[BepInDependency("potatoepet.advancedrepo.config")]
	[BepInDependency("dyxc666.RandomValuablePrice")]
	public class LuckyTouchMod : BaseUnityPlugin
	{
		private readonly Harmony harmony = new Harmony("dyxc666.LuckyTouch");
		public static ManualLogSource StaticLogger { get; private set; }
		public static LuckyTouchMod Instance { get; private set; }

		// 配置项
		public static ConfigField<bool> EnableLuckSystem;
		public static ConfigField<float> LuckAccumulationRate;
		public static ConfigField<float> LuckDecayRate;
		public static ConfigField<float> LuckMaxDelta;
		public static ConfigField<float> LuckMin;
		public static ConfigField<float> LuckMax;
		public static ConfigField<string> HighLuckColorHex;
		public static ConfigField<string> LowLuckColorHex;
		public static ConfigField<bool> EnableVerboseLogging;

		// 运行时变量
		public static float CurrentLuck = 1.0f;
		public static float LastMultiplier = 1.0f;
		internal static Color highLuckColor = Color.green;
		internal static Color lowLuckColor = Color.red;

		private static FieldInfo dollarValueCurrentField;
		private static FieldInfo dollarValueOriginalField;
		private static MethodInfo colorStatesMethod;

		private void Awake()
		{
			Instance = this;
			StaticLogger = Logger;
			Logger.LogInfo("=========================================");
			Logger.LogInfo("LuckyTouch 模组 v1.2.1 已加载");
			Logger.LogInfo("作者: dyxc666");
			Logger.LogInfo("=========================================");

			CreateConfigEntries();
			ParseColorConfigs();

			HighLuckColorHex.ValueChanged += (s, a) => ParseColorConfigs();
			LowLuckColorHex.ValueChanged += (s, a) => ParseColorConfigs();

			try
			{
				InitializeReflection();
				CacheRefreshMethod();
				ApplyHarmonyPatches();

				Logger.LogInfo($"✅ 模组初始化成功");
				if (EnableVerboseLogging.Value)
				{
					Logger.LogInfo($"⚙️ 当前配置：积累={LuckAccumulationRate.Value}，衰减={LuckDecayRate.Value}，上限={LuckMaxDelta.Value}");
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"❌ 模组初始化失败: {ex.Message}");
			}
		}

		private void InitializeReflection()
		{
			dollarValueCurrentField = typeof(ValuableObject).GetField("dollarValueCurrent",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			dollarValueOriginalField = typeof(ValuableObject).GetField("dollarValueOriginal",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
		}

		private void CacheRefreshMethod()
		{
			colorStatesMethod = AccessTools.Method(typeof(PhysGrabber), "ColorStates", Type.EmptyTypes);
			if (colorStatesMethod == null)
			{
				Logger.LogWarning("未能找到 ColorStates 方法，颜色刷新可能不即时");
			}
		}

		private void ParseColorConfigs()
		{
			if (!ColorUtility.TryParseHtmlString(HighLuckColorHex.Value, out highLuckColor))
				highLuckColor = Color.green;
			if (!ColorUtility.TryParseHtmlString(LowLuckColorHex.Value, out lowLuckColor))
				lowLuckColor = Color.red;
		}

		// 辅助方法
		private ConfigField<bool> CreateBoolConfig(string section, string key, bool defaultValue, string description, bool sync = true)
		{
			return new ConfigBuilder<bool>("LuckyTouch")
				.File("LuckyTouch.cfg")
				.Section(section)
				.Key(key)
				.Default(defaultValue)
				.Description(description)
				.Range(false, true)
				.Sync(sync)
				.Build();
		}

		private ConfigField<float> CreateFloatConfig(string section, string key, float defaultValue, string description, float min, float max, bool sync = true)
		{
			return new ConfigBuilder<float>("LuckyTouch")
				.File("LuckyTouch.cfg")
				.Section(section)
				.Key(key)
				.Default(defaultValue)
				.Description(description)
				.Range(min, max)
				.Sync(sync)
				.Build();
		}

		private ConfigField<string> CreateStringConfig(string section, string key, string defaultValue, string description, bool sync = true)
		{
			return new ConfigBuilder<string>("LuckyTouch")
				.File("LuckyTouch.cfg")
				.Section(section)
				.Key(key)
				.Default(defaultValue)
				.Description(description)
				.Sync(sync)
				.Build();
		}

		private void CreateConfigEntries()
		{
			EnableLuckSystem = CreateBoolConfig("基础设置", "启用运气系统", true, "总开关");
			LuckAccumulationRate = CreateFloatConfig("平衡参数", "价格_积累比例", 0.03f, "价格变化绝对值计入运气的比例", 0f, 0.5f);
			LuckDecayRate = CreateFloatConfig("平衡参数", "自然衰减比例", 0.15f, "每次抓取后运气值向1.0衰减的比例", 0f, 0.5f);
			LuckMaxDelta = CreateFloatConfig("平衡参数", "单次变化上限", 0.15f, "每次运气值变化的最大绝对值", 0f, 1f);
			LuckMin = CreateFloatConfig("平衡参数", "运气最小值", 0.2f, "允许的最低运气值", 0.1f, 1f);
			LuckMax = CreateFloatConfig("平衡参数", "运气最大值", 2.2f, "允许的最高运气值", 1f, 5f);
			HighLuckColorHex = CreateStringConfig("视觉效果", "高运气颜色", "#00FF00", "涨价时抓取光束颜色 (十六进制)");
			LowLuckColorHex = CreateStringConfig("视觉效果", "低运气颜色", "#FF0000", "跌价时抓取光束颜色 (十六进制)");
			EnableVerboseLogging = CreateBoolConfig("调试", "详细日志输出", false, "输出核心运气变化数据", sync: false);
		}

		private void ApplyHarmonyPatches()
		{
			var grabStarted = AccessTools.Method(typeof(PhysGrabObject), "GrabStarted");
			if (grabStarted != null)
				harmony.Patch(grabStarted, postfix: new HarmonyMethod(typeof(GrabPatch).GetMethod("Postfix_GrabStarted")));

			var grabLink = AccessTools.Method(typeof(PhysGrabObject), "GrabLink");
			if (grabLink != null)
				harmony.Patch(grabLink, postfix: new HarmonyMethod(typeof(GrabPatch).GetMethod("Postfix_GrabLink")));

			var colorMethod = AccessTools.Method(typeof(PhysGrabber), "ColorStateSetColor");
			if (colorMethod == null)
				colorMethod = AccessTools.Method(typeof(PhysGrabber), "SetBeamColor");
			if (colorMethod != null)
				harmony.Patch(colorMethod, prefix: new HarmonyMethod(typeof(BeamColorPatch).GetMethod("Prefix")));
			else
				Logger.LogError("无法找到光束颜色方法！颜色反馈将不可用。");

			Logger.LogInfo("✅ 成功应用 Harmony 补丁");
		}

		public static float GetItemPrice(ValuableObject vo)
		{
			return dollarValueCurrentField != null ? (float)dollarValueCurrentField.GetValue(vo) : 0f;
		}

		public static void SetItemPrice(ValuableObject vo, float newPrice)
		{
			dollarValueCurrentField?.SetValue(vo, newPrice);
			dollarValueOriginalField?.SetValue(vo, newPrice);
		}

		public static PlayerAvatar GetGrabbingPlayer(PhysGrabObject pgo)
		{
			var prop = pgo.GetType().GetProperty("playerAvatar");
			if (prop != null)
				return prop.GetValue(pgo) as PlayerAvatar;

			var field = pgo.GetType().GetField("playerAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null)
				return field.GetValue(pgo) as PlayerAvatar;

			field = pgo.GetType().GetField("playerGrabbing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null)
				return field.GetValue(pgo) as PlayerAvatar;

			return null;
		}

		// 强制刷新光束颜色（立即调用 ColorStates）
		public static void RefreshBeamColor(PhysGrabObject pgo)
		{
			PlayerAvatar player = GetGrabbingPlayer(pgo);
			if (player != null && player.physGrabber != null && colorStatesMethod != null)
			{
				colorStatesMethod.Invoke(player.physGrabber, null);
			}
		}

		// 延迟一帧再次刷新（确保渲染更新）
		private IEnumerator DelayedRefresh(PhysGrabber grabber)
		{
			yield return null;
			colorStatesMethod?.Invoke(grabber, null);
		}

		public void DelayedRefreshBeamColor(PhysGrabber grabber)
		{
			StartCoroutine(DelayedRefresh(grabber));
		}
	}

	[HarmonyPatch]
	public static class GrabPatch
	{
		private static HashSet<int> processedItems = new HashSet<int>();

		public static void Postfix_GrabStarted(PhysGrabObject __instance) => ProcessGrab(__instance);
		public static void Postfix_GrabLink(PhysGrabObject __instance) => ProcessGrab(__instance);

		private static void ProcessGrab(PhysGrabObject pgo)
		{
			if (!LuckyTouchMod.EnableLuckSystem.Value) return;

			var vo = pgo.GetComponent<ValuableObject>();
			if (vo == null) return; // 只对贵重物品生效

			int id = pgo.gameObject.GetInstanceID();
			if (processedItems.Contains(id)) return;

			float originalPrice = LuckyTouchMod.GetItemPrice(vo);
			if (originalPrice <= 0) return;

			// 基础随机倍率（0.5~2.0）
			float baseMultiplier = UnityEngine.Random.Range(0.5f, 2.0f);
			float finalMultiplier = baseMultiplier * LuckyTouchMod.CurrentLuck;
			float newPrice = originalPrice * finalMultiplier;
			newPrice = Mathf.Clamp(newPrice, 1f, 1000000f);

			LuckyTouchMod.SetItemPrice(vo, newPrice);

			float priceChange = Mathf.Abs(newPrice - originalPrice);
			float luckDelta = priceChange * LuckyTouchMod.LuckAccumulationRate.Value;
			luckDelta = Mathf.Clamp(luckDelta, -LuckyTouchMod.LuckMaxDelta.Value, LuckyTouchMod.LuckMaxDelta.Value);

			if (newPrice > originalPrice)
				LuckyTouchMod.CurrentLuck += luckDelta;
			else
				LuckyTouchMod.CurrentLuck -= luckDelta;

			float decay = LuckyTouchMod.LuckDecayRate.Value;
			LuckyTouchMod.CurrentLuck = Mathf.Lerp(LuckyTouchMod.CurrentLuck, 1.0f, decay);
			LuckyTouchMod.CurrentLuck = Mathf.Clamp(LuckyTouchMod.CurrentLuck,
				LuckyTouchMod.LuckMin.Value, LuckyTouchMod.LuckMax.Value);

			LuckyTouchMod.LastMultiplier = finalMultiplier;
			processedItems.Add(id);

			if (LuckyTouchMod.EnableVerboseLogging.Value)
			{
				LuckyTouchMod.StaticLogger.LogInfo($"[运气] 物品: {pgo.name} | 基础倍率={baseMultiplier:F2} | 最终乘数={finalMultiplier:F2} | 运气变化={luckDelta:F2} | 当前运气={LuckyTouchMod.CurrentLuck:F2}");
			}

			// 强制刷新光束颜色（立即）
			LuckyTouchMod.RefreshBeamColor(pgo);
			// 再延迟一帧刷新一次，确保渲染更新
			PlayerAvatar player = LuckyTouchMod.GetGrabbingPlayer(pgo);
			if (player != null && player.physGrabber != null)
			{
				LuckyTouchMod.Instance.DelayedRefreshBeamColor(player.physGrabber);
			}
		}
	}

	[HarmonyPatch]
	public static class BeamColorPatch
	{
		public static void Prefix(PhysGrabber __instance, ref Color mainColor, ref Color emissionColor)
		{
			if (!LuckyTouchMod.EnableLuckSystem.Value) return;

			float mult = LuckyTouchMod.LastMultiplier;
			if (mult > 1.0f)
			{
				mainColor = LuckyTouchMod.highLuckColor;
				emissionColor = LuckyTouchMod.highLuckColor;
			}
			else if (mult < 1.0f)
			{
				mainColor = LuckyTouchMod.lowLuckColor;
				emissionColor = LuckyTouchMod.lowLuckColor;
			}
			// 等于1.0时不干预，使用游戏默认颜色
		}
	}
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;

namespace SmartSkills;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class SmartSkills : BaseUnityPlugin
{
	private const string ModName = "Smart Skills";
	private const string ModVersion = "1.0.2";
	private const string ModGUID = "org.bepinex.plugins.smartskills";

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<int> skillRecoveryBonus = null!;
	private static ConfigEntry<int> catchupBonus = null!;
	private static ConfigEntry<Toggle> swimmingSkillLoss = null!;
	private static ConfigEntry<int> swimmingSkillGainIncrease = null!;
	private static ConfigEntry<int> shieldAttackXpFactor = null!;
	private static ConfigEntry<Toggle> removeShieldExpireXp = null!;
	private static ConfigEntry<float> sneakBonusDamage = null!;
	private static ConfigEntry<float> sneakBonusExperience = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0,
	}

	private static readonly HashSet<Skills.SkillType> attackSkills = new()
	{
		Skills.SkillType.Axes,
		Skills.SkillType.Blocking,
		Skills.SkillType.Bows,
		Skills.SkillType.Clubs,
		Skills.SkillType.Crossbows,
		Skills.SkillType.Knives,
		Skills.SkillType.Pickaxes,
		Skills.SkillType.Polearms,
		Skills.SkillType.Spears,
		Skills.SkillType.Swords,
		Skills.SkillType.Unarmed,
		Skills.SkillType.ElementalMagic,
	};

	private static readonly HashSet<Skills.SkillType> skills = new(attackSkills)
	{
		Skills.SkillType.BloodMagic,
		(Skills.SkillType)Math.Abs("Dual Swords".GetStableHashCode()),
		(Skills.SkillType)Math.Abs("Dual Axes".GetStableHashCode()),
		(Skills.SkillType)Math.Abs("Dual Clubs".GetStableHashCode()),
		(Skills.SkillType)Math.Abs("Dual Knives".GetStableHashCode()),
		(Skills.SkillType)Math.Abs("Dual Offhand".GetStableHashCode()),
	};

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		skillRecoveryBonus = config("1 - General", "Skill recovery bonus", 100, new ConfigDescription("Bonus XP in percent skills gain until they reach their highest recorded level again.", new AcceptableValueRange<int>(0, 200)));
		catchupBonus = config("2 - Weapons", "Skill catch up bonus", 50, new ConfigDescription("Bonus XP in percent weapon skills gain until they have caught up to the players highest weapon skill level.", new AcceptableValueRange<int>(0, 200)));
		swimmingSkillLoss = config("3 - Swimming", "Swimming skill loss", Toggle.Off, "If off, the swimming skill will no longer lose XP on death.");
		swimmingSkillGainIncrease = config("3 - Swimming", "Swimming experience bonus", 100, new ConfigDescription("Bonus XP in percent the swimming skill gets.", new AcceptableValueRange<int>(0, 200)));
		shieldAttackXpFactor = config("4 - Blood Magic", "Shield attack XP factor", 33, new ConfigDescription("The caster of a shield will gain a percentage of the weapon XP the shielded attacker gets.", new AcceptableValueRange<int>(0, 100)));
		removeShieldExpireXp = config("4 - Blood Magic", "Remove shield expired XP", Toggle.On, "If on, the shielded player will not gain blood magic XP, if the shield expires.");
		sneakBonusDamage = config("5 - Sneak", "Sneak bonus damage", 50f,  new ConfigDescription("Bonus damage in percent unaware enemies take if attacked while sneaking at skill level 100.", new AcceptableValueRange<float>(0f, 200f)));
		sneakBonusExperience = config("5 - Sneak", "Sneak bonus experience", 20f,  new ConfigDescription("Bonus experience the sneak skill gains, if you attack an unaware enemy while sneaking.", new AcceptableValueRange<float>(0f, 200f)));

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
	private static class StoreHighestLevel
	{
		private static Skills.Skill? swimmingSkill = null;

		private static void Prefix(Skills __instance)
		{
			if (swimmingSkillLoss.Value == Toggle.Off)
			{
				__instance.m_skillData.TryGetValue(Skills.SkillType.Swim, out swimmingSkill);
				__instance.m_skillData.Remove(Skills.SkillType.Swim);
			}

			foreach (KeyValuePair<Skills.SkillType, Skills.Skill> skill in __instance.m_skillData)
			{
				if (!__instance.m_player.m_customData.TryGetValue($"SmartSkills {(int)skill.Key}", out string skillLevelString) || !float.TryParse(skillLevelString, out float skillLevel) || skillLevel < skill.Value.m_level)
				{
					__instance.m_player.m_customData[$"SmartSkills {(int)skill.Key}"] = skill.Value.m_level.ToString(CultureInfo.InvariantCulture);
				}
			}
		}

		private static void Finalizer(Skills __instance)
		{
			if (swimmingSkillLoss.Value == Toggle.Off && swimmingSkill is not null)
			{
				__instance.m_skillData.Add(Skills.SkillType.Swim, swimmingSkill);
			}
		}
	}

	[HarmonyPatch(typeof(Skills.Skill), nameof(Skills.Skill.Raise))]
	private static class AddCatchUp
	{
		private static void Prefix(Skills.Skill __instance, ref float factor)
		{
			if (__instance.m_info.m_skill == Skills.SkillType.Swim)
			{
				factor *= 1 + swimmingSkillGainIncrease.Value / 100f;
			}

			if (Player.m_localPlayer.m_customData.TryGetValue($"SmartSkills {(int)__instance.m_info.m_skill}", out string skillLevelString) && float.TryParse(skillLevelString, out float skillLevel) && skillLevel > __instance.m_level)
			{
				factor *= 1 + skillRecoveryBonus.Value / 100f;
			}

			if (skills.Contains(__instance.m_info.m_skill))
			{
				float highestSkillLevel = skills.Max(s => Player.m_localPlayer.m_skills.m_skillData.TryGetValue(s, out Skills.Skill skill) ? skill.m_level : 0);
				if (__instance.m_level < highestSkillLevel)
				{
					factor *= 1 + catchupBonus.Value / 100f;
				}
			}
		}
	}

	[HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), typeof(int), typeof(bool), typeof(int), typeof(float))]
	private static class StoreCaster
	{
		private static void Prefix(SEMan __instance, int nameHash)
		{
			if (nameHash == "Staff_shield".GetStableHashCode())
			{
				__instance.m_character.m_nview.m_zdo.Set("Shield Staff Caster", Player.m_localPlayer.GetZDOID());
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	private static class AddRPCs
	{
		private static void Postfix(Player __instance)
		{
			__instance.m_nview.Register<float>("SmartSkills RaiseBloodMagic", raiseBloodMagic);
		}
	}

	private static void raiseBloodMagic(long obj, float increase)
	{
		Player.m_localPlayer.RaiseSkill(Skills.SkillType.BloodMagic, increase);
	}

	[HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.IsDone))]
	private static class PreventXPOnExpire
	{
		private static void Prefix(SE_Shield __instance)
		{
			if (removeShieldExpireXp.Value == Toggle.On)
			{
				__instance.m_levelUpSkillOnBreak = Skills.SkillType.None;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Character.RaiseSkill))]
	private static class RaiseBloodMagic
	{
		private static void Postfix(Player __instance, Skills.SkillType skill, float value)
		{
			if (attackSkills.Contains(skill) && __instance.m_seman.GetStatusEffect("Staff_shield".GetStableHashCode()) is SE_Shield statusEffect)
			{
				ZDOID caster = __instance.m_nview.GetZDO().GetZDOID("Shield Staff Caster");
				if (caster != ZDOID.None)
				{
					ZRoutedRpc.instance.InvokeRoutedRPC(caster.UserID, caster, "SmartSkills RaiseBloodMagic", value * statusEffect.m_levelUpSkillFactor * shieldAttackXpFactor.Value / 100f);
				}
			}
		}
	}

	[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
	private static class IncreaseSneakDamage
	{
		private static void Prefix(Character __instance, HitData hit)
		{
			if (hit.GetAttacker() is Player player && __instance.GetBaseAI() is { m_alerted: false } baseAi && !baseAi.HaveTarget())
			{
				hit.m_backstabBonus *= 1 + player.GetSkillFactor(Skills.SkillType.Sneak) * (sneakBonusDamage.Value / 100f);
				player.RaiseSkill(Skills.SkillType.Sneak, sneakBonusExperience.Value);
			}
		}
	}
}

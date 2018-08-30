using Harmony;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CarefulRaids
{
	public class CarefulRaidsMod : Mod
	{
		public static int carefulRadius = 3;
		public static int expiringTime = 4 * GenDate.TicksPerHour;

		public CarefulRaidsMod(ModContentPack content) : base(content)
		{
			var harmony = HarmonyInstance.Create("net.pardeike.rimworld.mod.carefulraids");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
		}

		// debug careful grid
		//
		[HarmonyPatch(typeof(MapInterface))]
		[HarmonyPatch("MapInterfaceUpdate")]
		class MapInterface_MapInterfaceUpdate_Patch
		{
			static void Postfix()
			{
				if (DebugViewSettings.writePathCosts == false) return;

				var map = Find.CurrentMap;
				var currentViewRect = Find.CameraDriver.CurrentViewRect;
				currentViewRect.ClipInsideMap(map);
				foreach (var cell in currentViewRect)
				{
					var carefulCell = CarefulGrid.GetCell(map, cell);
					if (carefulCell != null)
					{
						var severity = carefulCell.DebugInfo();
						if (severity > 0.0f)
						{
							var pos = new Vector3(cell.x, Altitudes.AltitudeFor(AltitudeLayer.Pawn - 1), cell.z);
							var color = new Color(1f, 0f, 0f, severity);
							Tools.DebugPosition(pos, color);
						}
					}
				}
			}
		}

		// reset/load careful grid
		//
		[HarmonyPatch(typeof(Map))]
		[HarmonyPatch("MapPreTick")]
		static class Map_MapPreTick_Patch
		{
			static void Postfix(Map __instance)
			{
				CarefulGrid.GetMapGrid(__instance).Tick(__instance);
			}
		}

		[HarmonyPatch(typeof(RegionTypeUtility))]
		[HarmonyPatch(nameof(RegionTypeUtility.GetExpectedRegionType))]
		static class RegionTypeUtility_GetExpectedRegionType_Patch
		{
			static bool Prefix(ref RegionType __result, IntVec3 c, Map map)
			{
				var carefulCell = CarefulGrid.GetCell(map, c);
				if (carefulCell != null)
				{
					if (carefulCell.infos.Values
							.Where(info => GenTicks.TicksAbs < info.timestamp + expiringTime && info.costs >= 10000)
							.Select(info => info.faction)
							.Any())
					{
						__result = RegionType.Portal;
						return false;
					}
				}
				return true;
			}
		}

		[HarmonyPatch(typeof(RegionMaker))]
		[HarmonyPatch(nameof(RegionMaker.TryGenerateRegionFrom))]
		static class RegionMaker_TryGenerateRegionFrom_Patch
		{
			static Building_Door GetDoor(IntVec3 c, Map map)
			{
				var carefulCell = CarefulGrid.GetCell(map, c);
				if (carefulCell != null)
				{
					var forbiddenFactions = carefulCell.infos.Values
							.Where(info => GenTicks.TicksAbs < info.timestamp + expiringTime && info.costs >= 10000)
							.Select(info => info.faction);
					var factionManager = Find.FactionManager;
					if (forbiddenFactions.Any())
						return new FakeDoor(map, c, forbiddenFactions.ToList());
				}
				return c.GetDoor(map);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_GetDoor1 = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetDoor));
				var m_GetDoor2 = AccessTools.Method(typeof(RegionMaker_TryGenerateRegionFrom_Patch), nameof(RegionMaker_TryGenerateRegionFrom_Patch.GetDoor));
				return instructions.MethodReplacer(m_GetDoor1, m_GetDoor2);
			}
		}

		// mark careful grid when a pawn is downed or dies
		//
		[HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility))]
		[HarmonyPatch("TryGiveThoughts")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
		static class PawnDiedOrDownedThoughtsUtility_TryGiveThoughts_Patch
		{
			static void Postfix(Pawn victim, PawnDiedOrDownedThoughtsKind thoughtsKind)
			{
				if (victim.Faction.HostileTo(Faction.OfPlayer) == false) return;
				if (thoughtsKind != PawnDiedOrDownedThoughtsKind.Downed && thoughtsKind != PawnDiedOrDownedThoughtsKind.Died) return;

				var pos = victim.Position;
				var map = victim.Map;
				if (map == null || pos.InBounds(map) == false) return;

				var timestamp = GenTicks.TicksAbs;
				var maxRadius = carefulRadius * carefulRadius;
				var maxCost = thoughtsKind == PawnDiedOrDownedThoughtsKind.Died ? 10000 : 5000;
				var factionManager = Find.FactionManager;
				map.floodFiller.FloodFill(pos, vec =>
				{
					if (!vec.Walkable(map)) return false;
					if ((float)vec.DistanceToSquared(pos) > maxRadius) return false;
					var door = vec.GetEdifice(map) as Building_Door;
					if (door != null && !door.CanPhysicallyPass(victim)) return false;
					return true;

				}, vec =>
				{
					var costs = (maxRadius - (vec - pos).LengthHorizontalSquared) * maxCost / maxRadius;
					if (costs > 0)
						CarefulGrid.AddCell(victim, vec, new Info() { costs = costs, timestamp = timestamp, faction = victim.Faction });

				}, int.MaxValue, false);

				map.reachability.ClearCache();
				map.pathGrid.RecalculatePerceivedPathCostAt(pos);
				map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

				map.mapPawns.AllPawnsSpawned
					.ToArray()
					.Where(pawn => pawn != victim && pawn.Spawned && pawn.Faction.HostileTo(Faction.OfPlayer))
					.Where(pawn => pawn.Downed == false && pawn.Dead == false && pawn.InMentalState == false)
					.Where(pawn => pawn.pather?.curPath?.NodesReversed.Contains(pos) ?? false)
					.Do(pawn => pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true));
			}
		}

		[HarmonyPatch(typeof(PathFinder))]
		[HarmonyPatch("FindPath")]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) })]
		public static class PathFinder_FindPath_Patch
		{
			static int GetExtraCosts(Pawn pawn, int idx)
			{
				if (pawn.Faction.HostileTo(Faction.OfPlayer) == false) return 0;
				var info = CarefulGrid.GetCell(pawn.Map, idx)?.GetInfo(pawn);
				if (info == null) return 0;
				if (GenTicks.TicksAbs > info.timestamp + expiringTime) return 0;
				//Log.Warning("costs " + info.costs + " " + pawn.Name.ToStringShort);
				return info.costs;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var refIdx = list.FirstIndexOf(ins => ins.operand is int && (int)ins.operand == 600);
				if (refIdx > 0)
				{
					if (list[refIdx - 4].opcode != OpCodes.Ldloc_S)
						Log.Error("Cannot find grid index (Ldloc_S)");
					if (list[refIdx + 2].opcode != OpCodes.Stloc_S)
						Log.Error("Cannot find sum index (Stloc_S)");

					var gridIdx = list[refIdx - 4].operand;
					var sumIdx = list[refIdx + 2].operand;
					var insertIdx = refIdx + 3;
					var movedLabels = list[insertIdx].labels;
					if (movedLabels.Count != 2)
						Log.Error("Wrong number of jump labels (" + movedLabels.Count + " instead of 2)");
					list[insertIdx].labels = new List<Label>();

					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldloc_S, sumIdx) { labels = movedLabels });
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldloc_0));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldloc_S, gridIdx));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PathFinder_FindPath_Patch), "GetExtraCosts")));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Add));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Stloc_S, sumIdx));
				}
				else
					Log.Error("Cannot find path cost 600 in PathFinder.FindPath");

				foreach (var instr in list)
					yield return instr;
			}
		}

		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		public static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static readonly MethodInfo m_ShouldCollideWithPawns = AccessTools.Method(typeof(PawnUtility), "ShouldCollideWithPawns");
			static readonly MethodInfo m_HasDangerInPath = AccessTools.Method(typeof(Pawn_PathFollower_NeedNewPath_Patch), "HasDangerInPath");
			static readonly FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

			/*static void Postfix(bool __result, Pawn ___pawn)
			{
				if (__result && ___pawn != null && ___pawn.Name != null && ___pawn.Faction.HostileTo(Faction.OfPlayer))
					Log.Warning(___pawn.Name.ToStringShort + " needs new path");
			}*/

			static bool HasDangerInPath(Pawn_PathFollower __instance, Pawn pawn)
			{
				if (pawn.Faction.HostileTo(Faction.OfPlayer) == false) return false;

				var path = __instance.curPath;
				if (path.NodesLeftCount < 5) return false;
				var lookAhead = path.Peek(4);
				var destination = path.LastNode;
				if ((lookAhead - destination).LengthHorizontalSquared < 25) return false;

				var map = pawn.Map;
				var info = CarefulGrid.GetCell(pawn.Map, lookAhead)?.GetInfo(pawn);
				if (info == null) return false;
				if (GenTicks.TicksAbs > info.timestamp + expiringTime) return false;
				return (info.costs > 0);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndexOf(code => code.opcode == OpCodes.Call && code.operand == m_ShouldCollideWithPawns) - 1;
				if (idx > 0)
				{
					if (list[idx].opcode == OpCodes.Ldfld)
					{
						var jump = generator.DefineLabel();

						// here we should have a Ldarg_0 but original code has one with a label on it so we reuse it
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
						list.Insert(idx++, new CodeInstruction(OpCodes.Call, m_HasDangerInPath));
						list.Insert(idx++, new CodeInstruction(OpCodes.Brfalse, jump));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldc_I4_1));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ret));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>() { jump } }); // add the missing Ldarg_0 from original code here
					}
					else
						Log.Error("Cannot find Ldfld one instruction before " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");
				}
				else
					Log.Error("Cannot find " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}
	}
}
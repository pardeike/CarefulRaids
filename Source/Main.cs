using Verse;
using Harmony;
using System.Reflection;
using RimWorld;
using System.Linq;
using Verse.AI;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CarefulRaids
{
	public class CarefulRaidsMod : Mod
	{
		public static CarefulGrid grid = new CarefulGrid();
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

				var map = Find.VisibleMap;
				var currentViewRect = Find.CameraDriver.CurrentViewRect;
				currentViewRect.ClipInsideMap(map);
				foreach (var cell in currentViewRect)
				{
					var carefulCell = grid.GetCell(map, cell);
					if (carefulCell != null)
					{
						var severity = carefulCell.DebugInfo();
						if (severity > 0.0)
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
		[HarmonyPatch("FinalizeLoading")]
		static class Map_FinalizeLoading_Patch
		{
			static void Prefix(Map __instance)
			{
				// TODO replace with loading

				var id = __instance.uniqueID;
				grid.grids[id] = new CarefulMapGrid(__instance);
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
						grid.AddCell(victim, vec, new Info() { costs = costs, timestamp = timestamp, faction = victim.Faction });

				}, int.MaxValue, false);

				map.reachability.ClearCache();
				map.pathGrid.RecalculatePerceivedPathCostAt(pos);
				map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

				map.mapPawns.AllPawnsSpawned
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
				var info = grid.GetCell(pawn.Map, idx)?.GetInfo(pawn);
				if (info == null) return 0;
				if (GenTicks.TicksAbs > info.timestamp + expiringTime) return 0;
				return info.costs;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var refIdx = list.FirstIndexOf(ins => ins.operand is int && (int)ins.operand == 600);
				if (refIdx > 0)
				{
					var gridIdx = list[refIdx - 4].operand;
					var sumIdx = list[refIdx - 1].operand;
					var insertIdx = refIdx + 3;
					var movedLabels = list[insertIdx].labels;
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

		/*
		[HarmonyPatch(typeof(RegionMaker))]
		[HarmonyPatch("TryGenerateRegionFrom")]
		public static class RegionMaker_TryGenerateRegionFrom_Patch
		{
			public static Building_Door GetDoorReplacement(IntVec3 c, Map map)
			{
				Log.Warning("TryGenerateRegionFrom " + c);

				var carefulCell = grid.GetCell(map, c);
				if (carefulCell != null)
				{
					var forbiddenFactions = carefulCell.infos.Values
						.Where(info => info.costs == 10000)
						.Select(info => info.faction);
					if (forbiddenFactions.Any())
						return new FakeDoor(map, c, forbiddenFactions.ToList());
				}
				return c.GetDoor(map);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_GetDoor = AccessTools.Method(typeof(GridsUtility), "GetDoor");
				var m_GetDoorReplacement = AccessTools.Method(typeof(RegionMaker_TryGenerateRegionFrom_Patch), "GetDoorReplacement");
				return Transpilers.MethodReplacer(instructions, m_GetDoor, m_GetDoorReplacement);
			}
		}
		*/

		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		public static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static MethodInfo m_ShouldCollideWithPawns = AccessTools.Method(typeof(PawnUtility), "ShouldCollideWithPawns");
			static MethodInfo m_HasDangerInPath = AccessTools.Method(typeof(Pawn_PathFollower_NeedNewPath_Patch), "HasDangerInPath");
			static FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

			static bool HasDangerInPath(Pawn_PathFollower __instance, Pawn pawn)
			{
				if (pawn.Faction.HostileTo(Faction.OfPlayer) == false) return false;

				var path = __instance.curPath;
				if (path.NodesLeftCount < 5) return false;
				var lookAhead = path.Peek(4);
				var destination = path.LastNode;
				if ((lookAhead - destination).LengthHorizontalSquared < 25) return false;

				var map = pawn.Map;
				var info = grid.GetCell(pawn.Map, lookAhead)?.GetInfo(pawn);
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

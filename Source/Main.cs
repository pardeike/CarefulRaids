using Brrainz;
using HarmonyLib;
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
			var harmony = new Harmony("net.pardeike.rimworld.mod.carefulraids");
			harmony.PatchAll();

			CrossPromotion.Install(76561197973010050);
		}

		// debug careful grid
		//
		[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
		class MapInterface_MapInterfaceUpdate_Patch
		{
			public static void Postfix()
			{
				if (DebugViewSettings.writePathCosts == false) return;

				var map = Find.CurrentMap;
				var currentViewRect = Find.CameraDriver.CurrentViewRect;
				_ = currentViewRect.ClipInsideMap(map);
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
		[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
		static class Map_MapPreTick_Patch
		{
			public static void Postfix(Map __instance)
			{
				CarefulGrid.GetMapGrid(__instance).Tick(__instance);
			}
		}

		[HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
		static class RegionTypeUtility_GetExpectedRegionType_Patch
		{
			public static bool Prefix(ref RegionType __result, IntVec3 c, Map map)
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

		[HarmonyPatch(typeof(RegionMaker), nameof(RegionMaker.TryGenerateRegionFrom))]
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
		[HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility), nameof(PawnDiedOrDownedThoughtsUtility.TryGiveThoughts))]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
		static class PawnDiedOrDownedThoughtsUtility_TryGiveThoughts_Patch
		{
			public static void Postfix(Pawn victim, PawnDiedOrDownedThoughtsKind thoughtsKind)
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
				var deathCells = new HashSet<IntVec3>();
				map.floodFiller.FloodFill(pos, vec =>
				{
					if (!vec.Walkable(map)) return false;
					if ((float)vec.DistanceToSquared(pos) > maxRadius) return false;
					if (vec.GetEdifice(map) is Building_Door door && !door.CanPhysicallyPass(victim)) return false;
					return true;

				}, vec =>
				{
					var costs = (maxRadius - (vec - pos).LengthHorizontalSquared) * maxCost / maxRadius;
					if (costs > 0)
					{
						CarefulGrid.AddCell(victim, vec, new Info() { costs = costs, timestamp = timestamp, faction = victim.Faction });
						_ = deathCells.Add(vec);
					}
				}, int.MaxValue, false);

				Tools.UpdateFactionMapState(map, deathCells, victim.Faction.loadID);
			}
		}

		[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPath))]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode), typeof(PathFinderCostTuning) })]
		static class PathFinder_FindPath_Patch
		{
			static readonly MethodInfo m_CellToIndex_int_int = AccessTools.Method(typeof(CellIndices), nameof(CellIndices.CellToIndex), new Type[] { typeof(int), typeof(int) });
			static readonly FieldInfo f_TraverseParms_pawn = AccessTools.Field(typeof(TraverseParms), nameof(TraverseParms.pawn));
			static readonly MethodInfo m_GetExtraCosts = SymbolExtensions.GetMethodInfo(() => GetExtraCosts(null, 0));

			public static int GetExtraCosts(Pawn pawn, int idx)
			{
				if (pawn == null || pawn.Faction == null || pawn.Map == null) return 0;
				if (pawn.Faction.HostileTo(Faction.OfPlayer) == false) return 0;
				var info = CarefulGrid.GetCell(pawn.Map, idx)?.GetInfo(pawn);
				if (info == null) return 0;
				if (GenTicks.TicksAbs > info.timestamp + expiringTime) return 0;
				//Log.Warning("costs " + info.costs + " " + pawn.Name.ToStringShort);
				return info.costs;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
			{
				var list = instructions.ToList();
				while (true)
				{
					var t_PathFinderNodeFast = AccessTools.Inner(typeof(PathFinder), nameof(PathFinder.PathFinderNodeFast));
					var f_knownCost = AccessTools.Field(t_PathFinderNodeFast, nameof(PathFinder.PathFinderNodeFast.knownCost));

					var idx = list.FirstIndex(ins => ins.Calls(m_CellToIndex_int_int));
					if (idx < 0 || list[idx + 1].opcode != OpCodes.Stloc_S)
					{
						Log.Error($"Cannot find CellToIndex(n,n)/Stloc_S in {original.FullDescription()}");
						break;
					}
					var gridIdx = list[idx + 1].operand;

					var insertLoc = list.FirstIndex(ins => ins.opcode == OpCodes.Ldfld && (FieldInfo)ins.operand == f_knownCost);
					while (insertLoc >= 0 && insertLoc < list.Count)
					{
						if (list[insertLoc].opcode == OpCodes.Add) break;
						insertLoc++;
					}
					if (insertLoc < 0 || insertLoc >= list.Count())
					{
						Log.Error($"Cannot find Ldfld knownCost ... Add in {original.FullDescription()}");
						break;
					}

					var traverseParmsIdx = original.GetParameters().FirstIndex(info => info.ParameterType == typeof(TraverseParms)) + 1;

					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Add));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldarga_S, traverseParmsIdx));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldfld, f_TraverseParms_pawn));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldloc_S, gridIdx));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Call, m_GetExtraCosts));
					break;
				}

				foreach (var instr in list)
					yield return instr;
			}
		}

		[HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.NeedNewPath))]
		static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static readonly MethodInfo m_ShouldCollideWithPawns = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.ShouldCollideWithPawns));
			static readonly MethodInfo m_HasDangerInPath = SymbolExtensions.GetMethodInfo(() => HasDangerInPath(default, default));
			static readonly FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.pawn));

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

				var info = CarefulGrid.GetCell(pawn.Map, lookAhead)?.GetInfo(pawn);
				if (info == null) return false;
				if (GenTicks.TicksAbs > info.timestamp + expiringTime) return false;
				return (info.costs > 0);
			}

			public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndex(code => code.Calls(m_ShouldCollideWithPawns)) - 1;
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

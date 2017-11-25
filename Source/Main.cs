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
		// public static int expiringTime = GenDate.TicksPerHour;

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
						var cost = carefulCell.DebugInfo();
						if (cost > 0)
						{
							var pos = new Vector3(cell.x, Altitudes.AltitudeFor(AltitudeLayer.Pawn - 1), cell.z);
							var color = new Color(0f, 0f, 1f, GenMath.LerpDouble(0, 10000, 0.1f, 0.8f, cost));
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
			static void Repath(Pawn victim)
			{
				var map = victim.Map;
				// var notOlderThan = GenTicks.TicksAbs - expiringTime;
				map.mapPawns.AllPawnsSpawned
					.Where(pawn => pawn != victim && pawn.Spawned && pawn.Faction.HostileTo(Faction.OfPlayer))
					.Where(pawn => pawn.Downed == false && pawn.Dead == false && pawn.InMentalState == false)
					.Where(pawn => pawn.pather?.curPath != null)
					.Do(pawn =>
					{
						if (pawn.pather.curPath.NodesReversed
							.Select(node => grid.GetCell(map, node)?.GetInfo(pawn))
							.Any(info => info?.costs == 10000 /* && info?.timestamp > notOlderThan */))
							pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
					});
			}

			static void Postfix(Pawn victim, PawnDiedOrDownedThoughtsKind thoughtsKind)
			{
				if (victim.IsColonist) return;
				if (thoughtsKind != PawnDiedOrDownedThoughtsKind.Downed && thoughtsKind != PawnDiedOrDownedThoughtsKind.Died) return;
				if (victim.Faction.HostileTo(Faction.OfPlayer) == false) return;

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
						grid.AddCell(victim, vec, new Info() { costs = costs, timestamp = timestamp });

				}, int.MaxValue, false);

				map.pathGrid.RecalculatePerceivedPathCostAt(pos);
				Repath(victim);
			}
		}

		// honor careful grid in FindBestReachableMeleeTarget
		// 
		[HarmonyPatch]
		public static class AttackTargetFinder_FindBestReachableMeleeTarget_Patch
		{
			static bool IsCarefulPosition(Pawn pawn, IntVec3 pos)
			{
				if (pawn.IsColonist) return false;
				return grid.GetCell(pawn.Map, pos)?.GetInfo(pawn)?.costs == 10000;
			}

			static MethodBase TargetMethod()
			{
				var type = AccessTools.FirstInner(typeof(AttackTargetFinder), t => t.Name.Contains("FindBestReachableMeleeTarget"));
				return type.GetMethods(AccessTools.all).First(m => m.ReturnType == typeof(bool));
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase originalMethod)
			{
				var baseType = originalMethod.DeclaringType;
				var jump = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(baseType, "searcherPawn"));
				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AttackTargetFinder_FindBestReachableMeleeTarget_Patch), "IsCarefulPosition"));
				yield return new CodeInstruction(OpCodes.Brfalse, jump);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(jump);
				foreach (var instr in list)
					yield return instr;
			}
		}

		// honor careful grid in NeedNewPath
		// 
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		public static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static bool IsCarefulPosition(IntVec3 pos, Pawn pawn)
			{
				if (pawn.IsColonist) return false;
				return grid.GetCell(pawn.Map, pos)?.GetInfo(pawn)?.costs == 10000;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndexOf(ins => ins.opcode == OpCodes.Call && ins.operand == AccessTools.Method(typeof(GenGrid), "Walkable"));
				if (idx >= 4)
				{
					var jump = generator.DefineLabel();
					list[idx + 2].labels.Add(jump);

					idx -= 4;

					var vecInstr = new CodeInstruction(list[idx]);
					var pawnInstr1 = new CodeInstruction(list[idx + 1]);
					var pawnInstr2 = new CodeInstruction(list[idx + 2]);

					list.Insert(idx++, vecInstr); // ldloc vec2
					list.Insert(idx++, pawnInstr1); // lodarg_0
					list.Insert(idx++, pawnInstr2); // ldfld pawn
					list.Insert(idx++, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Pawn_PathFollower_NeedNewPath_Patch), "IsCarefulPosition")));
					list.Insert(idx++, new CodeInstruction(OpCodes.Brtrue, jump));
				}
				else
					Log.Error("Cannot find CALL Walkable in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}

		// honor careful grid in GetAdjacentCells.MoveNext
		// 
		[HarmonyPatch]
		public static class CellFinder_GetAdjacentCells_MoveNext_Patch
		{
			static bool IsCarefulPosition(IntVec3 pos, Pawn pawn)
			{
				if (pawn.IsColonist) return false;
				return grid.GetCell(pawn.Map, pos)?.GetInfo(pawn)?.costs == 10000;
			}

			static MethodBase TargetMethod()
			{
				var type = AccessTools.FirstInner(typeof(CellFinder), t => t.Name.Contains("GetAdjacentCells"));
				return AccessTools.Method(type, "MoveNext");
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndexOf(ins => ins.opcode == OpCodes.Call && ins.operand == AccessTools.Method(typeof(GenGrid), "Walkable"));
				if (idx >= 5)
				{
					var jump = generator.DefineLabel();
					list[idx + 2].labels.Add(jump);

					idx -= 5;

					var vecInstr1 = new CodeInstruction(list[idx]);
					var vecInstr2 = new CodeInstruction(list[idx + 1]);
					var pawnInstr1 = new CodeInstruction(list[idx + 2]);
					var pawnInstr2 = new CodeInstruction(list[idx + 3]);

					list.Insert(idx++, vecInstr1); // ldarg_0
					list.Insert(idx++, vecInstr2); // ldfld iterator
					list.Insert(idx++, pawnInstr1); // ldarg_0
					list.Insert(idx++, pawnInstr2); // ldfld pawn
					list.Insert(idx++, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CellFinder_GetAdjacentCells_MoveNext_Patch), "IsCarefulPosition")));
					list.Insert(idx++, new CodeInstruction(OpCodes.Brtrue, jump));
				}
				else
					Log.Error("Cannot find CALL Walkable in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}

		// honor careful grid in Region.Allows
		/*
		[HarmonyPatch(typeof(Region))]
		[HarmonyPatch("Allows")]
		public static class Region_Allows_Patch
		{

		}
		*/

		// apply careful grid costs to path calculation
		//
		/*
		[HarmonyPatch(typeof(PathFinder))]
		[HarmonyPatch("FindPath")]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) })]
		public static class PathFinder_FindPath_Patch
		{
			public static MethodInfo m_CellToIndex = AccessTools.Method(typeof(CellIndices), "CellToIndex", new Type[] { typeof(int), typeof(int) });
			public static MethodInfo m_IsCarefulPosition = AccessTools.Method(typeof(PathFinder_FindPath_Patch), "IsCarefulPosition");

			static bool IsCarefulPosition(Pawn pawn, int idx)
			{
				if (pawn.IsColonist) return false;
				return grid.GetCell(pawn.Map, idx)?.GetInfo(pawn)?.costs == 10000;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var idx = list.FindIndex(ins => ins.opcode == OpCodes.Callvirt && ins.operand == m_CellToIndex);
				if (idx > 0)
				{
					var indexLocalVarNr = list[idx + 1].operand;

					var br = list.GetRange(idx, list.Count - idx - 1).FirstOrDefault(ins => ins.opcode == OpCodes.Br);
					if (br != null)
					{
						var jumpLocation = br.operand;

						idx += 2;
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldloc_0));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldloc_S, indexLocalVarNr));
						list.Insert(idx++, new CodeInstruction(OpCodes.Call, m_IsCarefulPosition));
						list.Insert(idx++, new CodeInstruction(OpCodes.Brtrue, jumpLocation));
					}
					else
						Log.Error("Cannot find br IL_0bad in PathFinder.FindPath");
				}
				else
					Log.Error("Cannot find CellToIndex(int, int) in PathFinder.FindPath");

				foreach (var instr in list)
					yield return instr;
			}
		}
		*/

		/*
		[HarmonyPatch(typeof(PathFinder))]
		[HarmonyPatch("FindPath")]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) })]
		public static class PathFinder_FindPath_Patch
		{
			public static MethodInfo m_GetCosts = AccessTools.Method(typeof(PathFinder_FindPath_Patch), "GetCosts");
			static Dictionary<Map, TickManager> tickManagerCache = new Dictionary<Map, TickManager>();

			static int GetCosts(Pawn pawn, int idx)
			{
				if (pawn.IsColonist) return 0;
				return grid.GetCell(pawn.Map, idx)?.GetInfo(pawn)?.costs ?? 0;
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
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Call, m_GetCosts));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Add));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Stloc_S, sumIdx));
				}
				else
					Log.Error("Cannot find path cost 600 in PathFinder.FindPath");

				foreach (var instr in list)
					yield return instr;
			}
		}
		*/

		/*
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("TrySetNewPath")]
		public static class Pawn_PathFollower_GenerateNewPath_Patch
		{
			static void Postfix(Pawn_PathFollower __instance, ref bool __result)
			{
				if (__result == false) return;
				var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
				if (pawn.Faction.HostileTo(Faction.OfPlayer) == false) return;
				if (__instance.curPath.NodesReversed.Any(vec => (grid.GetMapGrid(pawn.Map).GetCell(vec)?.GetInfo(pawn)?.costs ?? 0) == 10000))
				{
					pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
					__result = false;
				}
			}
		}
		*/

		/*
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("GenerateNewPath")]
		public static class Pawn_PathFollower_GenerateNewPath_Patch
		{
			static MethodInfo m_ProcessPath = AccessTools.Method(typeof(Pawn_PathFollower_GenerateNewPath_Patch), "ProcessPath");
			static FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

			static PawnPath ProcessPath(PawnPath path, Pawn pawn)
			{
				if (pawn.Faction.HostileTo(Faction.OfPlayer))
				{
					if (path.Found && path.NodesReversed.Any(vec => (grid.GetMapGrid(pawn.Map).GetCell(vec)?.GetInfo(pawn)?.costs ?? 0) == 10000))
					{
						Log.Warning("Denied path for " + pawn + " at " + pawn.Position);
						return PawnPath.NotFound;
					}
					else
						Log.Warning((path.Found ? "New" : "No valid") + " path for " + pawn + " at " + pawn.Position);
				}
				return path;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				foreach (var instr in instructions)
				{
					if (instr.opcode == OpCodes.Ret)
					{
						yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = instr.labels };
						yield return new CodeInstruction(OpCodes.Ldfld, f_pawn);
						yield return new CodeInstruction(OpCodes.Call, m_ProcessPath);
						yield return new CodeInstruction(OpCodes.Ret);
					}
					else
						yield return instr;
				}
			}
		}
		*/

		// check for careful grid markers
		//
		/*
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("TryEnterNextPathCell")]
		public static class Pawn_PathFollower_TryEnterNextPathCell_Patch
		{
			static FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
			static MethodInfo m_BeCareful = AccessTools.Method(typeof(Pawn_PathFollower_TryEnterNextPathCell_Patch), "BeCareful");
			static MethodInfo m_PatherFailed = AccessTools.Method(typeof(Pawn_PathFollower), "PatherFailed");

			static bool BeCareful(Pawn_PathFollower pather, Pawn pawn)
			{
				var stop = (grid.GetMapGrid(pawn.Map).GetCell(pather.nextCell)?.GetInfo(pawn)?.costs ?? 0) == 10000;
				if (stop)
				{
					pather.StopDead();
					pawn.jobs.curDriver.EndJobWith(JobCondition.Incompletable);
				}

				return stop;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var jump = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, f_pawn);
				yield return new CodeInstruction(OpCodes.Call, m_BeCareful);
				yield return new CodeInstruction(OpCodes.Brfalse, jump);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(jump);
				foreach (var instr in list)
					yield return instr;
			}
		}
		*/

		/*
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		public static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static MethodInfo m_get_LengthHorizontalSquared = AccessTools.Method(typeof(IntVec3), "get_LengthHorizontalSquared");
			static MethodInfo m_CarefulInPath = AccessTools.Method(typeof(Pawn_PathFollower_NeedNewPath_Patch), "CarefulInPath");
			static FieldInfo f_pawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

			static bool CarefulInPath(Pawn_PathFollower __instance, Pawn pawn)
			{
				if (pawn.IsColonist) return false;

				var path = __instance.curPath;
				if (path.NodesLeftCount < carefulRadius) return false;
				var lookAhead = path.Peek(carefulRadius - 1);
				var destination = path.LastNode;

				var costs = grid.GetCell(pawn.Map, lookAhead)?.GetInfo(pawn)?.costs ?? 0;
				if (costs == 10000)
					Log.Warning(pawn.NameStringShort + " at " + pawn.Position + " avoids " + lookAhead);

				return costs == 10000;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FindLastIndex(code => code.opcode == OpCodes.Call && code.operand == m_get_LengthHorizontalSquared);
				if (idx > 0)
				{
					idx = list.FirstIndexOf(code => code.opcode == OpCodes.Ret && list.IndexOf(code) > idx) + 2;
					if (idx > 2)
					{
						var jump = generator.DefineLabel();

						// here we should have a Ldarg_0 but original code has one with a label on it so we reuse it
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
						list.Insert(idx++, new CodeInstruction(OpCodes.Call, m_CarefulInPath));
						list.Insert(idx++, new CodeInstruction(OpCodes.Brfalse, jump));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldc_I4_1));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ret));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>() { jump } }); // add the missing Ldarg_0 from original code here
					}
					else
						Log.Error("Cannot find OpCode.Ret after last " + m_get_LengthHorizontalSquared + " in Pawn_PathFollower.NeedNewPath");
				}
				else
					Log.Error("Cannot find " + m_get_LengthHorizontalSquared + " in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}
		*/

		// now, it is possible we surround a pawn with corpse blockers. in that case, we simply undo
		// our last placement. not perfect but maybe a nice surprise
		/*
		var reverted = false;
		map.mapPawns
			.AllPawnsSpawned
			.Where(p =>
				p != deadPawn &&
				(
					p.RaceProps.Humanlike ||
					p.RaceProps.IsMechanoid
				) &&
				p.Dead == false &&
				p.Downed == false &&
				p.Faction.HostileTo(Faction.OfPlayer)
			)
			.Do(p =>
			{
				if (reverted == false)
				{
					var possibleEscape = RCellFinder.RandomWanderDestFor(p, p.Position, 3f, (pawn, vec) => vec != p.Position && vec.Standable(map), Danger.Deadly);
					if (possibleEscape.IsValid == false)
					{
						// undo our last corpse blocker placement
						newBlockers.ForEach(cb => cb.Destroy());
						reverted = true;
					}
				}
			});
		*/
	}
}

using Verse;
using Harmony;
using System.Reflection;
using System.Linq;
using RimWorld;
using System;
using Verse.AI;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CarefulRaids
{
	[StaticConstructorOnStartup]
	static class Main
	{
		public readonly static ThingDef corpseBlockerDef = ThingDef.Named("CorpseBlocker");

		static Main()
		{
			var harmony = HarmonyInstance.Create("net.pardeike.rimworld.mod.carefulraids");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
		}
	}

	public class CorpseBlocker : Building_Door
	{
		public Faction blocksFaction;
		public int ticksRemaining = 3;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.LookReference(ref blocksFaction, "blocksFaction", false);
			Scribe_Values.LookValue(ref ticksRemaining, "ticksRemaining");
		}

		public override void TickLong()
		{
			ticksRemaining--;
			if (ticksRemaining == 0)
				DeSpawn();
		}
	}

	[HarmonyPatch(typeof(Pawn_PathFollower))]
	[HarmonyPatch("TryEnterNextPathCell")]
	static class Pawn_PathFollower_TryEnterNextPathCell_Patch
	{
		static bool Patch(Pawn pawn, Building building)
		{
			if (pawn.IsColonist)
			{
				var pos = pawn.Position;
				var map = pawn.Map;
				for (var m = 0; m < 9; m++)
				{
					var c = pos + GenAdj.AdjacentCellsAndInside[m];
					if (c.InBounds(map))
						map.thingGrid.ThingsAt(c).OfType<CorpseBlocker>()
							.Do(b => b.DeSpawn());
				}
				return false;
			}

			if (building == null) return false;
			var corpseBlocker = building as CorpseBlocker;
			if (corpseBlocker == null) return false;
			return (pawn.Faction == corpseBlocker.blocksFaction);
		}

		class TryEnterNextPathCell_Infix : CodeProcessor
		{
			int counter = 0;
			Label label;

			public override List<CodeInstruction> Start(ILGenerator generator, MethodBase original)
			{
				label = generator.DefineLabel();
				return null;
			}

			public override List<CodeInstruction> Process(CodeInstruction instruction)
			{
				if (++counter == 4 && instruction.opcode == OpCodes.Ldloc_0)
				{
					var type = typeof(Pawn_PathFollower);
					var f_pawn = AccessTools.Field(type, "pawn");
					var m_Patch = AccessTools.Method(typeof(Pawn_PathFollower_TryEnterNextPathCell_Patch), "Patch");
					var m_PatherFailed = AccessTools.Method(type, "PatherFailed");
					if (f_pawn == null || m_Patch == null || m_PatherFailed == null)
						throw new NullReferenceException();

					// if (PatchClass.Patch(this.pawn, b))
					// {
					//		this.PatherFailed();
					//		return;
					//	}
					instruction.labels.Add(label);
					return new List<CodeInstruction>()
					{
						new CodeInstruction(OpCodes.Ldarg_0),
						new CodeInstruction(OpCodes.Ldfld, f_pawn),
						new CodeInstruction(OpCodes.Ldloc_0),
						new CodeInstruction(OpCodes.Call, m_Patch),
						new CodeInstruction(OpCodes.Brfalse_S, label),
						new CodeInstruction(OpCodes.Ldarg_0),
						new CodeInstruction(OpCodes.Call, m_PatherFailed),
						new CodeInstruction(OpCodes.Ret),
						instruction
					};
				}
				return new List<CodeInstruction>() { instruction };
			}
		}

		static HarmonyProcessor ProcessorFactory(MethodBase original)
		{
			var processor = new HarmonyProcessor();
			processor.Add(new TryEnterNextPathCell_Infix());
			return processor;
		}
	}

	[HarmonyPatch(typeof(Pawn_HealthTracker))]
	[HarmonyPatch("Kill")]
	static class Pawn_HealthTracker_Patch
	{
		static void Prefix(Pawn_HealthTracker __instance)
		{
			var deadPawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
			var deathCenter = deadPawn.Position;
			var faction = deadPawn.Faction;
			if (faction.HostileTo(Faction.OfPlayer))
			{
				var map = deadPawn.Map;
				if (map != null)
				{
					var offsets = GenAdj.AdjacentCellsAndInside;
					for (var m = 0; m < 9; m++)
					{
						var c = deathCenter + offsets[m];
						if (GenGrid.InBounds(c, map) && !GenGrid.InNoBuildEdgeArea(c, map))
						{
							if (map.thingGrid.CellContains(c, Main.corpseBlockerDef) == false)
							{
								var firstAlivePawn = map.thingGrid.ThingsAt(c).OfType<Pawn>()
									.Where(p => p != deadPawn && p.Dead == false).FirstOrDefault();
								if (firstAlivePawn == null)
								{
									var edifice = c.GetEdifice(map);
									var isImpassable = (edifice != null && edifice.def.passability == Traversability.Impassable);
									if (isImpassable == false)
									{
										var blocker = (CorpseBlocker)ThingMaker.MakeThing(Main.corpseBlockerDef, ThingDef.Named("Gold"));
										blocker.SetFaction(Faction.OfPlayer);
										blocker.blocksFaction = faction;
										GenSpawn.Spawn(blocker, c, map);
									}
								}
							}
						}
					}

					// TODO: find out if this section works
					map.mapPawns.AllPawnsSpawned
						.Where(p => p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)
						.Do(p =>
						{
							var possibleEscape = RCellFinder.RandomWanderDestFor(p, p.Position, 2f, (pawn, vec) => vec != p.Position && GenGrid.Standable(vec, map), Danger.Deadly);
							if (possibleEscape.IsValid == false)
							{
								var m = Rand.RangeInclusive(0, 3);
								var c = p.Position;
								for (var i = 0; i < 5; i++)
								{
									map.thingGrid.ThingsAt(c).OfType<CorpseBlocker>()
										.Where(b => b.Faction == faction).Do(b => b.DeSpawn());
									c += GenAdj.CardinalDirections[m];
								}
							}
						});
					map.reachability.ClearCache();

					map.mapPawns.AllPawnsSpawned
						.Where(p => p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)
						.Where(p => p.Faction.HostileTo(Faction.OfPlayer))
						.Do(p =>
						{
							var needsNewJob = p.pather == null || p.pather.curPath == null;
							if (!needsNewJob)
							{
								List<IntVec3> nodesReversed = p.pather.curPath.NodesReversed;
								for (int i = nodesReversed.Count - 2; i >= 1; i--)
								{
									var edifice = nodesReversed[i].GetEdifice(p.Map) as CorpseBlocker;
									if (edifice != null && edifice.def == Main.corpseBlockerDef)
									{
										needsNewJob = true;
										break;
									}
								}
							}
							if (needsNewJob)
								p.jobs.EndCurrentJob(JobCondition.Incompletable, true);
						});
				}
			}
		}
	}

	// for logging jobs that want to attack our corpseblockers
	[HarmonyPatch(typeof(Pawn_JobTracker))]
	[HarmonyPatch("StartJob")]
	static class Pawn_JobTracker_StartJob_Patch
	{
		static void Prefix(Pawn_JobTracker __instance, Job newJob, ThinkNode jobGiver, ThinkTreeDef thinkTree)
		{
			if (newJob == null || newJob.targetA == null || newJob.targetA.Thing == null) return;
			if (newJob.targetA.Thing.def != Main.corpseBlockerDef) return;

			var pos = newJob.targetA.Thing.Position;
			FileLog.Log("PAWN " + Traverse.Create(__instance).Field("pawn").GetValue<Pawn>());
			FileLog.Log("POS " + pos.x + "," + pos.z);
			FileLog.Log(Environment.StackTrace);
			FileLog.Log("");
		}
	}
}
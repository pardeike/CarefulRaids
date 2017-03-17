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

	// our class that marks "no go" cells. will tick down and despawn after a while
	// - open to colonists it will be basically invisible
	// - impassible and unstandable to raiders in aggro state
	//
	public class CorpseBlocker : Building_Door
	{
		public Faction blocksFaction;
		public int ticksRemaining = 3; // number of long-ticks that this will live

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.LookReference(ref blocksFaction, "blocksFaction", false);
			Scribe_Values.LookValue(ref ticksRemaining, "ticksRemaining");
		}

		public new bool SlowsPawns
		{
			get
			{
				return false;
			}
		}

		public new bool Open
		{
			get
			{
				return true;
			}
		}

		public override bool BlocksPawn(Pawn p)
		{
			return !PawnCanOpen(p);
		}

		public new bool CanPhysicallyPass(Pawn p)
		{
			return PawnCanOpen(p);
		}

		public override bool PawnCanOpen(Pawn p)
		{
			return p.Faction.IsPlayer || p.MentalStateDef == MentalStateDefOf.PanicFlee;
		}

		public override void TickLong()
		{
			ticksRemaining--;
			if (ticksRemaining == 0)
				DeSpawn();
		}
	}

	// the main patch of this mod. we do two things here:
	// - make sure raiders do not attack our corpse blockers
	// - clear the area around colonists from corpse blockers
	//
	[HarmonyPatch(typeof(Pawn_PathFollower))]
	[HarmonyPatch("TryEnterNextPathCell")]
	static class Pawn_PathFollower_TryEnterNextPathCell_Patch
	{
		// this patch will be used in the il-code that is inserted below
		// returns false to jump over our custom code and into the normal routine
		//
		static bool Patch(Pawn pawn, Building building)
		{
			// clear corpse blockers around colonists
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

			// do not attack our corpse blockers
			if (building == null) return false;
			var corpseBlocker = building as CorpseBlocker;
			if (corpseBlocker == null) return false;
			return (pawn.Faction == corpseBlocker.blocksFaction && pawn.MentalStateDef != MentalStateDefOf.PanicFlee);
		}

		// this infix patch will insert a custom code segment after the first line of
		// code in the method Pawn_PathFollower.TryEnterNextPathCell
		//
		static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, IEnumerable<CodeInstruction> instructions)
		{
			int counter = 0;
			foreach (var instruction in instructions)
			{
				if (++counter == 4 && instruction.opcode == OpCodes.Ldloc_0)
				{
					var type = typeof(Pawn_PathFollower);
					var f_pawn = AccessTools.Field(type, "pawn");
					var m_Patch = AccessTools.Method(typeof(Pawn_PathFollower_TryEnterNextPathCell_Patch), "Patch");
					var m_PatherFailed = AccessTools.Method(type, "PatherFailed");
					if (f_pawn == null || m_Patch == null || m_PatherFailed == null)
						throw new NullReferenceException();

					// the first line in the original method is:
					// Building b = this.BuildingBlockingNextPathCell();

					// We will insert this code after it:
					/*
					 * if (PatchClass.Patch(this.pawn, b))
					 * {
					 *		this.PatherFailed();
					 *		return;
					 *	}
					 */
					var label = generator.DefineLabel();
					yield return new CodeInstruction(OpCodes.Ldarg_0);
					yield return new CodeInstruction(OpCodes.Ldfld, f_pawn);
					yield return new CodeInstruction(OpCodes.Ldloc_0);
					yield return new CodeInstruction(OpCodes.Call, m_Patch);
					yield return new CodeInstruction(OpCodes.Brfalse_S, label);
					yield return new CodeInstruction(OpCodes.Ldarg_0);
					yield return new CodeInstruction(OpCodes.Call, m_PatherFailed);
					yield return new CodeInstruction(OpCodes.Ret);
					instruction.labels.Add(label);
				}
				yield return instruction;
			}
		}
	}

	// for edge cases, we need to prevent attacks on corpse blockers
	//
	[HarmonyPatch(typeof(Pawn_JobTracker))]
	[HarmonyPatch("StartJob")]
	static class Pawn_JobTracker_StartJob_Patch
	{
		static bool Prefix(Pawn_JobTracker __instance, Job newJob, JobCondition lastJobEndCondition)
		{
			// if EndCurrentJob is called with condition ErroredPather or Errored we most likely have a "no path into base"
			// situation. we cut down the wait time to 60 ticks. seems to lag so turned off for now
			//
			// if (newJob.def == JobDefOf.Wait && newJob.expiryInterval == 250 && newJob.checkOverrideOnExpire == false)
			//	newJob.expiryInterval = 60;

			if (newJob == null || newJob.targetA == null || newJob.targetA.Thing == null) return true;
			return (newJob.targetA.Thing.def != Main.corpseBlockerDef);
		}
	}

	// some raider died, lets add corpse blockers around the dead body
	// (todo: should we also do this on downed raiders?)
	//
	[HarmonyPatch(typeof(Pawn_HealthTracker))]
	[HarmonyPatch("Kill")]
	static class Pawn_HealthTracker_Patch
	{
		// we need to run this as prefix or else we cannot cancel the job before the original method is run
		//
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
					var newBlockers = new List<Thing>();

					// loop through all 9 cells at the place of death
					var offsets = GenAdj.AdjacentCellsAndInside;
					for (var m = 0; m < 9; m++)
					{
						var c = deathCenter + offsets[m];
						if (GenGrid.InBounds(c, map) && !GenGrid.InNoBuildEdgeArea(c, map))
						{
							// make sure we do not stack corpse blockers
							if (map.thingGrid.CellContains(c, Main.corpseBlockerDef) == false)
							{
								// we want to avoid placing corpse blockers at places where there is an alive pawn
								var firstAlivePawn = map.thingGrid.ThingsAt(c).OfType<Pawn>()
									.Where(p => p != deadPawn && p.Dead == false).FirstOrDefault();
								if (firstAlivePawn == null)
								{
									// we place corpse blockers only at places that are walkable (so no walls)
									var edifice = c.GetEdifice(map);
									var isImpassable = (edifice != null && edifice.def.passability == Traversability.Impassable);
									if (isImpassable == false)
									{
										// create a corpse blocker and mark the faction it will block
										var blocker = (CorpseBlocker)ThingMaker.MakeThing(Main.corpseBlockerDef, ThingDef.Named("Gold"));
										blocker.SetFaction(Faction.OfPlayer);
										blocker.blocksFaction = faction;
										var cb = GenSpawn.Spawn(blocker, c, map);
										newBlockers.Add(cb);
									}
								}
							}
						}
					}

					// now, it is possible we surround a pawn with corpse blockers. in that case, we simply undo
					// our last placement. not perfect but maybe a nice surprise
					var reverted = false;
					map.mapPawns.AllPawnsSpawned
						.Where(p => p != deadPawn && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid) && p.Dead == false && p.Downed == false && p.Faction.HostileTo(Faction.OfPlayer))
						.Do(p =>
						{
							if (reverted == false)
							{
								var possibleEscape = RCellFinder.RandomWanderDestFor(p, p.Position, 3f, (pawn, vec) => vec != p.Position && GenGrid.Standable(vec, map), Danger.Deadly);
								if (possibleEscape.IsValid == false)
								{
									// undo our last corpse blocker placement
									newBlockers.ForEach(cb => cb.Destroy());
									reverted = true;
								}
							}
						});

					// todo: is this really necessary?
					map.reachability.ClearCache();

					// finally, we need to cancel all raider jobs that have a path through a corpse blocker
					map.mapPawns.AllPawnsSpawned
						.Where(p => p != deadPawn && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid) && p.Dead == false && p.Downed == false && p.Faction.HostileTo(Faction.OfPlayer))
						.Do(p =>
						{
							var needsNewJob = p.pather == null || p.pather.curPath == null;
							if (!needsNewJob)
							{
								var edifice = p.pather.nextCell == null ? null : p.pather.nextCell.GetEdifice(p.Map) as CorpseBlocker;
								if (edifice != null && edifice.def == Main.corpseBlockerDef)
									needsNewJob = true;
								else
								{
									List<IntVec3> nodesReversed = p.pather.curPath.NodesReversed;
									for (int i = nodesReversed.Count - 2; i >= 1; i--)
									{
										edifice = nodesReversed[i].GetEdifice(p.Map) as CorpseBlocker;
										if (edifice != null && edifice.def == Main.corpseBlockerDef)
										{
											needsNewJob = true;
											break;
										}
									}
								}
							}
							// end current job
							if (needsNewJob)
								p.jobs.EndCurrentJob(JobCondition.Incompletable, true);
						});
				}
			}
		}
	}
}
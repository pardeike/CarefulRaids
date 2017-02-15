using Verse;
using Harmony;
using System.Reflection;
using System.Linq;
using RimWorld;

namespace CarefulRaids
{
	[StaticConstructorOnStartup]
	static class Main
	{
		public readonly static ThingDef corpseBlocker = ThingDef.Named("CorpseBlocker");

		static Main()
		{
			var harmony = HarmonyInstance.Create("net.pardeike.rimworld.mod.carefulraids");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
		}
	}

	public class CorpseBlocker : Thing
	{
		public Faction faction;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.LookReference(ref faction, "faction", false);
		}
	}

	// Someone died, update forbidden locations
	//
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
							if (map.thingGrid.CellContains(c, Main.corpseBlocker) == false)
							{
								var firstAlivePawn = map.thingGrid.ThingsAt(c).OfType<Pawn>()
									.Where(p => p != deadPawn && p.Dead == false).FirstOrDefault();
								if (firstAlivePawn == null)
								{
									var edifice = c.GetEdifice(map);
									var isImpassable = (edifice != null && edifice.def.passability == Traversability.Impassable);
									if (isImpassable == false)
									{
										var blocker = (CorpseBlocker)ThingMaker.MakeThing(Main.corpseBlocker, null);
										blocker.faction = faction;
										GenSpawn.Spawn(blocker, c, map);
									}
								}
							}
						}
					}
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
				}
			}
		}
	}

	// Expired corpses clear forbidden locations
	//
	[HarmonyPatch(typeof(Corpse))]
	[HarmonyPatch("TickRare")]
	class Corpse_Patch
	{
		static void Postfix(Corpse __instance)
		{
			var age = GenDate.TicksPerDay / 15;
			if (__instance.Age >= age && __instance.Age <= age + GenTicks.TickRareInterval * 3)
			{
				var deadPawn = __instance.InnerPawn;
				var deathCenter = deadPawn.Position;
				var offsets = GenAdj.AdjacentCellsAndInside;
				for (var m = 0; m < 9; m++)
				{
					var c = deathCenter + offsets[m];
					var map = Find.VisibleMap;
					if (GenGrid.InBounds(c, map))
					{
						map.thingGrid.ThingsAt(c).OfType<CorpseBlocker>()
							.Do(b => b.DeSpawn());
					}
				}
			}
		}
	}

	// Clear forbidden locations on faction assault start
	//
	[HarmonyPatch(typeof(LordJob_AssaultColony))]
	[HarmonyPatch("CreateGraph")]
	static class LordJob_AssaultColony_Patch
	{
		static void Postfix(LordJob_AssaultColony __instance)
		{
			var faction = Traverse.Create(__instance).Field("assaulterFaction").GetValue<Faction>();
			if (faction != null)
			{
				Find.VisibleMap.listerThings.ThingsOfDef(Main.corpseBlocker)
					.OfType<CorpseBlocker>().Where(b => b.Faction == faction).Do(b => b.DeSpawn());
			}
		}
	}
}
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace CarefulRaids
{
	public class Info : IExposable
	{
		public Faction faction;
		public int timestamp;
		public int costs;

		public void ExposeData()
		{
			Scribe_References.Look(ref faction, "faction");
			Scribe_Values.Look(ref timestamp, "timestamp");
			Scribe_Values.Look(ref costs, "costs");
		}
	}

	//

	public class CarefulCell : IExposable
	{
		public Dictionary<int, Info> infos = new Dictionary<int, Info>();

		public Info GetInfo(Pawn pawn)
		{
			if (pawn?.Faction == null)
				return null;

			Info info;
			if (infos.TryGetValue(pawn.Faction.loadID, out info))
				return info;

			return null;
		}

		public void ExposeData()
		{
			Scribe_Collections.Look(ref infos, "infos");
		}

		public void AddInfo(int factionID, Info info)
		{
			infos[factionID] = info;
		}

		public float DebugInfo()
		{
			var expired = GenTicks.TicksAbs + CarefulRaidsMod.expiringTime;
			var maxCost = 0;
			foreach (var info in infos.Values)
			{
				if (info.timestamp < expired && info.costs > maxCost)
					maxCost = info.costs;
			}
			if (maxCost == 0) return 0f;
			return GenMath.LerpDouble(0, 10000, 0.1f, 0.8f, maxCost);
		}
	}

	//

	public class CarefulMapGrid : MapComponent
	{
		public CarefulCell[] grid;
		public int width;
		public int height;

		public int tickCounter;

		public CarefulMapGrid(Map map) : base(map)
		{
			width = map.Size.x;
			height = map.Size.z;
			grid = new CarefulCell[width * height];
		}

		public override void ExposeData()
		{
			base.ExposeData();

			Tools.Look(ref grid, "pheromones", new object[0]);
			Scribe_Values.Look(ref width, "width");
			Scribe_Values.Look(ref height, "height");
			Scribe_Values.Look(ref tickCounter, "tickCounter");

			if (width == 0 || height == 0)
			{
				width = (int)Mathf.Sqrt(grid.Length);
				height = width;
			}
		}

		public void Tick(Map map)
		{
			if (++tickCounter >= 120)
			{
				tickCounter = 0;
				var maxTimestamp = GenTicks.TicksAbs - CarefulRaidsMod.expiringTime;
				var nonEmptyCells = new List<KeyValuePair<IntVec3, CarefulCell>>();
				for (var x = 0; x < width; x++)
					for (var z = 0; z < height; z++)
					{
						var cell = grid[z * width + x];
						if (cell != null)
							nonEmptyCells.Add(new KeyValuePair<IntVec3, CarefulCell>(new IntVec3(x, 0, z), cell));
					}

				var oldDeathCells = new List<IntVec3>();
				var factionIDs = Find.World.factionManager.AllFactionsListForReading.Select(faction => faction.loadID);
				foreach (var factionID in factionIDs)
				{
					oldDeathCells.Clear();
					for (var x = 0; x < width; x++)
						for (var z = 0; z < height; z++)
						{
							var cell = grid[z * width + x];
							if (cell != null)
							{
								if (cell.infos.TryGetValue(factionID, out var info))
									if (info.timestamp < maxTimestamp)
									{
										oldDeathCells.Add(new IntVec3(x, 0, z));
										cell.infos.Remove(factionID);
										// Log.Warning("Fake door removed " + pair.key);
									}
							}
						}
					Tools.UpdateFactionMapState(map, oldDeathCells, factionID);
				}
			}
		}

		public CarefulCell GetCell(int index)
		{
			return grid[index];
		}

		public CarefulCell GetCell(IntVec3 position)
		{
			if (position.x < 0 || position.x >= width || position.z < 0 || position.z >= height)
				return null;
			return grid[(position.z * width) + position.x];
		}

		public void AddCell(int factionID, IntVec3 position, Info info)
		{
			if (position.x < 0 || position.x >= width || position.z < 0 || position.z >= height)
				return;
			var idx = (position.z * width) + position.x;
			var cell = grid[idx];
			if (cell == null)
			{
				cell = new CarefulCell();
				grid[idx] = cell;
			}
			cell.AddInfo(factionID, info);
		}
	}

	//

	public class CarefulGrid
	{
		public static CarefulMapGrid GetMapGrid(Map map)
		{
			return map.GetComponent<CarefulMapGrid>();
		}

		public static CarefulCell GetCell(Map map, IntVec3 position)
		{
			var grid = map.GetComponent<CarefulMapGrid>();
			return grid.GetCell(position);
		}

		public static CarefulCell GetCell(Map map, int index)
		{
			var grid = map.GetComponent<CarefulMapGrid>();
			return grid.GetCell(index);
		}

		public static void AddCell(Pawn pawn, IntVec3 position, Info info)
		{
			if (pawn?.Faction == null || pawn?.Map == null)
				return;

			var grid = pawn.Map.GetComponent<CarefulMapGrid>();
			grid.AddCell(pawn.Faction.loadID, position, info);
		}
	}
}
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace CarefulRaids
{
	public class Info
	{
		public Faction faction;
		public int timestamp;
		public int costs;
	}

	//

	public class CarefulCell
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

		public void AddInfo(int factionID, Info info)
		{
			infos[factionID] = info;
		}

		public float DebugInfo()
		{
			var costs = infos.Values.Max(info => info.costs);
			var timestamp = infos.Values.Max(info => info.timestamp);
			if (GenTicks.TicksAbs > timestamp + CarefulRaidsMod.expiringTime) return 0.0f;
			return GenMath.LerpDouble(0, 10000, 0.1f, 0.8f, costs);
		}
	}

	//

	public class CarefulMapGrid
	{
		public CarefulCell[] grid;
		public int width;
		public int height;

		public CarefulMapGrid(Map map)
		{
			width = map.Size.x;
			height = map.Size.z;
			grid = new CarefulCell[width * height];
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
		public Dictionary<int, CarefulMapGrid> grids = new Dictionary<int, CarefulMapGrid>();

		public CarefulMapGrid GetMapGrid(Map map)
		{
			var id = map.uniqueID;
			CarefulMapGrid mapGrid;
			if (grids.TryGetValue(id, out mapGrid) == false)
			{
				mapGrid = new CarefulMapGrid(map);
				grids[id] = mapGrid;
			}
			return mapGrid;
		}

		public CarefulCell GetCell(Map map, IntVec3 position)
		{
			return GetMapGrid(map).GetCell(position);
		}

		public CarefulCell GetCell(Map map, int index)
		{
			return GetMapGrid(map).GetCell(index);
		}

		public void AddCell(Pawn pawn, IntVec3 position, Info info)
		{
			if (pawn?.Faction == null || pawn?.Map == null)
				return;

			var id = pawn.Map.uniqueID;
			CarefulMapGrid mapGrid;
			if (grids.TryGetValue(id, out mapGrid) == false)
			{
				mapGrid = new CarefulMapGrid(pawn.Map);
				grids[id] = mapGrid;
			}
			mapGrid.AddCell(pawn.Faction.loadID, position, info);
		}
	}
}
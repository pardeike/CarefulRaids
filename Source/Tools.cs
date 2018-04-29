using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Harmony;

namespace CarefulRaids
{
	public static class Tools
	{
		public static void DebugPosition(Vector3 pos, Color color)
		{
			pos.y = Altitudes.AltitudeFor(AltitudeLayer.Pawn - 1);
			var material = SolidColorMaterials.SimpleSolidColorMaterial(color);
			DrawScaledMesh(MeshPool.plane10, material, pos + new Vector3(0.5f, 0f, 0.5f), Quaternion.identity, 1.0f, 1.0f);
		}

		public static void DrawScaledMesh(Mesh mesh, Material mat, Vector3 pos, Quaternion q, float mx, float my, float mz = 1f)
		{
			var s = new Vector3(mx, mz, my);
			var matrix = new Matrix4x4();
			matrix.SetTRS(pos, q, s);
			Graphics.DrawMesh(mesh, matrix, mat, 0);
		}
	}

	/*public class FakeDoor : Building_Door
	{
		public List<Faction> factions;

		public FakeDoor(Map map, IntVec3 pos, List<Faction> factions)
		{
			this.factions = factions;
			SetFaction(Faction.OfPlayer);
			Traverse.Create(this).Field("map").SetValue(map);
			SetPositionDirect(pos);

			Log.Warning("Fake door created " + pos);
		}

		public override bool BlocksPawn(Pawn p)
		{
			return factions.Contains(p.Faction);
		}

		public new bool CanPhysicallyPass(Pawn p)
		{
			return factions.Contains(p.Faction) == false;
		}
	}*/
}
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Harmony;
using System.Xml;

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

		public static void Look<T>(ref T[] list, string label, params object[] ctorArgs) where T : IExposable
		{
			if (Scribe.EnterNode(label) == false) return;

			try
			{
				if (Scribe.mode == LoadSaveMode.Saving)
				{
					if (list == null)
						Scribe.saver.WriteAttribute("IsNull", "True");
					else
					{
						foreach (var current in list)
						{
							var t2 = current;
							Scribe_Deep.Look<T>(ref t2, false, "li", ctorArgs);
						}
					}
				}
				else if (Scribe.mode == LoadSaveMode.LoadingVars)
				{
					var curXmlParent = Scribe.loader.curXmlParent;
					var xmlAttribute = curXmlParent.Attributes["IsNull"];
					if (xmlAttribute != null && xmlAttribute.Value.ToLower() == "true")
						list = null;
					else
					{
						list = new T[curXmlParent.ChildNodes.Count];
						var i = 0;
						foreach (var subNode2 in curXmlParent.ChildNodes)
							list[i++] = ScribeExtractor.SaveableFromNode<T>((XmlNode)subNode2, ctorArgs);
					}
				}
			}
			finally
			{
				Scribe.ExitNode();
			}
		}
	}

	public class FakeDoor : Building_Door
	{
		public List<Faction> factions;

		public FakeDoor(Map map, IntVec3 pos, List<Faction> factions) : base()
		{
			def = ThingDefOf.Door;
			this.factions = factions;
			SetFaction(Faction.OfPlayer);
			Traverse.Create(this).Field("map").SetValue(map);
			SetPositionDirect(pos);

			// Log.Warning("Fake door created " + pos);
		}

		public override bool BlocksPawn(Pawn p)
		{
			return factions.Contains(p.Faction);
		}

		public new bool CanPhysicallyPass(Pawn p)
		{
			return factions.Contains(p.Faction) == false;
		}
	}
}
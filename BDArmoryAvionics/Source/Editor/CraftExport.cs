using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Export the craft in the VAB/SPH as a 3D model (Wavefront OBJ + MTL, metres, Y up, nose toward -Z like most tools):
    /// every visible part mesh as it sits on the craft, with its colour. Opens in Blender (which converts to FBX), Flyout's
    /// workflow and other RCS / rendering tools. Written to KSP/Exports/&lt;craft&gt;.obj.
    /// </summary>
    public static class CraftExport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ExportShip()
        {
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0 || EditorLogic.RootPart == null) return "No craft to export.";
            string name = ship.shipName; foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "Exports"); Directory.CreateDirectory(dir);
            string obj = Path.Combine(dir, name + ".obj"), mtl = Path.Combine(dir, name + ".mtl");
            var root = EditorLogic.RootPart.transform;
            var sb = new StringBuilder(); var mb = new StringBuilder();
            sb.AppendLine($"# {ship.shipName} exported by ELE (KSP craft, metres)"); sb.AppendLine($"mtllib {name}.mtl");
            int vbase = 1, meshes = 0, tris = 0;
            foreach (var p in ship.parts)
            {
                foreach (var mf in p.GetComponentsInChildren<MeshFilter>(false))
                {
                    var mr = mf.GetComponent<MeshRenderer>(); var mesh = mf.sharedMesh;
                    if (mr == null || !mr.enabled || mesh == null || !mesh.isReadable) continue;
                    if (mf.gameObject.layer != p.gameObject.layer && mf.gameObject.layer != 0) continue;   // skip FX / overlays
                    string g = $"{p.partInfo.name}_{meshes}"; sb.AppendLine($"o {g}");
                    var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    // KSP craft frame (nose +Y, up -Z) -> export frame (nose -Z, up +Y)
                    foreach (var v in mesh.vertices) { var l = m.MultiplyPoint3x4(v); sb.AppendLine($"v {F(l.x)} {F(-l.z)} {F(-l.y)}"); }
                    var mats = mr.sharedMaterials;
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    {
                        var mat = sm < mats.Length ? mats[sm] : null;
                        string mn = $"m{meshes}_{sm}";
                        var col = mat != null && mat.HasProperty("_Color") ? mat.color : Color.grey;
                        mb.AppendLine($"newmtl {mn}"); mb.AppendLine($"Kd {F(col.r)} {F(col.g)} {F(col.b)}");
                        if (mat != null && mat.mainTexture != null) mb.AppendLine($"# texture {mat.mainTexture.name}");
                        sb.AppendLine($"usemtl {mn}");
                        var t = mesh.GetTriangles(sm);
                        bool flip = m.determinant < 0;   // mirrored parts
                        for (int i = 0; i < t.Length; i += 3)
                        {
                            int a = t[i] + vbase, b = t[i + 1] + vbase, c = t[i + 2] + vbase;
                            sb.AppendLine(flip ? $"f {a} {c} {b}" : $"f {a} {b} {c}");   // the frame change mirrors once, turning Unity's clockwise faces counter-clockwise as OBJ expects
                        }
                        tris += t.Length / 3;
                    }
                    vbase += mesh.vertexCount; meshes++;
                }
            }
            File.WriteAllText(obj, sb.ToString()); File.WriteAllText(mtl, mb.ToString());
            Debug.Log($"[BDArmoryAvionics]: exported {ship.shipName}: {meshes} meshes, {tris} triangles -> {obj}");
            ScreenMessages.PostScreenMessage($"Exported to KSP/Exports/{name}.obj ({tris} triangles)", 4f, ScreenMessageStyle.UPPER_CENTER);
            return $"Exported {meshes} meshes / {tris} triangles to Exports/{name}.obj";
        }

        static string F(float f) => f.ToString("0.#####", Inv);
    }
}

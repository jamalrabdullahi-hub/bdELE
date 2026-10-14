using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>Load a KSP .mu with PartTools' reader and log its bounds and transforms (batchmode: -executeMethod MeasureMu.Run -muPath X).</summary>
public static class MeasureMu
{
    public static void Run()
    {
        var args = System.Environment.GetCommandLineArgs();
        string path = args[System.Array.IndexOf(args, "-muPath") + 1];
        var asm = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("KSPPartTools.PartReader") != null);
        var reader = asm.GetType("KSPPartTools.PartReader");
        var urlFileT = asm.GetTypes().First(t => t.FullName == "UrlDir+UrlFile");
        object uf = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(urlFileT);
        foreach (var f in urlFileT.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            if (f.FieldType == typeof(string)) f.SetValue(uf, f.Name == "_path" ? path : f.Name == "_fileExtension" ? "mu" : System.IO.Path.GetFileNameWithoutExtension(path));
        var prop = urlFileT.GetProperty("fullPath"); Debug.Log("ELE-MU fullPath prop " + (prop != null));
        var BF = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        reader.GetMethod("SetDefaultShaders").Invoke(null, null);
        foreach (var n in new[] { "matDummies", "boneDummies", "textureDummies" }) { var f = reader.GetField(n, BF); f.SetValue(null, System.Activator.CreateInstance(f.FieldType)); }
        var fileF = reader.GetField("file", BF); if (fileF != null) fileF.SetValue(null, uf);
        var br = new BinaryReader(File.Open(path, FileMode.Open));
        br.ReadInt32(); int ver = br.ReadInt32(); br.ReadString();
        var verF = reader.GetField("fileVersion", BF); if (verF != null) verF.SetValue(null, ver);
        GameObject go = null;
        try { go = (GameObject)reader.GetMethod("ReadChild", BF).Invoke(null, new object[] { br, null }); }
        catch (System.Exception e) { Debug.Log("ELE-MU partial: " + (e.InnerException?.Message ?? e.Message)); }
        br.Close();
        if (go == null) { foreach (var g in Object.FindObjectsOfType<GameObject>()) if (g.transform.parent == null && g.GetComponentsInChildren<MeshFilter>(true).Length > 0) go = g; }
        if (go == null) { Debug.Log("ELE-MU read failed"); return; }
        var b = new Bounds(go.transform.position, Vector3.zero);
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = mf.GetComponent<Renderer>(); if (r == null) continue;
            b.Encapsulate(r.bounds);
        }
        Debug.Log($"ELE-MU bounds center {b.center} size {b.size}");
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var nsum = Vector3.zero; foreach (var n in mf.sharedMesh.normals) nsum += n;
            Debug.Log($"ELE-MU mesh {mf.name} verts {mf.sharedMesh.vertexCount} avgNormal(root) {go.transform.InverseTransformDirection(mf.transform.TransformDirection(nsum.normalized))} bounds {mf.sharedMesh.bounds.size}");
        }
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("button") || t.name.Contains("Screen") || t.name.Contains("screen")) Debug.Log($"ELE-MU {t.name} pos {go.transform.InverseTransformPoint(t.position)} fwd {go.transform.InverseTransformDirection(t.forward)} up {go.transform.InverseTransformDirection(t.up)}");
    }
}

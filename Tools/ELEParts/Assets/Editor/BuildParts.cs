using UnityEngine;
using UnityEditor;
using KSPPartTools;
using System.Linq;

/// <summary>Builds ELE parts from primitives and writes them as KSP .mu models (batchmode: -executeMethod BuildParts.Build).</summary>
public static class BuildParts
{
    const string Parts = @"C:\Users\jabdu\Documents\KSP-Mods\BDArmory\BDArmoryAvionics\GameData\BDArmoryAvionics\Parts\";

    static Material Mat(string name, Color c, float shine)
    {
        var m = new Material(Shader.Find("KSP/Specular")) { name = name };
        m.SetColor("_Color", c); m.SetColor("_SpecColor", new Color(0.5f, 0.5f, 0.5f)); m.SetFloat("_Shininess", shine);
        return m;
    }

    static GameObject Prim(PrimitiveType t, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool keepCollider)
    {
        var g = GameObject.CreatePrimitive(t); g.name = name;
        g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localScale = scale;
        g.GetComponent<MeshRenderer>().sharedMaterial = mat;
        if (!keepCollider) Object.DestroyImmediate(g.GetComponent<Collider>());
        return g;
    }

    static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat) => Prim(PrimitiveType.Cube, name, parent, pos, size, mat, false);

    static void Write(Transform root, string dir)
    {
        int tris = 0; foreach (var mf in root.GetComponentsInChildren<MeshFilter>()) tris += mf.sharedMesh.triangles.Length / 3;
        // PartWriter is internal to PartToolsEditor.dll
        var w = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("KSPPartTools.PartWriter")).First(t => t != null);
        w.GetMethod("Write", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Invoke(null, new object[] { "model", dir, root, false, PartTools.TextureFormat.PNG });
        Debug.Log($"ELE: {root.name} written, {tris} triangles");
        Object.DestroyImmediate(root.gameObject);
    }

    public static void Build()
    {
        SensorBall(); EngineBlock(); DynamicFrame();
        TankGun("TankGun105", 5.2f, 0.065f); TankGun("TankGun120", 5.3f, 0.075f); TankGun("TankGun125", 6.0f, 0.08f);
        Loaders();
        MfdProp();
        Helicopter();
        DynamicJet();
        HeliBatch2();
    }

    static GameObject Empty(string name, Transform parent, Vector3 pos, Quaternion rot)
    {
        var g = new GameObject(name); g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localRotation = rot; return g;
    }

    static void SetLayer(Transform t, int layer) { t.gameObject.layer = layer; foreach (Transform c in t) SetLayer(c, layer); }

    /// <summary>Helicopter batch 2: transform skeletons (the visible shapes are lofted at runtime from the part configs).</summary>
    static void HeliBatch2()
    {
        var ph = Mat("ELE_Placeholder", new Color(0.8f, 0.8f, 0.8f), 0.2f);
        // Chin gun turret: hangs under the nose; +Y faces the hull. aimRotate yaws, aimPitch pitches, fireTransform +Z.
        var g = new GameObject("HeliChinGun").transform;
        Prim(PrimitiveType.Cube, "placeholder", g, new Vector3(0, -0.2f, 0.3f), new Vector3(0.4f, 0.4f, 1f), ph, true);
        var yaw = Empty("aimRotate", g, Vector3.zero, Quaternion.identity).transform;
        var pitch = Empty("aimPitch", yaw, new Vector3(0, -0.26f, 0), Quaternion.identity).transform;
        Empty("fireTransform", pitch, new Vector3(0, 0, 1.14f), Quaternion.identity);
        Write(g, Parts + "HeliChinGun");

        // Mast radar dome: sits on the rotor head.
        var r = new GameObject("HeliMastRadar").transform;
        Prim(PrimitiveType.Sphere, "placeholder", r, new Vector3(0, 0.45f, 0), Vector3.one * 0.85f, ph, true);
        Empty("scanRotation", r, new Vector3(0, 0.45f, 0), Quaternion.identity);
        Write(r, Parts + "HeliMastRadar");

        // Tandem cockpit interior (IVA space: up +Y, nose -Z). Seats face the nose; portrait cameras look back at the faces.
        var iva = new GameObject("TandemIVA").transform;
        Empty("Seat1", iva, new Vector3(0, -0.35f, -1.2f), Quaternion.Euler(0, 180, 0));
        Empty("Seat2", iva, new Vector3(0, -0.4f, -2.4f), Quaternion.Euler(0, 180, 0));
        foreach (var (name, pos) in new[] { ("Camera1", new Vector3(0, 0.32f, -1.78f)), ("Camera2", new Vector3(0, 0.27f, -2.98f)) })
        {
            var cg = Empty(name, iva, pos, Quaternion.identity);
            var cam = cg.AddComponent<Camera>(); cam.fieldOfView = 55; cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f; cam.enabled = false;
        }
        SetLayer(iva, 16);
        Write(iva, Parts + @"..\Spaces\TandemIVA");
    }

    /// <summary>Dynamic Jet: transforms only (the shape is built at runtime); a plain cylinder for the parts-list icon.</summary>
    static void DynamicJet()
    {
        var m = Mat("ELE_JetPlaceholder", new Color(0.55f, 0.57f, 0.58f), 0.4f);
        var root = new GameObject("DynamicJet").transform;
        Prim(PrimitiveType.Cylinder, "placeholder", root, Vector3.zero, new Vector3(1.25f, 1.1f, 1.25f), m, true);
        new GameObject("jetRoot").transform.SetParent(root, false);
        var t = new GameObject("thrustTransform").transform; t.SetParent(root, false); t.localPosition = new Vector3(0, -1.4f, 0); t.localRotation = Quaternion.Euler(90, 0, 0);
        var s = new GameObject("smokePoint").transform; s.SetParent(root, false); s.localPosition = new Vector3(0, -1.4f, 0); s.localRotation = Quaternion.Euler(90, 0, 0);
        Write(root, Parts + "DynamicJet");
    }

    // Helicopter batch 1. Airframe convention (SPH): length along Y (nose +Y), top is -Z.
    static void Helicopter()
    {
        var olive = Mat("ELE_HeliOlive", new Color(0.28f, 0.31f, 0.22f), 0.2f);
        var dark = Mat("ELE_HeliDark", new Color(0.14f, 0.15f, 0.14f), 0.2f);
        var glass = Mat("ELE_HeliGlass", new Color(0.08f, 0.12f, 0.16f), 0.95f);
        var metal = Mat("ELE_HeliMetal", new Color(0.4f, 0.41f, 0.4f), 0.5f);
        string H = Parts + "Heli";

        // Tandem cockpit: gunner front-low, pilot rear-high, chin gun. Node at the rear (y = 0).
        var c = new GameObject("HeliCockpit").transform;
        Prim(PrimitiveType.Cube, "body", c, new Vector3(0, 1.6f, 0.25f), new Vector3(1.0f, 3.2f, 0.9f), olive, true);
        Prim(PrimitiveType.Cube, "nose", c, new Vector3(0, 3.5f, 0.3f), new Vector3(0.7f, 0.8f, 0.7f), olive, true);
        Box("noseTip", c, new Vector3(0, 3.95f, 0.35f), new Vector3(0.45f, 0.3f, 0.45f), olive);
        Box("canopyFront", c, new Vector3(0, 2.75f, -0.3f), new Vector3(0.78f, 1.0f, 0.45f), glass).transform.localRotation = Quaternion.Euler(-14, 0, 0);
        Box("canopyRear", c, new Vector3(0, 1.65f, -0.48f), new Vector3(0.84f, 1.1f, 0.62f), glass).transform.localRotation = Quaternion.Euler(-10, 0, 0);
        Box("canopyFrame", c, new Vector3(0, 2.2f, -0.62f), new Vector3(0.86f, 0.06f, 0.2f), dark);
        Box("sensorTurret", c, new Vector3(0, 4.15f, 0.2f), new Vector3(0.35f, 0.25f, 0.35f), dark);
        Prim(PrimitiveType.Cylinder, "chinTurret", c, new Vector3(0, 3.4f, 0.75f), new Vector3(0.34f, 0.12f, 0.34f), dark, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        Prim(PrimitiveType.Cylinder, "chinGun", c, new Vector3(0, 3.85f, 0.8f), new Vector3(0.07f, 0.45f, 0.07f), dark, false);
        // EVA airlock: a trigger tagged "Airlock" on the right side of the pilot's canopy (kerbals exit here; FreeIva hatches link to it)
        UnityEditorInternal.InternalEditorUtility.AddTag("Airlock");
        var al = new GameObject("airlock"); al.transform.SetParent(c, false); al.transform.localPosition = new Vector3(0.6f, 1.45f, -0.55f);
        var alc = al.AddComponent<BoxCollider>(); alc.size = new Vector3(0.1f, 0.8f, 0.6f); alc.isTrigger = true; al.tag = "Airlock";
        Write(c, H + "Cockpit");

        // Centre fuselage with engine deck and rotor mast base. Nodes front/back (Y), top (rotor), bottom.
        var f = new GameObject("HeliFuselage").transform;
        Prim(PrimitiveType.Cube, "body", f, Vector3.zero, new Vector3(1.4f, 4f, 1.6f), olive, true);
        Box("deck", f, new Vector3(0, 0, -0.95f), new Vector3(1.0f, 3f, 0.4f), olive);
        Prim(PrimitiveType.Cylinder, "mastBase", f, new Vector3(0, 0.3f, -1.2f), new Vector3(0.5f, 0.06f, 0.5f), dark, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        Box("skidL", f, new Vector3(-0.6f, 0.2f, 0.9f), new Vector3(0.08f, 3.4f, 0.08f), dark);
        Box("skidR", f, new Vector3(0.6f, 0.2f, 0.9f), new Vector3(0.08f, 3.4f, 0.08f), dark);
        Box("doorL", f, new Vector3(-0.705f, 0.6f, 0), new Vector3(0.02f, 1.2f, 1.0f), dark);
        Box("doorR", f, new Vector3(0.705f, 0.6f, 0), new Vector3(0.02f, 1.2f, 1.0f), dark);
        Write(f, H + "Fuselage");

        // Tail boom with fin and horizontal stabiliser. Front node y = 0, tail rotor node on the fin's right side.
        var t = new GameObject("HeliTailBoom").transform;
        Prim(PrimitiveType.Cube, "boom1", t, new Vector3(0, -1.2f, -0.2f), new Vector3(0.9f, 2.4f, 1.0f), olive, true);
        Prim(PrimitiveType.Cube, "boom2", t, new Vector3(0, -3.4f, -0.3f), new Vector3(0.65f, 2.2f, 0.75f), olive, true);
        Prim(PrimitiveType.Cube, "boom3", t, new Vector3(0, -5.5f, -0.35f), new Vector3(0.45f, 2.2f, 0.55f), olive, true);
        Prim(PrimitiveType.Cube, "fin", t, new Vector3(0, -6.7f, -1.1f), new Vector3(0.14f, 1.2f, 2.0f), olive, true).transform.localRotation = Quaternion.Euler(20, 0, 0);
        Box("stabiliser", t, new Vector3(0, -6.0f, -0.3f), new Vector3(2.2f, 0.6f, 0.07f), olive);
        Box("tailSkid", t, new Vector3(0, -6.4f, 0.2f), new Vector3(0.06f, 0.06f, 0.5f), dark);
        Write(t, H + "TailBoom");

        // Turboshaft pod (srf-attached on the fuselage side), with intake and IR-suppressed exhaust.
        var e = new GameObject("HeliTurboshaft").transform;
        Prim(PrimitiveType.Cylinder, "nacelle", e, new Vector3(0.36f, 0, 0), new Vector3(0.64f, 1.2f, 0.64f), olive, true);
        Prim(PrimitiveType.Cylinder, "intakeLip", e, new Vector3(0.36f, 1.18f, 0), new Vector3(0.7f, 0.05f, 0.7f), metal, false);
        Prim(PrimitiveType.Cylinder, "intake", e, new Vector3(0.36f, 1.21f, 0), new Vector3(0.5f, 0.02f, 0.5f), dark, false);
        Box("suppressor", e, new Vector3(0.45f, -1.45f, -0.1f), new Vector3(0.5f, 0.6f, 0.45f), dark).transform.localRotation = Quaternion.Euler(0, 0, -15);
        Box("pylon", e, new Vector3(0.1f, 0, 0), new Vector3(0.2f, 1.6f, 0.3f), olive);
        Write(e, H + "Turboshaft");

        // Main rotor: mast, hub and four 7 m blades on rotorSpin; thrustTransform points down (lift up).
        var r = new GameObject("HeliMainRotor").transform;
        Prim(PrimitiveType.Cylinder, "mast", r, new Vector3(0, 0.4f, 0), new Vector3(0.24f, 0.4f, 0.24f), metal, true);
        var spin = new GameObject("rotorSpin").transform; spin.SetParent(r, false); spin.localPosition = new Vector3(0, 0.85f, 0);
        Prim(PrimitiveType.Cylinder, "hub", spin, Vector3.zero, new Vector3(0.6f, 0.1f, 0.6f), dark, false);
        Prim(PrimitiveType.Cylinder, "hubCap", spin, new Vector3(0, 0.15f, 0), new Vector3(0.3f, 0.08f, 0.3f), metal, false);
        for (int i = 0; i < 4; i++)
        {
            var arm = new GameObject("bladeArm" + i).transform; arm.SetParent(spin, false); arm.localRotation = Quaternion.Euler(0, i * 90, 0);
            Box("blade" + i, arm, new Vector3(3.8f, 0, 0), new Vector3(7f, 0.05f, 0.5f), dark).transform.localRotation = Quaternion.Euler(6, 0, 0);
        }
        var thrust = new GameObject("thrustTransform").transform; thrust.SetParent(r, false); thrust.localPosition = new Vector3(0, 0.85f, 0); thrust.localRotation = Quaternion.Euler(90, 0, 0);
        Write(r, H + "MainRotor");

        // Tail rotor: four 1.3 m blades on tailSpin (spins about its Y).
        var tr = new GameObject("HeliTailRotor").transform;
        Prim(PrimitiveType.Cylinder, "shaft", tr, new Vector3(0, 0.12f, 0), new Vector3(0.14f, 0.12f, 0.14f), metal, true);
        var ts = new GameObject("tailSpin").transform; ts.SetParent(tr, false); ts.localPosition = new Vector3(0, 0.28f, 0);
        Prim(PrimitiveType.Cylinder, "tailHub", ts, Vector3.zero, new Vector3(0.22f, 0.05f, 0.22f), dark, false);
        Box("tailBladesA", ts, Vector3.zero, new Vector3(2.6f, 0.03f, 0.22f), dark);
        Box("tailBladesB", ts, Vector3.zero, new Vector3(0.22f, 0.03f, 2.6f), dark);
        Write(tr, H + "TailRotor");

        // Stub wing: extends +X from the attach point, two pylons underneath (+Z) with stack nodes.
        var w = new GameObject("HeliStubWing").transform;
        Prim(PrimitiveType.Cube, "wing", w, new Vector3(1.1f, 0, 0), new Vector3(2.2f, 0.9f, 0.12f), olive, true);
        Box("pylonInner", w, new Vector3(0.8f, 0, 0.2f), new Vector3(0.1f, 0.6f, 0.28f), dark);
        Box("pylonOuter", w, new Vector3(1.65f, 0, 0.2f), new Vector3(0.1f, 0.6f, 0.28f), dark);
        Box("tipRail", w, new Vector3(2.2f, 0, 0), new Vector3(0.08f, 0.8f, 0.14f), dark);
        Write(w, H + "StubWing");
    }

    /// <summary>IVA multi-function display prop: face normal +Y, screen top +Z. Buttons have colliders for clicking.</summary>
    static void MfdProp()
    {
        var bezel = Mat("ELE_MfdBezel", new Color(0.13f, 0.14f, 0.14f), 0.2f);
        var key = Mat("ELE_MfdKey", new Color(0.28f, 0.3f, 0.3f), 0.3f);
        var glass = Mat("ELE_MfdGlass", Color.black, 0.9f);
        var root = new GameObject("ELE_MFD").transform;
        var m = new GameObject("mfdRoot").transform; m.SetParent(root, false);
        Box("bezel", m, Vector3.zero, new Vector3(0.26f, 0.02f, 0.23f), bezel);
        var scr = GameObject.CreatePrimitive(PrimitiveType.Quad); scr.name = "screen"; Object.DestroyImmediate(scr.GetComponent<Collider>());
        scr.transform.SetParent(m, false); scr.transform.localPosition = new Vector3(-0.015f, 0.0105f, 0.01f);
        scr.transform.localRotation = Quaternion.Euler(90, 0, 0); scr.transform.localScale = new Vector3(0.18f, 0.16f, 1);
        scr.GetComponent<MeshRenderer>().sharedMaterial = glass;
        float[] bx = { -0.09f, -0.0525f, -0.015f, 0.0225f, 0.06f };
        for (int i = 0; i < 5; i++) Prim(PrimitiveType.Cube, "btnPage" + i, m, new Vector3(bx[i], 0.014f, -0.095f), new Vector3(0.03f, 0.012f, 0.018f), key, true);
        float[] rz = { 0.065f, 0.03f, -0.005f, -0.04f };
        for (int i = 0; i < 4; i++) Prim(PrimitiveType.Cube, "btnAct" + i, m, new Vector3(0.105f, 0.014f, rz[i]), new Vector3(0.024f, 0.012f, 0.024f), key, true);
        Write(root, Parts + @"..\Props\ELE_MFD");
    }

    static void SensorBall()
    {
        var body = Mat("ELE_SensorBody", new Color(0.32f, 0.34f, 0.33f), 0.2f);
        var ball = Mat("ELE_SensorBall", new Color(0.62f, 0.64f, 0.62f), 0.35f);
        var glass = Mat("ELE_SensorGlass", new Color(0.04f, 0.06f, 0.12f), 0.9f);

        var root = new GameObject("SensorBall").transform;
        Prim(PrimitiveType.Cylinder, "basePlate", root, new Vector3(0, 0.02f, 0), new Vector3(0.3f, 0.02f, 0.3f), body, false);
        Prim(PrimitiveType.Cylinder, "mast", root, new Vector3(0, 0.22f, 0), new Vector3(0.12f, 0.2f, 0.12f), body, true);
        Prim(PrimitiveType.Cylinder, "yokeRing", root, new Vector3(0, 0.42f, 0), new Vector3(0.2f, 0.02f, 0.2f), body, false);

        // eyeHolderTransform: the gimbal BDArmory's targeting camera rotates. +Z is the look direction.
        var eye = new GameObject("eyeHolderTransform").transform; eye.SetParent(root, false); eye.localPosition = new Vector3(0, 0.56f, 0);
        Prim(PrimitiveType.Sphere, "ball", eye, Vector3.zero, Vector3.one * 0.28f, ball, true);
        Prim(PrimitiveType.Cylinder, "lens", eye, new Vector3(0, 0.02f, 0.13f), new Vector3(0.12f, 0.01f, 0.12f), glass, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        Prim(PrimitiveType.Cylinder, "laserPort", eye, new Vector3(0, -0.06f, 0.125f), new Vector3(0.05f, 0.01f, 0.05f), glass, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        var cam = new GameObject("camTransform").transform; cam.SetParent(eye, false); cam.localPosition = new Vector3(0, 0.02f, 0.15f);
        Write(root, Parts + "SensorBall");
    }

    /// <summary>V-engine block, under 500 triangles. Y up, sits on its bottom node; +Z is the front (radiator end).</summary>
    static void EngineBlock()
    {
        var iron = Mat("ELE_EngineIron", new Color(0.22f, 0.23f, 0.24f), 0.25f);
        var paint = Mat("ELE_EnginePaint", new Color(0.45f, 0.12f, 0.08f), 0.4f);
        var steel = Mat("ELE_EngineSteel", new Color(0.6f, 0.6f, 0.58f), 0.6f);
        var rad = Mat("ELE_EngineRadiator", new Color(0.1f, 0.1f, 0.1f), 0.1f);
        var root = new GameObject("EngineBlock").transform;
        Prim(PrimitiveType.Cube, "crankcase", root, new Vector3(0, 0.3f, 0), new Vector3(0.5f, 0.36f, 1.0f), iron, true);
        Box("oilPan", root, new Vector3(0, 0.07f, -0.05f), new Vector3(0.42f, 0.14f, 0.8f), iron);
        for (int side = -1; side <= 1; side += 2)
        {
            Box("head" + side, root, new Vector3(side * 0.2f, 0.54f, 0), new Vector3(0.26f, 0.16f, 0.94f), iron).transform.localRotation = Quaternion.Euler(0, 0, -side * 30);
            Box("valveCover" + side, root, new Vector3(side * 0.26f, 0.64f, 0), new Vector3(0.2f, 0.07f, 0.88f), paint).transform.localRotation = Quaternion.Euler(0, 0, -side * 30);
            Box("exhaustManifold" + side, root, new Vector3(side * 0.36f, 0.42f, 0), new Vector3(0.06f, 0.08f, 0.82f), steel);
            Prim(PrimitiveType.Cylinder, "exhaustStack" + side, root, new Vector3(side * 0.36f, 0.62f, -0.46f), new Vector3(0.07f, 0.24f, 0.07f), steel, false);
        }
        Box("intakePlenum", root, new Vector3(0, 0.7f, 0), new Vector3(0.18f, 0.1f, 0.8f), steel);
        for (int i = 0; i < 4; i++) Box("runner" + i, root, new Vector3(0, 0.62f, -0.3f + i * 0.2f), new Vector3(0.3f, 0.05f, 0.06f), steel);
        Box("radiator", root, new Vector3(0, 0.42f, 0.62f), new Vector3(0.56f, 0.58f, 0.08f), rad);
        Prim(PrimitiveType.Cylinder, "pulley", root, new Vector3(0, 0.22f, 0.53f), new Vector3(0.22f, 0.02f, 0.22f), steel, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        Write(root, Parts + "EngineBlock");
    }

    /// <summary>Placeholder mesh for the Dynamic Frame; ModuleDynamicFrame replaces it at runtime.</summary>
    static void DynamicFrame()
    {
        var m = Mat("ELE_Frame", new Color(0.33f, 0.36f, 0.22f), 0.15f);
        var root = new GameObject("DynamicFrame").transform;
        Prim(PrimitiveType.Cube, "frameMesh", root, Vector3.zero, new Vector3(2f, 3f, 0.8f), m, true);
        Write(root, Parts + "DynamicFrame");
    }

    static GameObject CylZ(string name, Transform parent, Vector3 pos, float radius, float length, Material mat, bool collider = false)
    {
        var g = Prim(PrimitiveType.Cylinder, name, parent, pos, new Vector3(radius * 2, length / 2, radius * 2), mat, collider);
        g.transform.localRotation = Quaternion.Euler(90, 0, 0); return g;
    }

    /// <summary>Tank gun on a low yaw ring: aimRotate (yaw) / aimPitch (pitch) / fireTransform at the muzzle, +Z forward.</summary>
    static void TankGun(string name, float barrel, float r)
    {
        var steel = Mat("ELE_GunSteel", new Color(0.3f, 0.32f, 0.28f), 0.3f);
        var dark = Mat("ELE_GunDark", new Color(0.12f, 0.12f, 0.12f), 0.2f);
        var root = new GameObject(name).transform;
        var yaw = new GameObject("aimRotate").transform; yaw.SetParent(root, false);
        Prim(PrimitiveType.Cylinder, "ring", yaw, new Vector3(0, 0.05f, 0), new Vector3(1.1f, 0.05f, 1.1f), dark, false);
        Box("trunnionL", yaw, new Vector3(-0.38f, 0.35f, 0), new Vector3(0.12f, 0.5f, 0.5f), steel);
        Box("trunnionR", yaw, new Vector3(0.38f, 0.35f, 0), new Vector3(0.12f, 0.5f, 0.5f), steel);
        var pitch = new GameObject("aimPitch").transform; pitch.SetParent(yaw, false); pitch.localPosition = new Vector3(0, 0.45f, 0);
        Prim(PrimitiveType.Cube, "mantlet", pitch, new Vector3(0, 0, 0.35f), new Vector3(0.62f, 0.42f, 0.4f), steel, true);
        Box("breech", pitch, new Vector3(0, 0, -0.45f), new Vector3(0.36f, 0.32f, 1.0f), dark);
        Box("recoilGuard", pitch, new Vector3(0, -0.05f, -1.05f), new Vector3(0.5f, 0.3f, 0.3f), steel);
        Box("coaxMG", pitch, new Vector3(0.22f, 0.08f, 0.62f), new Vector3(0.05f, 0.05f, 0.5f), dark);
        float z0 = 0.55f;
        CylZ("barrel", pitch, new Vector3(0, 0, z0 + barrel / 2), r, barrel, steel, true);
        CylZ("sleeve1", pitch, new Vector3(0, 0, z0 + barrel * 0.2f), r * 1.35f, barrel * 0.22f, steel);
        CylZ("evacuator", pitch, new Vector3(0, 0, z0 + barrel * 0.52f), r * 1.7f, barrel * 0.12f, steel);
        CylZ("sleeve2", pitch, new Vector3(0, 0, z0 + barrel * 0.75f), r * 1.3f, barrel * 0.2f, steel);
        CylZ("muzzle", pitch, new Vector3(0, 0, z0 + barrel - 0.08f), r * 1.4f, 0.16f, dark);
        var fire = new GameObject("fireTransform").transform; fire.SetParent(pitch, false); fire.localPosition = new Vector3(0, 0, z0 + barrel + 0.05f);
        Write(root, Parts + name);
    }

    static void Loaders()
    {
        var rack = Mat("ELE_RackGrey", new Color(0.35f, 0.37f, 0.36f), 0.2f);
        var brass = Mat("ELE_Brass", new Color(0.72f, 0.58f, 0.28f), 0.7f);
        var tip = Mat("ELE_ShellTip", new Color(0.15f, 0.15f, 0.15f), 0.3f);
        // Manual: ready rack (box with two rows of shells) and a loader's seat; Y up, front +Z.
        var m = new GameObject("LoaderStation").transform;
        Prim(PrimitiveType.Cube, "rack", m, new Vector3(0, 0.35f, -0.2f), new Vector3(1.2f, 0.7f, 0.5f), rack, true);
        for (int i = 0; i < 5; i++) for (int j = 0; j < 2; j++)
            Prim(PrimitiveType.Cylinder, $"shell{i}{j}", m, new Vector3(-0.48f + i * 0.24f, 0.25f + j * 0.28f, 0.06f), new Vector3(0.12f, 0.03f, 0.12f), j == 0 ? brass : tip, false).transform.localRotation = Quaternion.Euler(90, 0, 0);
        Box("seat", m, new Vector3(0, 0.25f, 0.45f), new Vector3(0.45f, 0.08f, 0.45f), rack);
        Box("seatBack", m, new Vector3(0, 0.5f, 0.66f), new Vector3(0.45f, 0.5f, 0.06f), rack);
        Box("seatPost", m, new Vector3(0, 0.1f, 0.45f), new Vector3(0.08f, 0.2f, 0.08f), rack);
        Write(m, Parts + "LoaderStation");
        // Auto: carousel under the turret ring with standing shells
        var a = new GameObject("Autoloader").transform;
        Prim(PrimitiveType.Cylinder, "carousel", a, new Vector3(0, 0.06f, 0), new Vector3(1.6f, 0.06f, 1.6f), rack, true);
        Prim(PrimitiveType.Cylinder, "hub", a, new Vector3(0, 0.2f, 0), new Vector3(0.3f, 0.12f, 0.3f), tip, false);
        for (int i = 0; i < 8; i++)
        {
            float ang = i * Mathf.PI / 4; var pos = new Vector3(Mathf.Cos(ang) * 0.62f, 0.3f, Mathf.Sin(ang) * 0.62f);
            Prim(PrimitiveType.Cylinder, "round" + i, a, pos, new Vector3(0.13f, 0.18f, 0.13f), i % 2 == 0 ? brass : tip, false);
        }
        Box("rammer", a, new Vector3(0, 0.32f, 0.55f), new Vector3(0.2f, 0.2f, 0.5f), tip);
        Write(a, Parts + "Autoloader");
    }
}
using SteamShelf.Placeables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Boxroom_ItchStand;

internal static class ItchStandFactory
{
    private static byte[] logoBytes;
    private static Mesh editedRackMesh;
    private static Mesh editedRackCollisionMesh;
    private static readonly List<PackedMaterial> editedRackMaterialSpecs = new();
    private static readonly List<Material> editedRackMaterials = new();

    internal static void ReleaseResources()
    {
        if (editedRackMesh != null) UnityEngine.Object.DestroyImmediate(editedRackMesh);
        if (editedRackCollisionMesh != null) UnityEngine.Object.DestroyImmediate(editedRackCollisionMesh);
        foreach (Material material in editedRackMaterials)
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
        logoBytes = null;
        editedRackMesh = null;
        editedRackCollisionMesh = null;
        editedRackMaterialSpecs.Clear();
        editedRackMaterials.Clear();
    }

    internal static async Task<GameObject> InstantiateAsync()
    {
        PlaceableData template = Core.FindTemplate();
        if (template == null) return null;
        GameObject instance = await Singleton<PlaceableManager>.Instance.InstantiatePlaceableAsync(template);
        if (instance == null) return null;

        instance.name = "The Itch Stand";
        PlacementTag tag = instance.GetComponent<PlacementTag>();
        if (tag != null) tag.PlaceableData = Core.Data;

        ApplyEditedRackMesh(instance);
        instance.AddComponent<ItchStandController>();
        return instance;
    }

    private static void ApplyEditedRackMesh(GameObject root)
    {
        Mesh replacement = LoadEditedRackMesh();
        if (replacement == null) return;

        MeshFilter target = null;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter?.sharedMesh == null ||
                !filter.sharedMesh.name.Contains("Shelf_Video_01", StringComparison.OrdinalIgnoreCase))
                continue;
            target = filter;
            break;
        }

        if (target == null)
        {
            Core.Log?.Error("Could not find Shelf_Video_01 MeshFilter for the edited Itch Stand mesh.");
            return;
        }

        Mesh original = target.sharedMesh;
        target.sharedMesh = replacement;
        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer != null) renderer.sharedMaterials = BuildEditedRackMaterials();
        foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            if (collider != null && (collider.sharedMesh == original || collider.gameObject == target.gameObject))
                collider.sharedMesh = editedRackCollisionMesh ?? replacement;
    }

    private static Mesh LoadEditedRackMesh()
    {
        if (editedRackMesh != null) return editedRackMesh;
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Boxroom_ItchStand.Assets.Shelf_Video_01_EDITED.meshbin");
        if (stream == null)
        {
            Core.Log?.Error("Embedded edited Itch Stand mesh was not found.");
            return null;
        }

        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "ISMB")
            throw new InvalidDataException("Edited Itch Stand mesh has an unsupported format.");
        int version = reader.ReadInt32();
        if (version is < 1 or > 2)
            throw new InvalidDataException($"Edited Itch Stand mesh format {version} is unsupported.");

        int vertexCount = reader.ReadInt32();
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        for (int index = 0; index < vertexCount; index++)
        {
            vertices[index] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            normals[index] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            uvs[index] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        var mesh = new Mesh { name = "Shelf_Video_01_ItchStand_EDITED" };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        int submeshCount = reader.ReadInt32();
        mesh.subMeshCount = submeshCount;
        editedRackMaterialSpecs.Clear();
        var collisionIndices = new List<int>();
        for (int submesh = 0; submesh < submeshCount; submesh++)
        {
            string materialName = version >= 2 ? ReadString(reader) : $"Legacy:{submesh}";
            Color materialColor = version >= 2
                ? new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                : new Color(250f / 255f, 92f / 255f, 92f / 255f, 1f);
            int indexCount = reader.ReadInt32();
            var indices = new int[indexCount];
            for (int index = 0; index < indexCount; index++) indices[index] = reader.ReadInt32();
            mesh.SetTriangles(indices, submesh, false);
            editedRackMaterialSpecs.Add(new PackedMaterial(materialName, materialColor));
            if (version == 1 || !materialName.Contains(":ITCH_LOGO_", StringComparison.Ordinal))
                collisionIndices.AddRange(indices);
        }
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        editedRackCollisionMesh = new Mesh { name = "Shelf_Video_01_ItchStand_COLLIDER" };
        editedRackCollisionMesh.vertices = vertices;
        editedRackCollisionMesh.triangles = collisionIndices.ToArray();
        editedRackCollisionMesh.RecalculateBounds();
        editedRackMesh = mesh;
        return editedRackMesh;
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 4096) throw new InvalidDataException("Invalid mesh material name.");
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static Material[] BuildEditedRackMaterials()
    {
        if (editedRackMaterials.Count == editedRackMaterialSpecs.Count)
            return editedRackMaterials.ToArray();

        foreach (Material old in editedRackMaterials)
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
        editedRackMaterials.Clear();
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        foreach (PackedMaterial spec in editedRackMaterialSpecs)
        {
            var material = new Material(shader) { name = spec.Name, color = spec.Color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", spec.Color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", spec.Color);
            // The edited Blender shelf uses single-surface panels and Blender's
            // material preview displays both sides. Match that in Unity so the
            // black outer cheeks and pink interior/backing remain visible from
            // their intended viewing sides.
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.doubleSidedGI = true;
            editedRackMaterials.Add(material);
        }
        return editedRackMaterials.ToArray();
    }

    internal static byte[] GetLogoBytes()
    {
        if (logoBytes != null) return (byte[])logoBytes.Clone();
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Boxroom_ItchStand.Assets.itchio-logo-white.png");
        if (stream == null) return Array.Empty<byte>();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        logoBytes = memory.ToArray();
        return (byte[])logoBytes.Clone();
    }

    private sealed class PackedMaterial
    {
        internal PackedMaterial(string name, Color color)
        {
            Name = name;
            Color = color;
        }

        internal string Name { get; }
        internal Color Color { get; }
    }
}


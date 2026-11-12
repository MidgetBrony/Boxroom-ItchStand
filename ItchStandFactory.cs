using SteamShelf.Placeables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace Boxroom_ItchStand;

internal static class ItchStandFactory
{
    private static readonly Color ItchRed = new(250f / 255f, 92f / 255f, 92f / 255f, 1f);
    private static Texture2D logoTexture;

    internal static async Task<GameObject> InstantiateAsync()
    {
        PlaceableData template = Core.FindTemplate();
        if (template == null) return null;
        GameObject instance = await Singleton<PlaceableManager>.Instance.InstantiatePlaceableAsync(template);
        if (instance == null) return null;

        foreach (Shelf shelf in instance.GetComponentsInChildren<Shelf>(true))
            UnityEngine.Object.DestroyImmediate(shelf);

        instance.name = "The Itch Stand";
        PlacementTag tag = instance.GetComponent<PlacementTag>();
        if (tag != null) tag.PlaceableData = Core.Data;

        RecolourNativeRack(instance);
        ItchStandController controller = instance.AddComponent<ItchStandController>();
        BuildBranding(instance.transform, controller);
        return instance;
    }

    private static void RecolourNativeRack(GameObject root)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.GetComponentInParent<IShelfItem>() != null) continue;
            Material[] materials = renderer.materials;
            for (int index = 0; index < materials.Length; index++)
            {
                Material material = materials[index];
                if (material == null) continue;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", ItchRed);
                if (material.HasProperty("_Color")) material.SetColor("_Color", ItchRed);
            }
            renderer.materials = materials;
        }
    }

    private static void BuildBranding(Transform root, ItchStandController controller)
    {
        Bounds local = CalculateLocalBounds(root);
        float width = Mathf.Max(0.45f, local.size.x * 0.9f);
        float front = ResolveFront(root, local);
        float frontZ = front < 0f ? local.min.z - 0.025f : local.max.z + 0.025f;

        GameObject topper = CreateCube("itch.io Topper", root, ItchRed);
        topper.transform.localPosition = new Vector3(local.center.x, local.max.y + 0.13f, local.center.z);
        topper.transform.localScale = new Vector3(width, 0.24f, Mathf.Max(0.055f, local.size.z * 0.12f));
        SetLayerRecursively(topper, root.gameObject.layer);

        Texture2D logo = LoadLogo();
        if (logo != null)
        {
            float logoWidth = width * 0.76f;
            float logoHeight = logoWidth * logo.height / logo.width;
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Official itch.io Logo";
            quad.transform.SetParent(root, false);
            quad.transform.localPosition = new Vector3(local.center.x, local.max.y + 0.13f, frontZ);
            quad.transform.localRotation = Quaternion.Euler(0f, front > 0f ? 180f : 0f, 0f);
            quad.transform.localScale = new Vector3(logoWidth, logoHeight, 1f);
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            Renderer renderer = quad.GetComponent<Renderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
            var material = new Material(shader) { mainTexture = logo, color = Color.white };
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            renderer.material = material;
            SetLayerRecursively(quad, root.gameObject.layer);
        }

        GameObject button = CreateCube("REFRESH Button", root, new Color(0.72f, 0.055f, 0.07f, 1f));
        button.transform.localPosition = new Vector3(local.max.x - 0.12f, local.min.y + 0.42f, frontZ - front * 0.025f);
        button.transform.localScale = new Vector3(0.18f, 0.10f, 0.055f);
        button.AddComponent<ItchRefreshButton>();
        AddButtonLabel(button.transform, front);
        SetLayerRecursively(button, root.gameObject.layer);
    }

    private static GameObject CreateCube(string name, Transform parent, Color color)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        Renderer renderer = cube.GetComponent<Renderer>();
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = color };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        renderer.material = material;
        return cube;
    }

    private static void AddButtonLabel(Transform button, float front)
    {
        var labelObject = new GameObject("REFRESH Label");
        labelObject.transform.SetParent(button, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, front < 0f ? -0.51f : 0.51f);
        labelObject.transform.localRotation = Quaternion.Euler(0f, front > 0f ? 180f : 0f, 0f);
        labelObject.transform.localScale = Vector3.one;
        var text = labelObject.AddComponent<TextMeshPro>();
        text.text = "REFRESH";
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.fontSize = 1.35f;
        text.enableAutoSizing = true;
        text.fontSizeMin = 0.25f;
        text.fontSizeMax = 1.35f;
        text.rectTransform.sizeDelta = new Vector2(1.8f, 0.9f);
    }

    private static Texture2D LoadLogo()
    {
        if (logoTexture != null) return logoTexture;
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Boxroom_ItchStand.Assets.itchio-logo-white.png");
        if (stream == null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        logoTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!logoTexture.LoadImage(memory.ToArray()))
        {
            UnityEngine.Object.Destroy(logoTexture);
            logoTexture = null;
        }
        return logoTexture;
    }

    private static float ResolveFront(Transform root, Bounds bounds)
    {
        PlaceableMediaContainer[] containers = root.GetComponentsInChildren<PlaceableMediaContainer>(true);
        if (containers.Length == 0) return -1f;
        float average = 0f;
        foreach (PlaceableMediaContainer container in containers)
            average += root.InverseTransformPoint(container.transform.position).z;
        average /= containers.Length;
        return average < bounds.center.z ? -1f : 1f;
    }

    private static Bounds CalculateLocalBounds(Transform root)
    {
        bool hasPoint = false;
        Bounds result = new(Vector3.zero, Vector3.zero);
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Bounds world = renderer.bounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = world.center + Vector3.Scale(world.extents, new Vector3(x, y, z));
                Vector3 local = root.InverseTransformPoint(corner);
                if (!hasPoint) { result = new Bounds(local, Vector3.zero); hasPoint = true; }
                else result.Encapsulate(local);
            }
        }
        return hasPoint ? result : new Bounds(Vector3.zero, Vector3.one);
    }

    private static void SetLayerRecursively(GameObject value, int layer)
    {
        value.layer = layer;
        foreach (Transform child in value.transform) SetLayerRecursively(child.gameObject, layer);
    }
}


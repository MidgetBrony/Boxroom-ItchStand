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

    internal static Texture2D LogoTexture => LoadLogo();

    internal static void ReleaseResources()
    {
        if (logoTexture != null) UnityEngine.Object.DestroyImmediate(logoTexture);
        logoTexture = null;
    }

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
        // The controller is added at runtime, after the native prefab's serialized
        // placement callbacks were built, so activate it explicitly.
        controller.OnPlaced();
        return instance;
    }

    private static void RecolourNativeRack(GameObject root)
    {
        Shader solidShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.GetComponentInParent<IShelfItem>() != null) continue;
            Material[] originals = renderer.sharedMaterials;
            var materials = new Material[Mathf.Max(1, originals.Length)];
            for (int index = 0; index < materials.Length; index++)
            {
                var material = new Material(solidShader) { color = ItchRed };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", ItchRed);
                if (material.HasProperty("_Color")) material.SetColor("_Color", ItchRed);
                materials[index] = material;
            }
            renderer.materials = materials;
        }
    }

    private static void BuildBranding(Transform root, ItchStandController controller)
    {
        PlaceableMediaContainer[] containers = root.GetComponentsInChildren<PlaceableMediaContainer>(true);
        if (containers.Length == 0) return;

        Vector3 up = Vector3.up;
        Vector3 widthAxis = containers[0].LayoutAxis switch
        {
            ELayoutAxis.Y => containers[0].transform.up,
            ELayoutAxis.Z => containers[0].transform.forward,
            _ => containers[0].transform.right
        };
        widthAxis = Vector3.ProjectOnPlane(widthAxis, up).normalized;
        if (widthAxis.sqrMagnitude < 0.5f) widthAxis = root.right;
        Vector3 front = Vector3.Cross(widthAxis, up).normalized;

        Vector3 rackCenter = Vector3.zero;
        Vector3 containerCenter = Vector3.zero;
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true)) rackCenter += renderer.bounds.center;
        Renderer[] rackRenderers = root.GetComponentsInChildren<Renderer>(true);
        if (rackRenderers.Length > 0) rackCenter /= rackRenderers.Length;
        foreach (PlaceableMediaContainer container in containers)
            containerCenter += container.GetComponent<BoxCollider>().bounds.center;
        containerCenter /= containers.Length;
        if (Vector3.Dot(containerCenter - rackCenter, front) < 0f) front = -front;

        float minWidth = float.PositiveInfinity;
        float maxWidth = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float frontDepth = float.NegativeInfinity;
        foreach (PlaceableMediaContainer container in containers)
        {
            Bounds bounds = container.GetComponent<BoxCollider>().bounds;
            foreach (Vector3 corner in WorldCorners(bounds))
            {
                float across = Vector3.Dot(corner, widthAxis);
                minWidth = Mathf.Min(minWidth, across);
                maxWidth = Mathf.Max(maxWidth, across);
                maxY = Mathf.Max(maxY, corner.y);
                frontDepth = Mathf.Max(frontDepth, Vector3.Dot(corner, front));
            }
        }

        float width = Mathf.Max(0.45f, (maxWidth - minWidth) * 0.96f);
        Vector3 widthCenter = widthAxis * ((minWidth + maxWidth) * 0.5f);
        Vector3 depthCenter = front * (frontDepth + 0.012f);
        Vector3 topperPosition = widthCenter + depthCenter + up * (maxY + 0.105f);
        Quaternion facing = Quaternion.LookRotation(front, up);

        GameObject topper = CreateCube("itch.io Topper", root, ItchRed);
        SetWorldTransform(topper.transform, topperPosition, facing, new Vector3(width, 0.19f, 0.025f));
        SetLayerRecursively(topper, root.gameObject.layer);

        Texture2D logo = LoadLogo();
        if (logo != null)
        {
            float logoWidth = width * 0.68f;
            float logoHeight = logoWidth * logo.height / logo.width;
            Sprite sprite = Sprite.Create(logo, new Rect(0f, 0f, logo.width, logo.height), new Vector2(0.5f, 0.5f), 100f);
            var logoObject = new GameObject("Official itch.io Logo");
            logoObject.transform.SetParent(root, false);
            SpriteRenderer renderer = logoObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = Color.white;
            renderer.sortingOrder = 10;
            SetWorldTransform(
                logoObject.transform,
                topperPosition + front * 0.014f,
                Quaternion.LookRotation(-front, up),
                new Vector3(logoWidth / sprite.bounds.size.x, logoHeight / sprite.bounds.size.y, 1f));
            SetLayerRecursively(logoObject, root.gameObject.layer);
        }

        GameObject button = CreateCube("REFRESH Button", root, new Color(0.72f, 0.055f, 0.07f, 1f));
        Vector3 buttonPosition = topperPosition + widthAxis * (width * 0.39f) + front * 0.025f;
        SetWorldTransform(button.transform, buttonPosition, facing, new Vector3(0.12f, 0.065f, 0.025f));
        button.AddComponent<ItchRefreshButton>();
        AddButtonLabel(button.transform);
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

    private static void AddButtonLabel(Transform button)
    {
        var labelObject = new GameObject("REFRESH Label");
        labelObject.transform.SetParent(button, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, 0.51f);
        labelObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
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

    private static IEnumerable<Vector3> WorldCorners(Bounds bounds)
    {
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
            yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
    }

    private static void SetWorldTransform(Transform value, Vector3 position, Quaternion rotation, Vector3 worldScale)
    {
        value.position = position;
        value.rotation = rotation;
        // lossyScale is measured on this already-rotated transform, so it also
        // accounts for the rack prefab's rotated/non-uniform parent axes.
        value.localScale = Vector3.one;
        Vector3 actualScale = value.lossyScale;
        value.localScale = new Vector3(
            worldScale.x / Mathf.Max(0.0001f, Mathf.Abs(actualScale.x)),
            worldScale.y / Mathf.Max(0.0001f, Mathf.Abs(actualScale.y)),
            worldScale.z / Mathf.Max(0.0001f, Mathf.Abs(actualScale.z)));
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


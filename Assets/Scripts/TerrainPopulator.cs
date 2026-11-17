using System;
using System.Collections.Generic;
using UnityEngine;

public class TerrainPopulator : MonoBehaviour
{
    [Header("Terrain")]
    public TerrainGenerator terrainGenerator;
    public Terrain targetTerrain;

    [Header("Grid")]
    [Min(1)] public int gridWidth = 100;
    [Min(1)] public int gridDepth = 120;
    [Min(0.1f)] public float cellSize = 1f;

    [Header("Prefabs")]
    public GameObject[] flowerPrefabs;
    public GameObject[] treePrefabs;
    [Range(0f, 1f)] public float flowerSpawnChance = 0.2f;
    [Range(0f, 1f)] public float treeSpawnChance = 0.04f;

    [Header("Placement Rules")]
    [Min(0f)] public float waterHeightThreshold = 0.5f;
    [Min(0)] public int waterBufferCells = 2;
    [Min(0)] public int borderBufferCells = 2;
    [Range(0f, 90f)] public float maximumSlope = 30f;
    public float spawnHeightOffset;
    public bool randomizeRotation = true;
    public Vector2 scaleRange = new Vector2(0.8f, 1.2f);

    [Header("Random Seed")]
    public bool useFixedSeed;
    public int seed = 12345;

    private const string GeneratedContainerName = "Generated Vegetation";

    [ContextMenu("Populate Grid")]
    public void PopulateGrid()
    {
        if (terrainGenerator == null)
            terrainGenerator = GetComponent<TerrainGenerator>();

        if (targetTerrain == null && terrainGenerator != null)
            targetTerrain = terrainGenerator.targetTerrain;

        if (targetTerrain == null)
            targetTerrain = Terrain.activeTerrain;

        if (targetTerrain == null || targetTerrain.terrainData == null)
        {
            Debug.LogWarning("TerrainPopulator: Assign a valid Terrain before populating.");
            return;
        }

        if (terrainGenerator != null)
        {
            gridWidth = terrainGenerator.gridWidth;
            gridDepth = terrainGenerator.gridDepth;
            cellSize = terrainGenerator.cellSize;
        }

        if (gridWidth < 1 || gridDepth < 1 || cellSize <= 0f)
        {
            Debug.LogWarning("TerrainPopulator: Grid dimensions and cell size must be greater than zero.");
            return;
        }

        TerrainData terrainData = targetTerrain.terrainData;
        Transform generatedContainer = GetOrCreateGeneratedContainer();
        ClearGeneratedObjects(generatedContainer);

        if (!HasAnyPrefabs(flowerPrefabs) && !HasAnyPrefabs(treePrefabs))
        {
            Debug.LogWarning("TerrainPopulator: Assign at least one flower or tree prefab.");
            return;
        }

        System.Random random = useFixedSeed
            ? new System.Random(seed)
            : new System.Random();

        bool[,] waterCells = BuildWaterCellMap(terrainData);
        List<Bounds> bridgeBounds = GetBridgeBounds();

        int borderX = GetBorderCellCount(terrainGenerator != null ? terrainGenerator.borderThickness : 0,
            terrainData.heightmapResolution, gridWidth) + borderBufferCells;
        int borderZ = GetBorderCellCount(terrainGenerator != null ? terrainGenerator.borderThickness : 0,
            terrainData.heightmapResolution, gridDepth) + borderBufferCells;

        int spawnedCount = 0;

        for (int z = 0; z < gridDepth; z++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                if (IsInsideBorder(x, z, borderX, borderZ))
                    continue;

                if (IsNearWater(waterCells, x, z))
                    continue;

                float u = (x + 0.5f) / gridWidth;
                float v = (z + 0.5f) / gridDepth;

                if (terrainData.GetSteepness(u, v) > maximumSlope)
                    continue;

                Vector3 worldPosition = GetCellWorldPosition(terrainData, x, z, u, v);

                if (IsInsideBridgeBounds(worldPosition, bridgeBounds))
                    continue;

                GameObject prefab = SelectPrefab(random);
                if (prefab == null)
                    continue;

                Quaternion rotation = randomizeRotation
                    ? Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f)
                    : prefab.transform.rotation;

                GameObject instance = Instantiate(prefab, worldPosition, rotation, generatedContainer);

                float scale = Mathf.Lerp(scaleRange.x, scaleRange.y, (float)random.NextDouble());
                instance.transform.localScale = prefab.transform.localScale * scale;

                spawnedCount++;
            }
        }

        Debug.Log($"TerrainPopulator: Spawned {spawnedCount} objects.");
    }

    private bool[,] BuildWaterCellMap(TerrainData terrainData)
    {
        bool[,] waterCells = new bool[gridWidth, gridDepth];

        if (terrainGenerator == null)
            return waterCells;

        float lowerTierHeight = targetTerrain.transform.position.y + 4f;
        float upperTierHeight = targetTerrain.transform.position.y
            + 4f
            + terrainGenerator.upperTierHeight;
        float tierMidpoint = (lowerTierHeight + upperTierHeight) * 0.5f;

        for (int z = 0; z < gridDepth; z++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                float u = (x + 0.5f) / gridWidth;
                float v = (z + 0.5f) / gridDepth;
                float height = targetTerrain.transform.position.y
                    + terrainData.GetInterpolatedHeight(u, v);

                bool belowLowerTier = height < lowerTierHeight - waterHeightThreshold;
                bool belowUpperTier = height > tierMidpoint
                    && height < upperTierHeight - waterHeightThreshold;

                waterCells[x, z] = belowLowerTier || belowUpperTier;
            }
        }

        return waterCells;
    }

    private bool IsNearWater(bool[,] waterCells, int x, int z)
    {
        for (int offsetZ = -waterBufferCells; offsetZ <= waterBufferCells; offsetZ++)
        {
            for (int offsetX = -waterBufferCells; offsetX <= waterBufferCells; offsetX++)
            {
                int checkX = x + offsetX;
                int checkZ = z + offsetZ;

                if (checkX < 0 || checkX >= gridWidth || checkZ < 0 || checkZ >= gridDepth)
                    continue;

                if (waterCells[checkX, checkZ])
                    return true;
            }
        }

        return false;
    }

    private bool IsInsideBorder(int x, int z, int borderX, int borderZ)
    {
        return x < borderX
            || x >= gridWidth - borderX
            || z < borderZ
            || z >= gridDepth - borderZ;
    }

    private Vector3 GetCellWorldPosition(
        TerrainData terrainData,
        int x,
        int z,
        float u,
        float v)
    {
        Vector3 terrainPosition = targetTerrain.transform.position;

        return new Vector3(
            terrainPosition.x + u * terrainData.size.x,
            terrainPosition.y + terrainData.GetInterpolatedHeight(u, v) + spawnHeightOffset,
            terrainPosition.z + v * terrainData.size.z);
    }

    private List<Bounds> GetBridgeBounds()
    {
        List<Bounds> bounds = new List<Bounds>();

        if (terrainGenerator == null)
            return bounds;

        Transform bridgeContainer = terrainGenerator.transform.Find("Generated Bridges");
        if (bridgeContainer == null)
            return bounds;

        foreach (Renderer renderer in bridgeContainer.GetComponentsInChildren<Renderer>())
            bounds.Add(renderer.bounds);

        foreach (Collider bridgeCollider in bridgeContainer.GetComponentsInChildren<Collider>())
            bounds.Add(bridgeCollider.bounds);

        return bounds;
    }

    private bool IsInsideBridgeBounds(Vector3 position, List<Bounds> bridgeBounds)
    {
        for (int i = 0; i < bridgeBounds.Count; i++)
        {
            Bounds bounds = bridgeBounds[i];
            bounds.Expand(new Vector3(cellSize, 0f, cellSize));

            if (position.x >= bounds.min.x
                && position.x <= bounds.max.x
                && position.z >= bounds.min.z
                && position.z <= bounds.max.z)
            {
                return true;
            }
        }

        return false;
    }

    private int GetBorderCellCount(int borderThickness, int heightmapResolution, int gridSize)
    {
        if (borderThickness <= 0 || heightmapResolution <= 1)
            return 0;

        return Mathf.CeilToInt(
            (float)borderThickness / (heightmapResolution - 1) * gridSize);
    }

    private GameObject SelectPrefab(System.Random random)
    {
        if (HasAnyPrefabs(treePrefabs)
            && random.NextDouble() < treeSpawnChance)
        {
            return GetRandomPrefab(treePrefabs, random);
        }

        if (HasAnyPrefabs(flowerPrefabs)
            && random.NextDouble() < flowerSpawnChance)
        {
            return GetRandomPrefab(flowerPrefabs, random);
        }

        return null;
    }

    private GameObject GetRandomPrefab(GameObject[] prefabs, System.Random random)
    {
        List<GameObject> availablePrefabs = new List<GameObject>();

        foreach (GameObject prefab in prefabs)
        {
            if (prefab != null)
                availablePrefabs.Add(prefab);
        }

        if (availablePrefabs.Count == 0)
            return null;

        return availablePrefabs[random.Next(availablePrefabs.Count)];
    }

    private bool HasAnyPrefabs(GameObject[] prefabs)
    {
        if (prefabs == null)
            return false;

        foreach (GameObject prefab in prefabs)
        {
            if (prefab != null)
                return true;
        }

        return false;
    }

    private Transform GetOrCreateGeneratedContainer()
    {
        Transform container = transform.Find(GeneratedContainerName);
        if (container != null)
            return container;

        GameObject containerObject = new GameObject(GeneratedContainerName);
        containerObject.transform.SetParent(transform, false);
        return containerObject.transform;
    }

    private void ClearGeneratedObjects(Transform container)
    {
        for (int i = container.childCount - 1; i >= 0; i--)
        {
            GameObject child = container.GetChild(i).gameObject;

            if (Application.isPlaying)
                Destroy(child);
            else
                DestroyImmediate(child);
        }
    }
}
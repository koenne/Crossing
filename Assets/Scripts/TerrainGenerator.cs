using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TerrainGenerator : MonoBehaviour
{
    [Header("Terrain Target")]
    public Terrain targetTerrain;

    [Header("Grid Scale")]
    public int gridWidth = 100;
    public int gridDepth = 120;
    public float cellSize = 1f;

    [Header("Elevation Settings")]
    public float maxTerrainHeight = 30f;
    public float upperTierHeight = 8f;

    [Header("Border Settings")]
    public int borderThickness = 5;
    public float borderCliffHeight = 12f;

    [Header("River Settings")]
    [Range(2, 15)] public int riverWidth = 8;
    public float riverDepth = 2f;
    [Range(5, 100)] public int pathNodeCount = 5;         // Key direction nodes along river
    [Range(10f, 40f)] public float maxTurnAngleX = 30f; // Allows wide, sweeping bends
    [Range(50, 100)] public int edgeMargin = 10;
    public int minRiverDistance = 20;

    [Header("Ramp Settings")]
    [Range(10, 80)] public int rampLength = 60;
    [Range(2, 8)] public int rampWidth = 4;
    public float rampCurviness = 8f;

    [Header("Lake Tuning")]
    [Range(0, 4)] public int lakeClosePasses = 2;     // how many close (dilate+erode) passes to run
    [Range(1, 200)] public int lakeMinAreaToKeep = 64; // minimum lake area (cells) to keep

    [Header("Headwater Lake Settings")]
    public bool spawnHeadwaterLakes = true;
    [Range(0, 100)] public int headwaterOffsetRows = 3;                 // rows inland from river start (used to nudge inward from border)
    [Min(0.5f)] public float headwaterRadiusMultiplier = 2.0f;        // scales riverWidth for headwater radius
    public float headwaterRadiusNoise = 4f;                           // added perlin variation
    public bool debugHeadwater = false;                               // log chosen coords when true

    private float[,] cellHeights;
    private int[] cliffPositionsX;
    private List<Vector2> river1SplinePoints = new List<Vector2>();
    private List<Vector2> river2SplinePoints = new List<Vector2>();

    [Header("Ocean Settings")]
    public bool spawnOcean = true;
    [Tooltip("How many rows from the north edge the ocean band occupies (controls ocean 'length').")]
    [Range(1, 240)] public int oceanLengthRows = 28;     // inspector control for ocean size
    [Min(0f)] public float oceanDepth = 8f;              // world units (meters) depth lower than base
    [Range(0.01f, 0.3f)] public float oceanNoiseFreq = 0.06f;
    [Range(0f, 1f)] public float oceanIrregularity = 0.55f;

    [Header("Bridge Settings")]
    public GameObject bridgePrefab;
    [Range(0.1f, 0.45f)] public float upperBridgeProgress = 0.25f;
    [Range(0.55f, 0.9f)] public float lowerBridgeProgress = 0.75f;
    public float bridgeHeightOffset = 0.5f;
    [Tooltip("Set this to 90 if the prefab's length runs along its local X axis instead of local Z.")]
    public float bridgeRotationOffset;

    private float mapSeed = 0f;
    private void Awake()
    {
        if (targetTerrain == null) targetTerrain = Terrain.activeTerrain;
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Start()
    {
        //GenerateMap();
    }

    [ContextMenu("Generate Map")]
    public void GenerateMap()
    {
        if (targetTerrain == null) targetTerrain = Terrain.activeTerrain;

        TerrainData data = targetTerrain.terrainData;
        int resX = data.heightmapResolution;
        int resZ = data.heightmapResolution;

        data.size = new Vector3(gridWidth * cellSize, maxTerrainHeight, gridDepth * cellSize);

        cellHeights = new float[resX, resZ];
        cliffPositionsX = new int[resX];
        river1SplinePoints.Clear();
        river2SplinePoints.Clear();

        // Normalized tier heights
        float baseTierNormHeight = 4f / maxTerrainHeight;
        float upperTierNormHeight = (4f + upperTierHeight) / maxTerrainHeight;
        float borderHeight = (4f + borderCliffHeight) / maxTerrainHeight;

        // 1. Generate cliff silhouette using FBM/ridge/plateaus/jitter
        int midZ = Mathf.RoundToInt(resZ * 0.5f);
        mapSeed = UnityEngine.Random.Range(0f, 1000f);

        for (int x = 0; x < resX; x++)
        {
            float baseNoise = FBM(x * 0.008f, mapSeed, 4) * 70f;
            float detailNoise = FBM(x * 0.03f, mapSeed + 100f, 3) * 22f;
            float ridge = 1f - Mathf.Abs(2f * Mathf.PerlinNoise(x * 0.015f, mapSeed + 200f) - 1f);
            detailNoise *= Mathf.Lerp(0.6f, 1.2f, ridge);
            float plateauSample = Mathf.PerlinNoise(x * 0.0035f, mapSeed + 300f);
            int plateauSteps = Mathf.FloorToInt(plateauSample * 6f);
            float plateauOffset = plateauSteps * 3f;
            float jitter = (Mathf.PerlinNoise(x * 0.07f, mapSeed + 400f) - 0.5f) * 8f;

            float combined = baseNoise + detailNoise + plateauOffset + jitter - 35f;
            cliffPositionsX[x] = Mathf.Clamp(midZ + Mathf.RoundToInt(combined), 10, resZ - 10);
        }

        // Smooth silhouette
        SmoothCliffPositions(cliffPositionsX, 2);

        // 2. Base elevations using cliff silhouette
        for (int x = 0; x < resX; x++)
        {
            int cliffZ = cliffPositionsX[x];
            for (int z = 0; z < resZ; z++)
            {
                cellHeights[z, x] = (z >= cliffZ) ? upperTierNormHeight : baseTierNormHeight;
            }
        }

        // 3. River splines generation
        int halfWidth = riverWidth / 2;
        int minAllowedX = Mathf.Max(edgeMargin, borderThickness + halfWidth + 2);
        int maxAllowedX = Mathf.Min(resX - edgeMargin, resX - borderThickness - halfWidth - 2);

        int midXBoundary = resX / 2;
        int river1MaxX = Mathf.Min(midXBoundary - (minRiverDistance / 2), maxAllowedX);
        int river2MinX = Mathf.Max(midXBoundary + (minRiverDistance / 2), minAllowedX);

        int river1StartX = UnityEngine.Random.Range(minAllowedX, Mathf.Max(minAllowedX + 1, river1MaxX - 10));
        int river2StartX = UnityEngine.Random.Range(river2MinX, Mathf.Max(river2MinX + 1, maxAllowedX - 5));

        GenerateSplineRiverPath(river1StartX, minAllowedX, river1MaxX, resZ, river1SplinePoints);
        GenerateSplineRiverPath(river2StartX, river2MinX, maxAllowedX, resZ, river2SplinePoints);

        // 4. Carve rivers
        CarveSmoothSplineChannel(river1SplinePoints, resX, resZ);
        CarveSmoothSplineChannel(river2SplinePoints, resX, resZ);

        // 5. Headwater lakes on plateau just inside border
        CreateHeadwaterLake(river1SplinePoints, resX, resZ, baseTierNormHeight, upperTierNormHeight);
        CreateHeadwaterLake(river2SplinePoints, resX, resZ, baseTierNormHeight, upperTierNormHeight);

        // 6. Smooth cliff silhouette a bit more (helps river->cliff transitions)
        SmoothCliffPositions(cliffPositionsX, 1);

        // 7. Create waterfall lakes at cliff edges
        GenerateWaterfallLakes(resX, resZ, baseTierNormHeight, upperTierNormHeight);

        // 8. Ensure access passes & clean borders
        EnsureRiverAccessPasses(resX, resZ, baseTierNormHeight, upperTierNormHeight);
        CleanCliffWaterBorders(resX, resZ, baseTierNormHeight, upperTierNormHeight);

        // 10. Compute ramp search ranges (safe left/right)
        int leftSearchMin = Mathf.RoundToInt(resX * 0.05f);
        int leftSearchMax = Mathf.RoundToInt(resX * 0.38f);
        int rightSearchMin = Mathf.RoundToInt(resX * 0.62f);
        int rightSearchMax = Mathf.RoundToInt(resX * 0.95f);

        int edgeXMargin = borderThickness + rampWidth + Mathf.CeilToInt(rampCurviness) + 2;
        leftSearchMin = Mathf.Max(leftSearchMin, edgeXMargin);
        leftSearchMax = Mathf.Min(leftSearchMax, resX - edgeXMargin);
        rightSearchMin = Mathf.Max(rightSearchMin, edgeXMargin);
        rightSearchMax = Mathf.Min(rightSearchMax, resX - edgeXMargin);

        // river extents
        int river1MinXExtent = int.MaxValue, river1MaxXExtent = int.MinValue;
        foreach (var p in river1SplinePoints) { int ix = Mathf.FloorToInt(p.x); river1MinXExtent = Mathf.Min(river1MinXExtent, ix); river1MaxXExtent = Mathf.Max(river1MaxXExtent, ix); }
        int river2MinXExtent = int.MaxValue, river2MaxXExtent = int.MinValue;
        foreach (var p in river2SplinePoints) { int ix = Mathf.FloorToInt(p.x); river2MinXExtent = Mathf.Min(river2MinXExtent, ix); river2MaxXExtent = Mathf.Max(river2MaxXExtent, ix); }

        int safeGap = Mathf.CeilToInt((riverWidth * 0.5f + rampWidth * 0.5f + rampCurviness) * 1.5f);
        leftSearchMax = Mathf.Min(leftSearchMax, river1MinXExtent - safeGap);
        rightSearchMin = Mathf.Max(rightSearchMin, river2MaxXExtent + safeGap);
        if (leftSearchMax < leftSearchMin) leftSearchMax = leftSearchMin;
        if (rightSearchMin > rightSearchMax) rightSearchMin = rightSearchMax;

        // 11. Find and carve ramps
        int ramp1X = FindValidRampX(leftSearchMin, leftSearchMax, resX, resZ, false);
        int ramp2X = FindValidRampX(rightSearchMin, rightSearchMax, resX, resZ, true);

        CarveCurvedRamp(ramp1X, baseTierNormHeight, upperTierNormHeight, resX, resZ, 1f);
        CarveCurvedRamp(ramp2X, baseTierNormHeight, upperTierNormHeight, resX, resZ, -1f);

        // 12. Apply border cliffs and finish
        ApplyBorderCliffs(resX, resZ, borderHeight);

        // 13. Apply ocean if enabled
        CreateOceanAtSouth(resX, resZ, baseTierNormHeight);
        data.SetHeights(0, 0, cellHeights);
        GenerateBridges(data);
        // 14. Apply textures based on slope and height
        TerrainTexturePainter painter = gameObject.GetComponent<TerrainTexturePainter>();
        if (painter != null)
        {
            painter.PaintTextures();
        }
        else
        {
            Debug.LogWarning("TerrainGenerator: No TerrainTexturePainter found on the terrain. Skipping texture painting.");
        }

        // 15. Apply terrain populator
        TerrainPopulator populator = gameObject.GetComponent<TerrainPopulator>();
        if (populator != null)
        {
            populator.PopulateGrid();
        }
        else
        {
            Debug.LogWarning("TerrainGenerator: No TerrainPopulator found on the terrain. Skipping terrain population.");
        }
    }

    void GenerateSplineRiverPath(int startX, int minX, int maxX, int resZ, List<Vector2> splineOutput)
    {
        List<Vector2> controlNodes = new List<Vector2>();
        controlNodes.Add(new Vector2(startX, resZ + 10));
        controlNodes.Add(new Vector2(startX, resZ - 1));

        int currentX = startX;
        float zStep = (float)resZ / (pathNodeCount - 1);

        for (int i = 1; i < pathNodeCount - 1; i++)
        {
            float z = resZ - 1 - (i * zStep);
            float xOffset = UnityEngine.Random.Range(-maxTurnAngleX, maxTurnAngleX);
            currentX = Mathf.Clamp(Mathf.RoundToInt(currentX + xOffset), minX, maxX);
            controlNodes.Add(new Vector2(currentX, z));
        }

        controlNodes.Add(new Vector2(currentX, 0));
        controlNodes.Add(new Vector2(currentX, -10));

        int resolutionPerSegment = 20;
        for (int i = 0; i < controlNodes.Count - 3; i++)
        {
            Vector2 p0 = controlNodes[i];
            Vector2 p1 = controlNodes[i + 1];
            Vector2 p2 = controlNodes[i + 2];
            Vector2 p3 = controlNodes[i + 3];

            for (int r = 0; r < resolutionPerSegment; r++)
            {
                float t = (float)r / resolutionPerSegment;
                Vector2 point = GetCatmullRomPosition(t, p0, p1, p2, p3);
                splineOutput.Add(point);
            }
        }
    }

    Vector2 GetCatmullRomPosition(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    // Carve river channel along a spline (uses grid cells)
    void CarveSmoothSplineChannel(List<Vector2> points, int resX, int resZ)
    {
        if (points == null || points.Count < 2) return;

        float depthOffset = riverDepth / maxTerrainHeight;
        float radius = riverWidth * 0.5f;

        float baseTierNormHeight = 4f / maxTerrainHeight;
        float upperTierNormHeight = (4f + upperTierHeight) / maxTerrainHeight;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 p1 = points[i];
            Vector2 p2 = points[i + 1];

            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p1.x, p2.x) - radius - 1), 0, resX - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p1.x, p2.x) + radius + 1), 0, resX - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p1.y, p2.y) - radius - 1), 0, resZ - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p1.y, p2.y) + radius + 1), 0, resZ - 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector2 cellPos = new Vector2(x, z);
                    float distToSegment = DistanceToSegment(cellPos, p1, p2);

                    if (distToSegment <= radius)
                    {
                        int localCliffZ = cliffPositionsX[x];
                        float currentRegionHeight = (z >= localCliffZ) ? upperTierNormHeight : baseTierNormHeight;
                        float flatFloorHeight = currentRegionHeight - depthOffset;

                        float normalizedDist = distToSegment / radius;
                        float shapeFactor = Mathf.Pow(normalizedDist, 2.5f);
                        float targetHeight = Mathf.Lerp(flatFloorHeight, currentRegionHeight, shapeFactor);

                        cellHeights[z, x] = Mathf.Min(cellHeights[z, x], targetHeight);
                    }
                }
            }
        }
    }

    float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float sqrLen = ab.sqrMagnitude;
        if (sqrLen == 0) return Vector2.Distance(p, a);

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLen);
        Vector2 projection = a + t * ab;
        return Vector2.Distance(p, projection);
    }

    int FindValidRampX(int searchMin, int searchMax, int resX, int resZ, bool preferRightToLeft = false)
    {
        float riverRadius = riverWidth * 0.5f;
        float rampHalf = rampWidth * 0.5f;
        float extraMargin = Mathf.Max(4, Mathf.CeilToInt(rampCurviness * 0.5f));
        float safeDistance = riverRadius + rampHalf + extraMargin;

        if (preferRightToLeft)
        {
            for (int attemptX = searchMax; attemptX >= searchMin; attemptX--)
                if (!IsRampCollidingWithRivers(attemptX, resX, resZ, safeDistance))
                    return attemptX;
        }
        else
        {
            for (int attemptX = searchMin; attemptX <= searchMax; attemptX++)
                if (!IsRampCollidingWithRivers(attemptX, resX, resZ, safeDistance))
                    return attemptX;
        }

        return (searchMin + searchMax) / 2;
    }

    bool IsRampCollidingWithRivers(int startX, int resX, int resZ, float safeDistance)
    {
        int cliffZ = cliffPositionsX[Mathf.Clamp(startX, 0, cliffPositionsX.Length - 1)];
        int halfLength = rampLength / 2;
        int northLimitZ = resZ - borderThickness;

        for (int i = 0; i <= rampLength; i++)
        {
            float progress = (float)i / rampLength;
            float curveOffset = Mathf.Sin(progress * Mathf.PI * 2f) * rampCurviness;

            for (int sign = -1; sign <= 1; sign += 2)
            {
                int currentX = startX + Mathf.RoundToInt(curveOffset * sign);
                int currentZ = (cliffZ + halfLength) - i;

                if (currentZ < 0 || currentZ >= resZ) continue;
                if (currentZ >= northLimitZ) return true;

                for (int rx = -rampWidth; rx <= rampWidth; rx++)
                {
                    int testX = currentX + rx;
                    if (testX < borderThickness || testX >= (resX - borderThickness)) return true;

                    Vector2 rampPos = new Vector2(Mathf.Clamp(testX, 0, gridWidth - 1), currentZ);

                    if (IsPosNearPoints(rampPos, river1SplinePoints, safeDistance) ||
                        IsPosNearPoints(rampPos, river2SplinePoints, safeDistance))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    void CarveCurvedRamp(int startX, float baseH, float upperH, int resX, int resZ, float curveDirection)
    {
        int cliffZ = cliffPositionsX[Mathf.Clamp(startX, 0, cliffPositionsX.Length - 1)];
        int halfLength = rampLength / 2;

        for (int i = 0; i <= rampLength; i++)
        {
            float progress = (float)i / rampLength;
            float currentH = Mathf.Lerp(upperH, baseH, progress);

            int currentZ = (cliffZ + halfLength) - i;
            if (currentZ < 0 || currentZ >= resZ) continue;

            float curveOffset = Mathf.Sin(progress * Mathf.PI * 2f) * rampCurviness * curveDirection;
            int currentX = startX + Mathf.RoundToInt(curveOffset);

            for (int rx = -rampWidth; rx <= rampWidth; rx++)
            {
                int evalX = Mathf.Clamp(currentX + rx, 0, resX - 1);
                cellHeights[currentZ, evalX] = currentH;
            }
        }
    }

    void ApplyBorderCliffs(int resX, int resZ, float borderHeight)
    {
        for (int x = 0; x < resX; x++)
        {
            for (int z = 0; z < resZ; z++)
            {
                bool isNorthEdge = z >= (resZ - borderThickness);
                bool isWestEdge = x < borderThickness;
                bool isEastEdge = x >= (resX - borderThickness);

                if (isNorthEdge || isWestEdge || isEastEdge)
                {
                    cellHeights[z, x] = borderHeight;
                }
            }
        }
    }

    void GenerateWaterfallLakes(int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        ProcessRiverForLakes(river1SplinePoints, resX, resZ, baseTierNormHeight, upperTierNormHeight);
        ProcessRiverForLakes(river2SplinePoints, resX, resZ, baseTierNormHeight, upperTierNormHeight);
    }

    void ProcessRiverForLakes(List<Vector2> points, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        if (points == null || points.Count < 2) return;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 p1 = points[i];
            Vector2 p2 = points[i + 1];

            int x1 = Mathf.Clamp(Mathf.FloorToInt(p1.x), 0, resX - 1);
            int x2 = Mathf.Clamp(Mathf.FloorToInt(p2.x), 0, resX - 1);

            int cliffZ1 = cliffPositionsX[x1];
            int cliffZ2 = cliffPositionsX[x2];

            bool desc1 = (p1.y >= cliffZ1) && (p2.y < cliffZ2);
            bool desc2 = (p2.y >= cliffZ2) && (p1.y < cliffZ1);

            if (!desc1 && !desc2) continue;

            float targetCliffZ = (cliffZ1 + cliffZ2) * 0.5f;
            float denom = (p1.y - p2.y);
            float t = denom == 0f ? 0.5f : Mathf.Clamp01((p1.y - targetCliffZ) / denom);
            float intersectionX = p1.x + (p2.x - p1.x) * t;

            int centerX = Mathf.Clamp(Mathf.RoundToInt(intersectionX), 0, resX - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(targetCliffZ) - 3, 0, resZ - 1);

            if (centerZ >= resZ - borderThickness - 1) continue;

            float perlin = Mathf.PerlinNoise(centerX * 0.11f, centerZ * 0.07f);
            float lakeRadius = Mathf.Max(riverWidth * 2.5f, 6f) + perlin * 6f;
            float lakeDepthNorm = (riverDepth) / maxTerrainHeight;

            CarveLake(centerX, centerZ, lakeRadius, lakeDepthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);
            CarveChannelToLake(centerX, Mathf.RoundToInt(targetCliffZ) - 1, centerX, centerZ, Mathf.CeilToInt(riverWidth * 0.5f), lakeDepthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);
        }
    }

    // Robust CarveLake: mask -> smooth -> flood-fill connected -> fill enclosed holes -> carve basin (preserve cliffs)
    void CarveLake(int centerX, int centerZ, float radius, float depthNorm, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        int r = Mathf.CeilToInt(radius) + 1;
        int offsetX = centerX - r;
        int offsetZ = centerZ - r;

        float noiseFreq = 0.08f;
        float irregularityAmp = 0.35f;
        int smoothPasses = 2;

        int localW = Mathf.CeilToInt(radius) * 2 + 1;
        int localH = localW;
        if (localW <= 0 || localH <= 0) return;

        // safety guard: avoid huge allocations
        const int kMaxLocal = 1024;
        if (localW > kMaxLocal || localH > kMaxLocal)
        {
            Debug.LogWarning($"CarveLake: skipped too-large lake (local size {localW}x{localH}). center=({centerX},{centerZ}) radius={radius}");
            return;
        }

        bool[,] mask = new bool[localW, localH];
        for (int ly = 0; ly < localH; ly++)
        {
            for (int lx = 0; lx < localW; lx++)
            {
                int wx = offsetX + lx;
                int wz = offsetZ + ly;
                if (wx < 0 || wx >= resX || wz < 0 || wz >= resZ) { mask[lx, ly] = false; continue; }
                float dx = wx - centerX;
                float dz = wz - centerZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                float n = Mathf.PerlinNoise(wx * noiseFreq, wz * noiseFreq);
                float irregularRadius = radius * (1f + (n - 0.5f) * 2f * irregularityAmp);
                mask[lx, ly] = dist <= irregularRadius;
            }
        }

        for (int s = 0; s < smoothPasses; s++)
        {
            bool[,] next = (bool[,])mask.Clone();
            for (int ly = 1; ly < localH - 1; ly++)
            {
                for (int lx = 1; lx < localW - 1; lx++)
                {
                    int count = 0;
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                            if (mask[lx + ox, ly + oy]) count++;
                    next[lx, ly] = count >= 5;
                }
            }
            mask = next;
        }

        bool[,] visited = new bool[localW, localH];
        Queue<Vector2Int> q = new Queue<Vector2Int>();
        int cx = Mathf.Clamp(centerX - offsetX, 0, localW - 1);
        int cz = Mathf.Clamp(centerZ - offsetZ, 0, localH - 1);

        if (!mask[cx, cz])
        {
            bool found = false;
            int searchRadius = Mathf.Max(1, localW);
            for (int s = 1; s <= searchRadius && !found; s++)
            {
                for (int oy = -s; oy <= s && !found; oy++)
                    for (int ox = -s; ox <= s && !found; ox++)
                    {
                        int nx = cx + ox;
                        int ny = cz + oy;
                        if (nx < 0 || nx >= localW || ny < 0 || ny >= localH) continue;
                        if (mask[nx, ny]) { cx = nx; cz = ny; found = true; }
                    }
            }
            if (!mask[cx, cz]) return;
        }

        q.Enqueue(new Vector2Int(cx, cz));
        visited[cx, cz] = true;
        while (q.Count > 0)
        {
            var v = q.Dequeue();
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (Mathf.Abs(ox) + Mathf.Abs(oy) == 2) continue;
                    int nx = v.x + ox, ny = v.y + oy;
                    if (nx < 0 || nx >= localW || ny < 0 || ny >= localH) continue;
                    if (visited[nx, ny]) continue;
                    if (!mask[nx, ny]) continue;
                    visited[nx, ny] = true;
                    q.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }

        // build connected list
        List<Vector2Int> connected = new List<Vector2Int>();
        for (int lx = 0; lx < localW; lx++)
            for (int ly = 0; ly < localH; ly++)
                if (visited[lx, ly]) connected.Add(new Vector2Int(lx, ly));

        if (connected.Count < lakeMinAreaToKeep) return;

        bool[,] reachableFalse = new bool[localW, localH];
        Queue<Vector2Int> q2 = new Queue<Vector2Int>();
        for (int lx = 0; lx < localW; lx++)
            for (int ly = 0; ly < localH; ly++)
                if ((lx == 0 || ly == 0 || lx == localW - 1 || ly == localH - 1) && !mask[lx, ly])
                {
                    reachableFalse[lx, ly] = true;
                    q2.Enqueue(new Vector2Int(lx, ly));
                }
        while (q2.Count > 0)
        {
            var v = q2.Dequeue();
            int vx = v.x, vy = v.y;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (Mathf.Abs(ox) + Mathf.Abs(oy) == 2) continue;
                    int nx = vx + ox, ny = vy + oy;
                    if (nx < 0 || nx >= localW || ny < 0 || ny >= localH) continue;
                    if (reachableFalse[nx, ny]) continue;
                    if (mask[nx, ny]) continue;
                    reachableFalse[nx, ny] = true;
                    q2.Enqueue(new Vector2Int(nx, ny));
                }
        }

        List<Vector2Int> finalCells = new List<Vector2Int>();
        HashSet<int> finalSet = new HashSet<int>();
        foreach (var v in connected)
        {
            finalCells.Add(v);
            finalSet.Add(v.x * 100000 + v.y);
        }
        for (int ly = 0; ly < localH; ly++)
            for (int lx = 0; lx < localW; lx++)
                if (!mask[lx, ly] && !reachableFalse[lx, ly])
                {
                    finalCells.Add(new Vector2Int(lx, ly));
                    finalSet.Add(lx * 100000 + ly);
                }

        if (finalCells.Count == 0) return;

        float maxDist = 0f;
        foreach (var c in finalCells)
        {
            int wx = offsetX + c.x;
            int wz = offsetZ + c.y;
            float dx = wx - centerX;
            float dz = wz - centerZ;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d > maxDist) maxDist = d;
        }
        maxDist = Mathf.Max(1f, maxDist);

        float innerRatio = 0.55f;
        float rimExponent = 3.0f;
        float innerRadius = Mathf.Max(0.5f, maxDist * innerRatio);
        float rimWidth = Mathf.Max(0.1f, maxDist - innerRadius);

        foreach (var c in finalCells)
        {
            int wx = offsetX + c.x;
            int wz = offsetZ + c.y;
            if (wx < 0 || wx >= resX || wz < 0 || wz >= resZ) continue;

            int localCliffZ = cliffPositionsX[Mathf.Clamp(wx, 0, cliffPositionsX.Length - 1)];
            if (wz >= localCliffZ) continue;

            float dx = wx - centerX;
            float dz = wz - centerZ;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            float currentRegionHeight = baseTierNormHeight;
            float flatFloorHeight = currentRegionHeight - depthNorm;

            float targetHeight;
            if (dist <= innerRadius) targetHeight = flatFloorHeight;
            else
            {
                float normalized = Mathf.Clamp01((dist - innerRadius) / rimWidth);
                float sharp = Mathf.Pow(normalized, rimExponent);
                targetHeight = Mathf.Lerp(flatFloorHeight, currentRegionHeight, sharp);
            }

            cellHeights[wz, wx] = Mathf.Min(cellHeights[wz, wx], targetHeight);
        }
    }

    void CarveLakeIgnoreCliff(int centerX, int centerZ, float radius, float depthNorm, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        // First attempt robust carve to preserve nice lakes where possible
        CarveLake(centerX, centerZ, radius, depthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);

        // Then run an irregular carve that ignores cliff-preserve rules so plateau headwater
        // basins are always formed, with Perlin noise to break perfect circularity.
        int r = Mathf.CeilToInt(radius) + 1;
        int offsetX = centerX - r;
        int offsetZ = centerZ - r;
        int localW = Mathf.CeilToInt(radius) * 2 + 1;
        int localH = localW;

        if (localW <= 0 || localH <= 0) return;

        const int kMaxLocal = 1024;
        if (localW > kMaxLocal || localH > kMaxLocal)
        {
            Debug.LogWarning($"CarveLakeIgnoreCliff: skipped too-large lake (local size {localW}x{localH}). center=({centerX},{centerZ}) radius={radius}");
            return;
        }

        // Perlin parameters for edge irregularity
        float noiseFreq = 0.08f;
        float irregularityAmp = 0.45f; // bigger -> more irregular edges
        int smoothPasses = Mathf.Clamp(lakeClosePasses + 1, 1, 6);

        // build initial mask using irregular radius
        bool[,] mask = new bool[localW, localH];
        for (int ly = 0; ly < localH; ly++)
        {
            for (int lx = 0; lx < localW; lx++)
            {
                int wx = offsetX + lx;
                int wz = offsetZ + ly;
                if (wx < 0 || wx >= resX || wz < 0 || wz >= resZ) { mask[lx, ly] = false; continue; }

                float dx = wx - centerX;
                float dz = wz - centerZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                float n = Mathf.PerlinNoise(wx * noiseFreq, wz * noiseFreq);
                float irregularRadius = radius * (1f + (n - 0.5f) * 2f * irregularityAmp);

                mask[lx, ly] = dist <= irregularRadius;
            }
        }

        // Smooth / close passes to remove tiny spurs and make shoreline natural
        for (int s = 0; s < smoothPasses; s++)
        {
            bool[,] next = (bool[,])mask.Clone();
            for (int ly = 1; ly < localH - 1; ly++)
            {
                for (int lx = 1; lx < localW - 1; lx++)
                {
                    int count = 0;
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                            if (mask[lx + ox, ly + oy]) count++;
                    // require a majority to be water to keep water; reduces thin spikes
                    next[lx, ly] = count >= 4;
                }
            }
            mask = next;
        }

        // Find a connected water region starting near the intended center (fuzzy search if needed)
        bool[,] visited = new bool[localW, localH];
        Queue<Vector2Int> q = new Queue<Vector2Int>();
        int cx = Mathf.Clamp(centerX - offsetX, 0, localW - 1);
        int cz = Mathf.Clamp(centerZ - offsetZ, 0, localH - 1);

        if (!mask[cx, cz])
        {
            bool found = false;
            int searchRadius = Mathf.Max(1, Mathf.CeilToInt(radius));
            for (int s = 1; s <= searchRadius && !found; s++)
            {
                for (int oy = -s; oy <= s && !found; oy++)
                    for (int ox = -s; ox <= s && !found; ox++)
                    {
                        int nx = cx + ox;
                        int ny = cz + oy;
                        if (nx < 0 || nx >= localW || ny < 0 || ny >= localH) continue;
                        if (mask[nx, ny]) { cx = nx; cz = ny; found = true; }
                    }
            }
            if (!mask[cx, cz]) return;
        }

        q.Enqueue(new Vector2Int(cx, cz));
        visited[cx, cz] = true;
        while (q.Count > 0)
        {
            var v = q.Dequeue();
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (Mathf.Abs(ox) + Mathf.Abs(oy) == 2) continue;
                    int nx = v.x + ox, ny = v.y + oy;
                    if (nx < 0 || nx >= localW || ny < 0 || ny >= localH) continue;
                    if (visited[nx, ny]) continue;
                    if (!mask[nx, ny]) continue;
                    visited[nx, ny] = true;
                    q.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }

        // gather final cells and threshold by area
        List<Vector2Int> finalCells = new List<Vector2Int>();
        for (int lx = 0; lx < localW; lx++)
            for (int ly = 0; ly < localH; ly++)
                if (visited[lx, ly]) finalCells.Add(new Vector2Int(lx, ly));

        if (finalCells.Count < lakeMinAreaToKeep) return;

        // compute shaping radii from final cells (preserves irregular outline)
        float maxDist = 0f;
        foreach (var c in finalCells)
        {
            int wx = offsetX + c.x;
            int wz = offsetZ + c.y;
            float dx = wx - centerX;
            float dz = wz - centerZ;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d > maxDist) maxDist = d;
        }
        maxDist = Mathf.Max(1f, maxDist);

        float innerRatio = 0.55f;
        float rimExponent = 3.0f;
        float innerRadius = Mathf.Max(0.5f, maxDist * innerRatio);
        float rimWidth = Mathf.Max(0.1f, maxDist - innerRadius);

        // Apply heights for final cells — ignore cliff-preserve (allow carving on plateau)
        foreach (var c in finalCells)
        {
            int wx = offsetX + c.x;
            int wz = offsetZ + c.y;
            if (wx < 0 || wx >= resX || wz < 0 || wz >= resZ) continue;

            // region baseline still chosen by cliff but we do not skip carving if on plateau
            int localCliffZ = cliffPositionsX[Mathf.Clamp(wx, 0, cliffPositionsX.Length - 1)];
            float currentRegionHeight = (wz >= localCliffZ) ? upperTierNormHeight : baseTierNormHeight;
            float flatFloorHeight = currentRegionHeight - depthNorm;

            float dx = wx - centerX;
            float dz = wz - centerZ;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            float targetHeight;
            if (dist <= innerRadius) targetHeight = flatFloorHeight;
            else
            {
                float normalized = Mathf.Clamp01((dist - innerRadius) / rimWidth);
                float sharp = Mathf.Pow(normalized, rimExponent);
                targetHeight = Mathf.Lerp(flatFloorHeight, currentRegionHeight, sharp);
            }

            cellHeights[wz, wx] = Mathf.Min(cellHeights[wz, wx], targetHeight);
        }
    }

    void CreateHeadwaterLake(List<Vector2> points, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        if (!spawnHeadwaterLakes) return;
        if (points == null || points.Count == 0) return;

        int northInnerLimit = resZ - borderThickness - 2;
        if (northInnerLimit < 1) return;

        // pick a river point inside playable area (near the north border)
        Vector2? chosen = null;
        for (int i = 0; i < points.Count; i++)
        {
            float py = points[i].y;
            if (py <= northInnerLimit && py >= 0f)
            {
                chosen = points[i];
                break;
            }
        }
        if (!chosen.HasValue)
        {
            foreach (var p in points)
                if (p.y >= 0f && p.y <= northInnerLimit) { chosen = p; break; }
        }
        if (!chosen.HasValue) return;

        Vector2 startPoint = chosen.Value;
        int startX = Mathf.Clamp(Mathf.RoundToInt(startPoint.x), 0, resX - 1);

        // Place lake the moment the border ends (inside playable area), clamp above cliff
        int desiredRow = northInnerLimit - Mathf.Clamp(headwaterOffsetRows, 0, northInnerLimit);
        desiredRow = Mathf.Clamp(desiredRow, 1, northInnerLimit);
        int lakeZ = desiredRow;

        // radius like waterfall lakes (Perlin-driven), with safe cap
        float perlinForRadius = Mathf.PerlinNoise(startX * 0.11f, lakeZ * 0.07f);
        float radius = Mathf.Max(riverWidth * headwaterRadiusMultiplier, 4f) + perlinForRadius * headwaterRadiusNoise;
        float maxAllowedRadius = Mathf.Max(6f, Mathf.Min(resZ * 0.15f, 80f));
        if (radius > maxAllowedRadius) radius = maxAllowedRadius;

        // lateral jitter so lake center isn't exactly on river X
        float jitter = Mathf.PerlinNoise((startX + 17) * 0.17f, (lakeZ + 29) * 0.11f) - 0.5f;
        int xOffset = Mathf.RoundToInt(jitter * Mathf.Max(1f, radius) * 0.9f);
        int lakeX = Mathf.Clamp(startX + xOffset, borderThickness + 1, resX - borderThickness - 2);

        if (lakeX == startX)
        {
            if (lakeX + 1 < resX - borderThickness) lakeX++;
            else if (lakeX - 1 > borderThickness) lakeX--;
        }

        if (debugHeadwater)
            Debug.Log($"Headwater lake at ({lakeX},{lakeZ}) radius {radius:F1} desiredRow {desiredRow} from startPoint ({startX},{Mathf.RoundToInt(startPoint.y)}) offset {xOffset}");

        float depthNorm = riverDepth / maxTerrainHeight;

        // carve basin ignoring cliff-preserve so plateau basins are allowed
        CarveLakeIgnoreCliff(lakeX, lakeZ, radius, depthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);

        // Carve a curved connector from the lake to the chosen river point so it stitches cleanly
        Vector2 lakeCenter = new Vector2(lakeX, lakeZ);
        CarveHeadwaterConnector(lakeCenter, startPoint, Mathf.CeilToInt(riverWidth * 0.5f), depthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);
    }

    // Helper: carve a curved connector between headwater basin center and a river spline point.
    // Samples a short Catmull‑Rom curve and reuses CarveSmoothSplineChannel so the profile matches rivers.
    void CarveHeadwaterConnector(Vector2 lakeCenter, Vector2 riverPoint, int channelHalfWidth, float depthNorm, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        if (Vector2.Distance(lakeCenter, riverPoint) < 1f)
        {
            CarveChannelToLake(Mathf.RoundToInt(lakeCenter.x), Mathf.RoundToInt(lakeCenter.y),
                               Mathf.RoundToInt(riverPoint.x), Mathf.RoundToInt(riverPoint.y),
                               channelHalfWidth, depthNorm, resX, resZ, baseTierNormHeight, upperTierNormHeight);
            return;
        }

        Vector2 dir = riverPoint - lakeCenter;
        float len = dir.magnitude;
        Vector2 dirNorm = dir.normalized;

        // build control nodes: extend endpoints slightly for nicer tangents
        Vector2 p0 = lakeCenter - dirNorm * Mathf.Min(6f, len * 0.12f);
        Vector2 p1 = lakeCenter;
        Vector2 mid = Vector2.Lerp(lakeCenter, riverPoint, 0.45f);

        // add lateral offset so connector is not a straight column
        float lateralAmp = Mathf.Clamp(len * 0.25f, 1f, 18f);
        float per = Mathf.PerlinNoise((lakeCenter.x + riverPoint.x) * 0.06f, (lakeCenter.y + riverPoint.y) * 0.05f) - 0.5f;
        Vector2 lateral = new Vector2(-dirNorm.y, dirNorm.x) * per * lateralAmp;
        Vector2 p2 = mid + lateral;

        Vector2 p3 = riverPoint + dirNorm * Mathf.Min(6f, len * 0.12f);

        List<Vector2> control = new List<Vector2> { p0, p1, p2, p3 };

        // sample the Catmull-Rom curve
        List<Vector2> sampled = new List<Vector2>();
        int resolutionPerSegment = Mathf.Clamp(Mathf.CeilToInt(len * 0.6f), 10, 64);
        for (int i = 0; i < control.Count - 3; i++)
        {
            for (int r = 0; r < resolutionPerSegment; r++)
            {
                float t = (float)r / resolutionPerSegment;
                sampled.Add(GetCatmullRomPosition(t, control[i], control[i + 1], control[i + 2], control[i + 3]));
            }
        }
        sampled.Add(control[control.Count - 2]); // ensure endpoint present

        // carve sampled spline with existing river channel routine
        CarveSmoothSplineChannel(sampled, resX, resZ);

        // reinforce seam at both ends so basin/channel stitch cleanly
        int lx = Mathf.RoundToInt(lakeCenter.x);
        int lz = Mathf.RoundToInt(lakeCenter.y);
        int rx = Mathf.RoundToInt(riverPoint.x);
        int rz = Mathf.RoundToInt(riverPoint.y);
        float baseFloor = (4f / maxTerrainHeight) - depthNorm;

        for (int dx = -channelHalfWidth - 1; dx <= channelHalfWidth + 1; dx++)
        {
            int wxL = Mathf.Clamp(lx + dx, 0, resX - 1);
            int wzL = Mathf.Clamp(lz, 0, resZ - 1);
            cellHeights[wzL, wxL] = Mathf.Min(cellHeights[wzL, wxL], baseFloor);
        }

        for (int dx = -channelHalfWidth - 1; dx <= channelHalfWidth + 1; dx++)
        {
            int wxR = Mathf.Clamp(rx + dx, 0, resX - 1);
            for (int dz = -2; dz <= 1; dz++)
            {
                int wzR = Mathf.Clamp(rz + dz, 0, resZ - 1);
                cellHeights[wzR, wxR] = Mathf.Min(cellHeights[wzR, wxR], baseFloor);
            }
        }
    }

    // FBM helper
    float FBM(float x, float seed, int octaves)
    {
        float value = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float totalAmp = 0f;

        for (int i = 0; i < octaves; i++)
        {
            value += Mathf.PerlinNoise(x * frequency, seed) * amplitude;
            totalAmp += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return (totalAmp > 0f) ? value / totalAmp : 0f;
    }

    public Vector2Int WorldToGrid(Vector3 worldPos)
    {
        int x = Mathf.FloorToInt(worldPos.x / cellSize);
        int z = Mathf.FloorToInt(worldPos.z / cellSize);
        return new Vector2Int(Mathf.Clamp(x, 0, gridWidth - 1), Mathf.Clamp(z, 0, gridDepth - 1));
    }

    public Vector3 GetGridCellCenterWorld(int gridX, int gridZ)
    {
        float worldX = (gridX * cellSize) + (cellSize * 0.5f);
        float worldZ = (gridZ * cellSize) + (cellSize * 0.5f);

        Vector3 rawPos = new Vector3(worldX, 0f, worldZ);
        rawPos.y = targetTerrain.SampleHeight(rawPos);

        return rawPos;
    }
    void CarveChannelToLake(int sx, int sz, int ex, int ez, int channelHalfWidth, float depthNorm, int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        int length = Mathf.Max(1, Mathf.Abs(ez - sz) + Mathf.Abs(ex - sx));
        for (int i = 0; i <= length; i++)
        {
            float t = (float)i / Mathf.Max(1, length);
            int cx = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(sx, ex, t)), 0, resX - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(sz, ez, t)), 0, resZ - 1);

            for (int rx = -channelHalfWidth; rx <= channelHalfWidth; rx++)
            {
                int tx = cx + rx;
                if (tx < 0 || tx >= resX) continue;

                int localCliffZ = cliffPositionsX[tx];
                float currentRegionHeight = (cz >= localCliffZ) ? upperTierNormHeight : baseTierNormHeight;
                float flatFloorHeight = currentRegionHeight - depthNorm;

                cellHeights[cz, tx] = Mathf.Min(cellHeights[cz, tx], flatFloorHeight);
            }
        }
    }

    bool IsPosNearPoints(Vector2 pos, List<Vector2> points, float maxDist)
    {
        if (points == null || points.Count < 2) return false;
        for (int i = 0; i < points.Count - 1; i++)
            if (DistanceToSegment(pos, points[i], points[i + 1]) <= maxDist)
                return true;
        return false;
    }

    // Ensure passes so cliff doesn't isolate river/lake pockets
    void EnsureRiverAccessPasses(int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        int passHalfWidth = Mathf.Max(2, rampWidth);
        int clearanceBelowRiver = 1;

        HashSet<int> riverXs = new HashSet<int>();
        foreach (var p in river1SplinePoints) riverXs.Add(Mathf.Clamp(Mathf.RoundToInt(p.x), 0, resX - 1));
        foreach (var p in river2SplinePoints) riverXs.Add(Mathf.Clamp(Mathf.RoundToInt(p.x), 0, resX - 1));

        foreach (int centerX in riverXs)
        {
            int lowestRiverZ = resZ;
            foreach (var p in river1SplinePoints) { int ix = Mathf.RoundToInt(p.x); if (ix == centerX) lowestRiverZ = Mathf.Min(lowestRiverZ, Mathf.RoundToInt(p.y)); }
            foreach (var p in river2SplinePoints) { int ix = Mathf.RoundToInt(p.x); if (ix == centerX) lowestRiverZ = Mathf.Min(lowestRiverZ, Mathf.RoundToInt(p.y)); }
            if (lowestRiverZ == resZ) continue;

            for (int dx = -passHalfWidth; dx <= passHalfWidth; dx++)
            {
                int x = Mathf.Clamp(centerX + dx, 0, resX - 1);
                if (x < borderThickness || x >= resX - borderThickness) continue;
                int cliffZ = cliffPositionsX[x];
                if (lowestRiverZ + clearanceBelowRiver < cliffZ)
                {
                    int newCliffZ = Mathf.Max(2, lowestRiverZ + clearanceBelowRiver);
                    cliffPositionsX[x] = Mathf.Min(cliffPositionsX[x], newCliffZ);

                    int topZ = newCliffZ;
                    int bottomZ = Mathf.Clamp(lowestRiverZ, 0, resZ - 1);
                    int heightSpan = Mathf.Max(1, topZ - bottomZ);

                    for (int z = bottomZ; z <= topZ; z++)
                    {
                        float t = (float)(z - bottomZ) / heightSpan;
                        float stepFactor = Mathf.Pow(t, 0.9f);
                        float targetRegionHeight = Mathf.Lerp(baseTierNormHeight, upperTierNormHeight, stepFactor);
                        cellHeights[z, x] = Mathf.Min(cellHeights[z, x], targetRegionHeight);
                    }

                    int swell = 1;
                    for (int sx = -swell; sx <= swell; sx++)
                    {
                        int wx = Mathf.Clamp(x + sx, 0, resX - 1);
                        for (int z = bottomZ; z <= Mathf.Min(bottomZ + 2, resZ - 1); z++)
                            cellHeights[z, wx] = Mathf.Min(cellHeights[z, wx], baseTierNormHeight - (riverDepth / maxTerrainHeight));
                    }
                }
            }
        }
    }

    void SmoothCliffPositions(int[] arr, int iterations)
    {
        int len = arr.Length;
        for (int iter = 0; iter < iterations; iter++)
        {
            int[] copy = (int[])arr.Clone();
            for (int i = 1; i < len - 1; i++)
                arr[i] = Mathf.RoundToInt((copy[i - 1] + copy[i] + copy[i + 1]) / 3f);
        }

        for (int i = 2; i < len - 2; i++)
        {
            float noise = Mathf.PerlinNoise(i * 0.06f, 0.42f);
            if (noise > 0.72f)
            {
                int dip = Mathf.RoundToInt((noise - 0.72f) * 20f);
                arr[i] = Mathf.Max(5, arr[i] - dip);
                if (i - 1 >= 0) arr[i - 1] = Mathf.RoundToInt(Mathf.Lerp(arr[i - 1], arr[i] + dip / 2f, 0.45f));
                if (i + 1 < len) arr[i + 1] = Mathf.RoundToInt(Mathf.Lerp(arr[i + 1], arr[i] + dip / 2f, 0.45f));
            }
        }

        int[] finalCopy = (int[])arr.Clone();
        for (int i = 1; i < len - 1; i++)
            arr[i] = Mathf.RoundToInt(Mathf.Lerp(finalCopy[i], (finalCopy[i - 1] + finalCopy[i] + finalCopy[i + 1]) / 3f, 0.45f));
    }

    void CleanCliffWaterBorders(int resX, int resZ, float baseTierNormHeight, float upperTierNormHeight)
    {
        float waterFloorNorm = baseTierNormHeight - (riverDepth / maxTerrainHeight);
        float waterDetectThreshold = Mathf.Lerp(baseTierNormHeight, waterFloorNorm, 0.5f);

        for (int x = 0; x < resX; x++)
        {
            int highestWaterZ = int.MaxValue;
            for (int z = 0; z < resZ; z++)
                if (cellHeights[z, x] <= waterDetectThreshold) highestWaterZ = Mathf.Min(highestWaterZ, z);

            if (highestWaterZ == int.MaxValue) continue;

            int desiredCliffZ = Mathf.Max(2, highestWaterZ + 1);
            if (cliffPositionsX[x] > desiredCliffZ) cliffPositionsX[x] = desiredCliffZ;

            for (int z = highestWaterZ; z <= cliffPositionsX[x] - 1 && z < resZ; z++)
            {
                float currentRegionHeight = (z >= cliffPositionsX[x]) ? upperTierNormHeight : baseTierNormHeight;
                float targetFloor = Mathf.Min(currentRegionHeight - (riverDepth / maxTerrainHeight), waterFloorNorm);
                cellHeights[z, x] = Mathf.Min(cellHeights[z, x], targetFloor);
            }

            for (int z = cliffPositionsX[x]; z <= Mathf.Min(resZ - 1, cliffPositionsX[x] + 2); z++)
            {
                float safeTarget = Mathf.Lerp(baseTierNormHeight, waterFloorNorm, 0.5f);
                cellHeights[z, x] = Mathf.Min(cellHeights[z, x], safeTarget);
            }
        }

        float neighborWaterThreshold = Mathf.Lerp(baseTierNormHeight, baseTierNormHeight - (riverDepth / maxTerrainHeight), 0.5f) + 0.0001f;
        for (int z = 0; z < resZ; z++)
        {
            for (int x = 0; x < resX; x++)
            {
                bool isHigh = cellHeights[z, x] > (baseTierNormHeight - (riverDepth / maxTerrainHeight)) + 0.02f;
                if (!isHigh) continue;

                bool neighbourIsWater = false;
                if (x > 0 && cellHeights[z, x - 1] <= neighborWaterThreshold) neighbourIsWater = true;
                if (x < resX - 1 && cellHeights[z, x + 1] <= neighborWaterThreshold) neighbourIsWater = true;
                if (z > 0 && cellHeights[z - 1, x] <= neighborWaterThreshold) neighbourIsWater = true;
                if (z < resZ - 1 && cellHeights[z + 1, x] <= neighborWaterThreshold) neighbourIsWater = true;

                if (neighbourIsWater)
                {
                    float safeLower = Mathf.Lerp(cellHeights[z, x], baseTierNormHeight, 0.5f);
                    cellHeights[z, x] = Mathf.Min(cellHeights[z, x], safeLower);
                }
            }
        }

        for (int x = 0; x < resX; x++)
        {
            for (int z = 1; z < resZ - 1; z++)
            {
                float avg = (cellHeights[z - 1, x] + cellHeights[z, x] + cellHeights[z + 1, x]) / 3f;
                cellHeights[z, x] = Mathf.Min(cellHeights[z, x], Mathf.Lerp(cellHeights[z, x], avg, 0.35f));
            }
        }
    }
    void CreateOceanAtSouth(int resX, int resZ, float baseTierNormHeight)
    {
        if (!spawnOcean) return;

        float oceanDepthNorm = Mathf.Clamp01(oceanDepth / maxTerrainHeight);
        int maxRows = Mathf.Clamp(oceanLengthRows, 1, resZ / 2);

        for (int x = 0; x < resX; x++)
        {
            float per = Mathf.PerlinNoise((x * oceanNoiseFreq) + (mapSeed * 0.001f), mapSeed * 0.002f);
            int shoreJitter = Mathf.RoundToInt(per * maxRows * oceanIrregularity);

            // shoreStartZ is the inland row where slope begins (greater than 0)
            int shoreStartZ = Mathf.Clamp(shoreJitter, 0, maxRows - 1);
            shoreStartZ = Mathf.Clamp(shoreStartZ + Mathf.RoundToInt(maxRows * 0.25f), 0, resZ - 1);

            int slopeSpan = Mathf.Max(1, shoreStartZ + 1); // number of rows from sea (0) to shoreStartZ

            for (int z = 0; z <= shoreStartZ; z++)
            {
                // t = 0 at shoreStartZ (inland), t = 1 at sea edge (z==0)
                float t = (float)(shoreStartZ - z) / (float)Mathf.Max(1, slopeSpan - 1);
                float shaped = Mathf.Pow(t, 0.9f);

                float edgeNoise = Mathf.PerlinNoise((x + mapSeed) * oceanNoiseFreq * 1.5f, (z + mapSeed) * oceanNoiseFreq * 0.8f) - 0.5f;
                float noiseAmp = oceanDepthNorm * 0.15f;
                float noisyT = Mathf.Clamp01(shaped + edgeNoise * noiseAmp);

                // interpolate from interior height -> ocean depth (noisyT==1 -> deepest)
                float targetHeight = Mathf.Lerp(baseTierNormHeight, baseTierNormHeight - oceanDepthNorm, noisyT);

                cellHeights[z, x] = Mathf.Min(cellHeights[z, x], targetHeight);
            }

            // Improved inlet logic: rarer and size-capped
            // only consider inlet when per is significantly high
            const float inletTrigger = 0.88f; // raise to make inlets rarer
            if (per > inletTrigger && oceanIrregularity > 0.02f)
            {
                // scale 0..1 from threshold..1
                float scaledPer = (per - inletTrigger) / (1f - inletTrigger);

                // maximum inlet radius limited to a quarter of ocean band
                int maxAllowedInlet = Mathf.Max(1, Mathf.RoundToInt(maxRows * 0.25f));

                // radius scaled by per and oceanIrregularity
                int inletRadius = Mathf.Clamp(Mathf.RoundToInt(maxRows * scaledPer * 0.5f * (0.5f + oceanIrregularity)), 1, maxAllowedInlet);

                // deterministic gate so not every high per spawns an inlet
                float gate = Mathf.PerlinNoise((x + mapSeed * 0.7f) * 0.13f, (mapSeed + x * 0.33f) * 0.29f);
                if (gate > 0.45f && inletRadius > 1)
                {
                    int inletCenterZ = Mathf.Clamp(Mathf.RoundToInt(shoreStartZ * 0.6f), 0, resZ - 1);
                    for (int iz = Mathf.Max(0, inletCenterZ - inletRadius); iz <= Mathf.Min(resZ - 1, inletCenterZ + inletRadius); iz++)
                    {
                        for (int ix = Mathf.Max(0, x - inletRadius); ix <= Mathf.Min(resX - 1, x + inletRadius); ix++)
                        {
                            float dx = ix - x;
                            float dz = iz - inletCenterZ;
                            float d = Mathf.Sqrt(dx * dx + dz * dz);
                            if (d <= inletRadius)
                            {
                                float depthFactor = Mathf.Clamp01(1f - (d / inletRadius));
                                float th = Mathf.Lerp(baseTierNormHeight, baseTierNormHeight - oceanDepthNorm, depthFactor);
                                cellHeights[iz, ix] = Mathf.Min(cellHeights[iz, ix], th);
                            }
                        }
                    }
                }
            }
        }

        // smooth the south band to reduce jagged steps
        int smoothRadius = 2;
        int zStart = Mathf.Max(0, 0);
        int zEnd = Mathf.Min(resZ - 1, maxRows + 2);
        for (int z = zStart; z <= zEnd; z++)
        {
            for (int x = 0; x < resX; x++)
            {
                float sum = 0f;
                int count = 0;
                for (int sx = -smoothRadius; sx <= smoothRadius; sx++)
                    for (int sz = -1; sz <= 1; sz++)
                    {
                        int nx = Mathf.Clamp(x + sx, 0, resX - 1);
                        int nz = Mathf.Clamp(z + sz, 0, resZ - 1);
                        sum += cellHeights[nz, nx];
                        count++;
                    }
                if (count > 0) cellHeights[z, x] = Mathf.Min(cellHeights[z, x], sum / count);
            }
        }
    }
    void GenerateBridges(TerrainData data)
    {
        Transform bridgeContainer = transform.Find("Generated Bridges");
        if (bridgeContainer == null)
        {
            GameObject containerObject = new GameObject("Generated Bridges");
            bridgeContainer = containerObject.transform;
            bridgeContainer.SetParent(transform, false);
        }

        for (int i = bridgeContainer.childCount - 1; i >= 0; i--)
        {
            GameObject child = bridgeContainer.GetChild(i).gameObject;
            if (Application.isPlaying)
                Destroy(child);
            else
                DestroyImmediate(child);
        }

        if (bridgePrefab == null)
        {
            Debug.LogWarning("TerrainGenerator: Assign a bridge prefab to generate river crossings.");
            return;
        }

        SpawnRiverBridges(river1SplinePoints, data, bridgeContainer);
        SpawnRiverBridges(river2SplinePoints, data, bridgeContainer);
    }

    void SpawnRiverBridges(List<Vector2> riverPoints, TerrainData data, Transform bridgeContainer)
    {
        if (riverPoints == null || riverPoints.Count < 3)
            return;

        SpawnBridgeAtProgress(riverPoints, upperBridgeProgress, data, bridgeContainer);
        SpawnBridgeAtProgress(riverPoints, lowerBridgeProgress, data, bridgeContainer);
    }

    void SpawnBridgeAtProgress(
        List<Vector2> riverPoints,
        float progress,
        TerrainData data,
        Transform bridgeContainer)
    {
        int pointIndex = Mathf.Clamp(
            Mathf.RoundToInt(progress * (riverPoints.Count - 1)),
            1,
            riverPoints.Count - 2);

        Vector2 point = riverPoints[pointIndex];
        Vector2 tangent = (riverPoints[pointIndex + 1] - riverPoints[pointIndex - 1]).normalized;

        float scaleX = data.size.x / (data.heightmapResolution - 1);
        float scaleZ = data.size.z / (data.heightmapResolution - 1);

        Vector3 riverDirection = new Vector3(tangent.x * scaleX, 0f, tangent.y * scaleZ).normalized;
        Vector3 crossingDirection = new Vector3(-riverDirection.z, 0f, riverDirection.x);
        Quaternion rotation = Quaternion.LookRotation(crossingDirection, Vector3.up)
            * Quaternion.Euler(0f, bridgeRotationOffset, 0f);

        Vector3 center = MapPointToWorld(point, data);
        float halfSpan = (riverWidth * 0.5f + 3f) * Mathf.Max(scaleX, scaleZ);
        float highestSurfaceHeight = float.MinValue;

        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            float offset = Mathf.Lerp(-halfSpan, halfSpan, t);
            Vector3 samplePosition = center + crossingDirection * offset;
            float surfaceHeight = GetTerrainHeightAtWorldPosition(samplePosition, data);
            highestSurfaceHeight = Mathf.Max(highestSurfaceHeight, surfaceHeight);
        }

        center.y = highestSurfaceHeight + bridgeHeightOffset;
        Instantiate(bridgePrefab, center, rotation, bridgeContainer);
    }

    Vector3 MapPointToWorld(Vector2 point, TerrainData data)
    {
        Vector3 terrainPosition = targetTerrain.transform.position;

        return new Vector3(
            terrainPosition.x + point.x / (data.heightmapResolution - 1) * data.size.x,
            terrainPosition.y,
            terrainPosition.z + point.y / (data.heightmapResolution - 1) * data.size.z);
    }

    float GetTerrainHeightAtWorldPosition(Vector3 worldPosition, TerrainData data)
    {
        Vector3 terrainPosition = targetTerrain.transform.position;
        int x = Mathf.Clamp(
            Mathf.RoundToInt((worldPosition.x - terrainPosition.x) / data.size.x * (data.heightmapResolution - 1)),
            0,
            data.heightmapResolution - 1);
        int z = Mathf.Clamp(
            Mathf.RoundToInt((worldPosition.z - terrainPosition.z) / data.size.z * (data.heightmapResolution - 1)),
            0,
            data.heightmapResolution - 1);

        return terrainPosition.y + cellHeights[z, x] * data.size.y;
    }
}
using UnityEngine;

[RequireComponent(typeof(Terrain))]
public class TerrainTexturePainter : MonoBehaviour
{
    [Header("Target")]
    public Terrain targetTerrain;

    [Header("Terrain Layers (0=ground,1=river,2=cliff)")]
    public TerrainLayer groundLayer;
    public TerrainLayer riverLayer;
    public TerrainLayer cliffLayer;

    [Header("River / Water")]
    [Tooltip("World Y height (meters) below which is considered water/river.")]
    public float waterLevel = 1f;
    [Tooltip("Max slope (degrees) allowed for river placement.")]
    public float riverMaxSlope = 12f;
    [Tooltip("World meters used to blend shore transition.")]
    public float shoreBlend = 2f;

    [Header("Cliff")]
    [Tooltip("Slope in degrees above which terrain is considered cliff.")]
    public float cliffSlope = 35f;
    [Tooltip("Slope falloff (degrees) used when blending cliff edges.")]
    public float cliffFalloff = 8f;

    [Header("Noise / Tuning")]
    [Range(0.001f, 0.5f)] public float noiseScale = 0.06f;
    [Range(0f, 1f)] public float noiseStrength = 0.35f;
    [Tooltip("Random seed for Perlin variations (0 = random)")]
    public float seed = 0f;

    [Header("Performance")]
    [Tooltip("Use terrain alpha map resolution for painting. Lower this if you want faster but blocky results.")]
    public int overrideAlphamapResolution = 0; // 0 = use terrain's resolution

    void Reset()
    {
        targetTerrain = GetComponent<Terrain>();
    }
    [ContextMenu("Paint Terrain Textures")]
    public void PaintTextures()
    {
        if (targetTerrain == null) targetTerrain = Terrain.activeTerrain;
        if (targetTerrain == null) { Debug.LogWarning("TerrainTexturePainter: no terrain assigned."); return; }

        TerrainData td = targetTerrain.terrainData;
        if (td == null) { Debug.LogWarning("TerrainTexturePainter: terrainData is null on the assigned Terrain."); return; }

        if (groundLayer == null || riverLayer == null || cliffLayer == null)
        {
            Debug.LogWarning("TerrainTexturePainter: assign all three TerrainLayer assets (ground, river, cliff). Aborting paint.");
            return;
        }

        // ensure terrain layers order matches expected indices (ground,river,cliff)
        td.terrainLayers = new TerrainLayer[] { groundLayer, riverLayer, cliffLayer };

        int alphRes = overrideAlphamapResolution > 0 ? overrideAlphamapResolution : td.alphamapResolution;
        int numLayers = 3;
        float[,] hm = null;

        // prepare alpha map
        float[,,] alphaMaps = new float[alphRes, alphRes, numLayers];

        float mapSeed = seed;
        if (mapSeed == 0f) mapSeed = Random.Range(1f, 10000f);

        // iterate over each alphamap cell
        for (int y = 0; y < alphRes; y++)
        {
            for (int x = 0; x < alphRes; x++)
            {
                // normalized coords (0..1) for interpolated queries (u = x/(res-1))
                float u = (float)x / (alphRes - 1f);
                float v = (float)y / (alphRes - 1f);

                // world height in meters at this point
                float worldHeight = td.GetInterpolatedHeight(u, v);

                // normal and slope
                Vector3 normal = td.GetInterpolatedNormal(u, v);
                float slopeDeg = Vector3.Angle(normal, Vector3.up);

                // noise to break straight lines
                float noise = (Mathf.PerlinNoise(u / noiseScale + mapSeed * 0.001f, v / noiseScale + mapSeed * 0.002f) - 0.5f) * 2f;
                noise *= noiseStrength;

                // river (water) score based on height below waterLevel and being relatively flat
                float heightDiff = waterLevel - worldHeight; // positive => below water level
                float shoreT = Mathf.Clamp01((heightDiff + shoreBlend) / (shoreBlend * 2f)); // soft band near waterLevel
                float slopeFactor = 1f - Mathf.Clamp01(slopeDeg / Mathf.Max(0.0001f, riverMaxSlope));
                float riverRaw = shoreT * slopeFactor;

                // apply noise influence (small)
                riverRaw = Mathf.Clamp01(riverRaw + noise * 0.25f);

                // cliff score based on slope above threshold
                float cliffRaw = Mathf.Clamp01((slopeDeg - cliffSlope) / Mathf.Max(0.0001f, cliffFalloff));
                cliffRaw = Mathf.Clamp01(cliffRaw + noise * 0.35f);

                // ground is remaining weight (but allow gentle blend)
                float groundRaw = Mathf.Clamp01(1f - Mathf.Max(riverRaw, cliffRaw));

                // optional extra weighting to avoid tiny islands of river/cliff
                // final blend with smoothstep
                riverRaw = Mathf.SmoothStep(0f, 1f, riverRaw);
                cliffRaw = Mathf.SmoothStep(0f, 1f, cliffRaw);
                groundRaw = Mathf.SmoothStep(0f, 1f, groundRaw);

                // normalization so all layers sum to 1
                float sum = groundRaw + riverRaw + cliffRaw;
                if (sum <= 0f) sum = 1f;
                alphaMaps[y, x, 0] = groundRaw / sum;
                alphaMaps[y, x, 1] = riverRaw / sum;
                alphaMaps[y, x, 2] = cliffRaw / sum;
            }
        }

        // apply splatmap
        td.SetAlphamaps(0, 0, alphaMaps);
        Debug.Log($"TerrainTexturePainter: Painted {alphRes}x{alphRes} splatmap (ground/river/cliff).");
    }
}
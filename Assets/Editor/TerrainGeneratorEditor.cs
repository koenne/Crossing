using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(TerrainGenerator))]
public class TerrainGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TerrainGenerator generator = (TerrainGenerator)target;
        //TerrainPopulator terrainPopulator = (TerrainPopulator)target;

        GUILayout.Space(15);
        GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f);

        if (GUILayout.Button("Generate New Map", GUILayout.Height(38)))
        {
            // Determine a valid Terrain to record/dirty for undo operations
            Terrain target = generator.targetTerrain != null ? generator.targetTerrain : Terrain.activeTerrain;
            if (target == null || target.terrainData == null)
            {
                Debug.LogWarning("TerrainGeneratorEditor: No valid Terrain (or terrainData) assigned/active. Assign a Terrain in the inspector before generating.");
                return;
            }

            Undo.RecordObject(target.terrainData, "Generate Terrain Map");
            generator.GenerateMap();
            EditorUtility.SetDirty(target.terrainData);
        }
        //if (GUILayout.Button("Generate New Populator", GUILayout.Height(38)))
        //{
        //    Undo.RecordObject(generator.targetTerrain.terrainData, "Generate Populator Map");
        //    terrainPopulator.PopulateGrid();
        //    EditorUtility.SetDirty(terrainPopulator.targetTerrain.terrainData);
        //}
    }
}
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Siccity.GLTFUtility;
using System.Globalization;

public class PlaceObjects : MonoBehaviour
{
    string basePath;
    Dictionary<string, Vector3> scaleMap;
    Dictionary<string, Vector3> primaryPositions;
    Dictionary<string, Vector3> secondaryPositions;
    string modelsFolder;
    Transform assetsParent;

    private void Start()
    {
        assetsParent = GameObject.Find("Asset")?.transform;
    }

    public void onButtonClicked()
    {
        basePath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");
        assetsParent = GameObject.Find("Asset")?.transform;

        if (assetsParent == null)
        {
            Debug.LogError("❌ Nessun oggetto chiamato 'Asset' trovato nella scena.");
            return;
        }

        LoadData();
    }

    private string LoadRecentFolder()
    {
        string modelRoot = Path.Combine(Application.dataPath, "SessionItems", "3DModels");
        var latestFolder = new DirectoryInfo(modelRoot).GetDirectories()
            .OrderByDescending(d => d.LastWriteTime).FirstOrDefault();

        if (latestFolder == null)
        {
            Debug.LogError("❌ Nessuna cartella trovata in 3DModels.");
            return null;
        }

        return latestFolder.FullName;
    }

    void LoadData()
    {
        string scalePath = Path.Combine(basePath, "scale.json");
        string primaryPath = Path.Combine(basePath, "primary_positions_xyz.json");
        string secondaryPath = Path.Combine(basePath, "secondary_positions_xyz.json");

        var rawScales = JsonConvert.DeserializeObject<Dictionary<string, float>>(File.ReadAllText(scalePath));
        scaleMap = new Dictionary<string, Vector3>();
        foreach (var kvp in rawScales)
        {
            scaleMap[kvp.Key] = new Vector3(kvp.Value, kvp.Value, kvp.Value);
        }
        primaryPositions = JsonConvert.DeserializeObject<Dictionary<string, Vector3>>(File.ReadAllText(primaryPath));
        secondaryPositions = JsonConvert.DeserializeObject<Dictionary<string, Vector3>>(File.ReadAllText(secondaryPath));

        modelsFolder = LoadRecentFolder();
    }

    public void PlaceSingleObject(string objectName, Vector3 position)
    {
        string folder = LoadRecentFolder();
        string objectPath = Path.Combine(folder, objectName + ".glb");

        basePath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");
        string scalePath = Path.Combine(basePath, "scale.json");
        float scaleFactor = 1f; // default

        if (File.Exists(scalePath))
        {
            try
            {
                string json = File.ReadAllText(scalePath);
                Dictionary<string, string> scaleMap = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

                string nameForScaleLookup = objectName.Replace("_", " ");

                if (scaleMap.TryGetValue(nameForScaleLookup, out string scaleStr) && float.TryParse(scaleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedScale))

                {
                    scaleFactor = parsedScale;
                }
                else
                {
                    Debug.LogWarning($"Nessun valore di scala trovato per '{objectName}', uso default = 1.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Errore nella lettura di scale.json: {ex.Message}");
            }
        }
        else
        {
            Debug.LogWarning("scale.json non trovato, uso scala = 1 per tutti gli oggetti.");
        }

        if (!File.Exists(objectPath))
        {
            Debug.LogError($"GLB file not found at: {objectPath}");
            return;
        }

        GameObject loadedObject;
        try
        {
            loadedObject = Importer.LoadFromFile(objectPath);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to load GLB file: {ex.Message}");
            return;
        }

        if (loadedObject != null)
        {
            loadedObject.transform.position = position;
            loadedObject.transform.localScale = Vector3.one * scaleFactor;
            loadedObject.name = objectName;
            loadedObject.transform.parent = assetsParent;

            Transform meshChild = loadedObject.transform.Find("geometry_0");

            if (meshChild != null)
            {
                // Aggiungi il BoxCollider (se non c'è già)
                if (meshChild.GetComponent<BoxCollider>() == null)
                {
                    BoxCollider collider = meshChild.gameObject.AddComponent<BoxCollider>();

                    // Opzionale: centra la posizione del genitore in base al collider (per sollevare se affonda)
                    float bottomY = collider.bounds.min.y;
                    if (bottomY < 0)
                    {
                        Vector3 correction = new Vector3(0, -bottomY, 0);
                        loadedObject.transform.position += correction;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"geometry_0 non trovato come figlio di {objectName}");
            }

            Debug.Log($"Loaded and placed '{objectName}' at {position} with scale {scaleFactor}");
        }
        else
        {
            Debug.LogError("❌ GLB loading returned null GameObject.");
        }
    }
    string ExtractBaseName(string fullName)
    {
        int lastUnderscore = fullName.LastIndexOf('_');
        if (lastUnderscore > 0)
            return fullName.Substring(0, lastUnderscore);
        return fullName;
    }
}

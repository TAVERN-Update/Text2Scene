using UnityEngine;
using TMPro;
using System.IO;
using System;
using System.Collections.Generic;
using static UnityEngine.EventSystems.EventTrigger;

public class ImageUISetup : MonoBehaviour
{
    [Header("References")]
    public TMP_Text sceneTitleText;
    public GameObject objectLayoutPrefab;
    public GameObject wallLayoutPrefab;
    public GameObject floorLayoutPrefab;
    public Transform gridContainer;
    public TMP_Text debug;

    public GameObject instructions;

    void Start()
    {
        SetupUIFromJson();
        debug.text = "Scene set up.";
        // Avvia generazione immagini dopo aver settato la UI
        StartCoroutine(FindObjectOfType<GeneratePictures>().GenerateAllImages());
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.X))
        {
            if (instructions != null)
            {
                instructions.SetActive(false);
            }
        }
    }

    void SetupUIFromJson()
    {
        string folderPath = Path.Combine(Application.dataPath, "SessionItems/SceneJson");
        if (!Directory.Exists(folderPath))
        {
            Debug.LogWarning("Cartella SceneJson non trovata.");
            return;
        }

        string latestFile = null;
        DateTime lastWrite = DateTime.MinValue;

        foreach (string file in Directory.GetFiles(folderPath, "*.json"))
        {
            DateTime writeTime = File.GetLastWriteTime(file);
            if (writeTime > lastWrite)
            {
                lastWrite = writeTime;
                latestFile = file;
            }
        }

        if (string.IsNullOrEmpty(latestFile))
        {
            Debug.LogWarning("Nessun file JSON trovato.");
            return;
        }

        string json = File.ReadAllText(latestFile);
        SceneData scene = JsonUtility.FromJson<SceneData>(json);

        // 2. Setta il titolo della scena
        if (sceneTitleText != null)
        {
            sceneTitleText.text = "Generating images for the " + scene.scene_name;
        }

        // 3. Per ogni oggetto, istanzia prefab e scrivi nome
        foreach (SceneObject obj in scene.objects)
        {
            GameObject entry = Instantiate(objectLayoutPrefab, gridContainer);

            ObjectImageLayout layout = entry.GetComponent<ObjectImageLayout>();
            layout.SetName(obj.name);

            string prompt = $"{obj.visual_description} for a {scene.scene_description}. Full view, neutral background, centered, studio lighting, high quality.";
            layout.prompt = prompt;
        }

        GameObject wallEntry = Instantiate(wallLayoutPrefab, gridContainer);
        ObjectImageLayout wallLayout = wallEntry.GetComponent<ObjectImageLayout>();
        wallLayout.SetName("Wall texture");

        GameObject floorEntry = Instantiate(floorLayoutPrefab, gridContainer);
        ObjectImageLayout floorLayout = floorEntry.GetComponent<ObjectImageLayout>();
        floorLayout.SetName("Floor texture");
    }
}

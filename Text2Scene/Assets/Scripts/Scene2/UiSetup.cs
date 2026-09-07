using UnityEngine;
using TMPro;
using System.IO;
using System.Linq;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System;

public class UISetup : MonoBehaviour
{
    public GameObject addObjectPrefab;
    public Transform contentContainer;

    public TMP_InputField sceneName;
    public TMP_InputField sceneDescription;
    public TMP_InputField sceneStyle;

    private SceneData originalData;
    private SceneData currentData;

    private string lastLoadedJsonPath;

    void Start()
    {
        LoadLatestSceneJson();
    }

    void LoadLatestSceneJson()
    {
        string folderPath = Path.Combine(Application.dataPath, "SessionItems/SceneJson");
        if (!Directory.Exists(folderPath))
        {
            Debug.LogWarning("SceneJson folder not found.");
            return;
        }

        string latestFile = Directory.GetFiles(folderPath, "*.json")
                                     .OrderByDescending(File.GetLastWriteTime)
                                     .FirstOrDefault();

        if (string.IsNullOrEmpty(latestFile))
        {
            Debug.LogWarning("🟡 Nessun file JSON trovato in " + folderPath);
            return;
        }

        lastLoadedJsonPath = latestFile;

        string rawJson = File.ReadAllText(lastLoadedJsonPath);
        string fixedJson = FixMalformedJson(rawJson);

        try {
            originalData = JsonUtility.FromJson<SceneData>(fixedJson);
            currentData = JsonUtility.FromJson<SceneData>(fixedJson);
            PopulateUIFromSceneData(currentData);
        }
        
        catch(Exception e)
        {
            Debug.LogError($"Errore parsing JSON:\n{e.Message}\nFile: {latestFile}");
        }
        
    }

    void PopulateUIFromSceneData(SceneData scene)
    {
        foreach (Transform child in contentContainer)
        {
            Destroy(child.gameObject);
        }

        if (scene != null && scene.objects != null && scene.scene_name != null && scene.scene_description != null)
        {
            sceneName.text = scene.scene_name;
            sceneDescription.text = scene.scene_description;
            sceneStyle.text = scene.visual_style;

            for (int i = 0; i < scene.objects.Count; i++)
            {
                var obj = scene.objects[i];
                GameObject entry = Instantiate(addObjectPrefab, contentContainer);

                TMP_Text nameField = entry.GetComponentInChildren<TMP_Text>();
                TMP_InputField qtyField = entry.transform.Find("ObjectQuantity")?.GetComponentInChildren<TMP_InputField>();
                Button removeButton = entry.transform.Find("RemoveButton")?.GetComponent<Button>();

                if (nameField != null)
                    nameField.text = obj.name;

                if (qtyField != null)
                {
                    qtyField.text = obj.quantity.ToString();
                    int index = i;
                    qtyField.onValueChanged.AddListener((value) =>
                    {
                        if (int.TryParse(value, out int result))
                        {
                            currentData.objects[index].quantity = result;
                        }
                    });
                }

                if (removeButton != null)
                {
                    int index = i; // cattura corretta
                    removeButton.onClick.AddListener(() =>
                    {
                        Destroy(entry);
                        currentData.objects.RemoveAt(index); 
                        PopulateUIFromSceneData(currentData);
                    });
                }
            }
        }
    }

    string FixMalformedJson(string json)
    {
        string fixedJson = System.Text.RegularExpressions.Regex.Replace(json, @"},\s*([\]}])", @"}$1");

        // 🔧 Eventuali altri fix futuri qui...
        return fixedJson;
    }


    public void ResetChanges()
    {
        currentData = JsonUtility.FromJson<SceneData>(JsonUtility.ToJson(originalData));
        PopulateUIFromSceneData(currentData);
    }

    public void SaveChanges()
    {
        string newName = sceneName.text;
        string newDescription = sceneDescription.text;
        string newStyle = sceneStyle.text;

        if(newName != "" || newName!= null)
            currentData.scene_name = newName;

        if(newDescription != "" || newDescription != null)
            currentData.scene_name = newDescription;

        if(newStyle != "" || newStyle != null)
            currentData.scene_name = newStyle;

        string json = JsonUtility.ToJson(currentData, true);
        string folderPath = Path.Combine(Application.dataPath, "SessionItems/SceneJson");
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string fileName = "scene_" + currentData.scene_name + ".json";
        string fullPath = Path.Combine(folderPath, fileName);
        File.WriteAllText(fullPath, json);
        Debug.Log("Scene saved to: " + fullPath);
    }

    public void OnNextSceneButtonClicked()
    {   
        SaveChanges();
        SceneManager.LoadScene("PictureScene");
    }

    public SceneData GetCurrentSceneData()
    {
        return currentData;
    }
}

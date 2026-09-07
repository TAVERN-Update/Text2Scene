using UnityEngine;
using System.IO;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using UnityEngine.UI;
using System.Linq;

public class AddObject : MonoBehaviour
{
    public TMP_InputField objectNameInput;
    public Transform contentContainer;
    public GameObject addObjectPrefab;
    public ScrollRect scrollRect;
    public UISetup uiSetup; // Reference to UISetup to access currentData
    private string sessionFolderPath;

    [System.Serializable]
    public class LLMAddObjectResponse
    {
        public string response;
    }

    [System.Serializable]
    public class RequestData
    {
        public string system_prompt;
        public string user_prompt;
    }

    public void OnAddObjectClicked()
    {
        string newObjectName = objectNameInput.text.Trim();
        if (!string.IsNullOrEmpty(newObjectName))
        {
            StartCoroutine(SendAddObjectRequest(newObjectName));
        }
    }

    IEnumerator SendAddObjectRequest(string newObjectName)
    {
        string folderPath = Path.Combine(Application.dataPath, "SessionItems/SceneJson");
        string latestFile = Directory.GetFiles(folderPath, "*.json")
                                     .OrderByDescending(File.GetLastWriteTime)
                                     .FirstOrDefault();

        if (string.IsNullOrEmpty(latestFile))
        {
            Debug.LogError("No JSON file found in: " + folderPath);
            yield break;
        }

        string currentSceneJson = File.ReadAllText(latestFile);

        string systemPrompt = @"You are a 3D scene assistant. The user will provide an existing scene JSON and the name of a new object to be added. Your task is to generate a short visual description and a 3D position (x,y,z) for the new object, ensuring it fits coherently with the existing scene context.

        Return ONLY a compact JSON object describing the new item. Do NOT include any text, explanation, or markdown. The format must be:
        {
          'name': 'Object Name',
          'visual_description': 'Visual description here.',
          'quantity': int
        }";

        string userPrompt = "Add the following object to the scene: '" + newObjectName + "'. Scene JSON:\n" + currentSceneJson;

        string jsonPayload = JsonUtility.ToJson(new RequestData
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt
        });

        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/scene_prompt", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            LLMAddObjectResponse response = JsonUtility.FromJson<LLMAddObjectResponse>(req.downloadHandler.text);
            string extractedJson = ExtractJsonBlock(response.response);

            if (!string.IsNullOrEmpty(extractedJson))
            {
                extractedJson = extractedJson.Replace('\'', '"');

                Debug.Log("LLM Response (parsed): " + extractedJson);

                SceneObject newObj = JsonUtility.FromJson<SceneObject>(extractedJson);
                if (newObj.quantity <= 0) newObj.quantity = 1;

                if (uiSetup != null && uiSetup.GetCurrentSceneData() != null)
                {
                    uiSetup.GetCurrentSceneData().objects.Add(newObj);
                    Debug.Log("current objects:" + uiSetup.GetCurrentSceneData().objects);
                }

                CreateObjectUIEntry(newObj);
                
            }
            else
            {
                Debug.LogWarning("LLM response didn't contain a valid JSON block.");
            }
        }
        else
        {
            Debug.LogError("Error from LLM: " + req.error);
            Debug.LogError(req.downloadHandler.text);
        }
    }

    void CreateObjectUIEntry(SceneObject obj)
    {
        GameObject entry = Instantiate(addObjectPrefab, contentContainer);

        TMP_Text nameField = entry.transform.Find("ObjectName")?.GetComponent<TMP_Text>();
        TMP_InputField qtyField = entry.transform.Find("ObjectQuantity/InputField (TMP)")?.GetComponent<TMP_InputField>();

        if (nameField != null)
            nameField.text = obj.name;

        if (qtyField != null)
            qtyField.text = obj.quantity.ToString();

        LayoutRebuilder.ForceRebuildLayoutImmediate(contentContainer.GetComponent<RectTransform>());
        scrollRect.verticalNormalizedPosition = 0f;

    }

    string ExtractJsonBlock(string response)
    {
        int firstBrace = response.IndexOf('{');
        int lastBrace = response.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            string jsonBlock = response.Substring(firstBrace, lastBrace - firstBrace + 1);
            int extraClose = jsonBlock.LastIndexOf("}");
            return jsonBlock.Substring(0, extraClose + 1);
        }

        return null;
    }
}

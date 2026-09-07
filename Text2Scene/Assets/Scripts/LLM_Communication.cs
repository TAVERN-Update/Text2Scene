using UnityEngine;
using System.IO;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

public class LLM_Communication : MonoBehaviour
{
    public TMP_InputField inputField;
    public LLM_UI uiManager;
    public GameObject proceedButton;

    private List<string> conversationHistory;

    [System.Serializable]
    public class PlannerResponse
    {
        public string response;
    }

    private void Start()
    {
        conversationHistory = new List<string>();
    }

    public void OnGenerateSceneButtonClicked()
    {
        string userPrompt = inputField.text;
        StartCoroutine(SendScenePrompt(userPrompt));
    }

    IEnumerator SendScenePrompt(string userPrompt)
    {
        /*string systemPrompt = @"You are a 3D Scene Design Assistant for game developers and visual artists. The user will provide a high-level natural language description of a desired 3D scene.

        Your task is to:

        1. Ask clarifying questions **only if necessary** to resolve ambiguities in the input (e.g., indoor/outdoor, number of objects, visual style, or scene purpose).

        2. Decompose the scene into a structured list of 3D objects. IMPORTANT: You must think in terms of individual objects. For example, a computer will consist of a case, mouse, keyboard, 
        and monitor as four separate objects. Avoid entries that aggregate multiple objects! For each object, include:
           - A short, clear name
           - A brief description explaining what the object is and its purpose in the scene
           - A 3D position in meters (float values), compatible with Unity, inside a 1x1m scene centered at (0, 0, 0)

        3. Define a unified visual style for the entire scene, including:
           - A style label (e.g., ""low-poly pastel"", ""realistic modern"", etc.)
           - A color palette (main colors and accents)
           - Shape characteristics (e.g., smooth, blocky, geometric)
           - Overall mood and atmosphere

        4. For each object, generate a single sentence visual reference prompt using this structure:
           A [object name and purpose summary], in [style label], [color keywords], [shape keywords]. Neutral background. Full-body object reference.
           - The style keywords must match the unified visual style defined above
           - A quantity value indicating the amount of that object present in the scene. It must strictly be a single numerical value, NO RANGE ALLOWED.

        When you have enough information, reply with ONLY your output in a JSON format. Follow this example structure:

        {
          ""scene_name"": ""Medieval Tavern Dining Room"",
          ""scene_description"": ""A cozy, dimly-lit dining area inside a stone-walled tavern. The room has a warm and rustic atmosphere, lit by torches and a fireplace. Mood: lively and immersive."",
          ""objects"": [
            {
              ""name"": ""Wooden Table"",
              ""visual_description"": ""Rough-hewn rectangular wooden table with visible grain and iron reinforcements."",
              ""quantity"": 4
            },
            {
              ""name"": ""Wooden Chair"",
              ""visual_description"": ""Sturdy wooden chair with a curved backrest, slightly uneven legs, and worn texture."",
              ""quantity"": 13
            },
            {
              ""name"": ""Wall Torch"",
              ""visual_description"": ""Iron wall-mounted torch with flickering flames, casting dynamic shadows on the stone walls."",
              ""quantity"": 3
            },
            {
              ""name"": ""Tavern Banner"",
              ""visual_description"": ""Faded cloth banner hanging from wooden beams, with a medieval crest in gold thread."",
              ""quantity"": 2
            }
          ]
        }";*/

        string systemPrompt = @"You are a 3D Scene Design Assistant for game developers and visual artists. 
            The user will provide a high-level natural language description of a desired 3D scene.

            Your task is to:

            1. Ask clarifying questions to resolve ambiguities in the input (e.g., number of objects, visual style, or scene purpose).

            2. Decompose the scene into a structured list of 3D objects. Each object should be a singular element (do not generate ""books"", but ""book"" with quantity set to > 1)
            For each object, include:
               - A short, clear name
               - A brief description explaining what the object is and its purpose in the scene
               - A quantity value indicating how many instances of the object should be in the scene

            3. Ensure the scene contains functionally complete arrangements. For example, a kitchen should contain a fridge, a cooking spot, oven, counter, different type of utensil and so on. Reason by decomposing each scene into single elements.

            4. Aim to generate at least 6 distinct object types, unless the scene is intentionally minimal. Avoid generic objects unless contextually justified.

            5. Define a unified visual style for the entire scene, including:
               - A style label (e.g., ""low-poly pastel"", ""realistic modern"", etc.)
               - A color palette (main colors and accents)
               - Shape characteristics (e.g., smooth, blocky)
               - Overall mood and atmosphere

            6. For each object, generate a single sentence visual reference prompt using this structure:
               A [object name and purpose summary].

            CONSTRAINTS: Do not generate floor or wall materials, tiles, ceiling lights. Only single, distinct objects to fill a scene. 

            Output only your result in JSON format. Here's the structure, you must follow it strictly:

            {
              ""scene_name"": ""Modern Teen Bedroom"",
              ""scene_description"": ""A practical and cozy bedroom for a teenage student. It includes both rest and work areas, with functional furniture and personal decorative elements. Mood: casual, tidy, and youthful."",
              ""visual_style"": ""Pastel colors, simple curvy geometry, dreamy style"",
              ""objects"": [
                {
                  ""name"": ""Single Bed"",
                  ""visual_description"": ""Standard single bed with wood frame and blue bedding. Provides a resting area."",
                  ""quantity"": 1
                },
                {
                  ""name"": ""Desk"",
                  ""visual_description"": ""Rectangular desk with drawers and space for a computer. Central workspace."",
                  ""quantity"": 1
                },
                {
                  ""name"": ""Office Chair"",
                  ""visual_description"": ""Ergonomic chair with wheels and padded backrest. Used with desk."",
                  ""quantity"": 1
                }
                ...
              ]
            }
            
            Since the scene is small, keep the number of objects reasonable. Do not exceed 10 distinct objects.
            Ensure all objects are relevant to the scene's purpose and style.";

        conversationHistory.Add("User: " + userPrompt);

        string memoryContext = BuildContext(maxTurns: 10);
        string fullPrompt = memoryContext + "\nUser: " + userPrompt;

        string jsonPayload = JsonUtility.ToJson(new RequestData
        {
            system_prompt = systemPrompt,
            user_prompt = fullPrompt
        });

        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/scene_prompt", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("✅ Scene JSON received from Flask:");
            Debug.Log(req.downloadHandler.text);

            PlannerResponse response = JsonUtility.FromJson<PlannerResponse>(req.downloadHandler.text);

            if (response != null && !string.IsNullOrEmpty(response.response))
            {
                string extractedJson = ExtractJsonBlock(response.response);

                if (!string.IsNullOrEmpty(extractedJson))
                {
                    uiManager.AddBotMessageToChat("<color=green> I generated a draft for your scene. Proceed to see it and edit the details!");
                    SaveJsonToAssets(extractedJson);
                    conversationHistory.Add("Assistant: [JSON generato]");
                }
                else
                {
                    uiManager.AddBotMessageToChat(response.response);
                    conversationHistory.Add("Assistant: " + response.response);
                }

                SaveConversationToFile();
            }
        }
        else
        {
            Debug.LogError("❌ Error calling Flask server: " + req.error);
            Debug.LogError("Raw response: " + req.downloadHandler.text);
        }
    }

    string ExtractJsonBlock(string response)
    {
        int firstBrace = response.IndexOf('{');
        int lastBrace = response.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return response.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return null; // No valid JSON found
    }

    string BuildContext(int maxTurns)
    {
        return string.Join("\n", conversationHistory.Skip(Mathf.Max(0, conversationHistory.Count - maxTurns)));
    }

    void SaveConversationToFile()
    {
        string path = Application.persistentDataPath + "/chat_log.txt";
        File.WriteAllText(path, string.Join("\n", conversationHistory));
        Debug.Log("💾 Chat salvata in: " + path);
    }

    void SaveJsonToAssets(string json)
    {
        string folderPath = Application.dataPath + "/SessionItems/SceneJson";
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string fileName = "scene_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
        string fullPath = Path.Combine(folderPath, fileName);

        File.WriteAllText(fullPath, json);
        Debug.Log("💾 JSON salvato in: " + fullPath);
        proceedButton.SetActive(true);
    }

    [System.Serializable]
    public class RequestData
    {
        public string system_prompt;
        public string user_prompt;
    }
}

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using System.Linq;
using System;
using System.Text;
using TMPro;

[Serializable]
public class RequestData
{
    public string system_prompt;
    public string user_prompt;
}

[Serializable]
public class RequestDataImage
{
    public string system_prompt;
    public string user_prompt;

    public string image_string;
}


[Serializable]
public class ResponseWrapper
{
    public string response;
}

public class ScaleModels : MonoBehaviour
{
    public UnityEngine.Events.UnityEvent OnScaleJsonReady;

    private string layoutOutputFile = "layout.json";

    public TMP_Text debug;

    public void Scale()
    {
        debug.text = "Starting the generation of scaling values...";
        string basePath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");
        string scalePath = Path.Combine(basePath, "scale.json");

        if (File.Exists(scalePath))
        {
            File.Delete(scalePath);
            Debug.Log("scale.json esistente eliminato.");
        }

        string jsonPath = GetMostRecentJson(basePath);
        string jsonContent =  File.ReadAllText(jsonPath);
        Debug.Log("Most recent JSON: " + jsonContent);
        SceneData scene = JsonUtility.FromJson<SceneData>(jsonContent);


        if (scene == null || scene.objects == null)
        {
            Debug.LogError("❌ Errore nel parsing del JSON scena.");
            return;
        }

        StartCoroutine(GenerateScale(scene));        
    }

    private string GetMostRecentJson(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Debug.LogError("Cartella non trovata: " + folderPath);
            return null;
        }

        var files = Directory.GetFiles(folderPath, "scene*.json")
            .Where(f => !Path.GetFileName(f).Equals(layoutOutputFile, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0) return null;

        string latestFile = files
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .First()
            .FullName;

        return latestFile;
    }

    private IEnumerator GenerateScale(SceneData scene)
    {
        string llamaEndpoint = "http://localhost:5000/scene_prompt";

        string systemPrompt = @"
            You are a spatial reasoning assistant specialized in virtual scene layout.

            Your task is to estimate the **real-world height (from floor to top)** of a list of objects found in a 3D room, and generate a **uniform scaling factor** for each object based on height.

            --- Instructions ---
            - Estimate the vertical height of each object in meters (from the floor to the top).
            - Use reasonable approximations based on typical furniture and indoor items.
            - A height of **1.0 meters corresponds to a scaling factor of 1.0**.
            - You must **normalize all scale values to stay within the range [0.4, 1.6]**.
            - For example:
              - An object that is 0.4m tall → scale = 0.4
              - An object that is 1.6m tall → scale = 1.6
              - An object that is 2.0m tall → clamp to 1.6
              - An object that is 0.2m tall → clamp to 0.4
            - Do NOT sort or compare objects with each other. Just evaluate their height and map it to the scale range.
            - Output a JSON object in the format: { object_name: scale }
            - Use dot (.) as decimal separator. No explanation, no markdown, no code blocks.

            --- One-shot Example ---

            Objects:
            - Laptop
            - Poster
            - Desk
            - Floor Lamp
            - Bookshelf

            Step-by-step:
            1. Estimate real-world heights:
               - Laptop → 0.03 m
               - Poster → 0.2 m
               - Desk → 0.75 m
               - Floor Lamp → 1.6 m
               - Bookshelf → 2.2 m

            2. Map height to scale:
               - Laptop (0.03 m) → scale = 0.4 (clamped)
               - Poster (0.2 m) → scale = 0.4 (clamped)
               - Desk (0.75 m) → scale = 0.75
               - Floor Lamp (1.6 m) → scale = 1.6
               - Bookshelf (2.2 m) → scale = 1.6 (clamped)

            Final JSON:
            {
              ""Laptop"": 0.4,
              ""Poster"": 0.4,
              ""Desk"": 0.75,
              ""Floor Lamp"": 1.6,
              ""Bookshelf"": 1.6
            }

            --- Now do the same for this scene ---
            ";

        string objects = "";
        foreach (var obj in scene.objects)
        {
            objects += $"\n- {obj.name}: {obj.visual_description}";
        }

        string userPrompt = scene.scene_description + "\nObjects (with descriptions):\n" + objects;

        RequestData request = new RequestData
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt
        };

        string jsonPayload = JsonUtility.ToJson(request);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);

        UnityWebRequest req = new UnityWebRequest(llamaEndpoint, "POST");
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("❌ Errore richiesta scaling CoT: " + req.error);
            yield break;
        }

        var wrapper = JsonUtility.FromJson<ResponseWrapper>(req.downloadHandler.text);
        string reasoningResponse = wrapper.response;
        Debug.Log("CoT reasoning per scaling:\n" + reasoningResponse);

        string extractionPrompt = "You just performed a reasoning step to determine object scales for a 3D scene. Now, please return ONLY the final scaling values as a JSON object in the format: { object_name: scale } — no explanation, no markdown, no code formatting.";

        RequestData cleanRequest = new RequestData
        {
            system_prompt = extractionPrompt,
            user_prompt = reasoningResponse
        };

        string jsonExtractPayload = JsonUtility.ToJson(cleanRequest);
        byte[] cleanBody = Encoding.UTF8.GetBytes(jsonExtractPayload);

        UnityWebRequest extractReq = new UnityWebRequest(llamaEndpoint, "POST");
        extractReq.uploadHandler = new UploadHandlerRaw(cleanBody);
        extractReq.downloadHandler = new DownloadHandlerBuffer();
        extractReq.SetRequestHeader("Content-Type", "application/json");

        yield return extractReq.SendWebRequest();

        if (extractReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("❌ Errore nella richiesta del JSON finale di scala: " + extractReq.error);
            yield break;
        }

        var finalWrapper = JsonUtility.FromJson<ResponseWrapper>(extractReq.downloadHandler.text);
        string scaleJson = finalWrapper.response;

        string outputPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "scale.json");
        File.WriteAllText(outputPath, scaleJson);
        Debug.Log("scale.json salvato in: " + outputPath);

        //OnScaleJsonReady?.Invoke();
    }


}

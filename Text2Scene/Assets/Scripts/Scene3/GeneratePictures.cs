using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using System.Collections;
using System.IO;
using System;
using TMPro;
using System.Text.RegularExpressions;
using UnityEngine.SceneManagement;
using static LLM_Communication;
using System.Text;
using UnityEditor.SearchService;

public class GeneratePictures : MonoBehaviour
{
    [Header("References")]
    public Transform gridContainer;
    public string serverUrl = "http://localhost:5000/generate_image";
    public TMP_Text debug;
    public GameObject proceed;

    private ObjectImageLayout floorLayout;
    private ObjectImageLayout wallLayout;
    private SceneData scene;

    private const string defaultNegativePrompt = "distortion, blur, artifacts, low quality";
    private string sessionFolderPath;

    void Start()
    {
        //StartCoroutine(GenerateAllImages());
    }

    public IEnumerator GenerateAllImages()
    {
        foreach (Transform child in gridContainer)
        {
            var layout = child.GetComponent<ObjectImageLayout>();
            if (layout == null) continue;

            if (layout.name.Contains("Floor"))
            {
                floorLayout = layout;
                Debug.Log("✅ Floor layout trovato!");
            }
            else if (layout.name.Contains("Wall"))
            {
                wallLayout = layout;
                Debug.Log("✅ Wall layout trovato!");
            }
            else
            {
                layout.SetNegativePromptVisible(false); // solo oggetti
            }
        }

        if (floorLayout == null || wallLayout == null)
        {
            Debug.LogError("❌ Floor or Wall layout non trovati!");
            yield break;
        }

        
        // Blocca interazione e input field per texture
        floorLayout.SetNegativePromptVisible(false);
        wallLayout.SetNegativePromptVisible(false);
        floorLayout.SetInteractable(false);
        wallLayout.SetInteractable(false);

        debug.text = "Loading pipeline...";
        yield return StartCoroutine(LoadPipeline());

        debug.text = "Starting generation...";

        string folderPath = Path.Combine(Application.dataPath, "SessionItems/SceneJson");
        if (!Directory.Exists(folderPath))
        {
            Debug.LogError("SceneJson folder not found.");
            yield break;
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
            Debug.LogError("No JSON file found.");
            yield break;
        }

        string jsonContent = File.ReadAllText(latestFile);
        scene = JsonUtility.FromJson<SceneData>(jsonContent);

        if (scene.objects == null || scene.objects.Count == 0)
        {
            Debug.LogError("No objects in JSON.");
            yield break;
        }

        sessionFolderPath = Path.Combine(Application.dataPath, "SessionItems/ObjectPictures", scene.scene_name.Replace(" ", "_"));
        Directory.CreateDirectory(sessionFolderPath);

        debug.text = "JSON Loaded! Found: " + scene.objects.Count + " objects";

        for (int i = 0; i < scene.objects.Count; i++)
        {
            if (i >= gridContainer.childCount) break;

            SceneObject obj = scene.objects[i];
            debug.text = "Starting generation of " + obj.name;
            ObjectImageLayout layout = gridContainer.GetChild(i).GetComponent<ObjectImageLayout>();

            string prompt = $"A {obj.visual_description}. {scene.visual_style}, neutral white background, studio lighting";
            layout.prompt = prompt;

            string negativePrompt = layout.GetNegativePrompt();
            if (string.IsNullOrWhiteSpace(negativePrompt))
                negativePrompt = defaultNegativePrompt;

            yield return StartCoroutine(GenerateImage(prompt, obj.name, layout, negativePrompt));
        }

        // Genera texture pavimento e muro
        yield return StartCoroutine(GenerateTextureImage("wall", scene.scene_name, wallLayout, ""));
        yield return StartCoroutine(GenerateTextureImage("floor", scene.scene_name, floorLayout, ""));
        

        debug.text = "All images generated!";
        proceed.SetActive(true);

        foreach (Transform child in gridContainer)
        {
            ObjectImageLayout layout = child.GetComponent<ObjectImageLayout>();
            if (layout == floorLayout || layout == wallLayout) continue;

            layout.SetInteractable(true);
            layout.overlayButton.onClick.RemoveAllListeners();
            layout.overlayButton.onClick.AddListener(() => OnImageClick(layout));
            layout.SetNegativePromptVisible(true);
        }

        floorLayout.SetNegativePromptVisible(true);
        wallLayout.SetNegativePromptVisible(true);
        floorLayout.SetInteractable(true);
        wallLayout.SetInteractable(true);

        floorLayout.overlayButton.onClick.RemoveAllListeners();
        floorLayout.overlayButton.onClick.AddListener(() => OnImageClick(floorLayout));

        wallLayout.overlayButton.onClick.RemoveAllListeners();
        wallLayout.overlayButton.onClick.AddListener(() => OnImageClick(wallLayout));
    }

    IEnumerator GenerateImage(string prompt, string objectName, ObjectImageLayout layout, string negativePrompt)
    {
        string json = "{\"prompt\":\"" + prompt.Replace("\"", "\\\"") + "\",\"negative_prompt\":\"" + negativePrompt.Replace("\"", "\\\"") + "\"}";

        UnityWebRequest request = new UnityWebRequest(serverUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Errore per " + objectName + ": " + request.error);
            yield break;
        }

        string jsonResponse = request.downloadHandler.text;
        string base64Data = JsonUtility.FromJson<ResponseWrapper>(jsonResponse).image;
        byte[] imageBytes = Convert.FromBase64String(base64Data);

        string filePath = Path.Combine(sessionFolderPath, objectName.Replace(" ", "_") + ".png");
        File.WriteAllBytes(filePath, imageBytes);

        Texture2D texture = new Texture2D(2, 2);
        texture.LoadImage(imageBytes);

        Sprite sprite = Sprite.Create(texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f));

        layout.SetImage(sprite);

        foreach (Transform child in gridContainer)
        {
            ObjectImageLayout l = child.GetComponent<ObjectImageLayout>();
            l.SetInteractable(true);
        }
    }

    IEnumerator GenerateTextureImage(string type, string sceneName, ObjectImageLayout target, string prompt)
    {
        if(prompt == "")
            prompt = $"A {type} texture that could be found on the {type} of a {sceneName}, high quality";

        string json = "{\"prompt\":\"" + prompt.Replace("\"", "\\\"") + "\",\"negative_prompt\":\"" + defaultNegativePrompt.Replace("\"", "\\\"") + "\"}";

        UnityWebRequest request = new UnityWebRequest(serverUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Errore nella generazione texture " + type + ": " + request.error);
            yield break;
        }

        string jsonResponse = request.downloadHandler.text;
        string base64Data = JsonUtility.FromJson<ResponseWrapper>(jsonResponse).image;
        byte[] imageBytes = Convert.FromBase64String(base64Data);

        string filePath = Path.Combine(sessionFolderPath, type + "_texture.png");
        File.WriteAllBytes(filePath, imageBytes);

        Texture2D texture = new Texture2D(2, 2);
        texture.LoadImage(imageBytes);

        Sprite sprite = Sprite.Create(texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f));

        if (target == null)
        {
            Debug.LogError($"[TEXTURE] Layout per {type} è null!");
            yield break;
        }

        Debug.Log($"[TEXTURE] Applying {type} texture to {target?.name}");
        target.SetImage(sprite);
        Debug.Log($"✅ TEXTURE {type} assegnata a {target.name}, sprite={sprite}, tex size={sprite.texture.width}x{sprite.texture.height}");

        foreach (Transform child in gridContainer)
        {
            ObjectImageLayout l = child.GetComponent<ObjectImageLayout>();
            l.SetInteractable(true);
        }

    }

    IEnumerator LoadPipeline()
    {
        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/load_pipeline", "POST");
        req.uploadHandler = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogError("Errore nel caricamento pipeline: " + req.error);
        else
            Debug.Log("Loaded pipeline.");
    }

    public void ProceedTo3D()
    {
        UnloadPipeline();
        SceneManager.LoadScene("3DGenerationScene");
    }

    IEnumerator UnloadPipeline()
    {
        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/unload_pipeline", "POST");
        req.uploadHandler = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogError("Errore nello scaricamento pipeline: " + req.error);
        else
            Debug.Log("Unloaded pipeline.");
    }

    void OnImageClick(ObjectImageLayout clickedLayout)
    {
        foreach (Transform child in gridContainer)
        {
            ObjectImageLayout layout = child.GetComponent<ObjectImageLayout>();
            layout.SetInteractable(false);
        }

        clickedLayout.ResetToWhite();

        string negativePrompt = clickedLayout.GetNegativePrompt();
        if (string.IsNullOrWhiteSpace(negativePrompt))
            negativePrompt = defaultNegativePrompt;


        if (clickedLayout != floorLayout && clickedLayout != wallLayout)
        {
            StartCoroutine(RefinePrompt(clickedLayout.prompt, clickedLayout.nameField.text, clickedLayout.GetNegativePrompt(), clickedLayout, negativePrompt, false, false));
        }

        else if (clickedLayout == floorLayout)
        {
            StartCoroutine(RefinePrompt(clickedLayout.prompt, clickedLayout.nameField.text, clickedLayout.GetNegativePrompt(), clickedLayout, negativePrompt, true, false));
        }

        else if (clickedLayout == wallLayout)
        {
            StartCoroutine(RefinePrompt(clickedLayout.prompt, clickedLayout.nameField.text, clickedLayout.GetNegativePrompt(), clickedLayout, negativePrompt, true, true));
        }
    }

    IEnumerator RefinePrompt(string prompt, string name, string disliked, ObjectImageLayout layout, string negativePrompt, bool isTexture, bool isWall)
    {
        Debug.Log("DISLIKED: " + disliked);
        string systemPrompt = @"
            You are an assistant helping a user refine their image generation prompt to improve the results. 

            The user is generating reference pictures of single objects (e.g., 'a wooden chair', 'a ceramic vase'), but did not like the result.
            Your job is to suggest a corrected version of the original prompt – without changing its core meaning – and to write a short negative prompt to help eliminate undesired traits.

            You will be given:
            - The original prompt used to generate the image.
            - A short user feedback about what went wrong or what they would like to change.

            Your job is to:
            - Preserve the subject, style, and scene type in the original prompt.
            - Apply only the requested changes.
            - Keep the output under 77 tokens.

            Your response must follow this exact format:

            NEW PROMPT: ""[new prompt]""
            NEGATIVE PROMPT: ""[negative prompt]""

            Example:

            ORIGINAL PROMPT: A plush armchair in warm fabric, offering a comfortable seating option. Rustic warm tones, natural materials, cozy and inviting style, neutral white background, studio lighting.  
            WHAT USER DIDN'T LIKE: I want it to be blue!

            OUTPUT:  
            NEW PROMPT: ""A blue plush armchair in warm fabric, offering a comfortable seating option. Rustic warm tones, natural materials, cozy and inviting style, neutral white background, studio lighting.""  
            NEGATIVE PROMPT: ""objects overlap, distortion, surrealism.""
";

        string userPrompt = $"ORIGINAL PROMPT: {prompt}\n USER REQUEST: {disliked}";

        string new_prompt = "";
        string new_negative_prompt = "";

        string jsonPayload = JsonUtility.ToJson(new RequestData
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt,
        });

        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/scene_prompt", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            PlannerResponse response = JsonUtility.FromJson<PlannerResponse>(req.downloadHandler.text);

            if (response != null && !string.IsNullOrEmpty(response.response))
            {
                Match matchPrompt = Regex.Match(response.response, @"NEW PROMPT:\s*""([^""]+)""");
                Match matchNegative = Regex.Match(response.response, @"NEGATIVE PROMPT:\s*""([^""]+)""");

                if (matchPrompt.Success) new_prompt = matchPrompt.Groups[1].Value;
                if (matchNegative.Success) new_negative_prompt = matchNegative.Groups[1].Value;

                if (!string.IsNullOrEmpty(new_prompt) && !string.IsNullOrEmpty(new_negative_prompt))
                {
                    if(!isTexture)
                        StartCoroutine(GenerateImage(new_prompt, name, layout, new_negative_prompt));
                    else if (isWall) 
                        StartCoroutine(GenerateTextureImage("wall", scene.scene_name, layout, "Generate a wall texture. It is described like: " + new_prompt));
                    else if(!isWall)
                        StartCoroutine(GenerateTextureImage("floor", scene.scene_name, layout, "Generate a floor texture. It is described like: " + new_prompt));
                }
                else
                {
                    Debug.LogError("Regex parsing failed. Response: " + response.response);
                }
            }        }
        else
        {
            Debug.LogError("Error calling Flask server: " + req.error);
            Debug.LogError("Raw response: " + req.downloadHandler.text);
        }    

    }

    [Serializable]
    private class ResponseWrapper
    {
        public string image;
    }
}

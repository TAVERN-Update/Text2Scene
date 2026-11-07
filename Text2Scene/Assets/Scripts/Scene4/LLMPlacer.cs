using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using static AddObject;
using TMPro;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;

public class LLMPlacer : MonoBehaviour
{
    string objectJson;
    string imagePath;
    string groupPath;
    string positionPath;
    string scaleJson;

    public TMP_Text debug_info;

    public TMP_InputField refineInput;
    public Button refineButton;
    public Button undoButton;

    public enum PlacementState
    {
        Phase1_FloorGeneration,
        Phase1_FloorPlacement,
        Phase2_AccessoryGeneration,
        Phase2_AccessoryPlacement,
        Done
    }

    public PlacementState currentState = PlacementState.Phase1_FloorGeneration;

    public GameObject grid;

    private ScenePlacer td;
    private string basePath = "Assets/SessionItems/ObjectPictures";
    public GameObject instructions;

    // Oggetti della fase corrente ancora da elaborare.
    private Queue<JObject> localQueue = new Queue<JObject>();

    // Nomi degli oggetti richiesti, ripetuti secondo la quantità.
    private List<string> requestedObjects = new List<string>();
    [HideInInspector] public bool lastActionWasRefine = false;

    SceneData sceneData;

    string gridDescription = @"
    COORDINATE SYSTEM
    - Work in meters. The floor plane uses (x, z) with origin at the room center (0,0).
    - Allowed ranges: x in [-2.0, +2.0], z in [-2.0, +2.0], in increments of 0.4. 
    Each increment corresponds to one dot in the floor grid.
    Example: -2.0, -1.6, -1.2, ..., 1.6, 2.0.
    - Axes orientation:
    * +X points toward the BACK (segment AB), -X toward the FRONT (segment CD).
    * +Z points toward the LEFT (segment AD), -Z toward the RIGHT (segment BC).
    - Visible references: FRONT -> corner D, BACK -> corner B, LEFT -> corner A, RIGHT -> corner C.

    Y (placement type)
    - y in { ""floor"", ""above"", ""wall"" }.
    * ""floor"": object stands on the ground (height handled elsewhere).
    * ""above"": object is on top of another; use the SAME (x, z) of its anchor and specify anchor name.
    * ""wall"": object is mounted to a wall; specify wall_side in { ""left"", ""right"" }.
        - left wall -> z = +2.0, right wall -> z = -2.0.

    DIRECTIONS (natural language to axis change)
    - ""front""  => decrease X (toward -2, corner D).
    - ""back""   => increase X (toward +2, corner B).
    - ""left""   => increase Z (toward +2, corner A).
    - ""right""  => decrease Z (toward -2, corner C).

    ROTATIONS (degrees around +Y)
    - 0   => faces +Z (toward AD segment).
    - 180 => faces -Z (toward BC segment).
    - 90  => faces +X (toward AB segment).
    - -90 => faces -X (toward DC segment).
    ";

    private void Start()
    {
        refineButton.gameObject.SetActive(false);
        grid.SetActive(false);
        LoadLatestObjectData();
        DeleteAllExceptGrid();
        DeleteHistoryFiles();

        if (undoButton != null)
            undoButton.onClick.AddListener(OnUndoPressed);

        string latestFolder = GetLatestFolder(basePath);
        if (string.IsNullOrEmpty(latestFolder))
        {
            Debug.LogError("No folder found in ObjectPictures.");
            return;
        }

        string floorPath = Path.Combine(latestFolder, "floor_texture.png");
        string wallPath = Path.Combine(latestFolder, "wall_texture.png");

        Texture2D floorTexture = LoadTexture(floorPath);
        Texture2D wallTexture = LoadTexture(wallPath);

        ApplyTextureTo("Floor", floorTexture);
        ApplyTextureTo("Wall1", wallTexture);
        ApplyTextureTo("Wall2", wallTexture);
    }

    public void PlaceButton()
    {
        lastActionWasRefine = false;

        switch (currentState)
        {
            case PlacementState.Phase1_FloorGeneration:
                debug_info.text = "Thinking... Generating Floor Layout.";
                // Richiede al modello il piano dei mobili.
                StartCoroutine(GenerateLayoutPhase("floor"));
                break;
            
            case PlacementState.Phase1_FloorPlacement:
                // Elabora il prossimo mobile della coda locale.
                PlaceNextFromQueue();
                break;

            case PlacementState.Phase2_AccessoryGeneration:
                debug_info.text = "Thinking... Generating Accessories Layout.";
                // Richiede al modello il piano degli accessori.
                StartCoroutine(GenerateLayoutPhase("accessory"));
                break;

            case PlacementState.Phase2_AccessoryPlacement:
                // Elabora il prossimo accessorio della coda locale.
                PlaceNextFromQueue();
                break;

            case PlacementState.Done:
                debug_info.text = GetPlacementStatusMessage("Placement queue finished. All processed objects were placed.");

                Debug.Log(debug_info.text);
                break;
        }
    }

    public void RefineButton()
    {
        debug_info.text = "Refining your request, please wait...";
        lastActionWasRefine = true;
        StartCoroutine(RefineScene());
    }

    private void DeleteHistoryFiles()
    {
        string historyFolder = Path.Combine(positionPath, "history");
        if (Directory.Exists(historyFolder))
        {
            var files = Directory.GetFiles(historyFolder, "*.json");
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to delete file {file}: {ex.Message}");
                }
            }
        }
        else
        {
            Debug.LogWarning("History folder does not exist.");
        }
    }

    private void OnUndoPressed()
    {
        // Prepare paths
        string aaaJsonPath = Path.Combine(positionPath, "aaa.json");
        string historyFolder = Path.Combine(positionPath, "history");

        if (Directory.Exists(historyFolder))
        {
            // Find the most recent backup in history
            var historyFiles = Directory.GetFiles(historyFolder, "aaa*_history.json")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (historyFiles.Count > 0)
            {
                string latestBackup = historyFiles[0];

                // Move (or overwrite) backup to aaa.json in the main folder
                File.Copy(latestBackup, aaaJsonPath, true);
                td.StartPlacing();

                // Optionally, delete the backup after restoring
                // File.Delete(latestBackup);
            }
            else
            {
                Debug.LogWarning("No backup found in history folder.");
            }
        }
        else
        {
            Debug.LogWarning("History folder does not exist.");
        }        
    }

    void LoadLatestObjectData()
    {
        string rootPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");
        positionPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");
        imagePath = Path.Combine(Application.dataPath, "Resources", "Screenshots");

        td = FindObjectOfType<ScenePlacer>();

        var jsonFiles = new DirectoryInfo(rootPath)
            .GetFiles("scene*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        if (jsonFiles.Count == 0)
        {
            Debug.LogError("Nessun JSON valido trovato in SceneJson.");
            return;
        }

        string objectJsonPath = jsonFiles[0].FullName;
        DeleteJson();

        groupPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "groups.json");
        if (File.Exists(groupPath))
            File.Delete(groupPath);

        objectJson = File.ReadAllText(objectJsonPath);
        string scalePath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "calculated_uniform_scale.json");
        if(scalePath == null || !File.Exists(scalePath))
        {
            Debug.LogError("calculated_uniform_scale.json not found in SceneJson.");
            scaleJson = "{}"; // Default to empty JSON if not found
        }
        else  
            scaleJson = File.ReadAllText(scalePath);

        sceneData = JsonUtility.FromJson<SceneData>(objectJson);

        requestedObjects.Clear();
        
        // Inserisce i nomi degli oggetti nell'elenco secondo la quantità richiesta.
        if (sceneData != null && sceneData.objects != null)
        {
            foreach (var obj in sceneData.objects)
            {
                for (int i = 0; i < obj.quantity; i++)
                {
                    requestedObjects.Add(obj.name);
                }
            }
        }
        else
        {
            Debug.LogWarning("Nessun oggetto trovato nel file JSON iniziale.");
        }
    }

    private void DeleteJson()
    {
        string jsonPath = Path.Combine(positionPath, "aaa.json");
        if (File.Exists(jsonPath))
            File.Delete(jsonPath);
    }

    void DeleteAllExceptGrid()
    {
        if (!Directory.Exists(imagePath)) return;

        foreach (var file in Directory.GetFiles(imagePath))
        {
            if (Path.GetFileName(file) != "grid0.png")
                File.Delete(file);
        }
    }

    string LoadRecentScreenshot()
    {
        var files = Directory.GetFiles(imagePath, "*.png")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();

        if (files.Count == 0)
        {
            Debug.LogError("Nessuno screenshot trovato in: " + imagePath);
            return null;
        }

        return ConvertImageToBase64(files[0]);
    }

    string GetLatestFolder(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return null;

        var dirs = new DirectoryInfo(rootPath).GetDirectories();
        if (dirs.Length == 0)
            return null;

        return dirs.OrderByDescending(d => d.LastWriteTime).First().FullName;
    }

    Texture2D LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError($"Texture file not found at {path}");
            return null;
        }

        byte[] fileData = File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        if (!tex.LoadImage(fileData))
        {
            Debug.LogError($"Failed to load texture from {path}");
            return null;
        }
        return tex;
    }

    void ApplyTextureTo(string objectName, Texture2D texture)
    {
        GameObject obj = GameObject.Find(objectName);
        if (obj == null || texture == null)
        {
            Debug.LogWarning($"Cannot apply texture: {objectName} or texture is null.");
            return;
        }

        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer == null)
        {
            Debug.LogWarning($"Object '{objectName}' does not have a Renderer.");
            return;
        }

        renderer.material.mainTexture = texture;
    }

    // Richiede il piano per la fase indicata e avvia l'elaborazione della coda.
    IEnumerator GenerateLayoutPhase(string phaseType)
    {
        // Legge il piano semantico salvato in aaa.json.
        string currentScene = File.Exists(positionPath + "/aaa.json") ? File.ReadAllText(positionPath + "/aaa.json") : "";
        string existingTargets = string.IsNullOrEmpty(currentScene) ? "NONE (Empty room)" : currentScene;
        string objectDescriptions = "";

        foreach (var obj in sceneData.objects)
        {
            objectDescriptions +=
                $"- {obj.name.Replace(" ", "_")}: " +
                $"{obj.description} {obj.visual_description}\n";
        }

        string systemPrompt;
        string userPrompt;

        // Prepara i prompt per la fase richiesta.
        if (phaseType == "floor")
        {
            // Fase mobili: oggetti che non richiedono un altro mobile come supporto.
            systemPrompt = @"You are an AI Interior Designer. You receive a list of objects.
            YOUR TASK: Filter the list. Process objects that do not require another scene object as support.
            Exclude tabletop accessories.
            Use the object descriptions to identify the required spatial and functional relationships.
            Refer to ROOM MAPPING when planning the layout, ensuring that the chosen placements respect those relationships and form a coherent overall arrangement.
            Respect the rules for the current phase and keep object names unchanged.

            SUPPORT DEFINITIONS:
            - The room floor and walls are part of the room, not other scene objects.
            - 'floor' applies to objects that do not require a separate furniture item as physical support.
            - 'tabletop' applies to objects that rest on a separate furniture item.
            - Classify each object according to what supports it. Providing a surface for other objects does not make an object a tabletop accessory.

            ROOM MAPPING:
            - Image edges: TOP = wall_AD; BOTTOM = wall_BC; RIGHT = wall_AB; LEFT = wall_CD.
            - In this rectangular room, opposite walls have no corner vertex in common. Walls sharing a corner vertex are adjacent, not opposite.
            - OPPOSITE wall pairs: wall_AB - wall_CD; wall_AD - wall_BC.
            - ADJACENT wall pairs: wall_AB - wall_AD; wall_AB - wall_BC; wall_CD - wall_AD; wall_CD - wall_BC.

            RULES FOR FLOOR ITEMS:
            - relation_type MUST be 'absolute' or 'relative'.
            - If absolute, target MUST be 'room' and relation MUST be 'center', a wall or a corner.
            - If relative, target MUST be an existing object.
            - placement MUST be 'floor'.
            - category MUST be 'furniture'.";

            userPrompt = $@"Room theme: {sceneData.scene_name}
            Room description: {sceneData.scene_description}
            Visual style: {sceneData.visual_style}
            All requested objects: {PrintList(requestedObjects)}.
            Objects descriptions: {objectDescriptions}
            Extract and position objects that do not require another scene object as support. Leave tabletop accessories out.";
        }
        else
        {
            // Fase accessori: oggetti da appoggiare sui mobili presenti.

            // Conserva solo gli oggetti realmente presenti e attivi in Unity.
            JObject sceneObjects = string.IsNullOrEmpty(currentScene)
                ? new JObject()
                : JObject.Parse(currentScene);

            JObject placedObjects = new JObject();
            Transform assets = GameObject.Find("Asset")?.transform;

            foreach (var entry in sceneObjects.Properties())
            {
                Transform instance = assets?.Find(entry.Name);

                if (instance != null && instance.gameObject.activeInHierarchy)
                    placedObjects[entry.Name] = entry.Value.DeepClone();
            }

            existingTargets = placedObjects.Count > 0
                ? placedObjects.ToString()
                : "NONE (No objects currently placed)";
                
            systemPrompt = @"You are an AI Interior Designer. You receive a list of objects and the current room layout.
            YOUR TASK: Filter the list. Process ONLY the small tabletop accessories. 
            IGNORE floor furniture.
            Use the object descriptions to guide functional relationships and placement. 
            Respect the rules for the current phase and keep object names unchanged.

            RULES FOR ACCESSORIES:
            - relation_type MUST be 'relative'.
            - relation MUST be 'above'.
            - target MUST be the exact name of a flat furniture piece already in the room.
            - placement MUST be 'tabletop'.
            - category MUST be 'accessory'.";

            userPrompt = $@"Room theme: {sceneData.scene_name}
            Room description: {sceneData.scene_description}
            Visual style: {sceneData.visual_style}
            All requested objects: {PrintList(requestedObjects)}.
            Objects descriptions: {objectDescriptions}
            Existing objects physically present in the room (use ONLY these as possible supports): {existingTargets}.
            Extract and position ONLY the accessories, placing them 'above' existing surfaces.";
        }

        Debug.Log($"[Placement prompt - {phaseType}]\n{userPrompt}");

        // Carica lo screenshot disponibile e prepara il payload della richiesta.
        string gridImage = LoadRecentScreenshot();
        string jsonPayload = JsonUtility.ToJson(new RequestDataImage
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt,
            image_string = gridImage
        });

        // Invia la richiesta al server Python tramite /generate_position.
        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/generate_position", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            // Legge la risposta del server ed estrae il JSON.
            LLMAddObjectResponse response = JsonConvert.DeserializeObject<LLMAddObjectResponse>(req.downloadHandler.text);
            string extractedJson = SanitizeJson(ExtractJsonBlock(response.response));

            if (!string.IsNullOrEmpty(extractedJson))
            {
                // Estrae l'array items dalla risposta JSON.
                JObject responseObj = JObject.Parse(extractedJson);
                JArray generatedObjects = (JArray)responseObj["items"];

                // Accoda gli oggetti restituiti dal modello nella coda locale.
                foreach (JObject obj in generatedObjects)
                {
                    localQueue.Enqueue(obj);
                }

                // Imposta lo stato di elaborazione della fase richiesta.
                if (phaseType == "floor") currentState = PlacementState.Phase1_FloorPlacement;
                else currentState = PlacementState.Phase2_AccessoryPlacement;

                PlaceNextFromQueue();
            }
            else
            {
                Debug.LogError("No valid JSON found in response.");
            }
        }
        else
        {
            debug_info.text = "Generation failed. Press to try again.";
            Debug.LogError($"LLM Error ({req.responseCode}): {req.error}\n{req.downloadHandler.text}");
        }
    }

    // Formatta l'elenco dei nomi per il prompt, sostituendo gli spazi con underscore.
    private string PrintList(List<string> list)
    {
        if (list == null || list.Count == 0) return "[]";
        return "[" + string.Join(", ", list.Select(item => $"\"{item.Replace(" ", "_")}\"")) + "]";
    }

    // Restituisce un messaggio in base all'esito dell'ultima ricostruzione.
    private string GetPlacementStatusMessage(string successMessage)
    {
        if (td == null || !td.LastPlacementCompleted)
            return "Placement not completed. Check Unity Console.";

        if (td.UnplacedObjects.Count > 0)
            return "Placement incomplete. Not placed: "
                + string.Join(", ", td.UnplacedObjects) + ".";

        return successMessage;
    }

    // Elabora il prossimo oggetto della coda o aggiorna lo stato a fine fase.
    private void PlaceNextFromQueue()
    {
        // Se la coda è vuota, aggiorna lo stato e il messaggio dell'interfaccia.
        if (localQueue.Count == 0)
        {
            if (currentState == PlacementState.Phase1_FloorPlacement)
            {
                currentState = PlacementState.Phase2_AccessoryGeneration;
                debug_info.text = GetPlacementStatusMessage("Floor queue finished.") + " Press to generate accessories.";
            }
            else if (currentState == PlacementState.Phase2_AccessoryPlacement)
            {
                currentState = PlacementState.Done;
                debug_info.text = GetPlacementStatusMessage("Placement queue finished. All processed objects were placed.");
            }
            return;
        }

        // Estrae il prossimo oggetto da elaborare.
        JObject nextObj = localQueue.Dequeue();
        
        // Sostituisce gli spazi nel nome con underscore.
        string objName = nextObj["name"]?.ToString().Replace(" ", "_");

        // Carica il piano salvato o inizializza un dizionario vuoto.
        JObject aaaData = File.Exists(positionPath + "/aaa.json") ? JObject.Parse(File.ReadAllText(positionPath + "/aaa.json")) : new JObject();

        if (aaaData[objName] != null)
        {
            Debug.LogWarning(
                $"Oggetto '{objName}' gia' elaborato: elemento della coda ignorato.");

            debug_info.text = "Object already processed. Press to place next.";
            return;
        }

        // Aggiunge l'oggetto al piano usando il nome come chiave.
        aaaData[objName] = nextObj;

        // Salva il piano aggiornato in aaa.json.
        File.WriteAllText(positionPath + "/aaa.json", aaaData.ToString());
        
        // Ricostruisce tutti gli oggetti presenti nel piano aggiornato.
        td.StartPlacing();
        CaptureScreenshot();

        debug_info.text = GetPlacementStatusMessage($"Placed {objName}.") + " Press to place next.";
    }

    string ExtractJsonBlock(string response)
    {
        // Individua il primo delimitatore di apertura.
        int firstBrace = response.IndexOf('{');
        int firstBracket = response.IndexOf('[');
        int start = -1;

        if (firstBrace >= 0 && firstBracket >= 0) start = Math.Min(firstBrace, firstBracket);
        else if (firstBrace >= 0) start = firstBrace;
        else if (firstBracket >= 0) start = firstBracket;

        // Individua l'ultimo delimitatore di chiusura.
        int lastBrace = response.LastIndexOf('}');
        int lastBracket = response.LastIndexOf(']');
        int end = Math.Max(lastBrace, lastBracket);

        if (start >= 0 && end > start)
        {
            return response.Substring(start, end - start + 1);
        }

        return response;
    }

    public static string ConvertImageToBase64(string filePath)
    {
        // Verifica che il file esista
        if (!File.Exists(filePath))
        {
            Debug.LogError($"Errore: il file non esiste in: {filePath}");
            return null;
        }

        try
        {
            // Leggi tutti i byte del file
            byte[] imageBytes = File.ReadAllBytes(filePath);

            // Converti i byte in stringa Base64
            string base64String = Convert.ToBase64String(imageBytes);

            return base64String;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Eccezione durante la conversione: {ex.Message}");
            return null;
        }
    }

    public void CaptureScreenshot()
    {
        StartCoroutine(TakeScreenshot());
    }

    private System.Collections.IEnumerator TakeScreenshot()
    {
        // Nasconde temporaneamente il Canvas trovato per escluderlo dallo screenshot.
        Canvas uiCanvas = FindObjectOfType<Canvas>();
        if (uiCanvas != null) uiCanvas.enabled = false;

        // Attende la fine del frame prima di acquisire l'immagine.
        yield return new WaitForEndOfFrame();  

        // Acquisisce l'immagine a schermo e la salva in formato PNG.
        Texture2D screenImage = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        screenImage.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        screenImage.Apply();

        byte[] imageBytes = screenImage.EncodeToPNG();

        string folderPath = Path.Combine(Application.dataPath, "Resources/Screenshots");
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filePath = Path.Combine(folderPath, $"screenshot_{timestamp}.png");
        File.WriteAllBytes(filePath, imageBytes);

        // Riabilita il Canvas al termine della cattura.
        if (uiCanvas != null) uiCanvas.enabled = true;
    }

    IEnumerator RefineScene()
    {
        // --- BACKUP aaa.json to history subfolder ---
        string aaaJsonPath = Path.Combine(positionPath, "aaa.json");
        string historyFolder = Path.Combine(positionPath, "history");
        if (File.Exists(aaaJsonPath))
        {
            if (!Directory.Exists(historyFolder))
                Directory.CreateDirectory(historyFolder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupPath = Path.Combine(historyFolder, $"aaa_{timestamp}_history.json");
            File.Copy(aaaJsonPath, backupPath, true);
        }
        // --- END BACKUP ---

        string currentScene;
        string userRequest;
        if (!File.Exists(positionPath + "/aaa.json"))
        {
            currentScene = "";
        }
        else
        {
            currentScene = File.ReadAllText(positionPath + "/aaa.json");
        }

        if (refineInput.text == "")
            userRequest = "You choose what to refine.";

        else userRequest = refineInput.text;

        string systemPrompt = $@"
            You are a 3D scene designer and composer specialist. Your task is to refine a virtual room layout based on:
            - A picture of the scene. The floor coordinate system is defined by two labeled axes: X (blue), and Z (red).
            - A JSON representing object positions and rotations and scaling
            - A user request (e.g. 'move the chair to the left')

            Your goal is to make the scene more realistic, well-arranged, and consistent with both the request and the visual layout. You must apply spatial reasoning: verify placements, rotations, overlaps, and relationships (e.g., chairs facing tables).
            Please state your reasoning steps!

            {gridDescription}

            > OUTPUT FORMAT: The final part of the output MUST end with a valid JSON structure like the following:

            {{ "" unique object name "": 
                  {{ ""name"": ""[exact object name with no numbers]"", 
                    ""rotation"": 0, 
                    ""position"": {{ ""x"": 1, ""y"": ""floor"", ""z"": 6 }},
                    ""scale"": [scale value]                    
                   }}, 

             "" unique object name "": 
                  {{ ""name"": ""[exact object name with no numbers]"", 
                    ""rotation"": 0, 
                    ""position"": {{ ""x"": 1, ""y"": ""floor"", ""z"": 6 }},
                    ""scale"": [scale value]
                   }}, 
                ...
            }}

            Modify ONLY the objects explicitly mentioned in the user request. 
            All other objects must remain exactly as they currently are (same positions/rotations/scale).
            Please always return the complete configuration of the room, not just the object you just modified!!!
            ";

        string userPrompt = @$"Room description is: {sceneData.scene_description}

            The current state of the room is: {currentScene}

            User request is: {userRequest}";

        string gridImageBase64 = LoadRecentScreenshot();
        string jsonPayload = JsonUtility.ToJson(new RequestDataImage
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt,
            image_string = gridImageBase64
        });

        Debug.Log(jsonPayload);

        UnityWebRequest req = new UnityWebRequest("http://localhost:5000/generate_position", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            LLMAddObjectResponse response = JsonConvert.DeserializeObject<LLMAddObjectResponse>(req.downloadHandler.text);

            Debug.Log("******** REFINEMENT REASONING STEPS**********: \n" + response.response);
            string extractedJson = ExtractJsonBlock(response.response);
            extractedJson = SanitizeJson(extractedJson);

            extractedJson = extractedJson.Replace("’", "'").Replace("‘", "'"); // fancy quotes -> standard
            extractedJson = Regex.Replace(extractedJson, @"(?<!\\)\'", "\""); // single quotes -> double quotes, but not already escaped

            if (!string.IsNullOrEmpty(extractedJson))
            {

                var refinedObjects = JsonConvert.DeserializeObject<Dictionary<string, object>>(extractedJson);
                JObject aaaData = File.Exists(positionPath + "/aaa.json")
                    ? JObject.Parse(File.ReadAllText(positionPath + "/aaa.json"))
                    : new JObject();

                foreach (var kv in refinedObjects)
                {
                    aaaData[kv.Key] = (JObject)kv.Value;
                }

                File.WriteAllText(positionPath + "/aaa.json", aaaData.ToString());
                lastActionWasRefine = true;
                td.StartPlacing();
                debug_info.text = "Operation done, you can continue!";
                CaptureScreenshot();
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
    
    private string SanitizeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // Rimuove i ritorni carrello dal testo.
        s = s.Replace("\r", "");
        
        // Individua i delimitatori esterni del blocco JSON.
        int firstBrace = s.IndexOf('{');
        int firstBracket = s.IndexOf('[');
        int start = -1;
        
        if (firstBrace >= 0 && firstBracket >= 0) start = Math.Min(firstBrace, firstBracket);
        else if (firstBrace >= 0) start = firstBrace;
        else if (firstBracket >= 0) start = firstBracket;

        int end = Math.Max(s.LastIndexOf('}'), s.LastIndexOf(']'));

        if (start >= 0 && end > start) 
            s = s.Substring(start, end - start + 1);

        // Rimuove le virgole finali prima di } oppure ].
        s = System.Text.RegularExpressions.Regex.Replace(s, @",\s*(\}|\])", "$1");

        Debug.Log($"[JSON PULITO INVIATO AL PARSER]:\n{s}");

        return s;
    }

}

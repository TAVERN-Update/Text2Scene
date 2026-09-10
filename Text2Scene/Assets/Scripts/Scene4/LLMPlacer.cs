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
using UnityEditor;
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

    public int id;

    public GameObject grid;

    private ScenePlacer td;
    //public Button generateButton;
    private string basePath = "Assets/SessionItems/ObjectPictures";
    public GameObject instructions;


    public List<List<String>> groups = new List<List<string>>();
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

        /*
        > SPACING & VALIDATION
        - Positions are CONTINUOUS floats (2 decimals are enough).
        - Keep a minimum center-to-center separation of 0.5 m in the (x, z) plane between distinct floor/wall items, unless ""above"".
        - Always keep x and z within [-2, +2]. If ""wall"", project to the wall plane as specified.";*/

    private void Start()
    {
        id = 0;
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
        debug_info.text = "Placing objects, please wait...";
        lastActionWasRefine = false;

        if (id == 0)
            StartCoroutine(ClassifyObjects());
        else if (id < groups.Count)
            StartCoroutine(PlaceObjects(groups[id]));
        else
            Debug.LogWarning("no more groups!");
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
        // Check if last action was NOT a refinement
        if (!lastActionWasRefine)
        {
            // Decrease id (but not below zero)
            if (id > 0)
                id--;
        }

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

    IEnumerator ClassifyObjects()
    {
        string systemPrompt = @"
            You are a 3D artist and scene composer specialist.

            You will receive a scene description and a set of object that has to be placed in such a 3D scene. Your task is to logically group those object into sets that have to be placed together (so they are in a ""coherence group"" like table and chair, desk and chair, plate and table and so on). 

            If and object doesnt have to rely on any other object, put them in a singleton. Group together only the objects that you think should be related to each other when positioned into a 3D scene.Put each object as many times in the groups as it is indicated in their respective ""quantity"" attribute.

            Explain your steps, and output a json in the following form:

        {
            ""group1"": [""Bed""],
            ""group2"": [""Desk"", ""Gaming Chair"", ""Computer""],
            ""group3"": [""Table"", ""Chair"", ""Chair""],
            ""group4"": [""Fridge"", ""Oven"", ""Counter""],
        }";

        string userPrompt = "Group the following objects for the given scene:\n\n" + objectJson;


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
            Debug.Log("Groups: " + response.response);
            string extractedJson = ExtractJsonBlock(response.response);

            extractedJson = extractedJson.Replace("’", "'").Replace("‘", "'"); // fancy quotes -> standard
            extractedJson = Regex.Replace(extractedJson, @"(?<!\\)\'", "\""); // single quotes -> double quotes, but not already escaped


            if (!string.IsNullOrEmpty(extractedJson))
            {
                File.WriteAllText(Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "groups.json"), extractedJson);
                //Debug.Log("JSON: " + extractedJson);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(extractedJson);
                groups = dict.Values.ToList();
                //Debug.Log("adijadwsiujf: " + PrintGroup(groups[0]));
                StartCoroutine(PlaceObjects(groups[0]));
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

/*    private string PrintGroup(List<string> group)
    {
        if (group == null) return null;
        string output = "[" + string.Join(", ", group.Select(item => $"\"{item}\"")) + "]";
        return output;
    }
*/
    private string PrintGroup(List<string> group)
    {
        if (group == null) return null;
        // Trasforma ogni spazio in underscore prima di inviarlo nel prompt!
        // Es: "Cozy Sofa" diventa "Cozy_Sofa", "Wooden Coffee Table" diventa "Wooden_Coffee_Table"
        string output = "[" + string.Join(", ", group.Select(item => $"\"{item.Replace(" ", "_")}\"")) + "]";
        return output;
    }
    public IEnumerator PlaceObjects(List<string> group)
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
        if (!File.Exists(positionPath + "/aaa.json"))
        {
            currentScene = "";
        }
        else
        {
            currentScene = File.ReadAllText(positionPath + "/aaa.json");
        }       

        /*string systemPrompt = $@"
            You are a 3D scene designer and composer specialist. 
            TASK: You will receive a description of a 3D scene, and an object to place within it, the current state of the object positioned and a set of measures. For each object, you must determine a plausible 3D position and rotation, ensuring that the layout is realistic, visually appealing, and consistent with the scene description.

            You will receive a picture of the current state of the room to help you orientate. The floor coordinate system is defined by two labeled axes: X (blue), and Z (red).

            Your goal is to make the scene more realistic, well-arranged, and consistent with both the request and the visual layout. You must apply spatial reasoning: verify placements, rotations, overlaps, and relationships (e.g., chairs facing tables).
            Please note that the scene title is not an object to place. Please state your reasoning steps!            

            {gridDescription}

            OUTPUT FORMAT: The final part of the output MUST end with a valid JSON structure like the following:
            
            {{ "" unique object name "": 
                  {{ ""name"": ""[exact object name with no numbers]"", 
                    ""rotation"": 0, 
                    ""position"": {{ ""x"": 1, ""y"": ""floor"", ""z"": 5 }},
                    ""scale"": [scale value]
                   }}, 

             "" unique object name "": 
                  {{ ""name"": ""[exact object name with no numbers]"", 
                    ""rotation"": 0, 
                    ""position"": {{ ""x"": 1, ""y"": ""floor"", ""z"": 5 }}, 
                    ""scale"": [scale value]
                   }}, 
                ...
            }}
            
            OUTPUT: return ONLY the entries for the listed objects. 
            Do NOT include or modify any other object already in the room.
            Keep names EXACTLY as given (underscores preserved). Please always return the complete configuration of the room!!!
;
            ";

        string userPrompt = @$"Room description is: {sceneData.scene_description}

            Object to place are: {PrintGroup(group)},

            The current state of the room is: {currentScene}

            The scaling values in which to search are: {scaleJson}. Please look here only for the new objects.";*/

            
        // 1. Il nuovo System Prompt Semantico (senza coordinate X,Y,Z)    
        string systemPrompt = @"You are an AI Interior Designer. Analyze the top-down 2D room image and output the spatial placement for the new object(s).

        --- CORE CONSTRAINTS ---
        1. VISUAL REALITY ONLY: Look ONLY at the physical objects in the image. Ignore shadows and grid lines. CRITICAL: Built-in parts of furniture (like pillows already on a sofa) are NOT separate objects and do not occupy adjacent floor space. 
        2. NO HALLUCINATIONS: Do not target or reference any object that is not explicitly visible in the image or listed in the prompt.
        3. EXACT NAMES: The ""name"" key in JSON must exactly match the requested object. No math, no coordinates.

        --- SPATIAL MAPPING (CRITICAL) ---
        The image represents a top-down view of the room. Use this EXACT mapping for the room's edges:
        - TOP edge of the image = ""wall_AD""
        - BOTTOM edge of the image = ""wall_BC""
        - RIGHT edge of the image = ""wall_AB""
        - LEFT edge of the image = ""wall_CD""
        - CORNERS: Top-Right is ""corner_A"", Bottom-Right is ""corner_B"", Bottom-Left is ""corner_C"", Top-Left is ""corner_D"".

        --- PLACEMENT LOGIC ---
        - ABSOLUTE PLACEMENT: Use to place items against perimetral walls or empty room zones.
        - RELATIVE PLACEMENT: Use to place items near or on existing furniture.
        - SECONDARY FURNITURE: Low tables MUST be relative and ""in_front_of"" seating furniture.
        - WALL ASSETS: Large electronics MUST be absolute against a wall facing the room center.

        --- STRICT ALLOWED VOCABULARY ---
        - ""relation_type"": ""absolute"" OR ""relative""
        - ""relation"":
            -> IF absolute: ""center"", ""wall_AB"", ""wall_BC"", ""wall_CD"", ""wall_AD"", ""corner_A"", ""corner_B"", ""corner_C"", ""corner_D""
            -> IF relative & category is ""furniture"": ""in_front_of"", ""behind"", ""to_the_left_of"", ""to_the_right_of""
            -> IF relative & category is ""accessory"": MUST BE ""above""
        - ""scale"": ""small"", ""medium"", ""large"" (NEVER NUMBERS)
        - ""placement"":
            -> IF category is ""furniture"": MUST BE ""floor""
            -> IF category is ""accessory"": MUST BE ""tabletop""
        - ""category"": ""furniture"" OR ""accessory""

        --- OUTPUT FORMAT ---
        You MUST structure your reasoning strictly following this template for EVERY object, followed by the JSON block.

        <thought_process>
        [Object Name 1]
        - Category: (Structural items like Sofas, Tables, Heaters are 'furniture'. Small decor/food like Sushi, Vases, Plants are 'accessory') -> [furniture OR accessory]
        - Occupancy Check: (Look at the desired target. Is it already occupied by another item in this generation step? Yes/No) -> [Yes/No]
        - Target Selection: (If Occupancy Check is Yes, choose a DIFFERENT empty target. Max 1 accessory per small table) -> [Target Name]
        - Relation: (Direction from vocabulary. If target is a wall with existing furniture, route as 'relative' to that furniture instead) -> [relation]
        
        [Object Name 2]
        ... (repeat the 4 points above for all requested objects)
        </thought_process>

        {
            ""[OBJECT_NAME_1]"": {
                ""name"": ""[OBJECT_NAME_1]"",
                ""relation_type"": ""absolute OR relative"",
                ""relation"": ""CHOOSE FROM STRICT VOCABULARY"",
                ""target"": ""room OR [ExistingObjectName]"",
                ""scale"": ""small OR medium OR large"",
                ""placement"": ""floor OR tabletop"",
                ""category"": ""furniture OR accessory""
            }
            // ... REPEAT the exact structure above for EVERY object requested. Do not leave any object out!
        }";
        
        // Se siamo al Turno 0 (primo gruppo), la stanza deve essere trattata come vuota
        string existingTargets = (id == 0 || string.IsNullOrEmpty(currentScene)) 
            ? "NONE (The room is completely empty)" 
            : currentScene;

        // 2. Il nuovo User Prompt Pulito 
        string userPrompt = $@"Room description: {sceneData.scene_description}
        Existing objects ALREADY physically in the room (use ONLY these as targets): {existingTargets}
        NEW objects to place logically in THIS turn: {PrintGroup(group)}.
        Analyze the attached screenshot. If the room is empty, use absolute placement. If occupied, target an existing object.";

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


            Debug.Log("********REASONING AND RESPONSE*********: \n" + response.response);
            string extractedJson = ExtractJsonBlock(response.response);
            extractedJson = SanitizeJson(extractedJson);

            extractedJson = extractedJson.Replace("’", "'").Replace("‘", "'"); // fancy quotes -> standard
            extractedJson = Regex.Replace(extractedJson, @"(?<!\\)\'", "\""); // single quotes -> double quotes, but not already escaped


            if (!string.IsNullOrEmpty(extractedJson))
            {
                var newObjects = JsonConvert.DeserializeObject<Dictionary<string, object>>(extractedJson);

                JObject aaaData = File.Exists(positionPath + "/aaa.json")
                    ? JObject.Parse(File.ReadAllText(positionPath + "/aaa.json"))
                    : new JObject();

                var scaleData = JsonConvert.DeserializeObject<Dictionary<string, float>>(scaleJson);

                foreach (var kv in newObjects)
                {
                    JObject objData = (JObject)kv.Value;

                    //if (!objData.ContainsKey("scale") && scaleData.ContainsKey(kv.Key))
                        //objData["scale"] = scaleData[kv.Key];

                    aaaData[kv.Key] = objData;
                }

                File.WriteAllText(positionPath + "/aaa.json", aaaData.ToString());

                id++;
                refineButton.gameObject.SetActive(true);
                lastActionWasRefine = false;
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

    /*public void CaptureScreenshot()
    {
        grid.SetActive(true);
        StartCoroutine(TakeScreenshot());
    }

    private System.Collections.IEnumerator TakeScreenshot()
    {
        yield return new WaitForEndOfFrame();  // Aspetta che il frame sia disegnato

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

        grid.SetActive(false);
    }
*/

    public void CaptureScreenshot()
    {
        // RIMOSSO: grid.SetActive(true) per avere un'immagine pulita senza la griglia
        StartCoroutine(TakeScreenshot());
    }

    private System.Collections.IEnumerator TakeScreenshot()
    {
        // 1. Troviamo l'interfaccia utente (Canvas) e la nascondiamo
        Canvas uiCanvas = FindObjectOfType<Canvas>();
        if (uiCanvas != null) uiCanvas.enabled = false;

        // 2. Aspetta che Unity disegni il frame 
        yield return new WaitForEndOfFrame();  

        // 3. Scattiamo la foto
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

        // 4. Riaccendiamo l'interfaccia utente
        if (uiCanvas != null) uiCanvas.enabled = true;
        
        // RIMOSSO: grid.SetActive(false);
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

        // togli code fences / markdown
        s = s.Replace("\r", "");
        int i = s.IndexOf('{'); int j = s.LastIndexOf('}');
        if (i >= 0 && j > i) s = s.Substring(i, j - i + 1);

        // normalizza apici curvi -> doppi apici
        s = s.Replace('’', '\'').Replace('‘', '\'');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<!\\)\'", "\"");

        // rimuovi virgole finali prima di } o ]
        s = System.Text.RegularExpressions.Regex.Replace(s, @",\s*(\}|\])", "$1");

        // chiudi eventuali parentesi mancanti (conta graffe)
        int opens = s.Count(c => c == '{'), closes = s.Count(c => c == '}');
        if (opens > closes) s += new string('}', opens - closes);

        return s;
    }
   

}

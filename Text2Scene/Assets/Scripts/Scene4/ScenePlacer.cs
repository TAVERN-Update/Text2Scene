using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Siccity.GLTFUtility;
using System;
using TMPro;

public class ScenePlacer : MonoBehaviour
{
    // --- Paths & scene refs ---
    string basePath;
    string modelsPath;
    string scalePath;
    Transform assetParent;
    string jsonPath;

    public TMP_Text debug_info;

    // --- Placement params ---
    [Header("Scaling")]
    public float scaleValue = 1f; // moltiplicatore globale

    [Header("Placement")]
    public float roomHalf = 2f; // stanza: x,z ∈ [-roomHalf, +roomHalf]
    public float minSeparationXZ = 0.5f; // distanza minima tra centri (floor/wall)
    public float wallInsetPad = 0.02f; // rientro extra dai muri
    public float wallMountY = 1.2f; // altezza default per mount a muro
    public float wallMaxY = 2.0f; // tetto per clamp Y muro
    public float offset = -0.07f; // margine anti compenetrazione

    public int maxPerObjectAttempts = 50;

    // Registry per anchor/above
    Dictionary<string, GameObject> _registry = new Dictionary<string, GameObject>();

    // Footprint per distanza minima (usa raggio= max(hx,hz) del collider)
    class Footprint { public Vector2 pos; public float radius; }

    public void StartPlacing()
    {
        assetParent = GameObject.Find("Asset")?.transform;
        if (assetParent == null)
        {
            Debug.LogError("Nessun oggetto chiamato 'Asset' trovato nella scena.");
            return;
        }
        foreach (Transform child in assetParent.Cast<Transform>().ToArray())
            GameObject.DestroyImmediate(child.gameObject);

        // Assicura colliders statici (floor e muri) per i raycast
        EnsureStaticColliders();

        basePath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson");

        jsonPath = Path.Combine(basePath, "aaa.json");
        scalePath = Path.Combine(basePath, "calculated_uniform_scale.json");
        modelsPath = LoadRecentFolder();
        if (string.IsNullOrEmpty(modelsPath)) return;

        if (!File.Exists(jsonPath))
        {
            Debug.LogError("aaa.json non trovato.");
            return;
        }

        PlaceAllObjects();
    }

    void EnsureStaticColliders()
    {
        static void Ensure(string name)
        {
            var go = GameObject.Find(name);
            if (!go) return;
            if (!go.GetComponent<Collider>()) go.AddComponent<BoxCollider>();
        }
        Ensure("Floor");
        Ensure("LeftWall");
        Ensure("RightWall");
    }

    private string LoadRecentFolder()
    {
        string modelRoot = Path.Combine(Application.dataPath, "SessionItems", "3DModels");
        var latestFolder = new DirectoryInfo(modelRoot).GetDirectories()
            .OrderByDescending(d => d.LastWriteTime).FirstOrDefault();

        if (latestFolder == null)
        {
            Debug.LogError("Nessuna cartella trovata in 3DModels.");
            return null;
        }

        return latestFolder.FullName;
    }

    private string FormatName(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        input = Regex.Replace(input, @"(_\d+)$", "");
        input = Regex.Replace(input, @"\s*\(\d+\)$", "");
        input = input.Replace(" ", "_");
        return input;
    }

    private float ComputeYPosition(Vector3 xz, string mode, GameObject go)
    {
        float y = 0f;

        if (string.Equals(mode, "floor", StringComparison.OrdinalIgnoreCase))
        {
            var floor = GameObject.FindWithTag("Floor");
            if (floor != null && floor.TryGetComponent<Collider>(out var floorCol))
            {
                Debug.Log("trovato floor con collider");
                var geometryChild = go.transform.Find("geometry_0");
                if (geometryChild != null && geometryChild.TryGetComponent<BoxCollider>(out var box))
                {
                    Debug.Log("calcolo offset");
                    float bottomY = geometryChild.localPosition.y + box.center.y - (box.size.y * geometryChild.localScale.y * 0.5f);

                    // 2. Se bottomY < 0 → l’oggetto è affondato
                    if (bottomY < 0)
                    {
                        // 3. Sposta l’oggetto in alto di esattamente -bottomY
                        y += -bottomY + offset;
                    }
                    else
                    {
                        // Se già sopra il floor, aggiungi solo offset
                        y += offset;
                    }
                }

                else
                {
                    Debug.LogWarning($"geometry_0 non trovato come figlio di {go.name} o senza BoxCollider. {geometryChild != null}");
                }
                return y;
            }
        }

        else if (string.Equals(mode, "above", StringComparison.OrdinalIgnoreCase))
        {
            // Trova il figlio di Asset che ha stesse x e stessa z (tolleranza per float)
            float tolerance = 0.01f;
            Transform found = null;
            foreach (Transform child in assetParent)
            {
                if (child == go.transform) continue; // ignora se stesso
                Vector3 pos = child.localPosition;
                if (Mathf.Abs(pos.x - xz.x) < tolerance && Mathf.Abs(pos.z - xz.z) < tolerance)
                {
                    found = child;
                    break;
                }
            }

            if (found != null)
            {
                Debug.Log($"[above] posiziono sopra '{found.name}'");
                // Prendi il bordo superiore del collider del figlio trovato
                var foundGeom = found.Find("geometry_0");
                var thisGeom = go.transform.Find("geometry_0");
                if (foundGeom != null && foundGeom.TryGetComponent<BoxCollider>(out var foundBox) &&
                    thisGeom != null && thisGeom.TryGetComponent<BoxCollider>(out var thisBox))
                {
                    float foundTop = foundGeom.position.y + foundBox.center.y * foundGeom.localScale.y + (foundBox.size.y * foundGeom.localScale.y * 0.5f);
                    float thisBottom = thisGeom.position.y + thisBox.center.y * thisGeom.localScale.y - (thisBox.size.y * thisGeom.localScale.y * 0.5f);
                    // Calcola quanto serve per mettere il bordo inferiore sopra il bordo superiore
                    float delta = foundTop - thisBottom + offset;
                    y += delta;
                }
                else
                {
                    Debug.LogWarning("[above] geometry_0 o BoxCollider mancante su uno degli oggetti.");
                }
            }
            else
            {
                Debug.LogWarning("[above] Nessun oggetto trovato a x=" + xz.x + " z=" + xz.z);
            }
        }
        else if (string.Equals(mode, "wall", StringComparison.OrdinalIgnoreCase))
        {
            // Monta a wallMountY, poi alza se collide con altri oggetti, max wallMaxY
            y = wallMountY;
            var geometryChild = go.transform.Find("geometry_0");
            float halfHeight = 0.0f;
            if (geometryChild != null && geometryChild.TryGetComponent<BoxCollider>(out var box))
                halfHeight = box.size.y * geometryChild.localScale.y * 0.5f;

            // Prova a salire finché non collide più, ma non oltre wallMaxY
            for (float testY = wallMountY; testY <= wallMaxY; testY += 0.05f)
            {
                Vector3 testPos = new Vector3(xz.x, testY, xz.z);
                if (geometryChild != null && geometryChild.TryGetComponent<BoxCollider>(out var testBox))
                {
                    Vector3 halfExtents = Vector3.Scale(testBox.size, geometryChild.localScale) * 0.5f;
                    Collider[] overlaps = Physics.OverlapBox(
                        testPos,
                        halfExtents,
                        geometryChild.rotation
                    );
                    // Se non collide con altri oggetti (escludendo se stesso)
                    if (overlaps.All(c => c.transform != geometryChild && c.transform != go.transform))
                    {
                        y = testY;
                        break;
                    }
                }
            }
        }

        return y;
    }

    private void PlaceAllObjects()
    {
        string jsonString = File.ReadAllText(jsonPath);
        // 1. Deserializziamo usando il nuovo schema neuro-simbolico
        var dict = JsonConvert.DeserializeObject<Dictionary<string, ObjectData>>(FixUnbalancedBraces(jsonString));

        foreach (var kvp in dict)
        {
            PlaceSingleObject(kvp.Value);
        }
    }

    
    // --- MOTORE DI RAGIONAMENTO SPAZIALE VETTORIALE ---
    // private void PlaceSingleObject(ObjectData objData)
    // {
    //     string objectName = FormatName(objData.name);
    //     string objectPath = Path.Combine(modelsPath, objectName + ".glb");

    //     GameObject loadedObject;
    //     try {
    //         loadedObject = Importer.LoadFromFile(objectPath);
    //     } catch (System.Exception ex) {
    //         Debug.LogError($"Failed to load GLB file for '{objectName}': {ex.Message}");
    //         return;
    //     }

    //     loadedObject.name = objectName;
    //     // Diventa ufficialmente figlio di assetParent (così il sistema di collisioni lo riconosce)
    //     loadedObject.transform.parent = assetParent;

    //     // 1. SETUP COLLIDER & SCALING
    //     var geometryChild = loadedObject.transform.Find("geometry_0");
    //     BoxCollider myBox = null;
    //     Vector3 myWorldExtents = Vector3.one; 

    //     if (geometryChild != null)
    //     {
    //         if (!geometryChild.TryGetComponent<BoxCollider>(out myBox))
    //             myBox = geometryChild.gameObject.AddComponent<BoxCollider>();
            
    //         Physics.SyncTransforms(); 

    //         float targetBaseSize = 1.0f; 
    //         if (objData.category == "accessory") targetBaseSize = 0.3f;
    //         else if (objData.scale == "small") targetBaseSize = 0.6f;
    //         else if (objData.scale == "large") targetBaseSize = 2.0f;

    //         Vector3 currentExtents = myBox.bounds.extents;
    //         float currentMaxSize = Mathf.Max(currentExtents.x, currentExtents.y, currentExtents.z) * 2f;

    //         if (currentMaxSize > 0.01f) 
    //         {
    //             float scaleFactor = targetBaseSize / currentMaxSize;
    //             geometryChild.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor) * scaleValue;
    //         }
    //         Physics.SyncTransforms(); 
    //     }

    //     // 2. PRE-ROTAZIONE (Fatta prima di muovere l'oggetto per avere gli ingombri corretti)
    //     if (string.Equals(objData.relation_type, "absolute", StringComparison.OrdinalIgnoreCase))
    //     {
    //         switch (objData.relation)
    //         {
    //             case "wall_AB": loadedObject.transform.rotation = Quaternion.Euler(0, -90, 0); break; // +X guarda -X
    //             case "wall_CD": loadedObject.transform.rotation = Quaternion.Euler(0, 90, 0); break;  // -X guarda +X
    //             case "wall_AD": loadedObject.transform.rotation = Quaternion.Euler(0, 180, 0); break; // +Z guarda -Z
    //             case "wall_BC": loadedObject.transform.rotation = Quaternion.Euler(0, 0, 0); break;   // -Z guarda +Z
                
    //             // Rotazioni per gli angoli (guardano verso il centro)
    //             case "corner_A": loadedObject.transform.rotation = Quaternion.Euler(0, 0, 0); break; 
    //             case "corner_B": loadedObject.transform.rotation = Quaternion.Euler(0, -90, 0); break; 
    //             case "corner_C": loadedObject.transform.rotation = Quaternion.Euler(0, 180, 0); break; 
    //             case "corner_D": loadedObject.transform.rotation = Quaternion.Euler(0, 90, 0); break;
    //         }
    //     }
    //     else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
    //     {
    //         Transform targetObj = assetParent.Find(FormatName(objData.target));
    //         if (targetObj != null && objData.relation != "above")
    //         {
    //             loadedObject.transform.rotation = targetObj.rotation;
    //         }
    //     }

    //     // AGGIORNA GLI INGOMBRI GLOBALI DOPO LA ROTAZIONE
    //     Physics.SyncTransforms(); 
    //     if (myBox != null) myWorldExtents = myBox.bounds.extents;

    //     // 3. CALCOLO POSIZIONE INIZIALE
    //     Vector3 targetPos = Vector3.zero;

    //     if (string.Equals(objData.relation_type, "absolute", StringComparison.OrdinalIgnoreCase))
    //     {
    //         if (objData.relation == "center") targetPos = Vector3.zero;
    //         else if (objData.relation == "wall_AB") targetPos = new Vector3(roomHalf - myWorldExtents.x - wallInsetPad, 0, 0);
    //         else if (objData.relation == "wall_CD") targetPos = new Vector3(-roomHalf + myWorldExtents.x + wallInsetPad, 0, 0);
    //         else if (objData.relation == "wall_AD") targetPos = new Vector3(0, 0, roomHalf - myWorldExtents.z - wallInsetPad);
    //         else if (objData.relation == "wall_BC") targetPos = new Vector3(0, 0, -roomHalf + myWorldExtents.z + wallInsetPad);
            
    //         else if (objData.relation == "corner_A") targetPos = new Vector3(roomHalf - myWorldExtents.x - wallInsetPad, 0, roomHalf - myWorldExtents.z - wallInsetPad);
    //         else if (objData.relation == "corner_B") targetPos = new Vector3(roomHalf - myWorldExtents.x - wallInsetPad, 0, -roomHalf + myWorldExtents.z + wallInsetPad);
    //         else if (objData.relation == "corner_C") targetPos = new Vector3(-roomHalf + myWorldExtents.x + wallInsetPad, 0, -roomHalf + myWorldExtents.z + wallInsetPad);
    //         else if (objData.relation == "corner_D") targetPos = new Vector3(-roomHalf + myWorldExtents.x + wallInsetPad, 0, roomHalf - myWorldExtents.z - wallInsetPad);
    //     }
    //     else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
    //     {
    //         Transform targetObj = assetParent.Find(FormatName(objData.target));
    //         if (targetObj != null)
    //         {
    //             var refGeom = targetObj.Find("geometry_0");
    //             BoxCollider refBox = refGeom != null ? refGeom.GetComponent<BoxCollider>() : null;
    //             Vector3 refExtents = refBox != null ? refBox.bounds.extents : Vector3.one * 0.5f;

    //             Vector3 refPos = (refGeom != null && refBox != null) ? refGeom.TransformPoint(refBox.center) : targetObj.position;
    //             refPos.y = 0;

    //             float distX = refExtents.x + myWorldExtents.x + minSeparationXZ;
    //             float distZ = refExtents.z + myWorldExtents.z + minSeparationXZ;

    //             switch (objData.relation.ToLower())
    //             {
    //                 case "in_front_of": targetPos = refPos + (targetObj.forward * distZ); break;
    //                 case "behind": targetPos = refPos - (targetObj.forward * distZ); break;
    //                 case "to_the_left_of": targetPos = refPos - (targetObj.right * distX); break;
    //                 case "to_the_right_of": targetPos = refPos + (targetObj.right * distX); break;
    //                 case "above": 
    //                     float safeSpanX = refExtents.x * 0.6f;
    //                     float safeSpanZ = refExtents.z * 0.6f;
    //                     float jitterX = UnityEngine.Random.Range(-safeSpanX, safeSpanX);
    //                     float jitterZ = UnityEngine.Random.Range(-safeSpanZ, safeSpanZ);
    //                     targetPos = new Vector3(refPos.x + jitterX, refPos.y + (refExtents.y * 2) + offset, refPos.z + jitterZ);
    //                     break;
    //                 default: targetPos = refPos + (targetObj.forward * distZ); break;
    //             }
    //         }
    //     }

    //     // 4. SOLVER COMPENETRAZIONI RIPARATO
    //     if (objData.relation != "above") 
    //     {
    //         // Passiamo myWorldExtents al solver per farlo calcolare perfettamente
    //         targetPos = FindValidPosition(loadedObject, myBox, targetPos, myWorldExtents);
    //     }
    //     else 
    //     {
    //         float maxX = roomHalf - myWorldExtents.x - wallInsetPad;
    //         float maxZ = roomHalf - myWorldExtents.z - wallInsetPad;
    //         targetPos.x = Mathf.Clamp(targetPos.x, -maxX, maxX);
    //         targetPos.z = Mathf.Clamp(targetPos.z, -maxZ, maxZ);
    //     }

    //     // 5. APPLICAZIONE FINALE
    //     targetPos.y = ComputeYPosition(targetPos, objData.relation == "above" ? "above" : "floor", loadedObject);
    //     loadedObject.transform.position = targetPos;
    //     Physics.SyncTransforms();
    // }

    private void PlaceSingleObject(ObjectData objData)
    {
        string objectName = FormatName(objData.name);
        string objectPath = Path.Combine(modelsPath, objectName + ".glb");

        GameObject loadedObject;
        try {
            loadedObject = Importer.LoadFromFile(objectPath);
        } catch (System.Exception ex) {
            Debug.LogError($"Failed to load GLB file for '{objectName}': {ex.Message}");
            return;
        }

        loadedObject.name = objectName;
        loadedObject.transform.parent = assetParent;

        // 1. SETUP COLLIDER & SCALING
        var geometryChild = loadedObject.transform.Find("geometry_0");
        BoxCollider myBox = null;
        Vector3 myWorldExtents = Vector3.one; 

        if (geometryChild != null)
        {
            if (!geometryChild.TryGetComponent<BoxCollider>(out myBox))
                myBox = geometryChild.gameObject.AddComponent<BoxCollider>();
            
            Physics.SyncTransforms(); 

            float targetBaseSize = 1.0f; 
            if (objData.category == "accessory") targetBaseSize = 0.3f;
            else if (objData.scale == "small") targetBaseSize = 0.6f;
            else if (objData.scale == "large") targetBaseSize = 2.0f;

            Vector3 currentExtents = myBox.bounds.extents;
            float currentMaxSize = Mathf.Max(currentExtents.x, currentExtents.y, currentExtents.z) * 2f;

            if (currentMaxSize > 0.01f) 
            {
                float scaleFactor = targetBaseSize / currentMaxSize;
                geometryChild.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor) * scaleValue;
            }
            Physics.SyncTransforms(); 
        }

        // 2. PRE-ROTAZIONE
        if (string.Equals(objData.relation_type, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            switch (objData.relation)
            {
                case "wall_AB": loadedObject.transform.rotation = Quaternion.Euler(0, -90, 0); break; 
                case "wall_CD": loadedObject.transform.rotation = Quaternion.Euler(0, 90, 0); break;  
                case "wall_AD": loadedObject.transform.rotation = Quaternion.Euler(0, 180, 0); break; 
                case "wall_BC": loadedObject.transform.rotation = Quaternion.Euler(0, 0, 0); break;   
                
                // Rotazioni per gli angoli (guardano verso il centro)
                case "corner_A": loadedObject.transform.rotation = Quaternion.Euler(0, 0, 0); break; 
                case "corner_B": loadedObject.transform.rotation = Quaternion.Euler(0, -90, 0); break; 
                case "corner_C": loadedObject.transform.rotation = Quaternion.Euler(0, 180, 0); break; 
                case "corner_D": loadedObject.transform.rotation = Quaternion.Euler(0, 90, 0); break; 
            }
        }
        else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
        {
            Transform targetObj = assetParent.Find(FormatName(objData.target));
            if (targetObj != null && objData.relation != "above")
            {
                loadedObject.transform.rotation = targetObj.rotation;
            }
        }

        Physics.SyncTransforms(); 
        if (myBox != null) myWorldExtents = myBox.bounds.extents;

        // --- CALCOLO OFFSET ALTEZZA PIVOT ---
        // Trova l'offset per poggiare perfettamente la base dell'oggetto senza compenetrare il piano
        float bottomOffset = 0f;
        if (myBox != null && geometryChild != null) {
            float worldCenterY = geometryChild.TransformPoint(myBox.center).y;
            float worldBottomY = worldCenterY - myWorldExtents.y;
            bottomOffset = loadedObject.transform.position.y - worldBottomY; 
        }

        // Parametri universali per il "recinto" (default : pavimento)
        float clampMinX = -roomHalf + myWorldExtents.x + wallInsetPad;
        float clampMaxX = roomHalf - myWorldExtents.x - wallInsetPad;
        float clampMinZ = -roomHalf + myWorldExtents.z + wallInsetPad;
        float clampMaxZ = roomHalf - myWorldExtents.z - wallInsetPad;
        
        float spiralStep = 0.25f;
        int spiralAttempts = 1500;
        float finalY = ComputeYPosition(Vector3.zero, "floor", loadedObject); // Y base del pavimento
        Transform objectToIgnore = null;

        Vector3 targetPos = Vector3.zero;

        // Logica di posizionamento basata sul tipo di relazione (assoluta o relativa)
        if (string.Equals(objData.relation_type, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (objData.relation == "center") targetPos = Vector3.zero;
            else if (objData.relation == "wall_AB") targetPos = new Vector3(clampMaxX, 0, 0);
            else if (objData.relation == "wall_CD") targetPos = new Vector3(clampMinX, 0, 0);
            else if (objData.relation == "wall_AD") targetPos = new Vector3(0, 0, clampMaxZ);
            else if (objData.relation == "wall_BC") targetPos = new Vector3(0, 0, clampMinZ);
            
            else if (objData.relation == "corner_A") targetPos = new Vector3(clampMaxX, 0, clampMaxZ);
            else if (objData.relation == "corner_B") targetPos = new Vector3(clampMaxX, 0, clampMinZ);
            else if (objData.relation == "corner_C") targetPos = new Vector3(clampMinX, 0, clampMinZ);
            else if (objData.relation == "corner_D") targetPos = new Vector3(clampMinX, 0, clampMaxZ);
        }
        else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
        {
            Transform targetObj = assetParent.Find(FormatName(objData.target));
            if (targetObj != null)
            {
                var refGeom = targetObj.Find("geometry_0");
                BoxCollider refBox = refGeom != null ? refGeom.GetComponent<BoxCollider>() : null;
                Vector3 refExtents = refBox != null ? refBox.bounds.extents : Vector3.one * 0.5f;

                if (objData.relation.ToLower() == "above" && refBox != null)
                {
                    Bounds objBounds = refBox.bounds;
                    objectToIgnore = targetObj; // Ignora l'oggetto su cui si poggia per evitare false collisioni

                    // Calcolo dei limiti di clamp per la spirale sopra la superficie d'appoggio
                    clampMinX = objBounds.min.x + myWorldExtents.x;
                    clampMaxX = objBounds.max.x - myWorldExtents.x;
                    clampMinZ = objBounds.min.z + myWorldExtents.z;
                    clampMaxZ = objBounds.max.z - myWorldExtents.z;

                    // Fallback: se l'accessorio è più grande dell'oggetto, lo posiziona esattamente al centro
                    if (clampMinX > clampMaxX) { float avg = (clampMinX + clampMaxX) / 2f; clampMinX = avg; clampMaxX = avg; }
                    if (clampMinZ > clampMaxZ) { float avg = (clampMinZ + clampMaxZ) / 2f; clampMinZ = avg; clampMaxZ = avg; }

                    // Altitudine matematica esatta (Tetto dell'oggetto + Offset del Pivot)
                    finalY = objBounds.max.y + bottomOffset;
                    
                    spiralStep = 0.05f; 
                    spiralAttempts = 500;

                    // Posizione ideale di partenza: Centro dell'oggetto
                    targetPos = new Vector3(objBounds.center.x, 0, objBounds.center.z);
                }
                else
                {
                    // Logica a pavimento standard
                    Vector3 refPos = (refGeom != null && refBox != null) ? refGeom.TransformPoint(refBox.center) : targetObj.position;
                    refPos.y = 0;

                    float distX = refExtents.x + myWorldExtents.x + minSeparationXZ;
                    float distZ = refExtents.z + myWorldExtents.z + minSeparationXZ;

                    switch (objData.relation.ToLower())
                    {
                        case "in_front_of": targetPos = refPos + (targetObj.forward * distZ); break;
                        case "behind": targetPos = refPos - (targetObj.forward * distZ); break;
                        case "to_the_left_of": targetPos = refPos - (targetObj.right * distX); break;
                        case "to_the_right_of": targetPos = refPos + (targetObj.right * distX); break;
                        default: targetPos = refPos + (targetObj.forward * distZ); break;
                    }
                }
            }
        }

        // 5. APPLICAZIONE DEL SOLVER UNIVERSALE
        targetPos.y = finalY; 
        targetPos = FindValidPosition(loadedObject, myBox, targetPos, clampMinX, clampMaxX, clampMinZ, clampMaxZ, spiralStep, spiralAttempts, objectToIgnore);
        
        // Piazzamento dell'oggetto
        loadedObject.transform.position = targetPos;
        Physics.SyncTransforms();
    }


    // // NUOVO SOLVER COMPENETRAZIONI
    // private Vector3 FindValidPosition(GameObject go, BoxCollider box, Vector3 intendedPos, Vector3 worldExtents)
    // {
    //     if (box == null) return intendedPos;
        
    //     Vector3 spiralPos = intendedPos;
    //     var geometryChild = go.transform.Find("geometry_0");
        
    //     Vector3 localHalfExtents = Vector3.Scale(box.size, geometryChild.lossyScale) * 0.5f;
        
    //     int maxAttempts = 1500;  
    //     float step = 0.25f;     
        
    //     Vector2[] dirs = { Vector2.right, Vector2.up, Vector2.left, Vector2.down };
    //     int dirIdx = 0;
    //     int stepLimit = 1;
    //     int stepCount = 0;

    //     float maxX = roomHalf - worldExtents.x - wallInsetPad;
    //     float maxZ = roomHalf - worldExtents.z - wallInsetPad;

    //     for (int i = 0; i < maxAttempts; i++)
    //     {
    //         // 1. Controlla se la posizione matematica attuale è fuori dai muri
    //         bool isOutOfBounds = spiralPos.x < -maxX || spiralPos.x > maxX || spiralPos.z < -maxZ || spiralPos.z > maxZ;

    //         // 2. Solo se è DENTRO la stanza, testa fisicamente le collisioni
    //         if (!isOutOfBounds)
    //         {
    //             go.transform.position = spiralPos;
    //             Physics.SyncTransforms();

    //             Vector3 worldCenter = geometryChild.TransformPoint(box.center);
    //             Collider[] hits = Physics.OverlapBox(worldCenter, localHalfExtents, geometryChild.rotation);
                
    //             bool isOverlapping = false;
    //             foreach (var hit in hits)
    //             {
    //                 if (hit.transform.IsChildOf(assetParent) && !hit.transform.IsChildOf(go.transform))
    //                 {
    //                     isOverlapping = true;
    //                     break; 
    //                 }
    //             }

    //             // Spazio libero e dentro la stanza trovato
    //             if (!isOverlapping) return spiralPos; 
    //         }

    //         // 3. Muovi la spirale (Continua a muoversi matematicamente anche se era finita oltre il muro)
    //         spiralPos.x += dirs[dirIdx].x * step;
    //         spiralPos.z += dirs[dirIdx].y * step;
            
    //         stepCount++;
    //         if (stepCount >= stepLimit)
    //         {
    //             stepCount = 0;
    //             dirIdx = (dirIdx + 1) % 4; 
    //             if (dirIdx % 2 == 0) stepLimit++; 
    //         }
    //     }

    //     Debug.LogWarning($"[Fallback] Spazio esaurito per '{go.name}'. Resta nella posizione originale forzata.");
    //     intendedPos.x = Mathf.Clamp(intendedPos.x, -maxX, maxX);
    //     intendedPos.z = Mathf.Clamp(intendedPos.z, -maxZ, maxZ);
    //     return intendedPos;
    // }

    // NUOVO SOLVER COMPENETRAZIONI
    private Vector3 FindValidPosition(GameObject go, BoxCollider box, Vector3 intendedPos, float minX, float maxX, float minZ, float maxZ, float step, int maxAttempts, Transform ignoreTarget)
    {
        if (box == null) return intendedPos;
        
        Vector3 spiralPos = intendedPos;
        var geometryChild = go.transform.Find("geometry_0");
        
        Vector3 localHalfExtents = Vector3.Scale(box.size, geometryChild.lossyScale) * 0.5f;
        
        Vector2[] dirs = { Vector2.right, Vector2.up, Vector2.left, Vector2.down };
        int dirIdx = 0;
        int stepLimit = 1;
        int stepCount = 0;

        for (int i = 0; i < maxAttempts; i++)
        {
            // 1. Controlla se la posizione matematica è al di fuori del "recinto"
            bool isOutOfBounds = spiralPos.x < minX || spiralPos.x > maxX || spiralPos.z < minZ || spiralPos.z > maxZ;

            // 2. Solo se è dentro il "recinto", testa fisicamente le collisioni
            if (!isOutOfBounds)
            {
                go.transform.position = spiralPos;
                Physics.SyncTransforms();

                Vector3 worldCenter = geometryChild.TransformPoint(box.center);
                Collider[] hits = Physics.OverlapBox(worldCenter, localHalfExtents, geometryChild.rotation);
                
                bool isOverlapping = false;
                foreach (var hit in hits)
                {
                    if (hit.transform.IsChildOf(assetParent) && !hit.transform.IsChildOf(go.transform))
                    {
                        if (ignoreTarget != null && hit.transform.IsChildOf(ignoreTarget))
                            continue;

                        isOverlapping = true;
                        break; 
                    }
                }

                if (!isOverlapping) return spiralPos; 
            }

            spiralPos.x += dirs[dirIdx].x * step;
            spiralPos.z += dirs[dirIdx].y * step;
            
            stepCount++;
            if (stepCount >= stepLimit)
            {
                stepCount = 0;
                dirIdx = (dirIdx + 1) % 4; 
                if (dirIdx % 2 == 0) stepLimit++; 
            }
        }

        Debug.LogWarning($"[Fallback] Spazio esaurito per '{go.name}'. Resta incastrato nei limiti massimi consentiti.");
        
        // Se non trova spazio, si schiaccia comunque dentro i limiti assegnati
        intendedPos.x = Mathf.Clamp(intendedPos.x, minX, maxX);
        intendedPos.z = Mathf.Clamp(intendedPos.z, minZ, maxZ);
        return intendedPos;
    }

    private string FixUnbalancedBraces(string input)
    {
        int openCount = input.Count(c => c == '{');
        int closeCount = input.Count(c => c == '}');
        int diff = openCount - closeCount;

        if (diff > 0)
        {
            input += new string('}', diff);
        }

        return input;
    }

    /*void FixWallCollisions(GameObject go)
    {
        // child con il BoxCollider reale
        var geometryChild = go.transform.Find("geometry_0");
        if (!geometryChild) return;
        var box = geometryChild.GetComponent<BoxCollider>();
        if (!box) return;

        // Trova muri per nome o tag 
        var walls = new List<Collider>();
        void TryAdd(string nameOrTag, bool byTag)
        {
            if (byTag)
            {
                var w = GameObject.FindWithTag(nameOrTag);
                if (w && w.TryGetComponent<Collider>(out var c)) walls.Add(c);
            }
            else
            {
                var w = GameObject.Find(nameOrTag);
                if (w && w.TryGetComponent<Collider>(out var c)) walls.Add(c);
            }
        }

        // 🔧 Uniforma a come sono in scena: se usi i nomi, togli i tag; viceversa.
        TryAdd("LeftWall", false);
        TryAdd("RightWall", false);
        TryAdd("Wall1", true);
        TryAdd("Wall2", true);

        const int MAX_SOLVES = 12;     // cap iterazioni
        const float SKIN = 0.002f;     // piccolo margine per non restare a contatto

        for (int i = 0; i < MAX_SOLVES; i++)
        {
            bool anyPenetration = false;

            foreach (var wallCol in walls)
            {
                if (!wallCol) continue;

                Vector3 dir;
                float dist;

                // Calcola penetrazione tra il box e il muro
                bool overlapped = Physics.ComputePenetration(
                    box, geometryChild.position, geometryChild.rotation,
                    wallCol, wallCol.transform.position, wallCol.transform.rotation,
                    out dir, out dist
                );

                if (overlapped && dist > 0f)
                {
                    // Sposta l'oggetto fuori dal muro lungo la normale corretta
                    go.transform.position += dir * (dist + SKIN);
                    anyPenetration = true;
                }
            }

            if (!anyPenetration)
                break; // risolto
        }
    }

    void FixFurnitureCollisions(GameObject go)
    {
        var geometryChild = go.transform.Find("geometry_0");
        if (!geometryChild) return;
        var box = geometryChild.GetComponent<BoxCollider>();
        if (!box) return;

        const int MAX_SOLVES = 20;     // Numero massimo di tentativi per spingere fuori il mobile
        const float STEP_DIST = 0.35f; // Di quanto lo spostiamo a ogni tentativo (35 cm)

        for (int i = 0; i < MAX_SOLVES; i++)
        {
            // 1. Calcoliamo il centro e le dimensioni mondiali del box collider
            Vector3 worldCenter = geometryChild.TransformPoint(box.center);
            Vector3 halfExtents = Vector3.Scale(box.size, geometryChild.lossyScale) * 0.5f;

            // 2. Generiamo un OverlapBox per vedere se collide con altri oggetti 
            Collider[] hitColliders = Physics.OverlapBox(worldCenter, halfExtents, geometryChild.rotation);

            bool collidedWithFurniture = false;
            Vector3 pushDirection = Vector3.zero;

            foreach (var hit in hitColliders)
            {
                // Ignoriamo l'oggetto stesso, il pavimento (poichè gestito da ComputeYPosition) e i muri (poichè gestiti da FixWallCollisions)
                if (hit.transform == geometryChild || hit.transform == go.transform) continue;
                if (hit.CompareTag("Floor") || hit.name.Contains("Wall") || hit.name.Contains("Floor")) continue;

                // Abbiamo trovato un mobile con cui collide, quindi dobbiamo spostare l'oggetto
                collidedWithFurniture = true;

                // Calcoliamo la direzione per scappare dal mobile (dal centro dell'ostacolo verso di noi)
                Vector3 awayFromObstacle = go.transform.position - hit.transform.position;
                awayFromObstacle.y = 0; // Rimaniamo sul piano orizzontale

                // Se l'oggetto è generato esattamente nello stesso identico punto (zero assoluto), scappiamo verso destra (+X)
                if (awayFromObstacle.sqrMagnitude < 0.001f)
                {
                    pushDirection = new Vector3(1, 0, 0);
                }
                else
                {
                    pushDirection = awayFromObstacle.normalized;
                }
                break;
            }

            // Se lo spazio è libero, abbiamo finito!
            if (!collidedWithFurniture)
                break;

            // 3. APPLICIAMO LA SPINTA: Spostiamo l'oggetto di 35 cm nella direzione di fuga
            go.transform.position += pushDirection * STEP_DIST;
            Physics.SyncTransforms(); // Aggiorniamo la fisica istantaneamente per il prossimo giro
        }
    }*/

    // DATA MODEL
    [System.Serializable]
    public class ObjectData
    {
        public string name;
        public string relation_type;
        public string relation;
        public string target;
        public string scale;
        public string placement;
        public string category;
    }
}
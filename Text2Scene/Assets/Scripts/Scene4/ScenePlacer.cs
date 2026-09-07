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

/*
    private void PlaceAllObjects()
    {
        string jsonString = File.ReadAllText(jsonPath);
        var dict = JsonConvert.DeserializeObject<Dictionary<string, ObjectData>>(FixUnbalancedBraces(jsonString));

        foreach (var kvp in dict)
        {
            var obj = kvp.Value;
            // Passa: objectname, rotation, xvalue, zvalue, ymode, scale
            PlaceSingle(obj.name, obj.rotation, obj.position.x, obj.position.z, obj.position.y, obj.scale);
        }
    }

    private void PlaceSingle(string name, int rotation, float x, float z, string mode, float scale)
    {
        string objectName = FormatName(name);
        string objectPath = Path.Combine(modelsPath, objectName + ".glb");

        GameObject loadedObject;
        try
        {
            loadedObject = Importer.LoadFromFile(objectPath);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to load GLB file for '{objectName}': {ex.Message}");
            return;
        }

        loadedObject.name = objectName;
        
        //loadedObject.transform.localPosition = new Vector3(x, 0, z);

        
        loadedObject.transform.rotation = Quaternion.Euler(0, rotation, 0);

        // AGGIUNGI BOX COLLIDER AL FIGLIO geometry_0
        var geometryChild = loadedObject.transform.Find("geometry_0");
        if (geometryChild != null)
        {
            geometryChild.transform.localScale = scale * scaleValue * Vector3.one;
            Physics.SyncTransforms();

            if (!geometryChild.gameObject.GetComponent<BoxCollider>())
                geometryChild.gameObject.AddComponent<BoxCollider>();             
        }

        else
        {
            Debug.LogWarning($"geometry_0 non trovato come figlio di {objectName}");
        }
        //loadedObject.transform.localScale = scale * scaleValue * Vector3.one;

        Physics.SyncTransforms();
        loadedObject.transform.localPosition = new Vector3(x, ComputeYPosition(new Vector3(x, 0, z), mode, loadedObject), z);
        
        loadedObject.transform.parent = assetParent;
        //FixWallCollisions(loadedObject);

    }
*/

    private void PlaceAllObjects()
    {
        string jsonString = File.ReadAllText(jsonPath);
        // 1. Deserializziamo usando il nuovo schema neuro-simbolico
        var dict = JsonConvert.DeserializeObject<Dictionary<string, NeuroSymbolicObjectData>>(FixUnbalancedBraces(jsonString));

        foreach (var kvp in dict)
        {
            PlaceSingleNeuroSymbolic(kvp.Value);
        }
    }

    // --- MOTORE DI RAGIONAMENTO SPAZIALE VETTORIALE ---
    private void PlaceSingleNeuroSymbolic(NeuroSymbolicObjectData objData)
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

        /*
        // 1. Configurazione Collider e Scala per calcolare gli Extents
        var geometryChild = loadedObject.transform.Find("geometry_0");
        BoxCollider myBox = null;

        if (geometryChild != null)
        {
            geometryChild.transform.localScale = scaleValue * Vector3.one; // Scala uniforme default
            if (!geometryChild.TryGetComponent<BoxCollider>(out myBox))
                myBox = geometryChild.gameObject.AddComponent<BoxCollider>();
        }
        Physics.SyncTransforms();

        // Calcolo degli Extents (Metà della dimensione del box in metri reali)
        Vector3 myExtents = myBox != null ? myBox.bounds.extents : Vector3.one * 0.5f; //Se non trova il collider dell'oggetto, applica un estensione di default di 0.5m 
        */
        Vector3 targetPos = Vector3.zero;

        // 1. Configurazione Collider e Scala Neuro-Simbolica
        var geometryChild = loadedObject.transform.Find("geometry_0");
        BoxCollider myBox;
        Vector3 myExtents = Vector3.one; // Fallback di default

        if (geometryChild != null)
        {
            // Aggiunta dell collider PRIMA per poter leggere le sue misure originali sballate
            if (!geometryChild.TryGetComponent<BoxCollider>(out myBox))
                myBox = geometryChild.gameObject.AddComponent<BoxCollider>();
            
            Physics.SyncTransforms(); // Sincronizazione per leggere i bounds grezzi

            // --- INIZIO NORMALIZZAZIONE SCALE ---
            // Lettura del moltiplicatore richiesto dall'IA
            float scaleMultiplier = 1.0f;
            if (objData.scale == "small") scaleMultiplier = 0.8f;
            else if (objData.scale == "large") scaleMultiplier = 1.2f;

            // Determinazione della misura base ideale in base al placement
            bool isAccessory = objData.category == "accessory";
            // 30 cm (0.3f) per qualsiasi accessorio (vasi, libri, sushi), 1.5m per i mobili veri
            float targetBaseSize = isAccessory ? 0.3f : 1.5f;

            // Misurazione della grandezza originale del modello
            Vector3 currentExtents = myBox.bounds.extents;
            float currentSize = isAccessory ? (currentExtents.y * 2f) : (currentExtents.x * 2f);

            // Applicazione dela formula (MisuraBase * MoltiplicatoreScala) / DimensioneReale
            if (currentSize > 0.01f) 
            {
                float scaleFactor = targetBaseSize * scaleMultiplier / currentSize;
                
                // Manteniamo anche il 'scaleValue' globale del tuo script per eventuale debug dell'intera stanza
                geometryChild.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor) * scaleValue;
            }
            // --- FINE NORMALIZZAZIONE SCALE ---

            // AGGIORNAMENTO CRITICO: Ricalcolo degli extents DEFINITIVI dopo il ridimensionamento
            Physics.SyncTransforms(); 
            myExtents = myBox.bounds.extents; 
        }

        // CASO 1: POSIZIONAMENTO ASSOLUTO (Stanza vuota o Muri)
        if (string.Equals(objData.relation_type, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (objData.relation == "center") {
                targetPos = Vector3.zero;
            }
            else if (objData.relation == "wall_AB") { // Muro Posteriore (+X)
                targetPos = new Vector3(roomHalf - myExtents.x - wallInsetPad, 0, 0);
            }
            else if (objData.relation == "wall_CD") { // Muro Frontale (-X)
                targetPos = new Vector3(-roomHalf + myExtents.x + wallInsetPad, 0, 0);
            }
            else if (objData.relation == "wall_AD") { // Muro Sinistro (+Z)
                targetPos = new Vector3(0, 0, roomHalf - myExtents.z - wallInsetPad);
            }
            else if (objData.relation == "wall_BC") { // Muro Destro (-Z)
                targetPos = new Vector3(0, 0, -roomHalf + myExtents.z + wallInsetPad);
            }
            else if (objData.relation == "corner_A") { // Incrocio AB + AD (+X, +Z)
                targetPos = new Vector3(roomHalf - myExtents.x - wallInsetPad, 0, roomHalf - myExtents.z - wallInsetPad);
            }
            else if (objData.relation == "corner_B") { // Incrocio AB + BC (+X, -Z)
                targetPos = new Vector3(roomHalf - myExtents.x - wallInsetPad, 0, -roomHalf + myExtents.z + wallInsetPad);
            }
            else if (objData.relation == "corner_C") { // Incrocio CD + BC (-X, -Z)
                targetPos = new Vector3(-roomHalf + myExtents.x + wallInsetPad, 0, -roomHalf + myExtents.z + wallInsetPad);
            }
            else if (objData.relation == "corner_D") { // Incrocio CD + AD (-X, +Z)
                targetPos = new Vector3(-roomHalf + myExtents.x + wallInsetPad, 0, roomHalf - myExtents.z - wallInsetPad);
            }
        }
/*
        // CASO 2: POSIZIONAMENTO RELATIVO (Rispetto ad arredi esistenti)
        else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
        {
            Transform targetObj = assetParent.Find(FormatName(objData.target));
            if (targetObj != null)
            {
                Vector3 refPos = targetObj.position;
                var refGeom = targetObj.Find("geometry_0");
                BoxCollider refBox = refGeom != null ? refGeom.GetComponent<BoxCollider>() : null;
                Vector3 refExtents = refBox != null ? refBox.bounds.extents : Vector3.one * 0.5f;

                // Formula Vettoriale: P_nuova = P_ancora + (v_direzione * (E_ancora + E_oggetto + margine))
                float distX = refExtents.x + myExtents.x + minSeparationXZ;
                float distZ = refExtents.z + myExtents.z + minSeparationXZ;

                switch (objData.relation.ToLower())
                {
                    case "in_front_of": // Il fronte della seduta punta verso AD (+Z)
                        targetPos = refPos + new Vector3(0, 0, distZ);
                        break;
                    case "behind": // Il retro del divano punta verso BC (-Z)
                        targetPos = refPos + new Vector3(0, 0, -distZ);
                        break;
                    case "to_the_left_of": // Sinistra (verso il lato C / -X)
                        targetPos = refPos + new Vector3(-distX, 0, 0);
                        break;
                    case "to_the_right_of": // Destra (verso il lato A / +X)
                        targetPos = refPos + new Vector3(distX, 0, 0);
                        break;
                    case "above": // Sopra al mobile (Tavoli/Mensole)
                        targetPos = new Vector3(refPos.x, refPos.y + (refExtents.y * 2) + offset, refPos.z);
                        break;
                    default:
                        targetPos = refPos + new Vector3(0, 0, distZ);
                        break;
                }
            }
            else
            {
                Debug.LogWarning($"[NeuroSymbolic] Target '{objData.target}' non trovato! Posiziono al centro.");
                targetPos = Vector3.zero;
            }
        }

        // Calcolo finale altezza Y (ancoraggio al pavimento se non è "above")
        string yMode = objData.relation == "above" ? "above" : "floor";
        targetPos.y = ComputeYPosition(targetPos, yMode, loadedObject);

        // Applica la posizione
        loadedObject.transform.position = targetPos;
        Physics.SyncTransforms();

        
        /*
        // 1. Evita compenetrazioni con altri mobili
        if (objData.relation != "above") 
        {
            FixFurnitureCollisions(loadedObject);
            Physics.SyncTransforms();
        }
        
        // 2. Evita compenetrazioni nei muri
        FixWallCollisions(loadedObject);
        Physics.SyncTransforms();

        Debug.Log($"[TESI OK] Posizionato '{objectName}' con logica '{objData.relation}' alle coordinate: {targetPos}");*/

        // CASO 2: POSIZIONAMENTO RELATIVO (Rispetto ad arredi esistenti)
        else if (string.Equals(objData.relation_type, "relative", StringComparison.OrdinalIgnoreCase))
        {
            Transform targetObj = assetParent.Find(FormatName(objData.target));
            if (targetObj != null)
            {
                Vector3 refPos = targetObj.position;
                var refGeom = targetObj.Find("geometry_0");
                BoxCollider refBox = refGeom != null ? refGeom.GetComponent<BoxCollider>() : null;
                Vector3 refExtents = refBox != null ? refBox.bounds.extents : Vector3.one * 0.5f;

                float distX = refExtents.x + myExtents.x + minSeparationXZ;
                float distZ = refExtents.z + myExtents.z + minSeparationXZ;

                switch (objData.relation.ToLower())
                {
                    case "in_front_of": 
                        targetPos = refPos + (targetObj.forward * distZ);
                        break;
                    case "behind": 
                        targetPos = refPos - (targetObj.forward * distZ);
                        break;
                    case "to_the_left_of": 
                        targetPos = refPos - (targetObj.right * distX);
                        break;
                    case "to_the_right_of": 
                        targetPos = refPos + (targetObj.right * distX);
                        break;
                    case "above": // Sopra al mobile (Tavoli/Mensole)
                        targetPos = new Vector3(refPos.x, refPos.y + (refExtents.y * 2) + offset, refPos.z);
                        break;
                    default:
                        targetPos = refPos + new Vector3(0, 0, distZ);
                        break;
                }
            }
            else
            {
                Debug.LogWarning($"[NeuroSymbolic] Target '{objData.target}' non trovato! Posiziono al centro.");
                targetPos = Vector3.zero;
            }
        }

        // Calcolo finale altezza Y 
        string yMode = objData.relation == "above" ? "above" : "floor";
        targetPos.y = ComputeYPosition(targetPos, yMode, loadedObject);

        // --- SISTEMA FISICO FINALE: CLAMPING E SOLVER ---
        if (objData.relation != "above") 
        {
            // Oggetti a pavimento: risolvi le compenetrazioni con gli altri mobili e fai clamp
            targetPos = FindValidPosition(loadedObject, myBox, targetPos);
        }
        else 
        {
            // Oggetti su tavoli: Solo clamp ai bordi della stanza (se usassimo il solver scapperebbero dal tavolo!)
            float maxX = roomHalf - myExtents.x - wallInsetPad;
            float maxZ = roomHalf - myExtents.z - wallInsetPad;
            targetPos.x = Mathf.Clamp(targetPos.x, -maxX, maxX);
            targetPos.z = Mathf.Clamp(targetPos.z, -maxZ, maxZ);
        }

        // Applica la posizione finale sicura
        loadedObject.transform.position = targetPos;
        Physics.SyncTransforms();

        Debug.Log($"[TESI OK] Posizionato '{objectName}' con logica '{objData.relation}' alle coordinate finali sicure: {targetPos}");
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

    private Vector3 FindValidPosition(GameObject go, BoxCollider box, Vector3 intendedPos)
    {
        Vector3 testPos = intendedPos;
        var geometryChild = go.transform.Find("geometry_0");
        
        // Estraiamo gli ingombri reali (metà dimensione del BoxCollider)
        Vector3 halfExtents = Vector3.Scale(box.size, geometryChild.lossyScale) * 0.5f;
        
        int maxAttempts = 60;  // Limite massimo di scatti prima del fallback
        float step = 0.15f;    // Spostamento di 15 cm a scatto

        // Vettori per creare il movimento a spirale sul pavimento (X, Z)
        Vector2[] dirs = { Vector2.right, Vector2.up, Vector2.left, Vector2.down };
        int dirIdx = 0;
        int stepLimit = 1;
        int stepCount = 0;

        for (int i = 0; i < maxAttempts; i++)
        {
            // 1. CLAMP: Forza le coordinate X e Z a rimanere dentro la stanza fisica
            float maxX = roomHalf - halfExtents.x - wallInsetPad;
            float maxZ = roomHalf - halfExtents.z - wallInsetPad;
            testPos.x = Mathf.Clamp(testPos.x, -maxX, maxX);
            testPos.z = Mathf.Clamp(testPos.z, -maxZ, maxZ);

            // Sposta fisicamente l'oggetto per permettere al sensore di testare l'area
            go.transform.position = testPos;
            Physics.SyncTransforms();

            // 2. SENSING: Controlliamo se in questo punto tocca altri mobili
            Vector3 worldCenter = geometryChild.TransformPoint(box.center);
            Collider[] hits = Physics.OverlapBox(worldCenter, halfExtents, geometryChild.rotation);
            
            bool isOverlapping = false;
            foreach (var hit in hits)
            {
                // Ignora sé stesso, i muri (che gestiamo col clamp) e il pavimento
                if (hit.transform != geometryChild && hit.transform != go.transform && 
                    !hit.CompareTag("Floor") && !hit.name.Contains("Wall"))
                {
                    isOverlapping = true;
                    break;
                }
            }

            // Se non tocca nulla, lo spazio è perfettamente valido
            if (!isOverlapping) return testPos;

            // 3. SPIRAL MOVE: Muoviamoci lungo il pattern a spirale per il prossimo tentativo
            testPos.x += dirs[dirIdx].x * step;
            testPos.z += dirs[dirIdx].y * step;
            
            stepCount++;
            if (stepCount >= stepLimit)
            {
                stepCount = 0;
                dirIdx = (dirIdx + 1) % 4; // Cambia direzione di 90 gradi
                if (dirIdx % 2 == 0) stepLimit++; // Ogni mezzo giro, allarga il raggio della spirale
            }
        }

        // 4. FALLBACK: Nessuno spazio libero trovato nei dintorni
        Debug.LogWarning($"[Fallback] Spazio esaurito per '{go.name}'. Posizionato al centro.");
        testPos.x = 0; 
        testPos.z = 0;
        return testPos;
    }


/*
    // ---------- DATA MODELS ----------
    public class ObjectData
    {
        public string name;
        public int rotation;
        public PositionData position;
        public float scale;
    }

    [System.Serializable]
    public class PositionData
    {
        public float x;
        public string y;
        public float z;
        public string wall_side;
        public string anchor;
    }
*/

    //DATA MODEL AGGIORNATO
    [System.Serializable]
    public class NeuroSymbolicObjectData
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
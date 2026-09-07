using Newtonsoft.Json;
using Siccity.GLTFUtility;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using System;
using TMPro;

public class ScalingCalculator : MonoBehaviour
{
    string modelsPath;

    // Fattore correttivo che avevi già (lo lascio invariato)
    private float scaleValue = 1.3f;

    // Percorsi dei JSON (stessa struttura cartelle di prima)
    private string measuresJsonPath;               // -> "measures.json" (altezze in metri)
    private string calculatedUniformScaleJsonPath; // -> "calculated_uniform_scale.json" (map nome->scala)

    public TMP_Text debug_info;

    public void ScaleButton()
    {
        debug_info.text = "Starting scaling calculation...";
        modelsPath = LoadRecentFolder();
        if (string.IsNullOrEmpty(modelsPath))
        {
            Debug.LogError("❌ Nessuna cartella di modelli trovata.");
            return;
        }

        string[] modelFiles = Directory.GetFiles(modelsPath, "*.glb");
        string modelNames = string.Join(", ", modelFiles.Select(path => Path.GetFileNameWithoutExtension(path)));
        Debug.Log("Model list: " + modelNames);

        measuresJsonPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "measures.json");
        calculatedUniformScaleJsonPath = Path.Combine(Application.dataPath, "SessionItems", "SceneJson", "calculated_uniform_scale.json");

        // Genera (o aggiorna) le misure tramite LLM; se fallisce ma measures.json esiste, calcola comunque le scale
        StartCoroutine(GenerateMeasuresThenScales(modelNames));
    }

    IEnumerator GenerateMeasuresThenScales(string models)
    {
        bool wroteMeasures = false;

        // ---- Costruzione prompt per il server LLM (come prima) ----
        string systemPrompt = @"Your task is to estimate real-life height in meters for each object name the user provides you. The names the user provides are in a list format, and they could be common furniture names or specific product-like names. You should both research online (if you know them) and infer the height by common sense for objects where the height is not explicitly known. Do not invent brands that do not exist. If you do not find an exact model, use a typical height of the category (e.g. typical desk ~0.75m, bed headboard ~1.1m, etc). The measure you have to estimate is the height, meaning only how tall from top to bottom the object is in meters. 

Output your answer in a valid JSON format like the following:

{
    ""object_name"" : 1.23,
    ""another_object"" : 0.75
}

IMPORTANT: Return ALL the objects you receive, using the names EXACTLY as in the list (keep underscores).";

        var names = models.Split(',')
                          .Select(s => s.Trim())
                          .Where(s => !string.IsNullOrEmpty(s))
                          .Distinct()
                          .ToList();
        string formattedList = "[ " + string.Join(", ", names.Select(n => $"\"{n}\"")) + " ]";
        string userPrompt = $@"List of objects: {formattedList}";

        string requestBody = JsonConvert.SerializeObject(new
        {
            system_prompt = systemPrompt,
            user_prompt = userPrompt
        });

        using (UnityWebRequest req = new UnityWebRequest("http://localhost:5000/scene_prompt", "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(requestBody);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 30;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300)
            {
                var wrapper = JsonConvert.DeserializeObject<LLMResponse>(req.downloadHandler.text);
                var llmText = wrapper?.response;

                if (string.IsNullOrEmpty(llmText))
                {
                    Debug.LogWarning("La risposta del server non contiene 'response'. Body: " + req.downloadHandler.text);
                }
                else
                {
                    string extractedJson = ExtractJsonBlock(llmText);
                    extractedJson = extractedJson?.Replace('’', '\'').Replace('‘', '\'');
                    if (!string.IsNullOrEmpty(extractedJson))
                    {
                        // unifica apici a doppi apici
                        extractedJson = Regex.Replace(extractedJson, @"(?<!\\)\'", "\"");
                        File.WriteAllText(measuresJsonPath, extractedJson);
                        wroteMeasures = true;
                        Debug.Log("✅ Misure salvate in: " + measuresJsonPath);
                    }
                    else
                    {
                        Debug.LogWarning("LLM response didn't contain a valid JSON block.");
                    }
                }
            }
            else
            {
                Debug.LogError($"HTTP {(int)req.responseCode} {req.error}\nBody: {req.downloadHandler.text}");
            }
        }

        // Anche se la parte LLM non ha scritto, se esiste già un measures.json lo usiamo
        if (!wroteMeasures && !File.Exists(measuresJsonPath))
        {
            Debug.LogWarning("Nessun measures.json disponibile. Interrompo senza calcolare scale.");
            yield break;
        }

        // Calcola SOLO le scale e scrive SOLO calculated_uniform_scale.json
        CalculateUniformScales();
        debug_info.text = "Scaling done! You can proceed.";
    }

    private string LoadRecentFolder()
    {
        string modelRoot = Path.Combine(Application.dataPath, "SessionItems", "3DModels");
        var latestFolder = new DirectoryInfo(modelRoot).GetDirectories()
            .OrderByDescending(d => d.LastWriteTime).FirstOrDefault();

        if (latestFolder == null)
        {
            Debug.LogError("❌ Nessuna cartella trovata in 3DModels.");
            return null;
        }

        return latestFolder.FullName;
    }

    private void CalculateUniformScales()
    {
        // 1) Leggi le altezze ideali da measures.json (mapping: name -> meters)
        Dictionary<string, float> idealHeights;
        try
        {
            idealHeights = JsonConvert.DeserializeObject<Dictionary<string, float>>(File.ReadAllText(measuresJsonPath));
        }
        catch (Exception ex)
        {
            Debug.LogError("Errore nel leggere/parsare measures.json: " + ex.Message);
            return;
        }

        var calculatedUniformScales = new Dictionary<string, float>();

        // 2) Per ogni GLB calcola l’altezza effettiva e la scala uniforme
        string[] glbFiles = Directory.GetFiles(modelsPath, "*.glb");
        foreach (string glbPath in glbFiles)
        {
            string modelName = Path.GetFileNameWithoutExtension(glbPath);

            // Normalizza solo suffisso numerico, mantiene gli underscore (forma che ti serve)
            string modelKey = Regex.Replace(modelName, @"(_\d+)$", "");

            if (!idealHeights.TryGetValue(modelKey, out float idealY))
            {
                Debug.LogWarning($"{modelKey} non presente in measures.json");
                continue;
            }

            GameObject loaded = null;
            try { loaded = Importer.LoadFromFile(glbPath); }
            catch (Exception ex) { Debug.LogError($"Errore nel caricare {glbPath}: {ex.Message}"); continue; }

            if (!TryGetObjectHeight(loaded, out float actualY) || actualY <= 0f)
            {
                Debug.LogWarning($"{modelName}: impossibile stimare l'altezza (niente Collider/Renderer valido).");
                Destroy(loaded);
                continue;
            }

            float uniformScale = idealY / actualY;
            float finalScale = Mathf.Clamp(uniformScale / scaleValue, 0.01f, 100f);

            calculatedUniformScales[modelKey] = finalScale;
            Destroy(loaded);
        }

        if (calculatedUniformScales.Count == 0)
        {
            Debug.LogWarning("Nessuna scala calcolata: controllo chiavi di measures.json vs nomi GLB.");
            return;
        }

        // 3) Scrivi SOLO calculated_uniform_scale.json
        try
        {
            var json = JsonConvert.SerializeObject(calculatedUniformScales, Formatting.Indented);
            File.WriteAllText(calculatedUniformScaleJsonPath, json);
            Debug.Log($"✅ calculated_uniform_scale.json scritto: {calculatedUniformScaleJsonPath}");
        }
        catch (Exception e)
        {
            Debug.LogError("Scrittura calculated_uniform_scale.json fallita: " + e.Message);
        }
    }

    private static bool TryGetObjectHeight(GameObject go, out float height)
    {
        height = 0f;

        // 1) Prova coi Collider
        var coll = go.GetComponentInChildren<Collider>();
        if (coll != null)
        {
            height = coll.bounds.size.y;
            if (height > 0f) return true;
        }

        // 2) Fallback: usa i Renderer
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers != null && renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            height = b.size.y;
            if (height > 0f) return true;
        }

        return false;
    }

    string ExtractJsonBlock(string response)
    {
        int firstBrace = response.IndexOf('{');
        int lastBrace = response.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
            return response.Substring(firstBrace, lastBrace - firstBrace + 1);

        return null;
    }
}

public class LLMResponse
{
    public string response;
}

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System;
using Siccity.GLTFUtility;
using System.Linq;
using TMPro;
using UnityEngine.UI;

public class ImgTo3DClient : MonoBehaviour
{
    public string trellisUrl = "http://127.0.0.1:7960";
    public TMP_Text debug;
    public Button scaleButton;

    int columns = 5; // Numero di colonne
    float spacing = 1.5f; // Distanza tra i modelli


    public IEnumerator ProcessAllImages()
    {
        string basePath = Path.Combine(Application.dataPath, "SessionItems", "ObjectPictures");

        
        string latestFolder = new DirectoryInfo(basePath)
            .GetDirectories()
            .OrderByDescending(d => d.LastWriteTime)
            .FirstOrDefault()?.FullName;

        if (string.IsNullOrEmpty(latestFolder))
        {
            Debug.LogError("❌ Nessuna cartella trovata in ObjectPictures.");
            yield break;
        }

        string folderName = new DirectoryInfo(latestFolder).Name;
        debug.text = $"Generating models for the {folderName}";

        string[] files = Directory
            .GetFiles(latestFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f =>
                (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) &&
                !Path.GetFileName(f).Equals("floor_texture.png", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(f).Equals("wall_texture.png", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Debug.LogWarning("❌ Nessuna immagine valida trovata (escluse floor/wall texture).");
            yield break;
        }

        // Crea cartella output con lo stesso nome della cartella immagini
        string sessionFolderPath = Path.Combine(Application.dataPath, "SessionItems/3DModels", folderName);
        Directory.CreateDirectory(sessionFolderPath);

        int index = 0;
        foreach (string imagePath in files)
        {
            debug.text = "Generating 3D model for " + imagePath;
            string base64Image = Convert.ToBase64String(File.ReadAllBytes(imagePath));

            WWWForm form = new WWWForm();
            form.AddField("image_base64", base64Image);
            form.AddField("seed", "42");
            form.AddField("ss_guidance_strength", "3");
            form.AddField("ss_sampling_steps", "16");
            form.AddField("slat_guidance_strength", "3");
            form.AddField("slat_sampling_steps", "16");
            form.AddField("mesh_simplify_ratio", "0.9");
            form.AddField("texture_size", "512");
            form.AddField("output_format", "glb");

            Debug.Log("📤 Inviando immagine a Trellis: " + Path.GetFileName(imagePath));
            UnityWebRequest post = UnityWebRequest.Post(trellisUrl + "/generate_no_preview", form);
            yield return post.SendWebRequest();

            if (post.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("POST error: " + post.error);
                continue;
            }

            // Poll di stato
            while (true)
            {
                UnityWebRequest status = UnityWebRequest.Get(trellisUrl + "/status");
                yield return status.SendWebRequest();

                if (status.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("Status error: " + status.error);
                    yield break;
                }

                string json = status.downloadHandler.text;
                if (json.Contains("\"status\":\"COMPLETE\""))
                    break;
                else if (json.Contains("\"status\":\"FAILED\""))
                {
                    Debug.LogError("❌ Generazione fallita per: " + Path.GetFileName(imagePath));
                    yield break;
                }

                yield return new WaitForSeconds(1);
            }

            // Scarica modello
            UnityWebRequest download = UnityWebRequest.Get(trellisUrl + "/download/model");
            yield return download.SendWebRequest();

            if (download.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("❌ Download fallito: " + download.error);
                continue;
            }

            string safeName = Path.GetFileNameWithoutExtension(imagePath);
            string modelPath = Path.Combine(sessionFolderPath, $"{safeName}.glb");
            File.WriteAllBytes(modelPath, download.downloadHandler.data);
            Debug.Log($"✅ Modello salvato: {modelPath}");

            // Istanzia modello
            yield return new WaitForSeconds(0.1f);

            GameObject model = Importer.LoadFromFile(modelPath);

            int row = index / columns;
            int col = index % columns;

            Vector3 position = new Vector3(col * spacing, 0f, -row * spacing);
            model.transform.position = position;

            // Set parent
            GameObject parent = GameObject.Find("Asset");
            if (parent != null)
            {
                model.transform.SetParent(parent.transform);
            }
            else
            {
                Debug.LogWarning("⚠️ GameObject 'Assets' non trovato nella scena. Il modello resterà nella root.");
            }

            Debug.Log($"🧱 Modello '{Path.GetFileName(modelPath)}' istanziato in scena a ({position.x}, {position.y}, {position.z})");

            index++;

            /*
            GameObject model = Importer.LoadFromFile(modelPath);
            model.transform.position = new Vector3(index * 1.5f, 0f, 0f);

            // Assegna il parent a "Assets"
            GameObject parent = GameObject.Find("Asset");
            if (parent != null)
            {
                model.transform.SetParent(parent.transform);
            }
            else
            {
                Debug.LogWarning("⚠️ GameObject 'Assets' non trovato nella scena. Il modello resterà nella root.");
            }
            Debug.Log($"Modello '{safeName}' istanziato in scena.");

            index++;*/
        }

        Debug.Log($"🎉 Tutti i modelli sono stati generati e salvati in {sessionFolderPath}");
        debug.text = $"All models generated and saved in {sessionFolderPath}";
        scaleButton.gameObject.SetActive(true);
    }
}

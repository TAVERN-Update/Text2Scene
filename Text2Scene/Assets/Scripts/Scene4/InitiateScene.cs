using Mono.Reflection;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class InitiateScene : MonoBehaviour
{
    public GameObject instructions;

    public void on3DPressed()
    {
        ImgTo3DClient imgTo3D = FindObjectOfType<ImgTo3DClient>();
        if (imgTo3D != null)
        {
            StartCoroutine(imgTo3D.ProcessAllImages());
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.X))
        {
            if (instructions != null)
            {
                instructions.SetActive(false);
            }
        }
    }


    public void ProceedToComposing()
    {
        SceneManager.LoadScene("ComposingScene");
    }
}

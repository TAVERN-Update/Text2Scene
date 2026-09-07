using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class ObjectImageLayout : MonoBehaviour
{
    [Header("UI References")]
    public TMP_Text nameField;
    public Image imageField;
    public Button overlayButton;
    public TMP_InputField negativePromptField;

    [HideInInspector] public string prompt;

    public void SetName(string objName)
    {
        nameField.text = objName;
    }


    public void SetImage(Sprite sprite)
    {
        imageField.sprite = sprite;
    }

    public void SetInteractable(bool state)
    {
        if (overlayButton != null)
            overlayButton.interactable = state;
    }

    public void ResetToWhite()
    {
        imageField.sprite = null;
        imageField.color = Color.white;
    }

    public string GetNegativePrompt()
    {
        return negativePromptField != null ? negativePromptField.text : "";
    }

    public void SetNegativePromptVisible(bool state)
    {
        if (negativePromptField != null)
            negativePromptField.gameObject.SetActive(state);
    }

}



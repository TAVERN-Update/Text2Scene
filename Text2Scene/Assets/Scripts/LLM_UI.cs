using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using static UISetup;
using UnityEngine.SceneManagement;
using static System.Net.Mime.MediaTypeNames;

public class LLM_UI : MonoBehaviour
{
    [Header("UI References")]
    public TMP_InputField inputField;
    public Button sendButton;
    public Transform contentTransform; 
    public GameObject messagePrefab;
    public GameObject botMessagePrefab;
    public ScrollRect scrollRect;

    void Start()
    {
        sendButton.onClick.AddListener(OnSendMessage);
        AddBotMessageToChat("Hi! I'll help you create a 3D scene from scratch.\r\nTo get started, could you describe what kind of scene you had in mind?");
    }

    void OnSendMessage()
    {
        string messageText = inputField.text.Trim();
        if (string.IsNullOrEmpty(messageText)) return;

        AddMessageToChat(messageText);
        inputField.text = "";
        inputField.ActivateInputField(); 
    }

    IEnumerator ScrollToBottom()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
    }


    void AddMessageToChat(string text)
    {
        GameObject newMessage = Instantiate(messagePrefab, contentTransform, false);
        RectTransform rt = newMessage.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.anchoredPosition = Vector2.zero;

        TMP_Text messageTMP = newMessage.GetComponentInChildren<TMP_Text>();
        if (messageTMP != null)
        {
            messageTMP.text = text;
        }

        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
        AddBotMessageToChat(".  .  .");
        StartCoroutine(ScrollToBottom());

    }

    public void AddBotMessageToChat(string text)
    {
        RemoveLastBotMessage();
        GameObject newMessage = Instantiate(botMessagePrefab, contentTransform, false);

        newMessage.transform.rotation = Quaternion.Euler(0, 0, 0);

        TMP_Text messageTMP = newMessage.GetComponentInChildren<TMP_Text>();
        if (messageTMP != null)
        {
            messageTMP.text = text;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(newMessage.GetComponent<RectTransform>());
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
    }

    public void RemoveLastBotMessage()
    {
        for (int i = contentTransform.childCount - 1; i >= 0; i--)
        {
            Transform message = contentTransform.GetChild(i);

            if (message.name.Contains("LLM"))
            {
                TextMeshProUGUI textComp = message.GetComponentInChildren<TextMeshProUGUI>();

                if (textComp != null && textComp.text == ".  .  .")
                {
                    Destroy(message.gameObject);
                    break;
                }
            }
        }
    }

    public void Proceed()
    {
       SceneManager.LoadScene("EditSceneDetails");
    }
}




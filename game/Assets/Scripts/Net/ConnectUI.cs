using TMPro;
using Unity.Netcode;
using UnityEngine;

public class ConnectUI : MonoBehaviour
{
    [SerializeField] TMP_InputField addressInput;
    [SerializeField] Bootstrap bootstrap;

    public void OnConnectPressed()
    {
        var addr = addressInput != null ? addressInput.text : "";
        bootstrap.OnClickConnect(addr);
    }

    // 에디터 테스트용 — 서버+클라 동시 실행
    public void OnHostPressed()
    {
        NetworkManager.Singleton.StartHost();
        Debug.Log("[HOST] started");
        gameObject.SetActive(false);
    }
}
using Unity.Netcode;
using UnityEngine;

public class PlayerTemp : NetworkBehaviour
{
    [SerializeField] Camera cam;
    [SerializeField] float speed = 6f;
    CharacterController cc;

    void Awake() => cc = GetComponent<CharacterController>();

    public override void OnNetworkSpawn()
    {
        Debug.Log($"[SPAWN] OwnerClientId={OwnerClientId}, LocalClientId={NetworkManager.Singleton.LocalClientId}, IsOwner={IsOwner}, cam={(cam != null)}");
        if (cam) cam.gameObject.SetActive(IsOwner);
    }

    void Update()
    {
        if (!IsOwner) return;
        var h = Input.GetAxisRaw("Horizontal");
        var v = Input.GetAxisRaw("Vertical");
        var dir = transform.right * h + transform.forward * v;
        cc.Move(dir.normalized * speed * Time.deltaTime);
        cc.Move(Physics.gravity * Time.deltaTime);
    }

}
using UnityEngine;

/// <summary>
/// 서버/클라 공통 틱 카운터. FixedUpdate(60Hz)와 동기화됩니다.
/// </summary>
public class NetworkTickSystem : MonoBehaviour
{
    public static NetworkTickSystem Instance { get; private set; }

    public int CurrentTick { get; private set; }
    public const int TickRate = 60;
    public const float TickInterval = 1f / TickRate;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        CurrentTick = 0;
    }

    void FixedUpdate()
    {
        CurrentTick++;
    }
}
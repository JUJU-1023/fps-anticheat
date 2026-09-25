// =====================================================================
//  NetPayloads.cs
//  경로: game/Assets/Scripts/Net/NetPayloads.cs
//        (기존 InputPayload / StatePayload 정의 파일. 실제 파일명에 맞출 것)
//
//  W8 Day 3 변경
//   buttons 에 bit4 reload 를 추가했다.
//
//   별도 RPC 를 만들지 않는 이유는 발사와 같다.
//     1) V-MOVE-01 의 토큰 버킷이 재장전 입력에도 그대로 적용된다.
//     2) 재장전과 사격이 같은 틱 타임라인 위에 놓여 순서가 확정된다.
//
//   MovementValidator 는 buttons 를 검사하지 않으므로(move/yaw/pitch 만
//   본다) 비트를 추가해도 V-MOVE 판정에 영향이 없다.
//
//   byte 에 아직 3비트 남아 있다. 직렬화 크기는 변하지 않는다.
// =====================================================================

using Unity.Netcode;
using UnityEngine;

public struct InputPayload : INetworkSerializable
{
    public int tick;
    public Vector2 move;      // (x=strafe, y=forward), 정규화됨
    public float yaw;
    public float pitch;

    /// <summary>bit0 jump, bit1 fire, bit2 crouch, bit3 sprint, bit4 reload</summary>
    public byte buttons;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tick);
        s.SerializeValue(ref move);
        s.SerializeValue(ref yaw);
        s.SerializeValue(ref pitch);
        s.SerializeValue(ref buttons);
    }
}

public struct StatePayload : INetworkSerializable
{
    public int tick;
    public Vector3 position;
    public Vector3 velocity;
    public float yaw;
    public float pitch;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tick);
        s.SerializeValue(ref position);
        s.SerializeValue(ref velocity);
        s.SerializeValue(ref yaw);
        s.SerializeValue(ref pitch);
    }
}
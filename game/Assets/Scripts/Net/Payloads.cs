using Unity.Netcode;
using UnityEngine;

public struct InputPayload : INetworkSerializable
{
    public int tick;
    public Vector2 move;      // (x=strafe, y=forward), ¡§±‘»≠µ 
    public float yaw;
    public float pitch;
    public byte buttons;     // bit0 jump, bit1 fire, bit2 crouch, bit3 sprint

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
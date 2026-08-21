public class CircularBuffer<T>
{
    private readonly T[] buffer;
    private readonly int size;

    public CircularBuffer(int size)
    {
        this.size = size;
        buffer = new T[size];
    }

    public void Set(int tick, T value) => buffer[tick % size] = value;
    public T Get(int tick) => buffer[tick % size];
}
namespace StressLab.Engines;

/// <summary>Fixed-capacity ring buffer of doubles (oldest value at index 0).</summary>
public sealed class RingBuffer
{
    private readonly double[] _items;
    private int _head;

    public int Capacity { get; }
    public int Count { get; private set; }

    public RingBuffer(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _items = new double[Capacity];
    }

    public void Add(double value)
    {
        _items[_head] = value;
        _head = (_head + 1) % Capacity;
        if (Count < Capacity) Count++;
    }

    public void Reset()
    {
        _head = 0;
        Count = 0;
    }

    /// <summary>Index 0 = oldest retained value.</summary>
    public double this[int index] => _items[((_head - Count + Capacity) % Capacity + index) % Capacity];
}

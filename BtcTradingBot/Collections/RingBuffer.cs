using System.Collections;

namespace BtcTradingBot.Collections;

/// <summary>
/// 고정 길이 링버퍼. 용량 초과 시 가장 오래된 항목을 자동 제거한다.
/// ObservableCollection 대비 GC 압박이 적고, 차트 캔들 수를 제한하는 데 사용.
/// </summary>
public class RingBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _buffer;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
            return _buffer[(_start + index) % _buffer.Length];
        }
        set
        {
            if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
            _buffer[(_start + index) % _buffer.Length] = value;
        }
    }

    public void Add(T item)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = item;
            _count++;
        }
        else
        {
            _buffer[_start] = item;
            _start = (_start + 1) % _buffer.Length;
        }
    }

    public void UpdateLast(T item)
    {
        if (_count == 0) throw new InvalidOperationException("Buffer is empty");
        _buffer[(_start + _count - 1) % _buffer.Length] = item;
    }

    public T Last()
    {
        if (_count == 0) throw new InvalidOperationException("Buffer is empty");
        return _buffer[(_start + _count - 1) % _buffer.Length];
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_buffer);
    }

    public List<T> ToList()
    {
        var list = new List<T>(_count);
        for (int i = 0; i < _count; i++)
            list.Add(this[i]);
        return list;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

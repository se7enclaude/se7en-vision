using System.Security.Cryptography;

namespace PhoneRemote.Core;

/// <summary>Cursor images already sent to the viewer, by content hash (short id), most recent kept.</summary>
public sealed class CursorShapeCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly LinkedList<string> _order = new();

    public CursorShapeCache(int capacity = 64) { _capacity = Math.Max(1, capacity); }

    public static string IdOf(ReadOnlySpan<byte> png) => Convert.ToHexString(SHA256.HashData(png).AsSpan(0, 4)).ToLowerInvariant();

    /// <summary>True when the id was already known (touches it); false when it is new (and now remembered).</summary>
    public bool Seen(string id)
    {
        if (_nodes.TryGetValue(id, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return true;
        }
        _nodes[id] = _order.AddFirst(id);
        if (_order.Count > _capacity)
        {
            var last = _order.Last!;
            _nodes.Remove(last.Value);
            _order.RemoveLast();
        }
        return false;
    }

    public void Forget(string id)
    {
        if (_nodes.Remove(id, out var node)) _order.Remove(node);
    }

    public int Count => _order.Count;
}

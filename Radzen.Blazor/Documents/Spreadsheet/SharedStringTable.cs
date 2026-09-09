using System;
using System.Buffers;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

sealed class SharedStringTable : IDisposable
{
    private const int InitialCapacity = 256;

    private string[] strings = [];
    private int[] buckets = [];
    private int capacity;
    private int mask;
    private int count;

    public int Count => count;

    public ReadOnlySpan<string> Strings => strings.AsSpan(0, count);

    public int GetOrAdd(string text)
    {
        var slot = 0;

        if (capacity > 0)
        {
            var index = IndexOf(text, out slot);

            if (index >= 0)
            {
                return index;
            }
        }

        if (count == capacity)
        {
            Grow();
            slot = EmptySlot(text);
        }

        strings[count] = text;
        buckets[slot] = count + 1;

        return count++;
    }

    private int IndexOf(string text, out int slot)
    {
        slot = text.GetHashCode(StringComparison.Ordinal) & mask;

        while (true)
        {
            var index = buckets[slot] - 1;

            if (index < 0)
            {
                return -1;
            }

            if (string.Equals(strings[index], text, StringComparison.Ordinal))
            {
                return index;
            }

            slot = (slot + 1) & mask;
        }
    }

    private int EmptySlot(string text)
    {
        var slot = text.GetHashCode(StringComparison.Ordinal) & mask;

        while (buckets[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        return slot;
    }

    private void Grow()
    {
        var oldStrings = strings;
        var oldBuckets = buckets;

        capacity = capacity == 0 ? InitialCapacity : capacity * 2;
        mask = capacity * 2 - 1;

        strings = ArrayPool<string>.Shared.Rent(capacity);
        buckets = ArrayPool<int>.Shared.Rent(capacity * 2);
        Array.Clear(buckets, 0, capacity * 2);

        for (var index = 0; index < count; index++)
        {
            strings[index] = oldStrings[index];
            buckets[EmptySlot(oldStrings[index])] = index + 1;
        }

        if (count > 0)
        {
            ArrayPool<string>.Shared.Return(oldStrings, clearArray: true);
            ArrayPool<int>.Shared.Return(oldBuckets);
        }
    }

    public void Dispose()
    {
        if (capacity > 0)
        {
            ArrayPool<string>.Shared.Return(strings, clearArray: true);
            ArrayPool<int>.Shared.Return(buckets);
        }

        strings = [];
        buckets = [];
        capacity = 0;
        mask = 0;
        count = 0;
    }
}

using System;
using System.Buffers;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// The shared string table a workbook is saved with: each distinct string once, at the index its cells are
/// written with, over arrays rented from <see cref="ArrayPool{T}"/> and returned when the save is done.
/// </summary>
/// <remarks>
/// A <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> allocates its arrays anew on every
/// save and doubles them as it grows. Renting them instead means a save after the first in a process pays
/// nothing for the table however it grows, so it needs no hint about how many strings a sheet holds.
/// </remarks>
sealed class SharedStringTable : IDisposable
{
    private const int InitialCapacity = 256;

    private string[] strings = [];
    private int[] buckets = [];
    private int capacity;
    private int mask;
    private int count;

    /// <summary>
    /// The number of distinct strings in the table.
    /// </summary>
    public int Count => count;

    /// <summary>
    /// The strings in the order of their indices.
    /// </summary>
    public ReadOnlySpan<string> Strings => strings.AsSpan(0, count);

    /// <summary>
    /// Returns the index of <paramref name="text"/>, adding it at the next index if it is not in the table.
    /// </summary>
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

    // The index of text, or -1 with slot left at the empty bucket where it would go.
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

    // The buckets hold an index plus one, so a rented bucket array cleared to zero is empty, and they stay
    // at most half full so a probe is short.
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

    /// <summary>
    /// Returns the arrays to the pool. The table is empty afterwards.
    /// </summary>
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

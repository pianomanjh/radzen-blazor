using System;
using System.Globalization;
using System.Xml;

// Does the span path write what the string path wrote?
//
// XlsxWriter formats numbers with TryFormat into a buffer instead of ToString(InvariantCulture) or
// XmlConvert.ToString. Those are documented to agree and the byte comparison says they do on the values
// a fixture happens to carry; this asks the values a fixture would not think of. A disagreement here
// would change a saved file silently, which is the one failure mode the tests cannot see.
var doubles = new double[]
{
    0, -0.0, 1, -1, 0.1, 1.0/3, 1234.5678, -0.000001234, 1e-7, 1e21, 1e300, 5e-324,
    double.MaxValue, double.MinValue, double.Epsilon, 44224.2129282407, 0.1+0.2,
    123456789012345.68, 1e16, 1e17, 9007199254740993.0, 2.2250738585072014e-308,
};

var ints = new int[] { 0, 1, -1, 9, 10, 99, 100, 12345, int.MaxValue, int.MinValue, 1048576, 16384 };
var floats = new float[] { 0f, 1f, 0.1f, -1.5e-30f, float.MaxValue, float.MinValue, float.Epsilon };
var decimals = new decimal[] { 0m, 1m, -1.005m, 0.0000000001m, decimal.MaxValue, decimal.MinValue, 1.10m };

// The writer's own buffer width.
var buffer = new char[64];
var bad = 0;

void Check(string kind, string expected, bool ok, int written)
{
    var actual = ok ? new string(buffer, 0, written) : "<TryFormat returned false>";
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        bad++;
        Console.WriteLine($"  {kind}: ToString gave '{expected}', TryFormat gave '{actual}'");
    }
}

foreach (var d in doubles)
{
    Check("double", d.ToString(CultureInfo.InvariantCulture), d.TryFormat(buffer, out var n, provider: CultureInfo.InvariantCulture), n);
}

foreach (var i in ints)
{
    Check("int/ToString", i.ToString(CultureInfo.InvariantCulture), i.TryFormat(buffer, out var n, provider: CultureInfo.InvariantCulture), n);
    Check("int/XmlConvert", XmlConvert.ToString(i), i.TryFormat(buffer, out var m, provider: CultureInfo.InvariantCulture), m);
}

foreach (var f in floats)
{
    Check("float", f.ToString(CultureInfo.InvariantCulture), f.TryFormat(buffer, out var n, provider: CultureInfo.InvariantCulture), n);
}

foreach (var m in decimals)
{
    Check("decimal", m.ToString(CultureInfo.InvariantCulture), m.TryFormat(buffer, out var n, provider: CultureInfo.InvariantCulture), n);
}

// The IFormattable fallback the writer keeps for anything else.
foreach (var v in new object[] { (short)7, (byte)9, (long)-5, 'x', TimeSpan.FromMinutes(3) })
{
    var expected = ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture);
    Console.WriteLine($"  fallback {v.GetType().Name,-9} -> '{expected}'");
}

Console.WriteLine(bad == 0
    ? $"all {doubles.Length + ints.Length * 2 + floats.Length + decimals.Length} comparisons agree"
    : $"{bad} DISAGREEMENTS");

// Polyfills.cs
//
// Compile-time shims so code written against modern .NET (records,
// init-only properties, range/index operators) builds under
// netstandard2.0 inside the Roslyn analyzer assembly.
//
// The Lib's source target net10.0 and uses C# 8 range/index + 9 init-only
// properties. Roslyn analyzers must target netstandard2.0; these shims
// reproduce just enough of the BCL types so the same source compiles
// without modification.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker type the compiler looks for to allow <c>init</c>-only setters
    /// and positional records under TFMs that do not ship the type.
    /// </summary>
    internal static class IsExternalInit { }
}

#if NETSTANDARD2_0
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }
        public static Index Start => new Index(0);
        public static Index End   => new Index(~0);
        public static Index FromStart(int value) => new Index(value);
        public static Index FromEnd(int value)   => new Index(value, fromEnd: true);
        public int Value   => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => FromStart(value);
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index o && Equals(o);
        public override int GetHashCode() => _value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End   { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range All => new Range(Index.Start, Index.End);
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end)     => new Range(Index.Start, end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int s = Start.GetOffset(length);
            int e = End.GetOffset(length);
            if ((uint)e > (uint)length || (uint)s > (uint)e)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (s, e - s);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range o && Equals(o);
        public override int GetHashCode()
        {
            unchecked { return (Start.GetHashCode() * 397) ^ End.GetHashCode(); }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class RuntimeHelpersShim
    {
        // String slicing via Range operator lowers to a call on
        // String.Substring(int, int) when GetSubStringByRange is
        // unavailable — but the compiler also looks for a Substring(int)
        // overload to materialise the GetOffsetAndLength result, which
        // the netstandard2.0 String already exposes. No additional
        // bridging needed; the polyfill structs alone are sufficient.
    }
}
#endif

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Draw-without-replacement bag: yields every item once (in random order)
    /// before any repeats, then reshuffles for the next cycle. Guarantees full
    /// variety per cycle and never repeats an item back-to-back across the
    /// reshuffle boundary.
    ///
    /// <para>Ordering is deterministic given the injected RNG delegate — pass a
    /// seeded <c>Random.Next</c> for reproducible slideshows, or an
    /// entropy-seeded one for a fresh order each run.</para>
    ///
    /// <para>The source set is supplied on every <see cref="Draw"/> call so the
    /// bag rebuilds transparently when membership changes between draws (the
    /// slideshow region pool refreshes live — a region saved mid-show joins the
    /// rotation without a restart).</para>
    /// </summary>
    public sealed class ShuffleBag<T>
    {
        private readonly Func<int, int> _next;   // rng.Next(exclusiveMax)
        private readonly IEqualityComparer<T> _cmp;
        private readonly List<T> _order = new();  // shuffled remaining draws (tail = next)
        private readonly HashSet<T> _known;       // current source membership
        private bool _hasLast;
        private T _last = default!;

        /// <param name="next">Bounded RNG: <c>next(n)</c> returns [0, n).
        /// Typically <c>new Random(seed).Next</c>.</param>
        /// <param name="comparer">Membership / dedup comparer. Defaults to
        /// <see cref="EqualityComparer{T}.Default"/>.</param>
        public ShuffleBag(Func<int, int> next, IEqualityComparer<T>? comparer = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _cmp = comparer ?? EqualityComparer<T>.Default;
            _known = new HashSet<T>(_cmp);
        }

        /// <summary>Draw the next item from <paramref name="items"/>. Rebuilds
        /// (fresh shuffle) when the membership differs from the previous call.
        /// Returns <c>default</c> when the source is empty.</summary>
        public T Draw(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0)
            {
                _order.Clear();
                _known.Clear();
                _hasLast = false;
                return default!;
            }

            if (SetChanged(items)) Rebuild(items);
            if (_order.Count == 0) Reshuffle(items);

            // Draw from the tail so removal is O(1).
            int idx = _order.Count - 1;
            T pick = _order[idx];
            _order.RemoveAt(idx);
            _last = pick;
            _hasLast = true;
            return pick;
        }

        private bool SetChanged(IReadOnlyList<T> items)
        {
            if (items.Count != _known.Count) return true;
            foreach (var it in items)
                if (!_known.Contains(it)) return true;
            return false;
        }

        private void Rebuild(IReadOnlyList<T> items)
        {
            _known.Clear();
            foreach (var it in items) _known.Add(it);
            Reshuffle(items);
        }

        private void Reshuffle(IReadOnlyList<T> items)
        {
            _order.Clear();
            _order.AddRange(items);

            // Fisher–Yates using the injected RNG.
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            // We draw from the tail; keep the just-drawn item off the next
            // draw so cycles don't butt the same item against itself.
            if (_hasLast && _order.Count > 1 && _cmp.Equals(_order[^1], _last))
                (_order[^1], _order[0]) = (_order[0], _order[^1]);
        }
    }
}

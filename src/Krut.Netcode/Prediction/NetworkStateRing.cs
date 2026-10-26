using System;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace Krut.Netcode
{
    /// <summary>
    /// One tick plus one value. The unit of interpolation: a buffered snapshot is always "the state at tick N",
    /// never just "a state" - without the tick there is no way to know whether a snapshot lies before or after
    /// the moment being drawn.
    /// </summary>
    public struct NetworkStateEntry<T>
    {
        /// <summary>Builds an entry.</summary>
        /// <param name="tick">The tick the value applies to. Unit: ticks.</param>
        public NetworkStateEntry(int tick, in T value)
        {
            Tick = tick;
            Value = value;
        }

        /// <summary>The session's whole tick the value applies to.</summary>
        public int Tick { get; set; }

        /// <summary>The state itself.</summary>
        public T Value { get; set; }
    }

    /// <summary>
    /// Fixed circular buffer of tick-stamped states, with strictly ascending ticks. With 16 units in the lobby
    /// that is over 1.7 million structs in memory, and they are never freed because the list only ever grows.
    /// The ring overwrites the oldest entry in that case, exactly as a circular buffer should. Exactly
    /// <c>capacity x sizeof(TEntry)</c>, allocated once in the constructor and never grown: the interpolator's
    /// default is 32 entries, and with a 12-float state <see cref="NetworkStateEntry{T}"/> is <c>4 (tick) + 4
    /// (padding) + 48 (state)</c> = 56 bytes, so 32 x 56 = 1,792 bytes ~ 1.75 kB per unit. Every public member
    /// here numbers entries from the OLDEST one, with 0 = oldest and <c>Count - 1</c> = newest - never from the
    /// physical slot. Internally <c>_head</c> points at the next write slot, which is also the oldest entry
    /// while the ring is non-empty, so an index has to be offset by <c>_head</c> before the modulo is taken.
    /// Doing it wrong does not throw: it returns yesterday's state, which reads as a desync nobody can find.
    /// </summary>
    /// <typeparam name="TEntry">
    /// The entry type - must be a struct with a tick in it (see the ctor: <c>tickOf</c>).
    /// </typeparam>
    public sealed class NetworkStateRing<TEntry>
        where TEntry : struct
    {
        /// <summary>Default capacity for the interpolator: 2-3 snapshot intervals plus margin.</summary>
        public const int DefaultCapacity = 32;

        /// <summary>The storage. Index 0 in ring terms is <c>_entries[_head]</c>, not <c>_entries[0]</c>.</summary>
        private readonly TEntry[] _entries;

        /// <summary>Where the next entry is written (modulo <see cref="_entries"/>'s length).</summary>
        private int _head;

        /// <summary>Reads the tick out of an entry - the delegate lets the ring avoid knowing the entry's shape.</summary>
        private readonly Func<TEntry, int> _tickOf;

        /// <param name="capacity">
        /// How many entries may remain at the same time (at least 2 - a ring with a single entry can never
        /// interpolate).
        /// </param>
        public NetworkStateRing(int capacity, Func<TEntry, int> tickOf)
        {
            if (capacity < 2)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A snapshot ring must hold at least two entries, otherwise interpolation is impossible.");
            if (tickOf is null)
                throw new ArgumentNullException(nameof(tickOf));

            _entries = new TEntry[capacity];
            _tickOf = tickOf;
        }

        /// <summary>How many entries may remain (decided in the ctor and never changed).</summary>
        public int Capacity => _entries.Length;

        /// <summary>Number of entries in the ring right now. Range 0..<see cref="Capacity"/>.</summary>
        public int Count { get; private set; }

        /// <summary>Is the ring empty? (Then <c>TryGet</c> should return false instead of throwing.)</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>The oldest entry's tick - the one we interpolate from.</summary>
        public int OldestTick
        {
            get
            {
                if (Count == 0)
                    throw new InvalidOperationException("The ring is empty - there is no oldest tick.");
                return _tickOf(this[0]);
            }
        }

        /// <summary>The newest entry's tick - the one we interpolate to, or extrapolate from.</summary>
        public int NewestTick
        {
            get
            {
                if (Count == 0)
                    throw new InvalidOperationException("The ring is empty - there is no newest tick.");
                return _tickOf(this[Count - 1]);
            }
        }

        /// <summary>
        /// The entry at position <paramref name="index"/> counted from the oldest one (0 = oldest).
        /// </summary>
        /// <param name="index">Position counted from the oldest entry. Valid range 0..<c>Count - 1</c>.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is outside the ring. Note that this is against <c>Count</c>, not
        /// <see cref="Capacity"/> - the slots beyond the newest entry hold stale data and must not be readable.
        /// </exception>
        public TEntry this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index), index, "Index outside the ring.");

                // _head points at the next write slot, i.e. at the oldest entry -
                // so the index has to be added before the modulo is taken.
                int slot = _head + index;
                if (slot >= _entries.Length)
                    slot -= _entries.Length;

                return _entries[slot];
            }
        }

        /// <summary>
        /// The entry at position <paramref name="index"/> (counted from the oldest one), or false if the index
        /// is invalid.
        /// </summary>
        /// <param name="index">Position counted from the oldest entry. Valid range 0..<c>Count - 1</c>.</param>
        public bool TryGet(int index, out TEntry entry)
        {
            if (index < 0 || index >= Count)
            {
                entry = default;
                return false;
            }

            entry = this[index];
            return true;
        }

        /// <summary>
        /// Overwrites the entry at position <paramref name="index"/> (counted from the oldest one).
        /// </summary>
        /// <param name="index">Position counted from the oldest entry. Valid range 0..<c>Count - 1</c>.</param>
        public void Set(int index, in TEntry entry)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index outside the ring.");

            int slot = _head + index;
            if (slot >= _entries.Length)
                slot -= _entries.Length;

            _entries[slot] = entry;
        }

        /// <summary>
        /// Inserts an entry. Returns false if <paramref name="tick"/> isn't strictly greater than the newest
        /// entry's - that is a delayed or duplicated snapshot (UDP packets sometimes arrive in the wrong
        /// order), and it must not touch the buffer.
        /// </summary>
        /// <param name="tick">The tick the entry applies to. Unit: ticks.</param>
        public bool Push(int tick, in TEntry entry)
        {
            if (Count > 0 && tick <= _tickOf(this[Count - 1]))
                return false;

            if (Count == _entries.Length)
            {
                // Full: overwrite the oldest and advance _head by one, otherwise the new entry would land in
                // the same slot as the oldest (_head == index 0).
                int oldestSlot = _head;
                _head++;
                if (_head == _entries.Length)
                    _head = 0;

                _entries[oldestSlot] = entry;
                return true;
            }

            int slot = _head + Count;
            if (slot >= _entries.Length)
                slot -= _entries.Length;

            _entries[slot] = entry;
            Count++;
            return true;
        }

        /// <summary>
        /// Inserts an entry and drops the oldest one if the ring is full. Returns true if something was
        /// actually dropped - the reconciler counts that, because for a client that sends inputs but never gets
        /// replies "buffer full" should rather be a visible diagnosis than a silently forgotten entry.
        /// </summary>
        /// <param name="tick">The tick the entry applies to. Unit: ticks.</param>
        public bool PushOrOverwrite(int tick, in TEntry entry)
        {
            if (Count > 0 && tick <= _tickOf(this[Count - 1]))
                return false;

            bool wasFull = Count == _entries.Length;
            Push(tick, entry);
            return wasFull;
        }

        /// <summary>
        /// Drops every entry with tick &lt;= <paramref name="tick"/> (acknowledged inputs, or snapshots that
        /// aren't needed any more) and returns how many disappeared.
        /// </summary>
        /// <param name="tick">The last tick to remove, inclusive. Unit: ticks.</param>
        public int RemoveAtOrBefore(int tick)
        {
            int removed = 0;
            while (Count > 0 && _tickOf(this[0]) <= tick)
            {
                _entries[_head] = default;
                _head++;
                if (_head == _entries.Length)
                    _head = 0;

                Count--;
                removed++;
            }

            return removed;
        }

        /// <summary>Empties the ring (new connection, new match, respawn).</summary>
        public void Clear()
        {
            Array.Clear(_entries);
            _head = 0;
            Count = 0;
        }

        /// <summary>
        /// Index (counted from the oldest entry) of the last entry with tick &lt;= <paramref name="tick"/>, or
        /// -1 if every entry is newer. A gap in the tick sequence (a lost snapshot) does not break the search -
        /// only strict ascending order matters, which is exactly what <see cref="Push"/> enforces.
        /// </summary>
        /// <param name="tick">The tick to search for, inclusive. Unit: ticks.</param>
        public int IndexOfTickAtOrBefore(int tick)
        {
            int low = 0;
            int high = Count - 1;
            int result = -1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                if (_tickOf(this[mid]) <= tick)
                {
                    result = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Blends, subtracts and measures the distance between two
    /// <c>unmanaged</c> states.
    /// <para>
    /// <b>Why does this look like this?</b> The library isn't allowed to know what a
    /// "state" is - it can be a car, an FPS player or a shooter's ragdoll, and
    /// the game decides the structure. Reflection (type fields,
    /// <c>FieldInfo</c>) would be the generic answer, but it is reflection-based
    /// and therefore forbidden in an AOT build.
    /// Instead the <i>memory</i> is blended as a row of 4-byte numbers, which is
    /// exactly what an interpolator should do to a struct of primitives, and it
    /// allocates nothing.
    /// </para>
    /// <para>
    /// <b>The limitation - and why it's fine:</b> the blending assumes every
    /// component is 4 bytes. <c>float</c>, <c>int</c>, <c>uint</c> work, because
    /// each group is treated as a <c>float</c> and written back to the same 4
    /// bytes. What does <b>not</b> work is a <c>bool</c> (1 byte), an enum or a
    /// pointer: then the bits move rather than the value. A state with such fields
    /// must either (a) keep them in a separate struct outside <c>TState</c> -
    /// which is almost always right anyway, since a <c>bool flags</c> has no
    /// meaningful "in-between" - or (b) install its own blending function via the
    /// interpolator's / reconciler's delegate.
    /// </para>
    /// <para>
    /// <b>The alignment rule, stated exactly.</b> Every function here walks the
    /// state as <c>((float*)ptr)[i]</c> for <c>i</c> in
    /// <c>0 .. sizeof(TState) / 4 - 1</c>: component <c>i</c> is the 4-byte word at
    /// byte offset <c>4 * i</c>. Three things follow:
    /// <list type="number">
    /// <item><c>sizeof(TState)</c> must be divisible by 4. A struct with a
    /// <c>bool</c> or a <c>char</c> fails here with a clear
    /// <see cref="ArgumentException"/> rather than silently reading past the end
    /// - check it ahead of time with <see cref="IsComponentWise{TState}"/>.</item>
    /// <item>Component order is memory order, so the <i>field order</i> of
    /// <c>TState</c> <i>is</i> the component order. That is what makes
    /// <see cref="PositionDistance{TState}"/> treat the first three components as
    /// X/Y/Z, and it is also why a game must not reorder its fields between
    /// versions without rebuilding both sides.</item>
    /// <item>Each component is read as a <c>float</c> regardless of what it
    /// actually is. An <c>int</c> field that overflows past 2^24 loses
    /// precision through the round trip, which is why tick numbers and ids
    /// normally live outside <c>TState</c> rather than inside it.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Rotation must never be interpolated component-wise.</b> Two unit
    /// quaternions blended component-wise become a non-unit quaternion, and a
    /// matrix built from that has a scale that isn't 1 - it shows as a model that
    /// "puffs" slightly. On top of that the quaternion is double-covered: q and -q
    /// are the same rotation, so a naive linear blend can turn 180 degrees between
    /// two samples. Use <see cref="SlerpRotation"/>, which takes the shortest way
    /// and normalizes. (The library's <c>NetworkCodec.ReadRotation</c> already
    /// normalizes what is read in, so the two endpoints are guaranteed unit
    /// length - but it is exactly the <i>middle</i> that breaks, and the middle is
    /// what gets drawn.)
    /// </para>
    /// </summary>
    public static unsafe class NetworkStateBlend
    {
        /// <summary>Number of bytes per interpolated component (float/int).</summary>
        public const int ComponentSize = sizeof(float);

        /// <summary>
        /// Linear blend of every component: <c>from + (to - from) * t</c>, with <c>t</c> clamped to [0, 1].
        /// </summary>
        /// <param name="from">The earlier state. Read, never written.</param>
        /// <param name="to">The later state. Read, never written.</param>
        /// <param name="t">
        /// Blend factor, normally in 0..1; clamped. Above 1 returns <paramref name="to"/> exactly, at or below
        /// 0 returns <paramref name="from"/> exactly.
        /// </param>
        public static TState Lerp<TState>(TState from, TState to, float t)
            where TState : unmanaged
        {
            if (t <= 0f)
                return from;
            if (t >= 1f)
                return to;

            return Blend<TState>(from, to, t);
        }

        /// <summary>
        /// The same linear blend but without clamping <c>t</c>, so that the line can be extended beyond the
        /// last known point - that is the whole of the extrapolation (dead reckoning). <c>t = 1</c> gives
        /// exactly <paramref name="to"/>, <c>t = 2</c> twice the distance from <paramref name="from"/> to
        /// <paramref name="to"/>, and so on. They may be shown but never forwarded - see
        /// <c>NetworkSnapshotInterpolator</c> and sec 7.3.
        /// </summary>
        /// <param name="from">The older state. Read, never written.</param>
        /// <param name="to">The newer state. Read, never written.</param>
        /// <param name="t">
        /// Line parameter, where 0 = <paramref name="from"/> and 1 = <paramref name="to"/>. Values above 1
        /// extend past <paramref name="to"/>; only exactly 0 short-circuits.
        /// </param>
        public static TState Extend<TState>(TState from, TState to, float t)
            where TState : unmanaged
        {
            if (t == 0f)
                return from;

            return Blend<TState>(from, to, t);
        }

        /// <summary>The shared blend, without clamping (see <see cref="Lerp"/> and <see cref="Extend"/>).</summary>
        private static TState Blend<TState>(TState from, TState to, float t)
            where TState : unmanaged
        {
            int size = checked(sizeof(TState));
            int components = size / ComponentSize;
            if (components * ComponentSize != size)
            {
                throw new ArgumentException(
                    $"TState {typeof(TState).Name} is {size} bytes, which is not divisible by {ComponentSize}. " +
                    "Interpolation requires a struct of 4-byte primitives.",
                    nameof(from));
            }

            // The pointers are validated here instead of on every iteration: the check is cheap, the loop's
            // isn't - and the outside-the-loop version is what lets the JIT/AOT keep both ends in registers.
            TState* fromPtr = &from;
            TState* toPtr = &to;
            TState result = default;
            TState* resultPtr = &result;

            for (int i = 0; i < components; i++)
            {
                float a = ((float*)fromPtr)[i];
                float b = ((float*)toPtr)[i];
                ((float*)resultPtr)[i] = a + ((b - a) * t);
            }

            return result;
        }

        /// <summary>
        /// Component-wise <c>state + (from - to) * share</c>: <paramref name="state"/> shifted by a share of
        /// the difference between two other states. The default visual correction of
        /// <c>NetworkReconciler.RenderOffset</c>: the running prediction shifted by the error that was visible
        /// when the correction arrived, with <paramref name="share"/> decaying from 1 to 0. Like the other
        /// component-wise functions it treats every 4-byte field as a float, so a state with integers needs its
        /// own function.
        /// </summary>
        /// <param name="state">The state to shift (normally the current prediction). Read, never written.</param>
        /// <param name="from">The state that was shown. Read, never written.</param>
        /// <param name="to">The corrected state at the same moment. Read, never written.</param>
        /// <param name="share">
        /// How much of the error is still shown; 0 returns <paramref name="state"/> exactly.
        /// </param>
        public static TState Offset<TState>(TState state, TState from, TState to, float share)
            where TState : unmanaged
        {
            if (share == 0f)
                return state;

            int components = checked(sizeof(TState)) / ComponentSize;
            if (components * ComponentSize != sizeof(TState))
                throw new ArgumentException($"TState {typeof(TState).Name} cannot be handled component-wise.", nameof(state));

            TState* statePtr = &state;
            TState* fromPtr = &from;
            TState* toPtr = &to;
            TState result = default;
            TState* resultPtr = &result;

            for (int i = 0; i < components; i++)
            {
                float error = ((float*)fromPtr)[i] - ((float*)toPtr)[i];
                ((float*)resultPtr)[i] = ((float*)statePtr)[i] + (error * share);
            }

            return result;
        }

        /// <summary>Component-wise <c>a - b</c> (float arithmetic over every 4-byte field).</summary>
        /// <param name="a">The minuend. Read, never written.</param>
        /// <param name="b">The subtrahend. Read, never written.</param>
        public static TState Subtract<TState>(TState a, TState b)
            where TState : unmanaged
        {
            int components = checked(sizeof(TState)) / ComponentSize;
            if (components * ComponentSize != sizeof(TState))
                throw new ArgumentException($"TState {typeof(TState).Name} cannot be handled component-wise.", nameof(a));

            TState* aPtr = &a;
            TState* bPtr = &b;
            TState result = default;
            TState* resultPtr = &result;

            for (int i = 0; i < components; i++)
            {
                ((float*)resultPtr)[i] = ((float*)aPtr)[i] - ((float*)bPtr)[i];
            }

            return result;
        }

        /// <summary>
        /// The distance between two states, as the length of the difference in the first three components (X,
        /// Y, Z - i.e. the position in the library's most common layout). The "same error must be teleported"
        /// threshold (see <c>VisualSmoothingSettings</c>) has to be measured in something, and without knowing
        /// the game's structure there is only one reasonable assumption: that the state starts with a position.
        /// </summary>
        /// <param name="a">The first state. Read, never written.</param>
        /// <param name="b">The second state. Read, never written.</param>
        public static float PositionDistance<TState>(TState a, TState b)
            where TState : unmanaged
        {
            int size = checked(sizeof(TState));
            if (size < ComponentSize * 3)
            {
                throw new ArgumentException(
                    $"TState {typeof(TState).Name} has only {size / ComponentSize} components and cannot have a position.",
                    nameof(a));
            }

            TState* aPtr = &a;
            TState* bPtr = &b;
            float dx = ((float*)aPtr)[0] - ((float*)bPtr)[0];
            float dy = ((float*)aPtr)[1] - ((float*)bPtr)[1];
            float dz = ((float*)aPtr)[2] - ((float*)bPtr)[2];
            return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        /// <summary>Shortest-path interpolation of a rotation, with the result normalized.</summary>
        public static Quaternion SlerpRotation(Quaternion from, Quaternion to, float t)
        {
            t = Math.Clamp(t, 0f, 1f);

            float lengthSquared = from.LengthSquared();
            if (lengthSquared <= 1e-12f)
                return Normalized(to);

            Quaternion a = from * (1f / MathF.Sqrt(lengthSquared));
            Quaternion b = Normalized(to);

            // -1 makes b into -b, which is the same rotation but gives a positive
            // dot product, i.e. the shortest way. The 0.9995 limit corresponds to
            // ~2 degrees, where a plain slerp starts to become numerically
            // unstable and nlerp is exactly accurate enough.
            float dot = ((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W));
            if (dot < 0f)
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
            else if (dot > 0.9995f)
                return Normalized(new Quaternion(
                    a.X + ((b.X - a.X) * t),
                    a.Y + ((b.Y - a.Y) * t),
                    a.Z + ((b.Z - a.Z) * t),
                    a.W + ((b.W - a.W) * t)));

            float theta0 = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            float sinTheta0 = MathF.Sin(theta0);
            float theta = theta0 * t;
            float s0 = MathF.Cos(theta) - (dot * MathF.Sin(theta) / sinTheta0);
            float s1 = MathF.Sin(theta) / sinTheta0;
            return Normalized(new Quaternion(
                (a.X * s0) + (b.X * s1),
                (a.Y * s0) + (b.Y * s1),
                (a.Z * s0) + (b.Z * s1),
                (a.W * s0) + (b.W * s1)));
        }

        /// <summary>
        /// Normalizes a quaternion, and returns the identity for a zero quaternion (where the length isn't
        /// defined).
        /// </summary>
        public static Quaternion Normalized(Quaternion value)
        {
            float lengthSquared = value.LengthSquared();
            if (lengthSquared <= 1e-6f)
                return Quaternion.Identity;

            float scale = 1f / MathF.Sqrt(lengthSquared);
            return new Quaternion(value.X * scale, value.Y * scale, value.Z * scale, value.W * scale);
        }

        /// <summary>
        /// True if a struct can be handled by <see cref="Lerp"/>/<see cref="Subtract"/>, i.e. its size is
        /// divisible by 4.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsComponentWise<TState>()
            where TState : unmanaged
        {
            int size = checked(sizeof(TState));
            return (size % ComponentSize) == 0;
        }
    }
}
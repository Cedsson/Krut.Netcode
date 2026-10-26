using System;

namespace Krut.Netcode
{
    /// <summary>Settings for how a correction should be shown, not computed (future-networking sec 7.6).</summary>
    public struct VisualSmoothingSettings
    {
        /// <summary>
        /// Errors in world units that should be not smoothed but teleported. Unit: the same world units as the
        /// game's state (tuned for a world where a player is about 64 units). Valid range: 0 or greater; 0 means
        /// "teleport every non-zero correction", i.e. <see cref="Disabled"/>.
        /// </summary>
        public float MinCorrectionDistance;

        /// <summary>Longest time a smoothing may take (seconds). Unit: seconds.</summary>
        public float MaxCorrectionSeconds;

        /// <summary>
        /// Shortest time a smoothing may take (seconds). Unit: seconds. Must not exceed
        /// <see cref="MaxCorrectionSeconds"/>; <see cref="Clamp"/> enforces that.
        /// </summary>
        public float MinCorrectionSeconds;

        /// <summary>
        /// Should the smoothing ease out (decelerating, "ease out") instead of running in a straight line? True
        /// by default: a straight line ends with a sharp stop exactly when it arrives, and it is that stop that
        /// is visible.
        /// </summary>
        public bool UseEaseOut;

        /// <summary>Values that suit most games: tone down small errors, teleport large ones.</summary>
        public static VisualSmoothingSettings Default => new()
        {
            MinCorrectionDistance = 1.0f,
            MaxCorrectionSeconds = 0.15f,
            MinCorrectionSeconds = 0.05f,
            UseEaseOut = true,
        };

        /// <summary>Smoothing disabled: every correction goes straight through to the renderer.</summary>
        public static VisualSmoothingSettings Disabled => new()
        {
            MinCorrectionDistance = 0f,
            MaxCorrectionSeconds = 0f,
            MinCorrectionSeconds = 0f,
            UseEaseOut = false,
        };

        /// <summary>
        /// Clamps the values to valid intervals (negative times, or a floor above the ceiling, would be
        /// nonsense).
        /// </summary>
        public void Clamp()
        {
            if (float.IsNaN(MinCorrectionDistance) || MinCorrectionDistance < 0f)
                MinCorrectionDistance = 0f;
            if (float.IsNaN(MaxCorrectionSeconds) || MaxCorrectionSeconds < 0f)
                MaxCorrectionSeconds = 0f;
            if (float.IsNaN(MinCorrectionSeconds) || MinCorrectionSeconds < 0f)
                MinCorrectionSeconds = 0f;
            if (MinCorrectionSeconds > MaxCorrectionSeconds)
                MinCorrectionSeconds = MaxCorrectionSeconds;
        }
    }

    /// <summary>One client input together with the state that was in effect before it ran.</summary>
    public struct ReconcileHistoryEntry<TState, TInput>
        where TState : unmanaged
        where TInput : unmanaged
    {
        /// <summary>Builds a history entry.</summary>
        /// <param name="tick">The tick the input applies to. Unit: ticks.</param>
        public ReconcileHistoryEntry(int tick, in TState stateBeforeInput, in TInput input)
        {
            Tick = tick;
            StateBeforeInput = stateBeforeInput;
            Input = input;
        }

        /// <summary>The tick the input applies to.</summary>
        public int Tick { get; set; }

        /// <summary>The state before the input ran - the point the client rolls back to.</summary>
        public TState StateBeforeInput { get; set; }

        /// <summary>The input that was sent (needed to re-run exactly the same step).</summary>
        public TInput Input { get; set; }
    }

    /// <summary>
    /// Reconciliation for the player's <b>own</b> unit (future-networking sec 7.1):
    /// the server's snapshot is the truth, the client rewinds to the most recent
    /// acknowledged input and re-runs everything that came after it.
    /// <para>
    /// <b>What a tick looks like in a game:</b>
    /// </para>
    /// <list type="number">
    /// <item>Read the input.</item>
    /// <item><c>RecordState(tick, currentState)</c> - save the state <i>before</i>
    /// the input runs, and <c>RecordInput(tick, input)</c>. That is everything
    /// needed to be able to redo the step later.</item>
    /// <item>Run the input against the own physics and set the result as
    /// <see cref="PredictedState"/>. (To avoid writing it twice there is
    /// <see cref="Predict"/>, which does 2 and 3 with the same function - and
    /// that function is also the one used for the rewind, which is the whole
    /// point of <b>purity</b> below.)</item>
    /// <item>Send the input (with the tick).</item>
    /// <item>When the server's reply arrives: <c>SetAuthoritativeState(...)</c> and
    /// <c>Reconcile(serverTick, lastProcessedInputTick, replay)</c>.</item>
    /// <item>Render <see cref="SmoothedState"/>, simulate physics against
    /// <see cref="PredictedState"/>.</item>
    /// </list>
    /// <para>
    /// <b>Why rewind at all, when the client already guessed right?</b> The guess
    /// goes wrong for two reasons: the server has a collision the client didn't
    /// see, and the server saw the same thing <i>but in a different order</i>
    /// (floating-point addition isn't associative, so a one-tick difference in
    /// execution order gives a slightly different result). Without rollback the
    /// error becomes permanent and grows; with rollback it gets corrected, and
    /// the longer the player runs without an answer the further back the client
    /// has to roll.
    /// </para>
    /// <para>
    /// <b>Purity is a requirement, not a recommendation.</b> Re-running an input
    /// must give <i>bit for bit</i> the same result as the first time, or every
    /// correction drags in a new deviation - and the client ends up in a loop of
    /// corrections that the player's motion can never escape. That is why both the
    /// normal update and the replay run the <i>same</i> function, and why the
    /// class stores the inputs, not just the results. Run the game's own physics
    /// step; don't take a "finished" position from some other code path.
    /// </para>
    /// <para>
    /// The class is standalone from <see cref="NetworkSession"/> and knows nothing
    /// about messages, channels or peers: it is a tick-stamped memory buffer plus
    /// a rollback. A game can therefore run several (e.g. one per own car in a
    /// split-screen mode), and a test can run one without any network at all.
    /// </para>
    /// <para>
    /// <b>What the history window costs, and when it overflows.</b> The memory
    /// is <c>capacity x sizeof(ReconcileHistoryEntry&lt;TState, TInput&gt;) +
    /// capacity x sizeof(TInput)</c> - the ring plus the replay scratch buffer,
    /// both allocated once in the constructor. Concretely, with a 12-float state
    /// (48 bytes) and a 10-byte <see cref="NetworkInputCommand"/>: the entry is
    /// <c>4 (tick) + 48 (state) + 10 (input)</c>, padded to a multiple of 4 =
    /// 64 bytes, so 128 entries is 8 kB plus 128 x 10 = 1.25 kB of scratch, about
    /// 9 kB per own unit. A split-screen game with four cars pays 36 kB, which is
    /// nothing next to the physics.
    /// </para>
    /// <para>
    /// <b>When it overflows.</b> The window holds <c>min(capacity, A)</c>
    /// entries, where A is how many ticks the server takes to acknowledge an
    /// input: one round trip plus the snapshot interval. At 60 Hz with 128 entries
    /// that means the oldest input starts being dropped only after
    /// 128 / 60 = 2.1 s of complete server silence; at 30 Hz it is
    /// 128 / 30 = 4.3 s. That is the real cost of the capacity choice: it is a
    /// promise that a client may run 2-4 seconds ahead of the server without
    /// losing the ability to rewind.
    /// </para>
    /// <para>
    /// <b>What an overflow looks like.</b> <see cref="IsInputBufferFull"/> is true
    /// and <see cref="DroppedInputCount"/> climbs - the client is running so far
    /// ahead that the server's next acknowledgement names a tick the client has
    /// already discarded, which lands in the rollback-miss branch of
    /// <see cref="Reconcile"/>: the authoritative state is adopted wholesale and
    /// the history is emptied. That is a silent failure by design (the client
    /// snaps once and carries on), which is why both counters are public - a
    /// climbing <see cref="DroppedInputCount"/> in a debug overlay is the only
    /// early warning.
    /// </para>
    /// <para>
    /// <b>What a client does with the server state it receives.</b> The decoded
    /// snapshot goes through three steps and nothing else:
    /// <list type="number">
    /// <item>For other units, <see cref="NetworkSnapshotInterpolator{TState}.Push"/>
    /// it - the interpolator decides when and whether it is ever displayed.</item>
    /// <item>For its own unit, <see cref="SetAuthoritativeState"/> it. That is a
    /// pure store: nothing moves yet, and a duplicate or late snapshot is dropped
    /// right here.</item>
    /// <item>Then <see cref="Reconcile"/> it, which rolls back and re-runs, and
    /// finally hands the result to the correction step - the only place that
    /// touches the predicted state.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Nothing that comes out of this class may be sent to anybody.</b> The
    /// predicted and smoothed states are the local player's guess and its visual
    /// smoothing; the authoritative state is the server's and belongs to the
    /// server. Forwarding any of them would make a client a second authority, and
    /// extrapolated state in particular (see
    /// <c>NetworkSnapshotInterpolator.TryExtrapolate</c> and
    /// docs/guide.md section 6) must never be forwarded to another peer:
    /// it is a guess about the future, so relaying it turns a 3-tick
    /// extrapolation error into a 6-tick one, and eventually into a car that
    /// accelerates straight into a wall for everyone downstream.
    /// </para>
    /// <para>
    /// <b>Note on the signature:</b> the design called for
    /// <c>NetworkReconciler&lt;TState&gt;</c> with
    /// <c>RecordInput(int tick, in TInput input) where TInput : unmanaged</c>. That
    /// doesn't compile - a method cannot introduce a new type parameter in its own
    /// where-clause. The class is therefore
    /// <c>NetworkReconciler&lt;TState, TInput&gt;</c>, which is the only
    /// difference.
    /// </para>
    /// </summary>
    /// <typeparam name="TState">The game's state (unmanaged; see <see cref="NetworkStateBlend"/> for the blending).</typeparam>
    /// <typeparam name="TInput">The game's input struct (unmanaged, so that it can live in a struct without boxing).</typeparam>
    public sealed class NetworkReconciler<TState, TInput>
        where TState : unmanaged
        where TInput : unmanaged
    {
        /// <summary>Default capacity for the input history. 128 ticks holds 2-4 seconds at 30-60 Hz.</summary>
        public const int DefaultInputHistoryCapacity = 128;

        /// <summary>The ring of unacknowledged inputs; strictly ascending ticks, index 0 = oldest.</summary>
        private readonly NetworkStateRing<ReconcileHistoryEntry<TState, TInput>> _history;

        /// <summary>Reusable array so a correction doesn't allocate (see the NetworkSession class for why).</summary>
        private readonly TInput[] _replayScratch;

        /// <summary>The corrected physics state - what <see cref="PredictedState"/> returns.</summary>
        private TState _predicted;

        /// <summary>The state handed to the renderer - what <see cref="SmoothedState"/> returns.</summary>
        private TState _rendered;

        /// <summary>The server's authoritative state as of <see cref="AuthoritativeTick"/>.</summary>
        private TState _authoritative;

        /// <summary>Whether at least one authoritative state has been adopted; false until the first server packet.</summary>
        private bool _hasState;

        /// <summary>Highest server tick already handled - the guard against duplicates and reordered delivery.</summary>
        private int _lastReconciledServerTick = -1;

        /// <summary>What was shown when the current correction arrived.</summary>
        private TState _smoothFrom;

        /// <summary>The corrected physics state at that moment. <c>_smoothFrom - _smoothTo</c> is the visible error that decays.</summary>
        private TState _smoothTo;

        /// <summary>Seconds the current smoothing has been running; never exceeds <see cref="_smoothDuration"/>.</summary>
        private float _smoothElapsed;

        /// <summary>Total length of the current smoothing in seconds; 0 means "no smoothing in progress".</summary>
        private float _smoothDuration;

        /// <param name="capacity">
        /// How many unacknowledged inputs may remain before the oldest one is dropped. See
        /// <see cref="InputHistoryCapacity"/> for the memory cost.
        /// </param>
        public NetworkReconciler(VisualSmoothingSettings smoothing, int capacity = DefaultInputHistoryCapacity)
        {
            Smoothing = smoothing;
            Smoothing.Clamp();
            _history = new NetworkStateRing<ReconcileHistoryEntry<TState, TInput>>(
                Math.Max(2, capacity),
                static entry => entry.Tick);
            _replayScratch = new TInput[_history.Capacity];
        }

        /// <summary>The settings in effect (copied in the ctor - change via <see cref="Smoothing"/> if you want to change it during a match).</summary>
        public VisualSmoothingSettings Smoothing { get; set; }

        /// <summary>The server's authoritative state for <see cref="AuthoritativeTick"/> - the truth.</summary>
        public TState AuthoritativeState => _authoritative;

        /// <summary>The tick <see cref="AuthoritativeState"/> applies to, or -1 if nothing has been set.</summary>
        public int AuthoritativeTick { get; private set; } = -1;

        /// <summary>The corrected physics state: the server's truth plus the client's re-run inputs.</summary>
        public TState PredictedState => _predicted;

        /// <summary>
        /// The state that should be rendered: either all the way <see cref="PredictedState"/> (when no
        /// smoothing is in progress) or a point in between during a smoothing.
        /// </summary>
        public TState SmoothedState => _rendered;

        /// <summary>Same as <see cref="SmoothedState"/>, with the name a render layer more often looks for.</summary>
        public TState RenderState => _rendered;

        /// <summary>Number of inputs that have been sent but not acknowledged by the server. Unit: ticks of unacknowledged input.</summary>
        public int UnacknowledgedInputCount => _history.Count;

        /// <summary>
        /// True when the history is full, i.e. the server has acknowledged no inputs for
        /// <see cref="InputHistoryCapacity"/> ticks.
        /// </summary>
        public bool IsInputBufferFull => _history.Count >= _history.Capacity;

        /// <summary>How many inputs may remain unacknowledged (decided in the ctor). Unit: ticks.</summary>
        public int InputHistoryCapacity => _history.Capacity;

        /// <summary>How many inputs are in the array during an ongoing <see cref="Reconcile"/>.</summary>
        public int PendingReplayCount { get; private set; }

        /// <summary>
        /// Number of saved inputs with a tick greater than <paramref name="tick"/> - the same thing as
        /// <see cref="PendingReplayCount"/> for a given acknowledged tick.
        /// </summary>
        /// <param name="tick">The tick to count from, exclusive. Unit: ticks.</param>
        public int CountInputsAfter(int tick)
        {
            int index = _history.IndexOfTickAtOrBefore(tick);
            return index < 0 ? _history.Count : _history.Count - index - 1;
        }

        /// <summary>Number of inputs dropped because the buffer was full (see <see cref="IsInputBufferFull"/>).</summary>
        public int DroppedInputCount { get; private set; }

        /// <summary>Number of times the server's reply arrived but <see cref="Reconcile"/> could not roll back to the given tick.</summary>
        public int RollbackMissCount { get; private set; }

        /// <summary>Number of <see cref="Reconcile"/> calls that actually rolled back and re-ran inputs.</summary>
        public int ReconcileCount { get; private set; }

        /// <summary>Sum of all re-run inputs - a measure of how much work the corrections cost.</summary>
        public int TotalReplayedInputs { get; private set; }

        /// <summary>Number of server replies that were ignored (duplicates or old snapshots - see <see cref="Reconcile"/>).</summary>
        public int IgnoredServerStateCount { get; private set; }

        /// <summary>The size of the last correction in <see cref="DistanceMetric"/> units (see <see cref="LastCorrectionDistance"/>).</summary>
        public float LastCorrectionDistance { get; private set; }

        /// <summary>The length of the last correction (seconds) - 0 if it was teleported.</summary>
        public float LastCorrectionSeconds { get; private set; }

        /// <summary>The last correction was so large that it was teleported instead of slid.</summary>
        public bool LastCorrectionWasTeleport { get; private set; }

        /// <summary>Is a visual smoothing in progress right now? (The counter for a "jittering" indicator in the debug overlay.)</summary>
        public bool IsSmoothing => _smoothDuration > 0f && _smoothElapsed < _smoothDuration;

        /// <summary>Which tick (index 0 = oldest) in the history - for a game that wants to inspect or clear it manually.</summary>
        public int OldestPendingTick => _history.IsEmpty ? -1 : _history[0].Tick;

        /// <summary>Which tick the history extends to - the tick that was sent last.</summary>
        public int NewestPendingTick => _history.IsEmpty ? -1 : _history[_history.Count - 1].Tick;

        /// <summary>
        /// How the distance between two states is measured, to decide whether a correction should be slid or
        /// teleported. The default reads the first three components as a position
        /// (<see cref="NetworkStateBlend.PositionDistance{TState}"/>), which fits the library's usual layout
        /// (position first, then rotation/velocity).
        /// </summary>
        public Func<TState, TState, float> DistanceMetric { get; set; } = NetworkStateBlend.PositionDistance<TState>;

        /// <summary>
        /// How a visual correction is laid on top of the running prediction: <c>(predicted, shownAtCorrection,
        /// correctedAtCorrection, remaining) -&gt; rendered</c>, where <c>remaining</c> falls from 1 to 0 over
        /// the smoothing. The rendered state is the CURRENT prediction shifted by the visible error at the
        /// moment of the correction, and that shift decays - it does not slide towards the corrected state as
        /// it was at that tick. The default (<see cref="NetworkStateBlend.Offset{TState}"/>) shifts every
        /// 4-byte component as a float. A game whose state holds integers (a gear, a contact flag) or
        /// quaternions sets its own function that shifts only the visible components - interpreting an
        /// integer's bits as a float gives garbage.
        /// </summary>
        public Func<TState, TState, TState, float, TState> RenderOffset { get; set; } = NetworkStateBlend.Offset<TState>;

        /// <summary>
        /// The function that runs one input against one state. Used by both <see cref="Predict"/> and
        /// <see cref="Reconcile"/> - it is the purity requirement that makes a re-run input give the same
        /// answer the second time (see the class summary). Only set if the game wants to use
        /// <see cref="Predict"/>; a game that would rather do <c>RecordState</c>/<c>RecordInput</c>/the step
        /// itself can leave it null.
        /// </summary>
        public Func<TState, int, TInput, TState>? Step { get; set; }

        /// <summary>
        /// Sets the server's authoritative state. UDP doesn't order packets, so a delayed server snapshot isn't
        /// an error, but it must not roll the world backwards (compare <see cref="IgnoredServerStateCount"/>).
        /// </summary>
        /// <param name="serverTick">The tick the state applies to. Unit: ticks.</param>
        /// <returns>
        /// True if the state was adopted, false if it was older than or equal to
        /// <see cref="AuthoritativeTick"/>.
        /// </returns>
        public bool SetAuthoritativeState(int serverTick, in TState state)
        {
            if (serverTick <= AuthoritativeTick)
            {
                IgnoredServerStateCount++;
                return false;
            }

            _authoritative = state;
            AuthoritativeTick = serverTick;
            return true;
        }

        /// <summary>
        /// Saves the client's state before the input for <paramref name="tick"/> runs. Must be called for every
        /// tick before <see cref="RecordInput"/> - otherwise there is no starting point to roll back to. Ticks
        /// must be recorded in ascending order, one entry per tick.
        /// </summary>
        /// <param name="tick">The tick about to be simulated. Unit: ticks.</param>
        public void RecordState(int tick, in TState state)
        {
            // If an entry already exists for the tick it is replaced: a tick runs once, but a game that updates
            // several times per frame can easily do it, and the latest version is the real one.
            int index = _history.IndexOfTickAtOrBefore(tick);
            if (index >= 0 && _history[index].Tick == tick)
            {
                var existing = _history[index];
                existing.StateBeforeInput = state;
                _history.Set(index, existing);
                return;
            }

            if (_history.IsEmpty || tick > _history[_history.Count - 1].Tick)
            {
                bool overwrote = _history.PushOrOverwrite(tick, new ReconcileHistoryEntry<TState, TInput>(tick, state, default));
                if (overwrote)
                    DroppedInputCount++;
                return;
            }

            // Ticks that are out of order can't be inserted into a ring without the history ceasing to be
            // strictly ascending, and then a rollback is no longer unambiguous.
            throw new InvalidOperationException(
                $"Tick {tick} cannot be recorded: the history spans {OldestPendingTick}-{NewestPendingTick}. " +
                "Call RecordState in tick order, once per tick.");
        }

        /// <summary>Registers an input that has been sent.</summary>
        /// <param name="tick">
        /// The tick the input applies to. Must match a <see cref="RecordState"/> call for the same tick.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// No state was recorded for <paramref name="tick"/>, so the step could never be reproduced.
        /// </exception>
        public int RecordInput(int tick, in TInput input)
        {
            int index = _history.IndexOfTickAtOrBefore(tick);
            if (index < 0 || _history[index].Tick != tick)
            {
                throw new InvalidOperationException(
                    $"Input for tick {tick} cannot be registered: no state recorded for that tick. " +
                    "Call RecordState before every input.");
            }

            var entry = _history[index];
            entry.Input = input;
            _history.Set(index, entry);
            return _history.Count;
        }

        /// <summary>
        /// Saves state + input and runs the step - i.e. the client's own prediction for a tick. Sets
        /// <see cref="PredictedState"/> (and thereby also <see cref="SmoothedState"/>, if no smoothing is in
        /// progress). Having this as a ready-made method - instead of asking the game to do <c>RecordState</c>
        /// + step + <c>RecordInput</c> itself - is not just convenient: it guarantees that <see cref="Step"/>
        /// is exactly the same function that is used during the replay, which is what stops a correction from
        /// creating the next deviation. The first time around, <see cref="PredictedState"/> must have a value
        /// to start from - call <see cref="Reset"/> (e.g. when a snapshot has arrived), otherwise it is a zero
        /// struct and the first prediction starts at the origin.
        /// </summary>
        /// <param name="tick">
        /// The tick being predicted. Unit: ticks; must be greater than every previously predicted tick.
        /// </param>
        /// <returns>The new predicted state.</returns>
        /// <exception cref="InvalidOperationException">
        /// <see cref="Step"/> has not been set. Call <see cref="SetAuthoritativeState"/> and
        /// <see cref="Reset"/> (or adopt a first snapshot) before the first prediction, so there is a state to
        /// start from.
        /// </exception>
        public TState Predict(int tick, in TInput input)
        {
            var step = Step ?? throw new InvalidOperationException(
                "Step must be set before Predict can run (see the documentation on Step).");

            RecordState(tick, _predicted);
            RecordInput(tick, input);
            TState next = step(_predicted, tick, input);
            _predicted = next;
            _rendered = IsSmoothing ? RenderOffset(next, _smoothFrom, _smoothTo, RemainingShare()) : next;
            return next;
        }

        /// <summary>
        /// The server's reply arrived: roll back to
        /// <paramref name="lastProcessedInputTick"/> and re-run everything after
        /// it. Returns the number of inputs that were re-run (0 = nothing to do).
        /// <para>
        /// <paramref name="replay"/> gets the server's state, the tick the server
        /// last processed, and an array with <b>all</b> unacknowledged inputs in
        /// tick order - and must return the final state. The array belongs to the
        /// client (reused buffer, no allocation per correction) and must be read
        /// but not written to; the number of entries is exactly the number
        /// returned.
        /// </para>
        /// <para>
        /// <b>Three things are aborted here, for different reasons:</b>
        /// </para>
        /// <list type="bullet">
        /// <item><b>The server's tick isn't newer than the most recent one already
        /// handled</b> (a duplicate or reordered delivery, which UDP does
        /// sometimes). Then nothing is replayed at all. Without this, a client
        /// that receives the same old snapshot repeatedly (a network that repeats
        /// packets, a hostile client, or a server that has hung) replays its
        /// whole history a thousand times a second - expensive, and the result
        /// gets worse with every replay.</item>
        /// <item><b>No new authoritative statement has been set</b> for this
        /// particular tick (<c>serverTick != AuthoritativeTick</c>). Without that
        /// check a call without a preceding
        /// <see cref="SetAuthoritativeState"/> would replay everything
        /// <i>again</i> against the same old truth, which is the bug above but
        /// worse.</item>
        /// <item><b>The rollback target is older than our oldest saved
        /// state</b>. Then there is no way to do it right, so the server's state
        /// is accepted, the history is emptied and
        /// <see cref="RollbackMissCount"/> is counted up. Keeping the history
        /// would have given a state that is a blend of two worlds.</item>
        /// </list>
        /// <para>
        /// <paramref name="serverTick"/> is only used to recognize duplicates and
        /// old snapshots - the server's <i>value</i> comes from
        /// <see cref="SetAuthoritativeState"/>, because C# doesn't allow an out
        /// parameter in the same call as a function type.
        /// </para>
        /// </summary>
        /// <param name="serverTick">The tick of the server state that was just set. Unit: ticks; must be greater than every previous value passed here.</param>
        /// <param name="lastProcessedInputTick">The highest input tick the server has consumed. Everything after it is re-run. Unit: ticks.</param>
        /// <param name="replay">Runs the re-run inputs. Must be pure and deterministic; see the class summary.</param>
        /// <returns>How many inputs were re-run. 0 means the authoritative state was adopted as is, and the three cases are told apart like this:
        /// <list type="bullet">
        /// <item><b>The very first server state</b> - there was nothing to compare
        /// against, so the world is simply adopted and the history is cleared.
        /// Nothing is wrong; the caller cannot tell this case apart from the
        /// others by the return value alone, which is why
        /// <see cref="ReconcileCount"/> and <see cref="RollbackMissCount"/> stay
        /// at 0.</item>
        /// <item><b>Duplicate, reordered or unsolicited</b> - counted in
        /// <see cref="IgnoredServerStateCount"/>, which is where a debug overlay
        /// should point at reordering.</item>
        /// <item><b>A genuine rollback miss</b> - counted in
        /// <see cref="RollbackMissCount"/>; the client snaps once and keeps
        /// going. See the class summary for when that happens.</item>
        /// </list>
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="replay"/> is null.</exception>
        public int Reconcile(int serverTick, int lastProcessedInputTick, Func<TState, int, TInput[], TState> replay)
        {
            if (replay is null)
                throw new ArgumentNullException(nameof(replay));

            if (serverTick <= _lastReconciledServerTick || serverTick != AuthoritativeTick)
            {
                IgnoredServerStateCount++;
                return 0;
            }

            // Set before the work, so that even a correction aborted by an error
            // can't be run again by a repeated delivery of the same packet.
            _lastReconciledServerTick = serverTick;
            return ReconcileCore(lastProcessedInputTick, replay);
        }

        /// <summary>Roll back and replay - see <see cref="Reconcile"/>, which owns the validation.</summary>
        private int ReconcileCore(int lastProcessedInputTick, Func<TState, int, TInput[], TState> replay)
        {
            if (!_hasState)
            {
                // The first server packet: there is nothing to compare against,
                // just take the world.
                _predicted = _authoritative;
                _rendered = _authoritative;
                _hasState = true;
                _history.Clear();
                return 0;
            }

            // Everything acknowledged? Then the server's state is the whole truth.
            if (_history.IsEmpty || lastProcessedInputTick >= NewestPendingTick)
            {
                _history.Clear();
                ApplyCorrection(_authoritative);
                return 0;
            }

            int target = _history.IndexOfTickAtOrBefore(lastProcessedInputTick);
            if (target < 0 || _history[target].Tick != lastProcessedInputTick)
            {
                // The server skipped past everything we have saved (we were far ahead and already had to drop
                // inputs, or the server's lastProcessedInputTick isn't a tick we ever sent).
                RollbackMissCount++;
                _history.Clear();
                ApplyCorrection(_authoritative);
                return 0;
            }

            // Collect every unacknowledged input after the server's last processed tick, in ascending tick
            // order.
            int count = _history.Count - target - 1;
            if (count > _replayScratch.Length)
                count = _replayScratch.Length;

            for (int i = 0; i < count; i++)
                _replayScratch[i] = _history[target + 1 + i].Input;

            TState corrected;
            PendingReplayCount = count;
            try
            {
                corrected = replay(_authoritative, lastProcessedInputTick, _replayScratch);
            }
            finally
            {
                PendingReplayCount = 0;
            }

            // Acknowledged inputs aren't needed any more - and dropping them right
            // away is what keeps the buffer's memory bounded.
            _history.RemoveAtOrBefore(lastProcessedInputTick);

            ApplyCorrection(corrected);

            ReconcileCount++;
            TotalReplayedInputs += count;
            return count;
        }

        /// <summary>Moves the visual smoothing one frame forward.</summary>
        /// <param name="deltaSeconds">
        /// Seconds since the previous frame, normally the game's frame delta. Unit: seconds; the value is not
        /// clamped, so a huge frame (a loading screen) simply completes the smoothing.
        /// </param>
        public void UpdateRenderState(float deltaSeconds)
        {
            if (!IsSmoothing)
                return;

            _smoothElapsed += deltaSeconds;
            if (_smoothElapsed >= _smoothDuration)
            {
                // Exactly on the prediction, not almost - otherwise the renderer is left with an unrounded
                // deviation that never becomes zero and that the next correction then looks like a threshold
                // crossing.
                _rendered = _predicted;
                _smoothElapsed = _smoothDuration;
                return;
            }

            _rendered = RenderOffset(_predicted, _smoothFrom, _smoothTo, RemainingShare());
        }

        /// <summary>
        /// How much of the visible error is still shown, 1 at the correction and 0 when the smoothing is done.
        /// </summary>
        private float RemainingShare()
        {
            float t = _smoothDuration <= 0f ? 1f : Math.Clamp(_smoothElapsed / _smoothDuration, 0f, 1f);
            if (Smoothing.UseEaseOut)
                t = 1f - ((1f - t) * (1f - t) * (1f - t));
            return 1f - t;
        }

        /// <summary>Forces the renderer to abandon an ongoing smoothing and snap to the physics state.</summary>
        public void SnapRenderStateToPrediction()
        {
            _smoothDuration = 0f;
            _smoothElapsed = 0f;
            _rendered = _predicted;
        }

        /// <summary>
        /// Empties the history and starts over from the server's state (new match, respawn, teleport).
        /// </summary>
        /// <param name="serverTick">
        /// The server's tick to adopt. Unit: ticks; no ordering check is made against the previous value.
        /// </param>
        /// <param name="state">
        /// The server's state to adopt. Copied into the predicted, rendered and authoritative slots.
        /// </param>
        public void Reset(int serverTick, in TState state)
        {
            _history.Clear();
            DroppedInputCount = 0;
            RollbackMissCount = 0;
            _authoritative = state;
            AuthoritativeTick = serverTick;
            _lastReconciledServerTick = serverTick;
            _predicted = state;
            _rendered = state;
            _hasState = true;
            _smoothDuration = 0f;
            _smoothElapsed = 0f;
        }

        /// <summary>Number of entries in the input history (for a test or a debug overlay).</summary>
        public int HistoryCount => _history.Count;

        /// <summary>The input saved for <paramref name="tick"/>, or false if it is no longer there.</summary>
        /// <param name="tick">The tick to look up. Unit: ticks.</param>
        public bool TryGetRecordedInput(int tick, out TInput input)
        {
            int index = _history.IndexOfTickAtOrBefore(tick);
            if (index < 0 || _history[index].Tick != tick)
            {
                input = default;
                return false;
            }

            input = _history[index].Input;
            return true;
        }

        /// <summary>
        /// The state saved before the input for <paramref name="tick"/> - useful in a rollback test.
        /// </summary>
        /// <param name="tick">The tick to look up. Unit: ticks.</param>
        public bool TryGetRecordedState(int tick, out TState state)
        {
            int index = _history.IndexOfTickAtOrBefore(tick);
            if (index < 0 || _history[index].Tick != tick)
            {
                state = default;
                return false;
            }

            state = _history[index].StateBeforeInput;
            return true;
        }

        /// <summary>Takes a new corrected state: the physics immediately, the renderer smoothed.</summary>
        private void ApplyCorrection(TState corrected)
        {
            // An acknowledgement that does not move the physics, during a smoothing: the smoothing in progress
            // is still right and keeps running. Restarting it from the rendered state would stretch the decay
            // with every snapshot - at 30 snapshots a second the visible error would never finish fading.
            if (IsSmoothing && _hasState && DistanceMetric(_predicted, corrected) <= 0f)
            {
                _predicted = corrected;
                LastCorrectionDistance = 0f;
                LastCorrectionWasTeleport = false;
                LastCorrectionSeconds = 0f;
                _rendered = RenderOffset(corrected, _smoothFrom, _smoothTo, RemainingShare());
                return;
            }

            // The distance is measured against what is actually being shown right now - not against the old
            // physics state.
            float distance = DistanceMetric(_rendered, corrected);
            _predicted = corrected;
            _hasState = true;

            if (distance >= Smoothing.MinCorrectionDistance || Smoothing.MaxCorrectionSeconds <= 0f)
            {
                // Over-correction.
                LastCorrectionDistance = distance;
                LastCorrectionWasTeleport = true;
                LastCorrectionSeconds = 0f;
                _smoothDuration = 0f;
                _smoothElapsed = 0f;
                _rendered = corrected;
                return;
            }

            if (distance <= 0f)
            {
                // No error then - just an acknowledgement. The rendering must land exactly in the physics state
                // at once, or an unrounded difference is left over that the next correction can look like a
                // threshold crossing.
                LastCorrectionDistance = 0f;
                LastCorrectionWasTeleport = false;
                LastCorrectionSeconds = 0f;
                _smoothDuration = 0f;
                _smoothElapsed = 0f;
                _rendered = corrected;
                return;
            }

            // Below the threshold: slide.
            float scale = Smoothing.MinCorrectionDistance <= 0f ? 1f : distance / Smoothing.MinCorrectionDistance;
            float duration = Math.Clamp(
                Smoothing.MaxCorrectionSeconds * scale,
                Smoothing.MinCorrectionSeconds,
                Smoothing.MaxCorrectionSeconds);

            // The starting point is the state rendered SO FAR, not the old physics state: two corrections in a
            // row should be able to change direction, not jump back and forth between each other.
            LastCorrectionDistance = distance;
            LastCorrectionWasTeleport = false;
            LastCorrectionSeconds = duration;
            _smoothFrom = _rendered;
            _smoothTo = corrected;
            _smoothElapsed = 0f;
            _smoothDuration = duration;
        }
    }
}
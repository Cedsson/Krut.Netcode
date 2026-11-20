namespace Krut.Netcode
{
    /// <summary>
    /// Fixed 64-packet sliding anti-replay window for strictly positive monotonic packet counters.
    /// Zero allocations; safe for out-of-order UDP delivery within 64 packets of the highest seen counter.
    /// </summary>
    internal struct AntiReplayWindow
    {
        private ulong _highest;
        private ulong _mask;

        /// <summary>
        /// Returns true if <paramref name="counter"/> is invalid (0), already recorded, or older than the 64-packet window.
        /// Call before decryption/mutation so unauthenticated packets cannot advance or poison the window.
        /// </summary>
        public readonly bool IsReplay(ulong counter)
        {
            if (counter == 0)
                return true;

            if (_highest == 0 || counter > _highest)
                return false;

            ulong diff = _highest - counter;
            if (diff >= 64)
                return true;

            return (_mask & (1UL << (int)diff)) != 0;
        }

        /// <summary>Records an authenticated <paramref name="counter"/> into the sliding window.</summary>
        public void Record(ulong counter)
        {
            if (counter == 0)
                return;

            if (_highest == 0)
            {
                _highest = counter;
                _mask = 1UL;
                return;
            }

            if (counter > _highest)
            {
                ulong diff = counter - _highest;
                _mask = diff >= 64 ? 1UL : (_mask << (int)diff) | 1UL;
                _highest = counter;
                return;
            }

            ulong offset = _highest - counter;
            if (offset < 64)
                _mask |= 1UL << (int)offset;
        }

        public void Reset()
        {
            _highest = 0;
            _mask = 0;
        }
    }
}

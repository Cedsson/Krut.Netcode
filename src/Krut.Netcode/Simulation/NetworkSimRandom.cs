using System;

namespace Krut.Netcode
{
    /// <summary>
    /// Small seeded PRNG (splitmix64 seeding, xoshiro256**) owned by one link. It exists so that a simulated link
    /// never shares state with System.Random and the same seed gives the same draws on every platform.
    /// </summary>
    internal sealed class NetworkSimRandom
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        public NetworkSimRandom(ulong seed)
        {
            _s0 = SplitMix(ref seed);
            _s1 = SplitMix(ref seed);
            _s2 = SplitMix(ref seed);
            _s3 = SplitMix(ref seed);
        }

        /// <summary>Mixes two values into one seed (used to derive a per-link stream from the simulation seed).</summary>
        public static ulong Mix(ulong seed, ulong salt)
        {
            ulong state = seed ^ (salt * 0x9E3779B97F4A7C15UL);
            return SplitMix(ref state);
        }

        public ulong NextUInt64()
        {
            ulong result = Rotl(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

        private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        private static ulong SplitMix(ref ulong state)
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

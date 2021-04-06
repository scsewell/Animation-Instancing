#ifndef ANIMATION_INSTANCING_SCAN
#define ANIMATION_INSTANCING_SCAN

#define REDUCE(data, count, threadID) {                     \
    int offset = 1;                                         \
    for (uint d = count >> 1; d > 0; d >>= 1)               \
    {                                                       \
        GroupMemoryBarrierWithGroupSync();                  \
                                                            \
        if (threadID < d)                                   \
        {                                                   \
            int ai = offset * int(2 * threadID + 1) - 1;    \
            int bi = offset * int(2 * threadID + 2) - 1;    \
            data[bi] += data[ai];                           \
        }                                                   \
                                                            \
        offset <<= 1;                                       \
    }                                                       \
}

#define PREFIX_SUM(data, count, threadID) {                 \
    int offset = 1;                                         \
    for (uint d = count >> 1; d > 0; d >>= 1)               \
    {                                                       \
        GroupMemoryBarrierWithGroupSync();                  \
                                                            \
        if (threadID < d)                                   \
        {                                                   \
            int ai = offset * int(2 * threadID + 1) - 1;    \
            int bi = offset * int(2 * threadID + 2) - 1;    \
            data[bi] += data[ai];                           \
        }                                                   \
                                                            \
        offset <<= 1;                                       \
    }                                                       \
                                                            \
    GroupMemoryBarrierWithGroupSync();                      \
                                                            \
    if (threadID == count - 1)                              \
    {                                                       \
        data[threadID] = 0;                                 \
    }                                                       \
                                                            \
    for (d = 1; d < count; d <<= 1)                         \
    {                                                       \
        offset >>= 1;                                       \
                                                            \
        GroupMemoryBarrierWithGroupSync();                  \
                                                            \
        if (threadID < d)                                   \
        {                                                   \
            int ai = offset * int(2 * threadID + 1) - 1;    \
            int bi = offset * int(2 * threadID + 2) - 1;    \
            uint t = data[ai];                              \
            data[ai] = data[bi];                            \
            data[bi] += t;                                  \
        }                                                   \
    }                                                       \
}

#endif // ANIMATION_INSTANCING_SCAN

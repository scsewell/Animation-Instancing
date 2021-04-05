// Copyright (c) 2020 Advanced Micro Devices, Inc. All rights reserved.
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#pragma kernel Count
#pragma kernel CountReduce
#pragma kernel Scan
#pragma kernel ScanAdd
#pragma kernel Scatter

#include "HLSLSupport.cginc"

#if defined(DXC_COMPILER)
#define ENABLE_WAVE_INTRINSICS
#endif

#define THREAD_GROUP_SIZE       128
#define ELEMENTS_PER_THREAD     4
#define BITS_PER_PASS           4
#define BIN_COUNT               (1 << BITS_PER_PASS)


CBUFFER_START(SortingPropertyBuffer)
uint _NumKeys;
int  _NumBlocksPerThreadGroup;
uint _NumThreadGroups;
uint _NumThreadGroupsWithAdditionalBlocks;
uint _NumReduceThreadGroupPerBin;
uint _NumScanValues;
CBUFFER_END

uint _ShiftBit;
RWStructuredBuffer<uint> _SrcBuffer;    // The unsorted keys or scan data
RWStructuredBuffer<uint> _DstBuffer;    // The sorted keys or prefixed data
RWStructuredBuffer<uint> _SumTable;     // The sum table we will write sums to
RWStructuredBuffer<uint> _ReduceTable;  // The reduced sum table we will write sums to
RWStructuredBuffer<uint> _Scan;         // The scan data
RWStructuredBuffer<uint> _ScanScratch;  // Scratch data for scan


groupshared uint gs_Histogram[THREAD_GROUP_SIZE * BIN_COUNT];

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void Count(uint localID : SV_GroupThreadID, uint groupID : SV_GroupID)
{
    // Start by clearing our local counts in LDS
    for (int i = 0; i < BIN_COUNT; i++)
    {
        gs_Histogram[(i * THREAD_GROUP_SIZE) + localID] = 0;
    }

    GroupMemoryBarrierWithGroupSync();

    // Data is processed in blocks, and how many we process can changed based on how much data we are processing
    // versus how many thread groups we are processing with
    int blockSize = ELEMENTS_PER_THREAD * THREAD_GROUP_SIZE;

    // Figure out this thread group's index into the block data (taking into account thread groups that need to do extra reads)
    uint threadgroupBlockStart = (blockSize * _NumBlocksPerThreadGroup * groupID);
    uint numBlocksToProcess = _NumBlocksPerThreadGroup;
    uint numThreadGroupsWithoutAdditionalBlocks = _NumThreadGroups - _NumThreadGroupsWithAdditionalBlocks;

    if (groupID >= numThreadGroupsWithoutAdditionalBlocks)
    {
        threadgroupBlockStart += (groupID - numThreadGroupsWithoutAdditionalBlocks) * blockSize;
        numBlocksToProcess++;
    }

    // Get the block start index for this thread
    uint blockIndex = threadgroupBlockStart + localID;

    // Count value occurrence
    for (uint blockCount = 0; blockCount < numBlocksToProcess; blockCount++, blockIndex += blockSize)
    {
        uint dataIndex = blockIndex;

        // Pre-load the key values in order to hide some of the read latency
        uint srcKeys[ELEMENTS_PER_THREAD];
        srcKeys[0] = _SrcBuffer[dataIndex];
        srcKeys[1] = _SrcBuffer[dataIndex + THREAD_GROUP_SIZE];
        srcKeys[2] = _SrcBuffer[dataIndex + (THREAD_GROUP_SIZE * 2)];
        srcKeys[3] = _SrcBuffer[dataIndex + (THREAD_GROUP_SIZE * 3)];

        for (uint i = 0; i < ELEMENTS_PER_THREAD; i++)
        {
            if (dataIndex < _NumKeys)
            {
                uint localKey = (srcKeys[i] >> _ShiftBit) & 0xf;
                InterlockedAdd(gs_Histogram[(localKey * THREAD_GROUP_SIZE) + localID], 1);
                dataIndex += THREAD_GROUP_SIZE;
            }
        }
    }

    // Even though our LDS layout guarantees no collisions, our thread group size is greater than a wave
    // so we need to make sure all thread groups are done counting before we start tallying up the results
    GroupMemoryBarrierWithGroupSync();

    if (localID < BIN_COUNT)
    {
        uint sum = 0;
        
        for (int i = 0; i < THREAD_GROUP_SIZE; i++)
        {
            sum += gs_Histogram[localID * THREAD_GROUP_SIZE + i];
        }
        
        _SumTable[localID * _NumThreadGroups + groupID] = sum;
    }
}

groupshared uint gs_LDSSums[THREAD_GROUP_SIZE];

uint ThreadgroupReduce(uint localSum, uint localID)
{
#if defined(ENABLE_WAVE_INTRINSICS)
    uint waveReduced = WaveActiveSum(localSum);

    // First lane in a wave writes out wave reduction to LDS (this accounts for num waves per group greater than HW wave size)
    // Note that some hardware with very small HW wave sizes (i.e. <= 8) may exhibit issues with this algorithm, and have not been tested.
    uint waveID = localID / WaveGetLaneCount();
    
    if (WaveIsFirstLane())
    {
        gs_LDSSums[waveID] = waveReduced;
    }

    GroupMemoryBarrierWithGroupSync();

    // First wave worth of threads sum up wave reductions
    if (!waveID)
    {
        waveReduced = WaveActiveSum((localID < (THREAD_GROUP_SIZE / WaveGetLaneCount())) ? gs_LDSSums[localID] : 0);
    }

    return waveReduced;
#else
    // load each value into group shared memory
    gs_LDSSums[localID] = localSum;

    // reduction
    int offset = 1;
    for (uint d = THREAD_GROUP_SIZE >> 1; d > 0; d >>= 1)
    {
        GroupMemoryBarrierWithGroupSync();

        if (localID < d)
        {
            int ai = offset * int(2 * localID + 1) - 1;
            int bi = offset * int(2 * localID + 2) - 1;
            gs_LDSSums[bi] += gs_LDSSums[ai];
        }

        offset <<= 1;
    }

    GroupMemoryBarrierWithGroupSync();

    // return the reduction
    return gs_LDSSums[THREAD_GROUP_SIZE - 1]; 
#endif
}

uint BlockScanPrefix(uint localSum, uint localID)
{
#if defined(ENABLE_WAVE_INTRINSICS)
    uint wavePrefixed = WavePrefixSum(localSum);

    // Since we are dealing with thread group sizes greater than HW wave size, we need to account for what wave we are in.
    uint waveID = localID / WaveGetLaneCount();
    uint laneID = WaveGetLaneIndex();

    // Last element in a wave writes out partial sum to LDS
    if (laneID == WaveGetLaneCount() - 1)
    {
        gs_LDSSums[waveID] = wavePrefixed + localSum;
    }
    
    GroupMemoryBarrierWithGroupSync();

    // First wave prefixes partial sums
    if (!waveID)
    {
        gs_LDSSums[localID] = WavePrefixSum(gs_LDSSums[localID]);
    }

    GroupMemoryBarrierWithGroupSync();

    // Add the partial sums back to each wave prefix
    wavePrefixed += gs_LDSSums[waveID];

    return wavePrefixed;
#else
    // load each value into group shared memory
    gs_LDSSums[localID] = localSum;
    
    // reduction
    int offset = 1;
    for (uint d = THREAD_GROUP_SIZE >> 1; d > 0; d >>= 1)
    {
        GroupMemoryBarrierWithGroupSync();

        if (localID < d)
        {
            int ai = offset * int(2 * localID + 1) - 1;
            int bi = offset * int(2 * localID + 2) - 1;
            gs_LDSSums[bi] += gs_LDSSums[ai];
        }

        offset <<= 1;
    }

    GroupMemoryBarrierWithGroupSync();

    if (localID == THREAD_GROUP_SIZE - 1)
    {
        gs_LDSSums[localID] = 0;
    }

    // downsweep
    for (uint d = 1; d < THREAD_GROUP_SIZE; d <<= 1)
    {
        offset >>= 1;

        GroupMemoryBarrierWithGroupSync();

        if (localID < d)
        {
            int ai = offset * int(2 * localID + 1) - 1;
            int bi = offset * int(2 * localID + 2) - 1;
            uint t = gs_LDSSums[ai];
            gs_LDSSums[ai] = gs_LDSSums[bi];
            gs_LDSSums[bi] += t;
        }
    }
    
    GroupMemoryBarrierWithGroupSync();
    
    // return the scan
    return gs_LDSSums[localID]; 
#endif
}

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void CountReduce(uint localID : SV_GroupThreadID, uint groupID : SV_GroupID)
{
    // Figure out what bin data we are reducing
    uint binID = groupID / _NumReduceThreadGroupPerBin;
    uint binOffset = binID * _NumThreadGroups;

    // Get the base index for this thread group
    uint baseIndex = (groupID % _NumReduceThreadGroupPerBin) * ELEMENTS_PER_THREAD * THREAD_GROUP_SIZE;

    // Calculate partial sums for entries this thread reads in
    uint threadgroupSum = 0;
    for (uint i = 0; i < ELEMENTS_PER_THREAD; ++i)
    {
        uint dataIndex = baseIndex + (i * THREAD_GROUP_SIZE) + localID;
        threadgroupSum += (dataIndex < _NumThreadGroups) ? _SumTable[binOffset + dataIndex] : 0;
    }

    // Reduce across the entirety of the thread group
    threadgroupSum = ThreadgroupReduce(threadgroupSum, localID);

    // First thread of the group writes out the reduced sum for the bin
    if (!localID)
    {
        _ReduceTable[groupID] = threadgroupSum;
    }
}

// This is to transform uncoalesced loads into coalesced loads and 
// then scattered loads from LDS
groupshared int gs_LDS[ELEMENTS_PER_THREAD][THREAD_GROUP_SIZE];

void ScanPrefix(uint localID, uint groupID, uint numValuesToScan, uint binOffset, uint baseIndex, bool addPartialSums)
{
    // Perform coalesced loads into LDS
    for (uint i = 0; i < ELEMENTS_PER_THREAD; i++)
    {
        uint dataIndex = baseIndex + (i * THREAD_GROUP_SIZE) + localID;

        uint col = ((i * THREAD_GROUP_SIZE) + localID) / ELEMENTS_PER_THREAD;
        uint row = ((i * THREAD_GROUP_SIZE) + localID) % ELEMENTS_PER_THREAD;
        
        gs_LDS[row][col] = (dataIndex < numValuesToScan) ? _Scan[binOffset + dataIndex] : 0;
    }

    GroupMemoryBarrierWithGroupSync();

    // Calculate the local scan-prefix for current thread
    uint threadgroupSum = 0;
    
    for (uint i = 0; i < ELEMENTS_PER_THREAD; i++)
    {
        uint tmp = gs_LDS[i][localID];
        gs_LDS[i][localID] = threadgroupSum;
        threadgroupSum += tmp;
    }

    // Scan prefix partial sums
    threadgroupSum = BlockScanPrefix(threadgroupSum, localID);

    // Add reduced partial sums if requested
    uint partialSum = 0;
    if (addPartialSums)
    {
        // Partial sum additions are a little special as they are tailored to the optimal number of 
        // thread groups we ran in the beginning, so need to take that into account
        partialSum = _ScanScratch[groupID];
    }

    // Add the block scanned-prefixes back in
    for (uint i = 0; i < ELEMENTS_PER_THREAD; i++)
    {
        gs_LDS[i][localID] += threadgroupSum;
    }

    GroupMemoryBarrierWithGroupSync();

    // Perform coalesced writes to scan dst
    for (uint i = 0; i < ELEMENTS_PER_THREAD; i++)
    {
        uint dataIndex = baseIndex + (i * THREAD_GROUP_SIZE) + localID;

        uint col = ((i * THREAD_GROUP_SIZE) + localID) / ELEMENTS_PER_THREAD;
        uint row = ((i * THREAD_GROUP_SIZE) + localID) % ELEMENTS_PER_THREAD;

        if (dataIndex < numValuesToScan)
        {
            _Scan[binOffset + dataIndex] = gs_LDS[row][col] + partialSum;
        }
    }
}

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void Scan(uint localID : SV_GroupThreadID, uint groupID : SV_GroupID)
{
    uint baseIndex = ELEMENTS_PER_THREAD * THREAD_GROUP_SIZE * groupID;
    
    ScanPrefix(localID, groupID, _NumScanValues, 0, baseIndex, false);
}

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void ScanAdd(uint localID : SV_GroupThreadID, uint groupID : SV_GroupID)
{
    // When doing adds, we need to access data differently because reduce 
    // has a more specialized access pattern to match optimized count
    // Access needs to be done similarly to reduce
    // Figure out what bin data we are reducing
    uint binID = groupID / _NumReduceThreadGroupPerBin;
    uint binOffset = binID * _NumThreadGroups;

    // Get the base index for this thread group
    uint baseIndex = (groupID % _NumReduceThreadGroupPerBin) * ELEMENTS_PER_THREAD * THREAD_GROUP_SIZE;

    ScanPrefix(localID, groupID, _NumThreadGroups, binOffset, baseIndex, true);
}

// Offset cache to avoid loading the offsets all the time
groupshared uint gs_BinOffsetCache[THREAD_GROUP_SIZE];
// Local histogram for offset calculations
groupshared uint gs_LocalHistogram[BIN_COUNT];
// Scratch area for algorithm
groupshared uint gs_LDSScratch[BIN_COUNT];

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void Scatter(uint localID : SV_GroupThreadID, uint groupID : SV_GroupID)
{
    // Load the sort bin threadgroup offsets into LDS for faster referencing
    if (localID < BIN_COUNT)
    {
        gs_BinOffsetCache[localID] = _SumTable[localID * _NumThreadGroups + groupID];
    }

    GroupMemoryBarrierWithGroupSync();

    // Data is processed in blocks, and how many we process can changed based on how much data we are processing
    // versus how many thread groups we are processing with
    int blockSize = ELEMENTS_PER_THREAD * THREAD_GROUP_SIZE;

    // Figure out this thread group's index into the block data (taking into account thread groups that need to do extra reads)
    uint threadgroupBlockStart = (blockSize * _NumBlocksPerThreadGroup * groupID);
    uint numBlocksToProcess = _NumBlocksPerThreadGroup;
    uint numThreadGroupsWithoutAdditionalBlocks = _NumThreadGroups - _NumThreadGroupsWithAdditionalBlocks;

    if (groupID >= numThreadGroupsWithoutAdditionalBlocks)
    {
        threadgroupBlockStart += (groupID - numThreadGroupsWithoutAdditionalBlocks) * blockSize;
        numBlocksToProcess++;
    }

    // Get the block start index for this thread
    uint blockIndex = threadgroupBlockStart + localID;

    // Count value occurences
    uint newCount;
    for (uint blockCount = 0; blockCount < numBlocksToProcess; blockCount++, blockIndex += blockSize)
    {
        uint dataIndex = blockIndex;
        
        // Pre-load the key values in order to hide some of the read latency
        uint srcKeys[ELEMENTS_PER_THREAD];
        srcKeys[0] = _SrcBuffer[dataIndex];
        srcKeys[1] = _SrcBuffer[dataIndex + THREAD_GROUP_SIZE];
        srcKeys[2] = _SrcBuffer[dataIndex + (THREAD_GROUP_SIZE * 2)];
        srcKeys[3] = _SrcBuffer[dataIndex + (THREAD_GROUP_SIZE * 3)];

        for (int i = 0; i < ELEMENTS_PER_THREAD; i++)
        {
            // Clear the local histogram
            if (localID < BIN_COUNT)
            {
                gs_LocalHistogram[localID] = 0;
            }

            uint localKey = (dataIndex < _NumKeys ? srcKeys[i] : 0xffffffff);

            // Sort the keys locally in LDS
            for (uint bitShift = 0; bitShift < BITS_PER_PASS; bitShift += 2)
            {
                // Figure out the keyIndex
                uint keyIndex = (localKey >> _ShiftBit) & 0xf;
                uint bitKey = (keyIndex >> bitShift) & 0x3;

                // Create a packed histogram 
                uint packedHistogram = 1 << (bitKey * 8);

                // Sum up all the packed keys (generates counted offsets up to current thread group)
                uint localSum = BlockScanPrefix(packedHistogram, localID);

                // Last thread stores the updated histogram counts for the thread group
                // Scratch = 0xsum3|sum2|sum1|sum0 for thread group
                if (localID == (THREAD_GROUP_SIZE - 1))
                {
                    gs_LDSScratch[0] = localSum + packedHistogram;
                }

                GroupMemoryBarrierWithGroupSync();

                // Load the sums value for the thread group
                packedHistogram = gs_LDSScratch[0];

                // Add prefix offsets for all 4 bit "keys" (packedHistogram = 0xsum2_1_0|sum1_0|sum0|0)
                packedHistogram = (packedHistogram << 8) + (packedHistogram << 16) + (packedHistogram << 24);

                // Calculate the proper offset for this thread's value
                localSum += packedHistogram;

                // Calculate target offset
                uint keyOffset = (localSum >> (bitKey * 8)) & 0xff;

                // Re-arrange the keys (store, sync, load)
                gs_LDSSums[keyOffset] = localKey;
                GroupMemoryBarrierWithGroupSync();
                localKey = gs_LDSSums[localID];

                GroupMemoryBarrierWithGroupSync();
            }

            // Need to recalculate the keyIndex on this thread now that values have been copied around the thread group
            uint keyIndex = (localKey >> _ShiftBit) & 0xf;

            // Reconstruct histogram
            InterlockedAdd(gs_LocalHistogram[keyIndex], 1);

            GroupMemoryBarrierWithGroupSync();

#if defined(ENABLE_WAVE_INTRINSICS)
            // Prefix histogram
            uint histogramPrefixSum = WavePrefixSum(localID < BIN_COUNT ? gs_LocalHistogram[localID] : 0);

            // Broadcast prefix-sum via LDS
            if (localID < BIN_COUNT)
            {
                gs_LDSScratch[localID] = histogramPrefixSum;
            }
#else
            if (localID < BIN_COUNT)
            {
                gs_LDSScratch[localID] = gs_LocalHistogram[localID];
            }

            GroupMemoryBarrierWithGroupSync();

            // reduction
            int offset = 1;
            for (uint d = BIN_COUNT >> 1; d > 0; d >>= 1)
            {
                GroupMemoryBarrierWithGroupSync();

                if (localID < d)
                {
                    int ai = offset * int(2 * localID + 1) - 1;
                    int bi = offset * int(2 * localID + 2) - 1;
                    gs_LDSScratch[bi] += gs_LDSScratch[ai];
                }

                offset <<= 1;
            }

            GroupMemoryBarrierWithGroupSync();

            if (localID == BIN_COUNT - 1)
            {
                gs_LDSScratch[localID] = 0;
            }

            // downsweep
            for (uint d = 1; d < BIN_COUNT; d <<= 1)
            {
                offset >>= 1;

                GroupMemoryBarrierWithGroupSync();

                if (localID < d)
                {
                    int ai = offset * int(2 * localID + 1) - 1;
                    int bi = offset * int(2 * localID + 2) - 1;
                    uint t = gs_LDSScratch[ai];
                    gs_LDSScratch[ai] = gs_LDSScratch[bi];
                    gs_LDSScratch[bi] += t;
                }
            }
#endif

            // Get the global offset for this key out of the cache
            uint globalOffset = gs_BinOffsetCache[keyIndex];

            GroupMemoryBarrierWithGroupSync();

            // Get the local offset (at this point the keys are all in increasing order from 0 -> num bins in localID 0 -> thread group size)
            uint localOffset = localID - gs_LDSScratch[keyIndex];

            // Write to destination
            uint totalOffset = globalOffset + localOffset;

            if (totalOffset < _NumKeys)
            {
                _DstBuffer[totalOffset] = localKey;
            }

            GroupMemoryBarrierWithGroupSync();

            // Update the cached histogram for the next set of entries
            if (localID < BIN_COUNT)
            {
                gs_BinOffsetCache[localID] += gs_LocalHistogram[localID];
            }
            
            // Increase the data offset by thread group size
            dataIndex += THREAD_GROUP_SIZE;
        }
    }
}

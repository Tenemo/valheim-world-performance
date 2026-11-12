cbuffer VisibilityParameters : register(b0)
{
    float4 _SlotCompaction;
};

// The spatial renderer's merged meshes hold every slot's indices at the start of their index buffer and draw a region after
// them holding only the slots drawing now, so slots at other detail levels run no vertex program. Each run copies a range of
// slots' indices there in order: x is its first index, y its first place in the drawn region and z its count.
StructuredBuffer<uint4> _SlotRuns : register(t0);
RWByteAddressBuffer _MergedIndices : register(u0);

// The index a place in the drawn region copies, from the runs _SlotCompaction.x to x + y - 1, which fill z places from
// index w on.
uint SlotSource(uint place)
{
    uint lower = (uint)_SlotCompaction.x, upper = lower + (uint)_SlotCompaction.y - 1;
    while (lower < upper)
    {
        uint middle = (lower + upper + 1) >> 1;
        if (_SlotRuns[middle].y <= place) lower = middle; else upper = middle - 1;
    }
    uint4 run = _SlotRuns[lower];
    return run.x + place - run.y;
}

// A 16-bit mesh's drawn region starts at an even index, and each thread writes two of its indices as one word.
[numthreads(64, 1, 1)]
void CompactSlots16(uint3 thread : SV_DispatchThreadID)
{
    uint place = thread.x * 2, drawn = (uint)_SlotCompaction.z;
    if (place >= drawn) return;
    uint first = SlotSource(place), second = place + 1 < drawn ? SlotSource(place + 1) : first;
    uint low = (_MergedIndices.Load((first >> 1) << 2) >> ((first & 1) << 4)) & 0xffff;
    uint high = (_MergedIndices.Load((second >> 1) << 2) >> ((second & 1) << 4)) & 0xffff;
    _MergedIndices.Store(((uint)_SlotCompaction.w + place) << 1, low | (high << 16));
}

[numthreads(64, 1, 1)]
void CompactSlots32(uint3 thread : SV_DispatchThreadID)
{
    uint place = thread.x;
    if (place >= (uint)_SlotCompaction.z) return;
    _MergedIndices.Store(((uint)_SlotCompaction.w + place) << 2, _MergedIndices.Load(SlotSource(place) << 2));
}

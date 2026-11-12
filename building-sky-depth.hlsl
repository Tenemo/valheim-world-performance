// Vertex program of the game's sky depth shader (Unlit/DepthWrite) for merged building pieces. It repeats the native
// program's arithmetic in the same order, with the object-to-world matrix read from the piece slot that each merged
// vertex names; the native fragment program is used unchanged. Slots hold the object-to-world and world-to-object
// matrices of one native renderer, in that order, as the merged colour and shadow passes read them.
cbuffer Globals : register(b0)
{
    float4 _MainTex_ST : packoffset(c2);
};

cbuffer TenemoPieces : register(b1)
{
    float4 TenemoPieceTransforms[1024];
};

cbuffer UnityPerFrame : register(b2)
{
    float4x4 unity_MatrixV : packoffset(c9);
    float4x4 unity_MatrixVP : packoffset(c17);
};

struct Input
{
    float3 position : POSITION;
    float2 texcoord : TEXCOORD0;
    uint slot : TEXCOORD4;
};

struct Output
{
    float2 texcoord : TEXCOORD0;
    float4 position : SV_POSITION;
    float3 view : TEXCOORD2;
};

Output main(Input input)
{
    Output output;
    output.texcoord = input.texcoord * _MainTex_ST.xy + _MainTex_ST.zw;
    uint slot = input.slot * 8;
    float4 world = TenemoPieceTransforms[slot + 1] * input.position.y;
    world = TenemoPieceTransforms[slot] * input.position.x + world;
    world = TenemoPieceTransforms[slot + 2] * input.position.z + world;
    world = world + TenemoPieceTransforms[slot + 3];
    output.position = mul(unity_MatrixVP, world);
    output.view = mul(unity_MatrixV, world).xyz;
    return output;
}

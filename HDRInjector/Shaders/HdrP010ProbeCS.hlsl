Texture2D<float> InputTexture : register(t0);
RWTexture2D<float4> OutputTexture : register(u0);
[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    InputTexture.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float y = InputTexture.Load(int3(id.xy, 0));
    OutputTexture[id.xy] = float4(y, y, y, 1.0);
}

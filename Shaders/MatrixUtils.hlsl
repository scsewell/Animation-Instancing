#ifndef ANIMATION_INSTANCING_MATRIX_UTILS
#define ANIMATION_INSTANCING_MATRIX_UTILS

float4x4 InvertMatrix(float4x4 m)
{
#define minor(a,b,c) determinant(float3x3(m.a, m.b, m.c))

    float4x4 cofactors = float4x4(
        minor(_22_23_24, _32_33_34, _42_43_44),
        -minor(_21_23_24, _31_33_34, _41_43_44),
        minor(_21_22_24, _31_32_34, _41_42_44),
        -minor(_21_22_23, _31_32_33, _41_42_43),

        -minor(_12_13_14, _32_33_34, _42_43_44),
        minor(_11_13_14, _31_33_34, _41_43_44),
        -minor(_11_12_14, _31_32_34, _41_42_44),
        minor(_11_12_13, _31_32_33, _41_42_43),

        minor(_12_13_14, _22_23_24, _42_43_44),
        -minor(_11_13_14, _21_23_24, _41_43_44),
        minor(_11_12_14, _21_22_24, _41_42_44),
        -minor(_11_12_13, _21_22_23, _41_42_43),

        -minor(_12_13_14, _22_23_24, _32_33_34),
        minor(_11_13_14, _21_23_24, _31_33_34),
        -minor(_11_12_14, _21_22_24, _31_32_34),
        minor(_11_12_13, _21_22_23, _31_32_33)
        );
#undef minor
    return transpose(cofactors) / determinant(m);
}

float4x4 QuaternionToMatrix(float4 q)
{
    float3 n0 = q.xyz * 2.0;
    float3 n1 = q.xyz * n0.xyz;
    float3 n2 = q.xxy * n0.yzz;
    float3 n3 = q.www * n0.xyz;
    float3 n4 = 1.0 - (n1.yxx + n1.zzy);
    float3 n5 = n2.xzy + n3.zxy;
    float3 n6 = n2.yxz - n3.yzx;

    float4x4 r =
    {
        n4.x, n5.x, n6.x, 0.0,
        n6.y, n4.y, n5.y, 0.0,
        n5.z, n6.z, n4.z, 0.0,
        0.0,  0.0,  0.0,  1.0
    };
    return r;
}

float4x4 TRS(float3 t, float4 r, float3 s)
{
    float4x4 ts =
    {
        s.x, 0.0, 0.0, t.x,
        0.0, s.y, 0.0, t.y,
        0.0, 0.0, s.z, t.z,
        0.0, 0.0, 0.0, 1.0
    };
    return mul(ts, QuaternionToMatrix(r));
}

float4x4 MatrixDecompress(float3x4 m)
{
    return float4x4(
        m._11_12_13_14,
        m._21_22_23_24,
        m._31_32_33_34,
        float4(0.0, 0.0, 0.0, 1.0)
    );
}

#endif // ANIMATION_INSTANCING_MATRIX_UTILS
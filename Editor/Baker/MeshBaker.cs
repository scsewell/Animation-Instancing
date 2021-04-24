using System.Runtime.InteropServices;

using Unity.Collections;
using Unity.Mathematics;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

namespace AnimationInstancing
{
    partial class Baker
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
        struct Vertex32
        {
            public static readonly VertexAttributeDescriptor[] k_layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UNorm16, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float16, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm16, 2),
            };

            public half3 pos;
            public half bindPosX;

            public sbyte nrmX;
            public sbyte nrmY;
            public sbyte nrmZ;
            public sbyte nrmW;

            public sbyte tanX;
            public sbyte tanY;
            public sbyte tanZ;
            public sbyte tanW;

            public Color32 col;

            public ushort uvX;
            public ushort uvY;

            public half bindPosY;
            public half bindPosZ;

            public ushort boneUV;
            public ushort _UNUSED;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        struct Vertex64
        {
            public static readonly VertexAttributeDescriptor[] k_layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UNorm16, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm16, 2),
            };

            public float3 pos;
            public float bindPosX;

            public short nrmX;
            public short nrmY;
            public short nrmZ;
            public short nrmW;

            public short tanX;
            public short tanY;
            public short tanZ;
            public short tanW;

            public Color col;

            public ushort uvX;
            public ushort uvY;

            public float bindPosY;
            public float bindPosZ;

            public ushort boneUV;
            public ushort _UNUSED;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
        struct Vertex80
        {
            public static readonly VertexAttributeDescriptor[] k_layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1),
            };

            public float3 pos;
            public float bindPosX;

            public float3 nrm;
            public float4 tan;
            public Color col;
            public float2 uv;

            public float bindPosY;
            public float bindPosZ;
            public float boneUV;
        }

        bool BakeMeshes()
        {
            m_meshes.Clear();

            try
            {
                var renderers = m_config.renderers;
                
                // get the combined size of the meshes
                var vertexCount = 0;
                var indexCount = 0;
                var subMeshCount = 0;

                for (var i = 0; i < renderers.Length; i++)
                {
                    var mesh = renderers[i].sharedMesh;

                    vertexCount += mesh.vertexCount;
                    for (var j = 0; j < mesh.subMeshCount; j++)
                    {
                        indexCount += (int)mesh.GetIndexCount(j);
                        subMeshCount++;
                    }
                }

                // get the data for each mesh combined into a single set of data
                var vertices = new NativeArray<Vertex80>(vertexCount, Allocator.Temp);
                var indices = new NativeArray<ushort>(indexCount, Allocator.Temp);
                var subMeshes = new NativeArray<SubMeshDescriptor>(subMeshCount, Allocator.Temp);

                var currentVertex = 0;
                var currentIndex = 0;
                var currentSubMesh = 0;

                for (var i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    var mesh = renderer.sharedMesh;

                    var title = "Baking Meshes...";
                    var info = $"{i + 1}/{renderers.Length} {renderer.name}";
                    var progress = (float)i / renderers.Length;

                    if (EditorUtility.DisplayCancelableProgressBar(title, info, progress))
                    {
                        return true;
                    }

                    // get the index data
                    for (var j = 0; j < mesh.subMeshCount; j++)
                    {
                        var ind = mesh.GetIndices(j);

                        subMeshes[currentSubMesh] = new SubMeshDescriptor
                        {
                            topology = mesh.GetTopology(j),
                            indexStart = currentIndex,
                            indexCount = ind.Length,
                            baseVertex = currentVertex,
                        };
                        currentSubMesh++;

                        for (var k = 0; k < ind.Length; k++)
                        {
                            indices[currentIndex] = (ushort)ind[k];
                            currentIndex++;
                        }
                    }

                    // get the vertex data, transformed into the space of a single transform
                    var from = renderer.transform;
                    var to = m_config.animator.transform;

                    var verts = mesh.vertices;
                    var normals = mesh.normals;
                    var tangents = mesh.tangents;
                    var colors = mesh.colors32;
                    var uvs = mesh.uv;
                    var weights = mesh.boneWeights;

                    var indexMap = m_renderIndexMaps[renderer];

                    for (var j = 0; j < mesh.vertexCount; j++)
                    {
                        var pos = Vector3.zero;
                        var nrm = Vector3.zero;
                        var tan = Vector4.zero;
                        var col = Color.white;
                        var uv = Vector2.zero;

                        if (verts.Length > 0)
                        {
                            pos = to.InverseTransformPoint(from.TransformPoint(verts[j]));
                        }
                        if (normals.Length > 0)
                        {
                            nrm = to.InverseTransformDirection(from.TransformDirection(normals[j]));
                        }
                        if (tangents.Length > 0)
                        {
                            var t = to.InverseTransformDirection(from.TransformDirection(tangents[j]));
                            tan = new Vector4(t.x, t.y, t.z, tangents[j].w);
                        }
                        if (colors.Length > 0)
                        {
                            col = colors[j];
                        }
                        if (uvs.Length > 0)
                        {
                            uv = uvs[j];
                        }

                        // Get the bind pose position and bone index used by each vertex,
                        // with the assumption that each vertex is influenced by a single bone.
                        var index = indexMap[weights[j].boneIndex0];
                        var bindPos = (Vector3)m_bindPoses[index].inverse.GetColumn(3);

                        // This coordinate gives the row in the animation texture this vertex should read from.
                        // We offset the coordinate to be in the center of the pixel.
                        var boneCoord = 0.5f * ((index + 0.5f) / m_bones.Length);

                        vertices[currentVertex] = new Vertex80
                        {
                            pos = pos,
                            nrm = nrm,
                            tan = tan,
                            col = col,
                            uv = uv,

                            bindPosX = bindPos.x,
                            bindPosY = bindPos.y,
                            bindPosZ = bindPos.z,
                            boneUV = boneCoord,
                        };

                        currentVertex++;
                    }
                }

                var combinedMesh = new Mesh
                {
                    name = $"Mesh_{m_config.animator.name}",
                };

                SetVertexData(combinedMesh, vertices);
                combinedMesh.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
                combinedMesh.SetIndexBufferData(indices, 0, 0, indices.Length);
                combinedMesh.SetSubMeshes(subMeshes, 0, subMeshes.Length);

                combinedMesh.UploadMeshData(true);

                vertices.Dispose();
                indices.Dispose();
                subMeshes.Dispose();

                var lods = GetLods();
                var lodCount = (lods == null ? 1 : Mathf.Max(1, lods.Length));
                m_meshes.Add(new InstancedMesh(combinedMesh, combinedMesh.subMeshCount / lodCount, lods));
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        LodInfo[] GetLods()
        {
            if (m_config.lod == null)
            {
                return null;
            }

            var lodGroup = m_config.lod.GetLODs();
            var lods = new LodInfo[lodGroup.Length];

            for (var i = 0; i < lods.Length; i++)
            {
                lods[i] = new LodInfo(
                    lodGroup[i].screenRelativeTransitionHeight,
                    m_config.shadowLodOffset
                );
            }

            return lods;
        }

        void SetVertexData(Mesh combinedMesh, NativeArray<Vertex80> vertices)
        {
            switch (m_config.vertexMode)
            {
                case VertexCompression.High:
                {
                    var vertexBuffer = new NativeArray<Vertex32>(vertices.Length, Allocator.Temp);
                    
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var vert = vertices[i];

                        vertexBuffer[i] = new Vertex32
                        {
                            pos = new half3(vert.pos),

                            nrmX = FloatToSnorm8(vert.nrm.x),
                            nrmY = FloatToSnorm8(vert.nrm.y),
                            nrmZ = FloatToSnorm8(vert.nrm.z),
                            nrmW = FloatToSnorm8(0f),

                            tanX = FloatToSnorm8(vert.tan.x),
                            tanY = FloatToSnorm8(vert.tan.y),
                            tanZ = FloatToSnorm8(vert.tan.z),
                            tanW = FloatToSnorm8(vert.tan.w),

                            col = vert.col,

                            uvX = FloatToUnorm16(vert.uv.x),
                            uvY = FloatToUnorm16(vert.uv.y),

                            bindPosX = (half)vert.bindPosX,
                            bindPosY = (half)vert.bindPosY,
                            bindPosZ = (half)vert.bindPosZ,

                            boneUV = FloatToUnorm16(vert.boneUV),
                        };
                    }

                    combinedMesh.SetVertexBufferParams(vertexBuffer.Length, Vertex32.k_layout);
                    combinedMesh.SetVertexBufferData(vertexBuffer, 0, 0, vertexBuffer.Length);

                    vertexBuffer.Dispose();
                    break;
                }
                case VertexCompression.Low:
                {
                    var vertexBuffer = new NativeArray<Vertex64>(vertices.Length, Allocator.Temp);

                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var vert = vertices[i];

                        vertexBuffer[i] = new Vertex64
                        {
                            pos = vert.pos,

                            nrmX = FloatToSnorm16(vert.nrm.x),
                            nrmY = FloatToSnorm16(vert.nrm.y),
                            nrmZ = FloatToSnorm16(vert.nrm.z),
                            nrmW = FloatToSnorm16(0f),

                            tanX = FloatToSnorm16(vert.tan.x),
                            tanY = FloatToSnorm16(vert.tan.y),
                            tanZ = FloatToSnorm16(vert.tan.z),
                            tanW = FloatToSnorm16(vert.tan.w),

                            col = vert.col,

                            uvX = FloatToUnorm16(vert.uv.x),
                            uvY = FloatToUnorm16(vert.uv.y),

                            bindPosX = vert.bindPosX,
                            bindPosY = vert.bindPosY,
                            bindPosZ = vert.bindPosZ,

                            boneUV = FloatToUnorm16(vert.boneUV),
                        };
                    }

                    combinedMesh.SetVertexBufferParams(vertexBuffer.Length, Vertex64.k_layout);
                    combinedMesh.SetVertexBufferData(vertexBuffer, 0, 0, vertexBuffer.Length);

                    vertexBuffer.Dispose();
                    break;
                }
                default:
                {
                    combinedMesh.SetVertexBufferParams(vertices.Length, Vertex80.k_layout);
                    combinedMesh.SetVertexBufferData(vertices, 0, 0, vertices.Length);
                    break;
                }
            }
        }

        static byte FloatToUnorm8(float v)
        {
            return (byte)math.clamp((v * byte.MaxValue) + 0.5f, byte.MinValue, byte.MaxValue);
        }

        static ushort FloatToUnorm16(float v)
        {
            return (ushort)math.clamp((v * ushort.MaxValue) + 0.5f, ushort.MinValue, ushort.MaxValue);
        }

        static sbyte FloatToSnorm8(float v)
        {
            return (sbyte)math.clamp((v * sbyte.MaxValue) + (v > 0f ? 0.5f : -0.5f), sbyte.MinValue, sbyte.MaxValue);
        }

        static short FloatToSnorm16(float v)
        {
            return (short)math.clamp((v * short.MaxValue) + (v > 0f ? 0.5f : -0.5f), short.MinValue, short.MaxValue);
        }
    }
}

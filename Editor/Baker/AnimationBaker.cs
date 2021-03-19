using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace AnimationInstancing
{
    partial class Baker
    {
        bool BakeAnimations()
        {
            m_animations.Clear();

            try
            {
                var animations = m_config.animations;
                var frameRates = m_config.frameRates;

                // All of the animations are baked into a single texture. Each animation occupies a
                // rectangular region of that texture. The height of each animation region is twice the
                // number of bones, as each bone uses two rows per frame of animation, while the length
                // in pixels of the animation is the number of frames in the animation. Since there are
                // the same number of bones per animation, all animations have the same height in the
                // texture. We want to pack the animation textures such that no animation runs off the
                // edge of the texture, while minimizing the wasted space.
                var animationSizes = new Vector2Int[animations.Length];

                // find the size required by each animation
                var height = m_bones.Length * 2;

                for (var i = 0; i < animations.Length; i++)
                {
                    var animation = animations[i];
                    var frameRate = frameRates[animation];
                    var length = Mathf.RoundToInt(animation.length * frameRate);

                    animationSizes[i] = new Vector2Int(length, height);
                }

                // find a reasonably optimal packing of the animation textures
                var regions = Pack(animationSizes, out var size);

                // create the texture data buffer
                var texture = new ushort[size.x * size.y * 4];

                // start animation mode, allowing us to sample animation clip frames in the editor
                AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling();

                for (var i = 0; i < animations.Length; i++)
                {
                    var animation = animations[i];
                    var region = regions[i];

                    var title = "Baking Animations...";
                    var info = $"{i + 1}/{animations.Length} {animation.name}";
                    var progress = (float)i / animations.Length;

                    if (EditorUtility.DisplayCancelableProgressBar(title, info, progress))
                    {
                        return true;
                    }

                    var bounds = BakeAnimation(texture, size, animation, region);

                    m_animations.Add(new InstancedAnimation(region, animation.length, bounds));
                }

                // create the animation texture
                m_animationTexture = new Texture2D(size.x, size.y, GraphicsFormat.R16G16B16A16_SFloat, 0, TextureCreationFlags.None)
                {
                    name = $"Anim_{m_config.animator.name}",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                };

                m_animationTexture.SetPixelData(texture, 0);
                m_animationTexture.Apply(false, true);

                return false;
            }
            finally
            {
                AnimationMode.EndSampling();
                AnimationMode.StopAnimationMode();
                EditorUtility.ClearProgressBar();
            }
        }

        Bounds BakeAnimation(ushort[] texture, Vector2Int textureSize, AnimationClip animation, RectInt region)
        {
            var animator = m_config.animator.gameObject;
            var renderers = m_config.renderers;

            // Bake the animation to the texture, while finding the bounds of the meshes
            // during the course of the animation.
            var bounds = default(Bounds);
            var boundsInitialized = false;
            var boundsMeshes = new Mesh[renderers.Length];

            for (var i = 0; i < boundsMeshes.Length; i++)
            {
                boundsMeshes[i] = new Mesh();
            }

            for (var frame = 0; frame < region.width; frame++)
            {
                var normalizedTime = (float)frame / region.width;
                var time = normalizedTime * animation.length;

                // play a frame in the animation
                AnimationMode.SampleAnimationClip(animator, animation, time);

                for (var bone = 0; bone < m_bones.Length; bone++)
                {
                    // get the offset from the bind pose to the current pose for the bone
                    var root = animator.transform;
                    var t = m_bones[bone];

                    var pos = root.InverseTransformPoint(t.position);
                    var rot = t.rotation * m_bindPoses[bone].rotation;

                    // write the pose to the animation texture
                    var x = region.x + frame;
                    var y = region.y + bone;
                    SetValue(texture, textureSize, x, y, pos);
                    SetValue(texture, textureSize, x, y + (region.height / 2), new Vector4(rot.x, rot.y, rot.z, rot.w));
                }

                // calculate the bounds for the meshes for the frame in the animator's space
                for (var i = 0; i < boundsMeshes.Length; i++)
                {
                    var mesh = boundsMeshes[i];
                    var renderer = renderers[i];
                    var transform = renderer.transform;

                    renderer.BakeMesh(mesh);

                    if (TryGetTransformedBounds(mesh.vertices, transform, animator.transform, out var meshBounds))
                    {
                        if (!boundsInitialized)
                        {
                            bounds = meshBounds;
                            boundsInitialized = true;
                        }
                        else
                        {
                            bounds.Encapsulate(meshBounds);
                        }
                    }
                }
            }

            for (var i = 0; i < boundsMeshes.Length; i++)
            {
                Object.DestroyImmediate(boundsMeshes[i]);
            }

            return bounds;
        }

        void SetValue(ushort[] texture, Vector2Int textureSize, int x, int y, Vector4 value)
        {
            var i = ((y * textureSize.x) + x) * 4;

            texture[i] = Mathf.FloatToHalf(value.x);
            texture[i + 1] = Mathf.FloatToHalf(value.y);
            texture[i + 2] = Mathf.FloatToHalf(value.z);
            texture[i + 3] = Mathf.FloatToHalf(value.w);
        }

        bool TryGetTransformedBounds(Vector3[] vertices, Transform from, Transform to, out Bounds bounds)
        {
            if (vertices.Length == 0)
            {
                bounds = default;
                return false;
            }

            var localToWorld = from.localToWorldMatrix;
            var worldToLocal = to.worldToLocalMatrix;

            var min = float.MaxValue * Vector3.one;
            var max = float.MinValue * Vector3.one;

            for (var i = 0; i < vertices.Length; i++)
            {
                var vert = vertices[i];
                vert = localToWorld.MultiplyPoint3x4(vert);
                vert = worldToLocal.MultiplyPoint3x4(vert);

                min = Vector3.Min(min, vert);
                max = Vector3.Max(max, vert);
            }

            var center = (max + min) * 0.5f;
            var size = max - min;
            bounds = new Bounds(center, size);
            return true;
        }

        static RectInt[] Pack(Vector2Int[] boxes, out Vector2Int packedSize)
        {
            var area = 0;
            var minWidth = 0;

            for (var i = 0; i < boxes.Length; i++)
            {
                var size = boxes[i];
                area += size.x * size.y;
                minWidth = Mathf.Max(minWidth, size.x);
            }

            var sortedByWidth = Enumerable.Range(0, boxes.Length)
                .OrderByDescending(i => boxes[i].x)
                .ToArray();

            // we want a squarish container
            var width = Mathf.Max(minWidth, Mathf.CeilToInt(Mathf.Sqrt(area / 0.95f)));

            var spaces = new List<RectInt>()
            {
                new RectInt(0, 0, width, int.MaxValue),
            };

            packedSize = Vector2Int.zero;
            var packed = new RectInt[boxes.Length];

            for (var i = 0; i < sortedByWidth.Length; i++)
            {
                var boxIndex = sortedByWidth[i];
                var box = boxes[boxIndex];

                // pack the box in the smallest free space
                for (var j = spaces.Count - 1; j >= 0; j--)
                {
                    var space = spaces[j];

                    if (box.x > space.width || box.y > space.height)
                    {
                        continue;
                    }

                    var packedBox = new RectInt(space.x, space.y, box.x, box.y);
                    packed[boxIndex] = packedBox;
                    packedSize = Vector2Int.Max(packedSize, packedBox.max);

                    if (box.x == space.width && box.y == space.height)
                    {
                        spaces.RemoveAt(j);
                    }
                    else if (box.x == space.width)
                    {
                        space.y += box.y;
                        space.height -= box.y;
                        spaces[j] = space;
                    }
                    else if (box.y == space.height)
                    {
                        space.x += box.x;
                        space.width -= box.x;
                        spaces[j] = space;
                    }
                    else
                    {
                        spaces.Add(new RectInt
                        {
                            x = space.x + box.x,
                            y = space.y,
                            width = space.width - box.x,
                            height = box.y,
                        });

                        space.y += box.y;
                        space.height -= box.y;
                        spaces[j] = space;
                    }
                    break;
                }
            }

            return packed;
        }
    }
}

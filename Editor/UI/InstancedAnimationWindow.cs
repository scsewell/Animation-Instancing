using System.Linq;

using UnityEditor;
using UnityEditor.Animations;

using UnityEngine;
using UnityEngine.Rendering;

namespace AnimationInstancing
{
    /// <summary>
    ///     A window used to bake animation data for instanced meshes.
    /// </summary>
    class InstancedAnimationWindow : EditorWindow
    {
        const string k_assetsPath = "Assets/";

        [SerializeField]
        Vector2 m_scroll = Vector2.zero;

        [SerializeField]
        Animator m_animator;

        [SerializeField]
        SerializableDictionary<AnimationClip, float> m_frameRates;

        [SerializeField]
        SerializableDictionary<Material, Material> m_materialRemap;

        [SerializeField]
        VertexCompression m_vertexCompression = VertexCompression.High;

        [SerializeField]
        string m_path = k_assetsPath;

        void OnEnable()
        {
            autoRepaintOnSceneChange = true;

            Undo.undoRedoPerformed += Repaint;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= Repaint;
        }

        public void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(m_scroll))
            {
                m_scroll = scroll.scrollPosition;

                // bake configuration
                Input();
                AnimationConfig();
                MaterialRemapping();
                MeshConfig();
                Output();

                // bake button
                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledGroupScope(!CanBake(false)))
                {
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(Contents.bakeButton, Contents.bakeButtonSize))
                    {
                        Bake();
                    }

                    GUILayout.FlexibleSpace();
                }

                // diplay messages explaining any issues
                CanBake(true);
            }
        }

        [MenuItem("Window/Animation/Instanced Animation Baker")]
        static void ShowWindow()
        {
            var window =
                GetWindow(
                    typeof(InstancedAnimationWindow),
                    false,
                    "Instanced Animation Baker",
                    true
                ) as InstancedAnimationWindow;
            window.minSize = new Vector2(300, 350);
            window.maxSize = new Vector2(600, 2000);
            window.Show();
        }

        void Input()
        {
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var animator =
                    EditorGUILayout.ObjectField(Contents.animator, m_animator, typeof(Animator), true) as Animator;

                if (change.changed)
                {
                    Undo.RecordObject(this, "Set Animator");
                    m_animator = animator;
                    EditorUtility.SetDirty(this);
                }
            }

            EditorGUILayout.Space(Contents.spacing);
        }

        void AnimationConfig()
        {
            if (m_animator == null || !(m_animator.runtimeAnimatorController is AnimatorController controller))
            {
                return;
            }

            EditorGUILayout.LabelField("Animations", EditorStyles.boldLabel);

            var animations = controller.animationClips;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Contents.animation, Contents.mappingWidth);
                EditorGUILayout.LabelField(Contents.frameRate, GUILayout.MinWidth(0f));
            }

            for (var i = 0; i < animations.Length; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    var animation = animations[i];

                    if (!m_frameRates.TryGetValue(animation, out var frameRate))
                    {
                        frameRate = animation.frameRate;
                        m_frameRates.Add(animation, frameRate);
                    }

                    using (new EditorGUI.DisabledGroupScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            GUIContent.none,
                            animation,
                            typeof(AnimationClip),
                            false,
                            Contents.mappingWidth
                        );
                    }

                    var currentRate = Mathf.RoundToInt(frameRate);
                    var maxRate = Mathf.RoundToInt(animation.frameRate);
                    var newRate = EditorGUILayout.IntSlider(currentRate, 1, maxRate, GUILayout.MinWidth(0f));

                    if (change.changed)
                    {
                        Undo.RecordObject(this, "Set Frame Rate");
                        m_frameRates[animation] = newRate;
                        EditorUtility.SetDirty(this);
                    }
                }
            }

            EditorGUILayout.Space(Contents.spacing);
        }

        void MaterialRemapping()
        {
            if (m_animator == null)
            {
                return;
            }

            var originalMaterials = GetOriginalMaterials();

            EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Contents.originalMaterial, Contents.mappingWidth);
                EditorGUILayout.LabelField(Contents.instancedMaterial, GUILayout.MinWidth(0f));
            }

            for (var i = 0; i < originalMaterials.Length; i++)
            {
                var originalMat = originalMaterials[i];

                m_materialRemap.TryGetValue(originalMat, out var remappedMat);

                using (new EditorGUILayout.HorizontalScope())
                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    using (new EditorGUI.DisabledGroupScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            GUIContent.none,
                            originalMat,
                            typeof(Material),
                            false,
                            Contents.mappingWidth
                        );
                    }

                    var newMat = EditorGUILayout.ObjectField(
                        GUIContent.none,
                        remappedMat,
                        typeof(Material),
                        false,
                        GUILayout.MinWidth(0f)
                    ) as Material;

                    if (change.changed)
                    {
                        Undo.RecordObject(this, "Set Material");
                        m_materialRemap[originalMat] = newMat;
                        EditorUtility.SetDirty(this);
                    }
                }
            }

            EditorGUILayout.Space(Contents.spacing);
        }

        void MeshConfig()
        {
            if (m_animator == null)
            {
                return;
            }

            EditorGUILayout.LabelField("Meshes", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var compression = (VertexCompression) EditorGUILayout.EnumPopup(
                    Contents.vertexCompression,
                    m_vertexCompression
                );

                if (change.changed)
                {
                    Undo.RecordObject(this, "Set Vertex Compression");
                    m_vertexCompression = compression;
                    EditorUtility.SetDirty(this);
                }
            }

            EditorGUILayout.Space(Contents.spacing);
        }

        void Output()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            // allow picking the output directory
            using (new EditorGUILayout.HorizontalScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                EditorGUILayout.PrefixLabel(Contents.directory);

                var path = GUILayout.TextField(m_path, Contents.directoryMinWidth);

                if (GUILayout.Button(
                    Contents.directorySelector,
                    EditorStyles.miniButton,
                    Contents.directorySelectorWidth
                ))
                {
                    path = EditorUtility.SaveFolderPanel("Choose Output Directory", path, string.Empty);

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var index = path.IndexOf(k_assetsPath);

                        if (index >= 0)
                        {
                            path = path.Substring(index);
                        }
                        else
                        {
                            path = k_assetsPath;
                        }
                    }

                    GUI.changed = true;
                }

                if (change.changed)
                {
                    Undo.RecordObject(this, "Set Output Directory");
                    m_path = path;
                    EditorUtility.SetDirty(this);
                }
            }

            EditorGUILayout.Space(Contents.spacing);
        }

        void Bake()
        {
            var controller = m_animator.runtimeAnimatorController as AnimatorController;

            var config = new BakeConfig
            {
                animator = m_animator,
                vertexMode = m_vertexCompression,
                lod = m_animator.GetComponentInChildren<LODGroup>(true),
                animations = controller.animationClips,
                frameRates = m_frameRates,
                renderers = m_animator.GetComponentsInChildren<SkinnedMeshRenderer>(true),
                materialRemap = m_materialRemap,
            };

            var baker = new Baker(config);

            baker.Bake(m_path);
        }

        bool CanBake(bool drawMessages)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (drawMessages)
                {
                    EditorGUILayout.HelpBox("Can't bake animations while in play mode.", MessageType.Warning);
                }

                return false;
            }

            if (m_animator == null)
            {
                if (drawMessages)
                {
                    EditorGUILayout.HelpBox("An animator is required.", MessageType.Info);
                }

                return false;
            }

            var canBake = true;
            var renderers = m_animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            if (renderers.Length == 0)
            {
                if (drawMessages)
                {
                    EditorGUILayout.HelpBox("The selected animator has no skinned meshes.", MessageType.Warning);
                }

                canBake = false;
            }
            else
            {
                foreach (var renderer in renderers)
                {
                    var mesh = renderer.sharedMesh;

                    for (var i = 0; i < mesh.subMeshCount; i++)
                    {
                        var topology = mesh.GetTopology(i);
                        var indexFormat = mesh.indexFormat;

                        if (topology != MeshTopology.Triangles)
                        {
                            if (drawMessages)
                            {
                                EditorGUILayout.HelpBox(
                                    $"Mesh \"{mesh.name}\" has a submesh with {topology} topology. Only triangle meshes are supported.",
                                    MessageType.Warning
                                );
                            }

                            canBake = false;
                        }

                        if (indexFormat != IndexFormat.UInt16)
                        {
                            if (drawMessages)
                            {
                                EditorGUILayout.HelpBox(
                                    $"Mesh \"{mesh.name}\" uses index format {indexFormat}. Only 16 bit indices supported.",
                                    MessageType.Warning
                                );
                            }

                            canBake = false;
                        }
                    }

                    var matCount = renderer.sharedMaterials.Length;
                    var subCount = mesh.subMeshCount;

                    if (matCount != subCount)
                    {
                        if (drawMessages)
                        {
                            EditorGUILayout.HelpBox(
                                $"Renderer \"{renderer.name}\" has {matCount} material{(matCount != 1 ? "s" : "")} assigned but \"mesh\" {mesh.name} has {subCount} submesh{(subCount != 1 ? "es" : "")}. These must be equal.",
                                MessageType.Warning
                            );
                        }

                        canBake = false;
                    }

                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat == null)
                        {
                            if (drawMessages)
                            {
                                EditorGUILayout.HelpBox(
                                    $"Renderer \"{renderer.name}\" has a null material. Assign a suitable material.",
                                    MessageType.Warning
                                );
                            }

                            canBake = false;
                        }
                    }
                }

                var originalMaterials = GetOriginalMaterials();

                for (var i = 0; i < originalMaterials.Length; i++)
                {
                    var originalMat = originalMaterials[i];

                    if (!m_materialRemap.TryGetValue(originalMat, out var remapped) || remapped == null)
                    {
                        if (drawMessages)
                        {
                            EditorGUILayout.HelpBox(
                                $"Material \"{originalMat.name}\" must be remapped. Assign a material with a shader that supports instanced animation.",
                                MessageType.Warning
                            );
                        }

                        canBake = false;
                    }
                }
            }

            var controller = m_animator.runtimeAnimatorController as AnimatorController;

            if (controller == null)
            {
                if (drawMessages)
                {
                    EditorGUILayout.HelpBox(
                        "The selected animator must have an animator controller assigned.",
                        MessageType.Warning
                    );
                }

                canBake = false;
            }
            else
            {
                var animations = controller.animationClips;

                if (animations.Length == 0)
                {
                    if (drawMessages)
                    {
                        EditorGUILayout.HelpBox(
                            "The assigned animator controller has no animations.",
                            MessageType.Warning
                        );
                    }

                    canBake = false;
                }
            }

            return canBake;
        }

        Material[] GetOriginalMaterials()
        {
            return m_animator
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null)
                .Distinct()
                .ToArray();
        }

        static class Contents
        {
            public static readonly float spacing = 21f;

            public static readonly GUIContent animator = new GUIContent("Animator", "The animator to bake.");

            public static readonly GUILayoutOption mappingWidth = GUILayout.Width(150f);
            public static readonly GUIContent animation = new GUIContent("Clip");
            public static readonly GUIContent frameRate = new GUIContent("Frame Rate");

            public static readonly GUIContent originalMaterial = new GUIContent("Original");
            public static readonly GUIContent instancedMaterial = new GUIContent("Instanced");

            public static readonly GUIContent vertexCompression = new GUIContent(
                "Compression",
                "Reduces the size of the mesh in memory."
            );

            public static readonly GUIContent directory = new GUIContent(
                "Directory",
                "The directory to save the baked data in."
            );

            public static readonly GUILayoutOption directoryMinWidth = GUILayout.MinWidth(0f);
            public static readonly GUIContent directorySelector = new GUIContent("\u2299", "Select a directory.");
            public static readonly GUILayoutOption directorySelectorWidth = GUILayout.Width(22f);

            public static readonly GUIContent bakeButton = new GUIContent("Bake");
            public static readonly GUILayoutOption[] bakeButtonSize = {GUILayout.Width(150f), GUILayout.Height(25f)};
        }
    }
}

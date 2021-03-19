using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;

using UnityEngine;

namespace AnimationInstancing
{
    [Flags]
    public enum DirtyFlags
    {
        None            = 0,
        InstanceCount   = 1 << 0,
        Mesh            = 1 << 1,
        Animation       = 1 << 2,
        Material        = 1 << 3,
    }
    
    public interface IInstanceProvider
    {
        int InstanceCount { get; }
        InstancedMesh Mesh { get; }
        InstancedAnimation Animation { get; }
        DirtyFlags DirtyFlags { get; }
        
        void ClearDirtyFlags();
    }
}

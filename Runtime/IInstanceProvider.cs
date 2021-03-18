using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;

namespace InstancedAnimation
{
    public interface IInstanceProvider
    {
        int InstanceCount { get; }

    }
}

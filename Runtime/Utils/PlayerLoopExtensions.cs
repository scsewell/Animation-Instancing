using UnityEngine.LowLevel;

namespace InstancedAnimation
{
    /// <summary>
    /// A class that contains extension methods used to modify the player loop.
    /// </summary>
    static class PlayerLoopExtensions
    {
        /// <summary>
        /// Recursively finds a subsystem of this system by type.
        /// </summary>
        /// <typeparam name="T">The type of subsystem to find.</typeparam>
        /// <param name="system">The system to search.</param>
        /// <param name="result">The returned subsystem.</param>
        /// <returns>True if a subsystem with a matching type was found; false otherwise.</returns>
        public static bool TryFindSubSystem<T>(this PlayerLoopSystem system, out PlayerLoopSystem result)
        {
            if (system.type == typeof(T))
            {
                result = system;
                return true;
            }

            if (system.subSystemList != null)
            {
                foreach (var subSystem in system.subSystemList)
                {
                    if (subSystem.TryFindSubSystem<T>(out result))
                    {
                        return true;
                    }
                }
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Applies changes made to a subsystem to a system.
        /// </summary>
        /// <param name="system">The system to update.</param>
        /// <param name="subSystemToUpdate">The modified subsystem.</param>
        /// <returns>True if the subsystem was successfully updated; false otherwise.</returns>
        public static bool TryUpdate(this ref PlayerLoopSystem system, PlayerLoopSystem subSystemToUpdate)
        {
            if (system.type == subSystemToUpdate.type)
            {
                system = subSystemToUpdate;
                return true;
            }

            if (system.subSystemList != null)
            {
                for (var i = 0; i < system.subSystemList.Length; i++)
                {
                    if (system.subSystemList[i].TryUpdate(subSystemToUpdate))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Adds a new subsystem to a system.
        /// </summary>
        /// <typeparam name="T">The type of the subsystem to add.</typeparam>
        /// <param name="system">The system to add the subsystem to.</param>
        /// <param name="index">The index of the subsystem in the subsystem array.</param>
        /// <param name="update">The function called to update the new subsystem.</param>
        public static void AddSubSystem<T>(this ref PlayerLoopSystem system, int index, PlayerLoopSystem.UpdateFunction update)
        {
            var subSystems = system.subSystemList;
            var oldLength = subSystems != null ? subSystems.Length : 0;

            var newSubSystems = new PlayerLoopSystem[oldLength + 1];

            for (var i = 0; i < oldLength; i++)
            {
                if (i < index)
                {
                    newSubSystems[i] = subSystems[i];
                }
                else if (i >= index)
                {
                    newSubSystems[i + 1] = subSystems[i];
                }
            }

            newSubSystems[index] = new PlayerLoopSystem
            {
                type = typeof(T),
                updateDelegate = update,
            };

            system.subSystemList = newSubSystems;
        }
    }
}

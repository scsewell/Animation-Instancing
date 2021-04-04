using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace AnimationInstancing
{
    struct NestableNativeSlice<T> where T : struct
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe byte* m_Buffer;
        int m_Stride;
        int m_Length;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        int m_MinIndex;
        int m_MaxIndex;
        AtomicSafetyHandle m_Safety;
#endif

        public static implicit operator NativeSlice<T>(NestableNativeSlice<T> slice)
        {
            return UnsafeUtility.As<NestableNativeSlice<T>, NativeSlice<T>>(ref slice);
        }
        
        public static implicit operator NestableNativeSlice<T>(NativeSlice<T> slice)
        {
            return UnsafeUtility.As<NativeSlice<T>, NestableNativeSlice<T>>(ref slice);
        }
    }
}

using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Unity APIs that differ between the game's Unity 6 and the 2021 reference assemblies the stub build compiles
    /// against. Plugin code calls these instead of the engine members, so it compiles in both builds.
    /// </summary>
    public static class Compat
    {
        public static Vector3 GetVelocity(Rigidbody body)
        {
            if (body == null)
                return Vector3.zero;
#if STUB_GAME
            return body.velocity;
#else
            return body.linearVelocity;
#endif
        }

        public static void SetVelocity(Rigidbody body, Vector3 velocity)
        {
            if (body == null)
                return;
#if STUB_GAME
            body.velocity = velocity;
#else
            body.linearVelocity = velocity;
#endif
        }

        public static Vector3 GetAngularVelocity(Rigidbody body) => body == null ? Vector3.zero : body.angularVelocity;

        /// <summary>Every loaded scene object of type <typeparamref name="T"/>, unordered.</summary>
        public static T[] FindAll<T>(bool includeInactive) where T : Object
        {
#if STUB_GAME
            return Object.FindObjectsOfType<T>(includeInactive);
#else
            return Object.FindObjectsByType<T>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#endif
        }

        /// <summary>Any one loaded scene object of type <typeparamref name="T"/>, or null.</summary>
        public static T FindAny<T>(bool includeInactive) where T : Object
        {
#if STUB_GAME
            return Object.FindObjectOfType<T>(includeInactive);
#else
            return Object.FindAnyObjectByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
#endif
        }
    }
}

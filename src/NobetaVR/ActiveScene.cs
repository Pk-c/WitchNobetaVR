using UnityEngine;

namespace NobetaVR
{
    /// <summary>
    /// The active scene's name, asked once a frame however many times it is wanted.
    ///
    /// Three separate components watch for a scene change, because a scene change is what
    /// invalidates the interface, the fade and the diagnostics all at once, and each of them
    /// forces its own rescan off it. Each was calling <c>GetActiveScene().name</c> from its own
    /// per-frame method — and that property is not a field read: it crosses into il2cpp and
    /// marshals a fresh managed string back out on every call, so a value none of them ever
    /// modified was being rebuilt three times a frame for the rest of the session.
    ///
    /// <para>
    /// Keyed on the frame rather than on Unity's <c>activeSceneChanged</c> event, which would
    /// mean hanging an il2cpp delegate off a static event for the life of the process. The
    /// frame counter answers the same question with nothing to unsubscribe and nothing to
    /// survive a domain that never reloads.
    /// </para>
    ///
    /// <para>
    /// Unity's <c>SceneManager</c> is named in full below because the game has one of its own
    /// in the global namespace -- its per-stage god object -- and an unqualified name resolves
    /// to that one rather than to the engine's.
    /// </para>
    /// </summary>
    internal static class ActiveScene
    {
        private static int _frame = -1;
        private static string _name = string.Empty;

        /// <summary>The active scene's name, or an empty string if there is somehow none.</summary>
        internal static string Name
        {
            get
            {
                if (_frame == Time.frameCount) return _name;
                _frame = Time.frameCount;

                _name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
                return _name;
            }
        }
    }
}

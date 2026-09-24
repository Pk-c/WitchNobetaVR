using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Finds an alpha-blended shader that survived the build.
    ///
    /// Shaders are stripped to what the game itself uses, so the one we want may simply not be
    /// in the build and there is nothing to be done about that at runtime. These are tried in
    /// order of preference; all are alpha-blended, and all are ones a game with a uGUI
    /// interface is overwhelmingly likely to have kept.
    ///
    /// Shared by everything the mod draws in the world, and deliberately fails rather than
    /// falling back to whatever <c>Shader.Find</c> last returned. An opaque shader does not
    /// degrade gracefully here: it turns a transparent panel into a solid slab in front of the
    /// player's face, which is worse than not drawing at all.
    /// </summary>
    internal static class TransparentShader
    {
        private static readonly string[] Candidates =
        {
            "UI/Default",
            "Sprites/Default",
            "Unlit/Transparent",
            "Universal Render Pipeline/Unlit",
        };

        private static Shader _found;
        private static bool _searched;

        public static Shader Find()
        {
            if (_searched) return _found;
            _searched = true;

            foreach (var name in Candidates)
            {
                _found = Shader.Find(name);
                if (_found != null) return _found;
            }

            Plugin.Log.LogError("No alpha-blended shader survived stripping. Anything the mod "
                              + "would draw in the world is left off.");
            return null;
        }
    }
}

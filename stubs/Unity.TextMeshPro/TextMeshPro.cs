// Compile-time stub of TextMeshPro (Unity.TextMeshPro.dll): TMP_Text and its concrete components, with the
// well-known members the plugin uses. Never shipped.
using UnityEngine.UI;

namespace TMPro
{
    public enum TextAlignmentOptions
    {
        TopLeft = 257,
        Top = 258,
        Left = 513,
        Center = 514,
        Right = 516,
        MidlineLeft = 4097
    }

    public enum FontStyles
    {
        Normal = 0,
        Bold = 1,
        Italic = 2,
        UpperCase = 16
    }

    public abstract class TMP_Asset : UnityEngine.ScriptableObject
    {
        public UnityEngine.Material material;
    }

    public class TMP_FontAsset : TMP_Asset
    {
    }

    public enum TextRenderFlags { DontRender = 0, Render = 255 }

    public abstract class TMP_Text : MaskableGraphic
    {
        public TextRenderFlags renderMode { get { throw null; } set { throw null; } }
        public virtual void ForceMeshUpdate(bool ignoreActiveState = false, bool forceTextReparsing = false) { throw null; }
        public virtual string text { get { throw null; } set { throw null; } }
        public float fontSize { get { throw null; } set { throw null; } }
        public float fontSizeMin { get { throw null; } set { throw null; } }
        public float fontSizeMax { get { throw null; } set { throw null; } }
        public bool enableAutoSizing { get { throw null; } set { throw null; } }
        public bool richText { get { throw null; } set { throw null; } }
        public TextAlignmentOptions alignment { get { throw null; } set { throw null; } }
        public FontStyles fontStyle { get { throw null; } set { throw null; } }
        public TMP_FontAsset font { get { throw null; } set { throw null; } }
        public UnityEngine.Material fontSharedMaterial { get { throw null; } set { throw null; } }
    }

    public class TextMeshProUGUI : TMP_Text
    {
    }

    public class TextMeshPro : TMP_Text
    {
    }
}

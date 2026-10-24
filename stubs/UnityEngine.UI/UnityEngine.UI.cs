// Compile-time stub of uGUI (UnityEngine.UI.dll): only the types the game's documented fields and the plugin's menus
// use, with well-known public members of the real package (same names and signatures). Never shipped.
using UnityEngine;
using UnityEngine.Events;

namespace UnityEngine.EventSystems
{
    public abstract class UIBehaviour : MonoBehaviour
    {
    }

    public interface IEventSystemHandler
    {
    }

    public enum MoveDirection
    {
        Left,
        Up,
        Right,
        Down,
        None
    }

    public abstract class AbstractEventData
    {
        public virtual void Use() { throw null; }
        public virtual bool used { get { throw null; } }
    }

    public class BaseEventData : AbstractEventData
    {
        public BaseEventData(EventSystem eventSystem) { throw null; }
        public GameObject selectedObject { get { throw null; } set { throw null; } }
    }

    public class AxisEventData : BaseEventData
    {
        public AxisEventData(EventSystem eventSystem) : base(eventSystem) { throw null; }
        public Vector2 moveVector { get { throw null; } set { throw null; } }
        public MoveDirection moveDir { get { throw null; } set { throw null; } }
    }

    public interface IMoveHandler : IEventSystemHandler
    {
        void OnMove(AxisEventData eventData);
    }

    public class EventSystem : UIBehaviour
    {
        public static EventSystem current { get { throw null; } set { throw null; } }
        public GameObject currentSelectedGameObject { get { throw null; } }
        public void SetSelectedGameObject(GameObject selected) { throw null; }
    }
}

namespace UnityEngine.UI
{
    public abstract class Graphic : UnityEngine.EventSystems.UIBehaviour
    {
        public virtual Color color { get { throw null; } set { throw null; } }
        public RectTransform rectTransform { get { throw null; } }
        public virtual bool raycastTarget { get { throw null; } set { throw null; } }
    }

    public abstract class MaskableGraphic : Graphic
    {
    }

    public class Image : MaskableGraphic
    {
        public enum Type
        {
            Simple,
            Sliced,
            Tiled,
            Filled
        }

        public Sprite sprite { get { throw null; } set { throw null; } }
        public float fillAmount { get { throw null; } set { throw null; } }
        public Type type { get { throw null; } set { throw null; } }
        public float pixelsPerUnitMultiplier { get { throw null; } set { throw null; } }
    }

    public class Text : MaskableGraphic
    {
        public virtual string text { get { throw null; } set { throw null; } }
    }

    public struct Navigation
    {
        public enum Mode
        {
            None = 0,
            Horizontal = 1,
            Vertical = 2,
            Automatic = 3,
            Explicit = 4
        }

        public Mode mode { get { throw null; } set { throw null; } }
        public bool wrapAround { get { throw null; } set { throw null; } }
        public Selectable selectOnUp { get { throw null; } set { throw null; } }
        public Selectable selectOnDown { get { throw null; } set { throw null; } }
        public Selectable selectOnLeft { get { throw null; } set { throw null; } }
        public Selectable selectOnRight { get { throw null; } set { throw null; } }
        public static Navigation defaultNavigation { get { throw null; } }
    }

    public struct ColorBlock
    {
        public Color normalColor { get { throw null; } set { throw null; } }
        public Color highlightedColor { get { throw null; } set { throw null; } }
        public Color pressedColor { get { throw null; } set { throw null; } }
        public Color selectedColor { get { throw null; } set { throw null; } }
        public Color disabledColor { get { throw null; } set { throw null; } }
    }

    public class Selectable : UnityEngine.EventSystems.UIBehaviour
    {
        public bool interactable { get { throw null; } set { throw null; } }
        public virtual bool IsInteractable() { throw null; }
        public Navigation navigation { get { throw null; } set { throw null; } }
        public ColorBlock colors { get { throw null; } set { throw null; } }
        public Graphic targetGraphic { get { throw null; } set { throw null; } }
        public Image image { get { throw null; } set { throw null; } }
        public virtual void Select() { throw null; }
    }

    public class Button : Selectable
    {
        public class ButtonClickedEvent : UnityEvent
        {
        }

        public ButtonClickedEvent onClick { get { throw null; } set { throw null; } }
    }

    public abstract class LayoutGroup : UnityEngine.EventSystems.UIBehaviour
    {
        public RectOffset padding { get { throw null; } set { throw null; } }
        public TextAnchor childAlignment { get { throw null; } set { throw null; } }
    }

    public abstract class HorizontalOrVerticalLayoutGroup : LayoutGroup
    {
        public float spacing { get { throw null; } set { throw null; } }
        public bool childForceExpandWidth { get { throw null; } set { throw null; } }
        public bool childForceExpandHeight { get { throw null; } set { throw null; } }
        public bool childControlWidth { get { throw null; } set { throw null; } }
        public bool childControlHeight { get { throw null; } set { throw null; } }
    }

    public class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup
    {
    }

    public class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup
    {
    }

    public class GridLayoutGroup : LayoutGroup
    {
    }

    public class LayoutElement : UnityEngine.EventSystems.UIBehaviour
    {
        public virtual bool ignoreLayout { get { throw null; } set { throw null; } }
        public virtual float minWidth { get { throw null; } set { throw null; } }
        public virtual float minHeight { get { throw null; } set { throw null; } }
        public virtual float preferredWidth { get { throw null; } set { throw null; } }
        public virtual float preferredHeight { get { throw null; } set { throw null; } }
        public virtual float flexibleWidth { get { throw null; } set { throw null; } }
        public virtual float flexibleHeight { get { throw null; } set { throw null; } }
    }

    public class ContentSizeFitter : UnityEngine.EventSystems.UIBehaviour
    {
        public enum FitMode
        {
            Unconstrained,
            MinSize,
            PreferredSize
        }

        public FitMode horizontalFit { get { throw null; } set { throw null; } }
        public FitMode verticalFit { get { throw null; } set { throw null; } }
    }

    public class CanvasScaler : UnityEngine.EventSystems.UIBehaviour
    {
        public enum ScaleMode
        {
            ConstantPixelSize,
            ScaleWithScreenSize,
            ConstantPhysicalSize
        }

        public enum ScreenMatchMode
        {
            MatchWidthOrHeight,
            Expand,
            Shrink
        }

        public ScaleMode uiScaleMode { get { throw null; } set { throw null; } }
        public Vector2 referenceResolution { get { throw null; } set { throw null; } }
        public ScreenMatchMode screenMatchMode { get { throw null; } set { throw null; } }
        public float matchWidthOrHeight { get { throw null; } set { throw null; } }
    }

    public class GraphicRaycaster : MonoBehaviour
    {
    }

    public static class LayoutRebuilder
    {
        public static void ForceRebuildLayoutImmediate(RectTransform layoutRoot) { throw null; }
    }
}

using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Draws the version stamp on whatever UIDocument it rides, for scenes that
    /// have no menu screen to carry it. The balance contest gets its stamp from
    /// Systems_ContestSetupMenu; the walk contest has no setup menu, so it went
    /// without one entirely until this existed.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_VersionStamp : MonoBehaviour
    {
        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            Systems_UiTheme.ApplyDefaultFont(root);
            Systems_UiTheme.AddVersionStamp(root);
        }
    }
}

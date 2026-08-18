using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Opening menu: title, FIGHT button, roster line, and the mandated
    /// version stamp anchored top-left (project rule). Loads the contest
    /// scene async via UniTask. UI built in code, styled by USS_Contest.uss.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_MainMenu : MonoBehaviour
    {
        [SerializeField] private StyleSheet _styleSheet;
        [SerializeField] private string _contestSceneName = "SCN_TEST_BALANCE_CONTEST";

        private bool _loading;

        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (_styleSheet != null)
            {
                root.styleSheets.Add(_styleSheet);
            }
            Systems_UiTheme.ApplyDefaultFont(root);

            var menuRoot = new VisualElement();
            menuRoot.AddToClassList("menu-root");
            root.Add(menuRoot);

            // Added after menuRoot so it draws above the menu background.
            var stamp = new Label(Application.version);
            stamp.AddToClassList("version-stamp");
            stamp.pickingMode = PickingMode.Ignore;
            root.Add(stamp);

            var title = new Label("PoBox");
            title.AddToClassList("menu-title");
            menuRoot.Add(title);

            var subtitle = new Label("BALANCE CONTEST");
            subtitle.AddToClassList("menu-subtitle");
            menuRoot.Add(subtitle);

            var play = new Button(StartContest) { text = "FIGHT" };
            play.AddToClassList("menu-play");
            menuRoot.Add(play);

            var roster = new Label("Standard   •   Grandma   •   Grandpa");
            roster.AddToClassList("menu-roster");
            menuRoot.Add(roster);
        }

        public void StartContest()
        {
            if (_loading)
            {
                return;
            }
            _loading = true;
            LoadContestAsync().Forget();
        }

        private async UniTaskVoid LoadContestAsync()
        {
            await SceneManager.LoadSceneAsync(_contestSceneName)
                .ToUniTask(cancellationToken: this.GetCancellationTokenOnDestroy());
        }
    }
}

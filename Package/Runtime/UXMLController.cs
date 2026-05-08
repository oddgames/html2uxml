using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace ODDGames.Html2Uxml
{
    /// <summary>
    /// Base for MonoBehaviours that drive UI Toolkit UXML documents.
    /// Owns UIDocument setup, delayed visual-tree wiring, callback cleanup,
    /// and transient object disposal. Generated controllers should override
    /// ConfigureDocument, WireUI, OnWired, and OnUnwire instead of defining
    /// Start or OnDestroy.
    /// </summary>
    public abstract class UXMLController : MonoBehaviour
    {
        private const int MaxWireAttempts = 30;

        private UIDocument _doc;
        private PanelSettings _ownedPanelSettings;
        private bool _wired;
        private bool _destroyed;
        private readonly List<Action> _unwireActions = new();

        protected UIDocument Doc => _doc;
        protected VisualElement Root => _doc != null ? _doc.rootVisualElement : null;
        protected bool IsWired => _wired;
        protected bool IsDestroyed => _destroyed;

        /// <summary>
        /// Resolve or create the UIDocument this controller drives.
        /// Override to host the document on a child or related GameObject.
        /// </summary>
        protected virtual UIDocument ResolveDocument()
        {
            var existing = GetComponent<UIDocument>();
            return existing != null ? existing : gameObject.AddComponent<UIDocument>();
        }

        /// <summary>
        /// Create PanelSettings for this document. Returning non-null transfers
        /// ownership to the controller, which destroys it during teardown.
        /// </summary>
        protected virtual PanelSettings CreatePanelSettings() => null;

        /// <summary>
        /// Assign VisualTreeAsset, PanelSettings-dependent data, or pre-wire
        /// setup before the WireUI retry loop begins.
        /// </summary>
        protected virtual void ConfigureDocument(UIDocument doc) { }

        /// <summary>
        /// Cache element references and wire callbacks via the Wire* helpers.
        /// Return false while the visual tree is not ready yet.
        /// </summary>
        protected abstract bool WireUI(VisualElement root);

        /// <summary>
        /// Called once after WireUI succeeds.
        /// </summary>
        protected virtual void OnWired() { }

        /// <summary>
        /// Called before automatic unwiring during teardown.
        /// </summary>
        protected virtual void OnUnwire() { }

        private void Start() => StartCoroutine(WireRoutine());

        private IEnumerator WireRoutine()
        {
            Trace("Start");
            _doc = ResolveDocument();
            if (_doc == null)
            {
                Debug.LogError($"[{GetType().Name}] ResolveDocument returned null; destroying.");
                Destroy(gameObject);
                yield break;
            }

            var panelSettings = CreatePanelSettings();
            if (panelSettings != null)
            {
                _ownedPanelSettings = panelSettings;
                _doc.panelSettings = panelSettings;
            }

            ConfigureDocument(_doc);

            for (int i = 0; i < MaxWireAttempts; i++)
            {
                yield return null;
                if (_destroyed || _doc == null)
                    yield break;

                var root = _doc.rootVisualElement;
                if (root == null)
                    continue;

                bool ok;
                try
                {
                    ok = WireUI(root);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[{GetType().Name}] WireUI threw; destroying: {e}");
                    Destroy(gameObject);
                    yield break;
                }

                if (!ok)
                    continue;

                _wired = true;
                Trace("Wired");
                try
                {
                    OnWired();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[{GetType().Name}] OnWired threw: {e}");
                }
                yield break;
            }

            Debug.LogError($"[{GetType().Name}] Failed to wire UI after {MaxWireAttempts} frames; destroying.");
            Destroy(gameObject);
        }

        /// <summary>
        /// Wire a Button.clicked handler. Auto-unwired on destroy.
        /// </summary>
        protected void WireClick(Button button, Action handler)
        {
            if (handler == null)
                return;
            if (button == null)
            {
                WarnNullTarget(nameof(WireClick));
                return;
            }
            button.clicked += handler;
            _unwireActions.Add(() => button.clicked -= handler);
        }

        /// <summary>
        /// Attach a Clickable manipulator for non-Button click actions.
        /// Auto-removed on destroy.
        /// </summary>
        protected void WireClickable(VisualElement element, Action handler)
        {
            if (handler == null)
                return;
            if (element == null)
            {
                WarnNullTarget(nameof(WireClickable));
                return;
            }
            var manipulator = new Clickable(handler);
            element.AddManipulator(manipulator);
            _unwireActions.Add(() => element.RemoveManipulator(manipulator));
        }

        /// <summary>
        /// Register a UI Toolkit event callback. Auto-unregistered on destroy.
        /// </summary>
        protected void WireCallback<TEvent>(VisualElement element, EventCallback<TEvent> handler)
            where TEvent : EventBase<TEvent>, new()
        {
            if (handler == null)
                return;
            if (element == null)
            {
                WarnNullTarget(nameof(WireCallback));
                return;
            }
            element.RegisterCallback(handler);
            _unwireActions.Add(() => element.UnregisterCallback(handler));
        }

        /// <summary>
        /// Register a value-changed callback on any INotifyValueChanged field.
        /// Auto-unregistered on destroy.
        /// </summary>
        protected void WireValueChanged<T>(
            INotifyValueChanged<T> field,
            EventCallback<ChangeEvent<T>> handler)
        {
            if (handler == null)
                return;
            if (field == null)
            {
                WarnNullTarget(nameof(WireValueChanged));
                return;
            }
            field.RegisterValueChangedCallback(handler);
            _unwireActions.Add(() => field.UnregisterValueChangedCallback(handler));
        }

        /// <summary>
        /// Track an arbitrary unwire delegate for reverse-order teardown.
        /// </summary>
        protected void TrackUnwire(Action unwire)
        {
            if (unwire != null)
                _unwireActions.Add(unwire);
        }

        /// <summary>
        /// Track a transient Unity object that this controller owns.
        /// </summary>
        protected void TrackDispose(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            _unwireActions.Add(() =>
            {
                if (obj != null)
                    Destroy(obj);
            });
        }

        /// <summary>
        /// Add a Resources stylesheet to an element if it is not already present.
        /// </summary>
        protected static void AddStyleSheet(VisualElement target, string resourcePath)
        {
            if (target == null || string.IsNullOrEmpty(resourcePath))
                return;
            var sheet = Resources.Load<StyleSheet>(resourcePath);
            if (sheet == null || target.styleSheets.Contains(sheet))
                return;
            target.styleSheets.Add(sheet);
        }

        private void UnwireAll()
        {
            for (int i = _unwireActions.Count - 1; i >= 0; i--)
            {
                try
                {
                    _unwireActions[i]?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{GetType().Name}] unwire {i} threw: {e.Message}");
                }
            }
            _unwireActions.Clear();
        }

        private void OnDestroy()
        {
            if (_destroyed)
                return;
            _destroyed = true;
            Trace("Destroy");
            try
            {
                OnUnwire();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{GetType().Name}] OnUnwire threw: {e.Message}");
            }
            UnwireAll();
            if (_ownedPanelSettings != null)
            {
                Destroy(_ownedPanelSettings);
                _ownedPanelSettings = null;
            }
        }

        [Conditional("UNITY_EDITOR")]
        private void WarnNullTarget(string helper)
        {
            Debug.LogWarning(
                $"[{GetType().Name}] {helper}: target was null. " +
                "Likely a misnamed Q<>() lookup or a control missing from the UXML.");
        }

        [Conditional("UXML_TRACE")]
        protected void Trace(string evt)
        {
            Debug.Log($"[UXML] {GetType().Name} {evt} (frame={Time.frameCount})");
        }
    }
}

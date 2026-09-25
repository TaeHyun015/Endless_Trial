using System;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SephiriaTrial
{
    // Title.unity/UI_SaveProfilePanel is copied into the Trial bundle. Its
    // original profile/host callbacks are removed during the bundle build.
    public sealed class TrialSaveSlotMenu : MonoBehaviour
    {
        private static TrialSaveSlotMenu? instance;
        private Action<int>? onSelect;
        private UI_SaveProfilePanel? nativePanel;
        private TextMeshProUGUI? title;
        private Canvas? canvas;
        private GraphicRaycaster? raycaster;
        private TrialSaveSlotControl? control;
        private bool controlRegistered;
        private bool closing;
        private bool selectionCommitted;

        public static bool Open(Action<int> selected)
        {
            if (instance != null)
            {
                instance.onSelect = selected;
                instance.Refresh();
                return true;
            }
            if (!EndlessMod.TryGetTrialSavePanelPrefab(out GameObject prefab))
            {
                Debug.LogError("[시련] 원본 UI_SaveProfilePanel 복제 프리팹이 번들에 없습니다.");
                return false;
            }

            GameObject root = new GameObject("TrialSaveSlotMenu", typeof(RectTransform));
            TrialSaveSlotMenu menu = root.AddComponent<TrialSaveSlotMenu>();
            menu.onSelect = selected;
            if (!menu.Build(prefab))
            {
                Destroy(root);
                return false;
            }
            instance = menu;
            menu.Refresh();
            Debug.Log("[시련] 원본 UI_SaveProfilePanel 기반 시련 슬롯 화면 열기");
            return true;
        }

        public static void RefreshOpen()
        {
            if (instance != null) instance.Refresh();
        }

        public static void CloseOpen()
        {
            if (instance == null) return;
            TrialSaveSlotMenu menu = instance;
            instance = null;
            menu.Close();
        }

        private bool Build(GameObject prefab)
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            // The native SaveProfilePanel lives on the Panels canvas, while
            // UI_Cursor is rendered by the higher AppUI canvas. Keep this
            // modal panel above the other panels but below the game's cursor.
            Canvas? cursorCanvas = UI_Cursor.Current != null
                ? UI_Cursor.Current.GetComponentInParent<Canvas>() : null;
            if (cursorCanvas != null)
            {
                canvas.sortingLayerID = cursorCanvas.sortingLayerID;
                canvas.sortingOrder = cursorCanvas.sortingOrder - 1;
            }
            else
            {
                Canvas? nativePanelCanvas = GameObject.Find("[UI] Panels")?.GetComponent<Canvas>();
                if (nativePanelCanvas != null)
                {
                    canvas.sortingLayerID = nativePanelCanvas.sortingLayerID;
                    canvas.sortingOrder = nativePanelCanvas.sortingOrder + 1;
                }
                else
                {
                    canvas.sortingOrder = 32767;
                    Debug.LogWarning("[시련] 원본 UI Canvas를 찾지 못해 슬롯 화면의 정렬 순서를 기본값으로 사용합니다.");
                }
            }
            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // Title.unity's parent Canvas uses these values for UI_SaveProfilePanel.
            scaler.referenceResolution = new Vector2(640f, 360f);
            scaler.referencePixelsPerUnit = 16f;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            raycaster = gameObject.AddComponent<GraphicRaycaster>();
            gameObject.AddComponent<CanvasGroup>();

            GameObject visual = Instantiate(prefab, transform, false);
            visual.name = "TrialSaveProfilePanel";
            nativePanel = visual.GetComponent<UI_SaveProfilePanel>();
            if (nativePanel == null || nativePanel.buttons == null ||
                nativePanel.buttons.Length != EndlessMod.TrialSlotCount)
            {
                Debug.LogError("[시련] 복제한 저장 화면에 원본 슬롯 버튼 3개가 없습니다.");
                return false;
            }
            nativePanel.enabled = false;
            nativePanel.titleLobby = null;
            RectTransform visualRect = (RectTransform)visual.transform;
            visualRect.anchorMin = Vector2.zero;
            visualRect.anchorMax = Vector2.one;
            visualRect.offsetMin = Vector2.zero;
            visualRect.offsetMax = Vector2.zero;
            CanvasGroup? panelGroup = visual.GetComponent<CanvasGroup>();
            if (panelGroup != null)
            {
                panelGroup.interactable = true;
                panelGroup.blocksRaycasts = true;
            }
            // Decorative images and text in the copied title UI may cover its buttons.
            // Only the panel backdrop and actual button/card targets need raycasts.
            foreach (Graphic graphic in visual.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = false;
            Graphic? backdrop = visual.GetComponent<Graphic>();
            if (backdrop != null) backdrop.raycastTarget = true;
            UI_LocalizationStringText? nativeTitleLocalization =
                visual.GetComponentsInChildren<UI_LocalizationStringText>(true)
                    .FirstOrDefault(localization => localization != null &&
                        localization.valueString != null &&
                        localization.valueString.key == "SaveProfile_PanelName");
            title = nativeTitleLocalization != null ? nativeTitleLocalization.text : null;
            if (nativeTitleLocalization != null)
                nativeTitleLocalization.enabled = false;
            if (title == null)
                title = visual.GetComponentsInChildren<TextMeshProUGUI>(true)
                    .FirstOrDefault(text => text != null && text.text == "Save Profiles");

            GameObject? firstSelectable = null;
            for (int i = 0; i < nativePanel.buttons.Length; i++)
            {
                int slot = i + 1;
                UI_SaveProfileButton profile = nativePanel.buttons[i];
                Transform? select = profile != null ? profile.transform.Find("Button") : null;
                Button? selectButton = select != null ? select.GetComponent<Button>() : null;
                Button? deleteButton = profile?.deleteButton != null
                    ? profile.deleteButton.GetComponent<Button>() : null;
                if (profile == null || profile.profileInfoText == null ||
                    profile.deleteButton == null || selectButton == null || deleteButton == null)
                {
                    Debug.LogError("[시련] 복제한 저장 화면의 " + slot + "번 슬롯 연결이 빠졌습니다.");
                    return false;
                }
                profile.enabled = false;
                Graphic? cardGraphic = profile.GetComponent<Graphic>();
                if (cardGraphic != null) cardGraphic.raycastTarget = true;
                if (selectButton.targetGraphic != null) selectButton.targetGraphic.raycastTarget = true;
                if (deleteButton.targetGraphic != null) deleteButton.targetGraphic.raycastTarget = true;
                selectButton.interactable = true;
                deleteButton.interactable = true;
                if (firstSelectable == null) firstSelectable = selectButton.gameObject;
                profile.gameObject.AddComponent<TrialSaveSlotClickTarget>().Configure(() => Select(slot));
                if (select != null)
                {
                    foreach (UI_LocalizationStringText localization in
                        select.GetComponentsInChildren<UI_LocalizationStringText>(true))
                        localization.enabled = false;
                }
                selectButton.onClick.AddListener(() => Select(slot));
                deleteButton.onClick.AddListener(() => ConfirmDelete(slot));
            }

            Transform? leave = visual.transform.Find("LeaveButton");
            Button? closeButton = leave != null ? leave.GetComponent<Button>() : null;
            if (closeButton == null)
            {
                Debug.LogError("[시련] 원본 저장 화면의 닫기 버튼을 찾지 못했습니다.");
                return false;
            }
            if (closeButton.targetGraphic != null) closeButton.targetGraphic.raycastTarget = true;
            closeButton.interactable = true;
            closeButton.onClick.AddListener(Close);
            visual.SetActive(true);
            UIRoot? nativeRoot = UIManager.Instance?.GetElement<UI_MessageBoxHolder>()?.ParentRoot;
            if (nativeRoot == null)
            {
                Debug.LogError("[시련] 게임 UI의 입력 루트를 찾지 못했습니다.");
                return false;
            }
            control = gameObject.AddComponent<TrialSaveSlotControl>();
            control.Configure(this);
            control.hasControl = true;
            control.isPlayerUITHing = true;
            control.canCloseControlWithESC = true;
            control.defaultSelectable = firstSelectable;
            control.SetRoot(nativeRoot);
            control.AddControlToParent();
            controlRegistered = true;
            return true;
        }

        public void Refresh()
        {
            if (nativePanel == null) return;
            if (title != null)
                title.text = EndlessMod.GetSafeText("trial.slot.title", "시련 저장 슬롯");
            for (int i = 0; i < nativePanel.buttons.Length; i++)
            {
                UI_SaveProfileButton profile = nativePanel.buttons[i];
                if (profile == null) continue;
                profile.profileInfoText.text = EndlessMod.GetTrialSlotSummary(i + 1).Replace(" · ", "\n");
                Transform? select = profile.transform.Find("Button");
                if (select != null)
                {
                    TextMeshProUGUI? label = select.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (label != null)
                        label.text = EndlessMod.GetSafeText("trial.slot.select", "세이브 선택");
                }
                profile.deleteButton.SetActive(EndlessMod.CanDeleteTrialSlot(i + 1));
                if (profile.selectedImage != null)
                    profile.selectedImage.gameObject.SetActive(false);
            }
        }

        private void Select(int slot)
        {
            if (selectionCommitted) return;
            selectionCommitted = true;
            Debug.Log("[시련] 저장 슬롯 선택: " + slot);
            Action<int>? callback = onSelect;
            Close();
            callback?.Invoke(slot);
        }

        private void ConfirmDelete(int slot)
        {
            UI_MessageBoxHolder? holder = UIManager.Instance?.GetElement<UI_MessageBoxHolder>();
            if (holder == null) return;
            string prompt = string.Format(EndlessMod.GetSafeText("trial.slot.delete_confirm",
                "시련 슬롯 {0}을 삭제하시겠습니까?"), slot);
            SetVisible(false);
            UI_MessageBox confirmation = holder.OpenYesNo(prompt, () =>
            {
                TrialController? controller = TrialController.Instance;
                if (controller == null)
                    EndlessMod.ShowLocalizedSystemMessage("trial.msg.slot_unavailable");
                else
                    controller.DeleteTrialSlotFromHost(slot);
            }, () => { }, false);
            EndlessMod.TrackTrialPopup(confirmation, "trial.slot.delete_confirm", slot);
            confirmation.onClosed += () =>
            {
                if (this == null) return;
                SetVisible(true);
                StartCoroutine(RefreshAfterDelete());
            };
        }

        private void SetVisible(bool visible)
        {
            if (canvas != null) canvas.enabled = visible;
            if (raycaster != null) raycaster.enabled = visible;
        }

        private IEnumerator RefreshAfterDelete()
        {
            yield return new WaitForSecondsRealtime(0.2f);
            Refresh();
        }

        private void Close()
        {
            if (closing) return;
            closing = true;
            if (ReferenceEquals(instance, this)) instance = null;
            ReleaseControl();
            Destroy(gameObject);
        }

        internal void CloseFromEsc() => Close();

        private void ReleaseControl()
        {
            if (!controlRegistered || control == null || UIManager.Instance == null || control.ParentRoot == null) return;
            controlRegistered = false;
            control.RemoveControlFromParent();
        }

        private void OnDestroy()
        {
            ReleaseControl();
            if (ReferenceEquals(instance, this)) instance = null;
        }
    }

    // UIInputModule sends ESC to the top UIBase in UIManager's control stack.
    // The copied profile panel itself stays disabled so its title-scene save
    // callbacks cannot run in the trial lobby.
    public sealed class TrialSaveSlotControl : UIBase
    {
        private TrialSaveSlotMenu? owner;

        internal void Configure(TrialSaveSlotMenu menu) => owner = menu;

        public override void CloseFromEsc() => owner?.CloseFromEsc();
    }

    // A click on the empty part of a native profile card selects that card too.
    // Unity sends button clicks to the child Button first, so it runs only once.
    public sealed class TrialSaveSlotClickTarget : MonoBehaviour, IPointerClickHandler
    {
        private Action? onClick;

        public void Configure(Action callback) => onClick = callback;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                onClick?.Invoke();
        }
    }
}

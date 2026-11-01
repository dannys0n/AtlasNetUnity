using System;
using TMPro;
using UnityEngine;

namespace AtlasNet.M2
{
    public enum M2SlideLayout
    {
        FullScreen,
        [InspectorName("Left panel / Top in portrait")] LeftPanel,
        [InspectorName("Right panel / Bottom in portrait")] RightPanel
    }

    [Serializable]
    public sealed class M2Slide
    {
        public string title;
        public M2SlideLayout layout;
        [Tooltip("Fraction of screen width in landscape, or height in portrait.")]
        [Range(.25f, .75f)] public float panelWidth = .55f;
        public Texture2D image;
        [Tooltip("Animated GIFs are supplied as ordered frames. No decoder runs during the presentation.")]
        public Texture2D[] frames = Array.Empty<Texture2D>();
        [Min(.1f)] public float framesPerSecond = 12;
    }

    /// <summary>Renders the replicated presentation state using this viewer's screen layout.</summary>
    public sealed class M2Slides : MonoBehaviour
    {
        static readonly M2Slide[] PreviewSlides =
        {
            new() { title = "FULL-SCREEN PREVIEW", layout = M2SlideLayout.FullScreen },
            new() { title = "LEFT-PANEL PREVIEW", layout = M2SlideLayout.LeftPanel },
            new() { title = "RIGHT-PANEL PREVIEW", layout = M2SlideLayout.RightPanel }
        };
        M2Presentation presentation;
        RectTransform panel, mediaArea;
        UnityEngine.UI.RawImage picture;
        UnityEngine.UI.AspectRatioFitter aspect;
        TMP_Text heading, placeholder, footer;
        Camera view;
        Rect originalViewport;
        Vector2 layoutSize;
        int index = -1;
        double started;
        M2Slide current;
        public bool Visible => panel && panel.gameObject.activeSelf;
        public int Index => index;
        public bool Partial => Visible && current != null && current.layout != M2SlideLayout.FullScreen;

        public void Initialize(M2Presentation owner, Transform canvas, TMP_FontAsset font)
        {
            presentation = owner;
            panel = new GameObject("Presentation overlay", typeof(RectTransform), typeof(UnityEngine.UI.Image)).GetComponent<RectTransform>();
            panel.SetParent(canvas, false);
            panel.GetComponent<UnityEngine.UI.Image>().color = new Color(.025f, .04f, .065f, 1);
            mediaArea = new GameObject("Media area", typeof(RectTransform)).GetComponent<RectTransform>();
            mediaArea.SetParent(panel, false);
            picture = new GameObject("Slide media", typeof(RectTransform), typeof(UnityEngine.UI.RawImage), typeof(UnityEngine.UI.AspectRatioFitter)).GetComponent<UnityEngine.UI.RawImage>();
            picture.transform.SetParent(mediaArea, false);
            picture.raycastTarget = false;
            aspect = picture.GetComponent<UnityEngine.UI.AspectRatioFitter>();
            aspect.aspectMode = UnityEngine.UI.AspectRatioFitter.AspectMode.FitInParent;
            heading = Label("Slide title", font, 28);
            SetRect(heading.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(20, -76), new Vector2(-20, -20));
            placeholder = Label("Empty media preview", font, 25);
            SetRect(placeholder.rectTransform, Vector2.zero, Vector2.one, new Vector2(30, 100), new Vector2(-30, -100));
            footer = Label("Slide navigation", font, 18);
            SetRect(footer.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(16, 16), new Vector2(-16, 60));
            panel.gameObject.SetActive(false);
        }

        TMP_Text Label(string name, TMP_FontAsset font, float size)
        {
            var text = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            text.transform.SetParent(panel, false);
            text.font = font; text.fontSize = size; text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center; text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            return text;
        }

        static void SetRect(RectTransform rect, Vector2 min, Vector2 max, Vector2 insetMin, Vector2 insetMax)
        {
            rect.anchorMin = min; rect.anchorMax = max;
            rect.offsetMin = insetMin; rect.offsetMax = insetMax;
        }

        int Count(M2Stage stage) => stage.slides.Length > 0 ? stage.slides.Length :
            stage.screenshots.Length > 0 ? stage.screenshots.Length : stage.previewSlidesWhenEmpty ? PreviewSlides.Length : 0;

        public int SlideCount => presentation.Stage ? Count(presentation.Stage) : 0;

        public void Apply(int selectedIndex, double elapsed)
        {
            var stage = presentation.Stage;
            if (!stage || selectedIndex < 0 || selectedIndex >= Count(stage)) { Hide(); return; }
            int count = Count(stage);
            index = selectedIndex;
            current = stage.slides.Length > 0 ? stage.slides[index] : stage.screenshots.Length > 0
                ? new M2Slide { title = stage.screenshots[index] ? stage.screenshots[index].name : "Empty slide", image = stage.screenshots[index] }
                : PreviewSlides[index];
            if (current == null) { Hide(); return; }
            if (view != stage.view)
            {
                RestoreCamera();
                view = stage.view;
                if (view) originalViewport = view.rect;
            }
            ApplyLayout();
            started = Time.unscaledTimeAsDouble - Math.Max(0, elapsed);
            footer.text = presentation.manager.IsServer ? $"{index + 1} / {count}    Left / Right: slide    Esc: close" : $"{index + 1} / {count}    Following presenter";
            panel.gameObject.SetActive(true);
            UpdateMedia();
        }

        void ApplyLayout()
        {
            layoutSize = ((RectTransform)panel.parent).rect.size;
            bool portrait = layoutSize.y > layoutSize.x;
            Rect viewport = CalculateViewport(current.layout, current.panelWidth, portrait, originalViewport, out var overlay);
            Vector2 min = overlay.min, max = overlay.max;
            bool previewLeft = ReferenceEquals(current, PreviewSlides[1]);
            bool previewRight = ReferenceEquals(current, PreviewSlides[2]);
            heading.text = portrait && previewLeft ? "TOP-PANEL PREVIEW" : portrait && previewRight ? "BOTTOM-PANEL PREVIEW" : current.title;
            placeholder.text = "MEDIA PLACEHOLDER\n\nAssign an image or animation frames\nto this stage's Slides list.\n\n" +
                (current.layout == M2SlideLayout.FullScreen ? "The game stays running underneath." :
                portrait ? "Your player remains in the game area " + (current.layout == M2SlideLayout.LeftPanel ? "below" : "above") + " this panel." :
                "Your player remains in the game area beside this panel.");
            SetRect(panel, min, max, Vector2.zero, Vector2.zero);
            if (view) view.rect = viewport;
            presentation.hud.SetPresentationViewport(viewport);
            SetRect(mediaArea, Vector2.zero, Vector2.one, new Vector2(20, 76), new Vector2(-20, -88));
        }

        // Normalized rectangles keep both camera and uGUI anchors aligned at every resolution.
        public static Rect CalculateViewport(M2SlideLayout layout, float fraction, bool portrait, Rect original, out Rect overlay)
        {
            float size = Mathf.Clamp(fraction, .25f, .75f);
            overlay = new Rect(0, 0, 1, 1);
            Rect game = original;
            if (layout == M2SlideLayout.FullScreen) return game;
            bool leading = layout == M2SlideLayout.LeftPanel;
            if (portrait)
            {
                overlay = new Rect(0, leading ? 1 - size : 0, 1, size);
                game.height = original.height * (1 - size);
                if (!leading) game.y = original.y + original.height * size;
            }
            else
            {
                overlay = new Rect(leading ? 0 : 1 - size, 0, size, 1);
                game.width = original.width * (1 - size);
                if (leading) game.x = original.x + original.width * size;
            }
            return game;
        }

        void UpdateMedia()
        {
            Texture2D texture = current.image;
            if (current.frames != null && current.frames.Length > 0)
            {
                int frame = (int)(((Time.unscaledTimeAsDouble - started) * Mathf.Max(.1f, current.framesPerSecond)) % current.frames.Length);
                texture = current.frames[frame];
            }
            picture.gameObject.SetActive(texture);
            placeholder.gameObject.SetActive(!texture);
            if (!texture) return;
            if (picture.texture != texture) picture.texture = texture;
            aspect.aspectRatio = (float)texture.width / texture.height;
        }

        void Update()
        {
            if (!Visible) return;
            if (!presentation.manager.IsRunning || presentation.Loading) { Hide(); return; }
            if (((RectTransform)panel.parent).rect.size != layoutSize) ApplyLayout();
            UpdateMedia();
        }

        void RestoreCamera()
        {
            if (view) view.rect = originalViewport;
            if (presentation && presentation.hud) presentation.hud.SetPresentationViewport(new Rect(0, 0, 1, 1));
            view = null;
        }
        public void Hide()
        {
            RestoreCamera();
            if (panel) panel.gameObject.SetActive(false);
            index = -1; current = null;
        }
        void OnDisable() => Hide();
    }
}

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
        [TextArea(3, 12)] public string body;
        public M2SlideLayout layout;
        [Tooltip("Fraction of screen width in landscape, or height in portrait.")]
        [Range(.25f, .75f)] public float panelWidth = .55f;
        [Tooltip("Portrait panel height; zero uses Panel Width.")]
        [Range(0f, .75f)] public float portraitPanelHeight;
        public Texture2D image;
        [Tooltip("Optional MP4 path relative to StreamingAssets; loaded when this slide opens.")]
        public string videoPath;
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
        UnityEngine.UI.RawImage headingBackdrop, footerBackdrop;
        readonly Vector3[] corners = new Vector3[4];
        Camera view;
        Rect originalViewport;
        Vector2 layoutSize;
        int index = -1;
        double started;
        M2Slide current;
        UnityEngine.Video.VideoPlayer video;
        bool videoFailed;
        public bool Visible => panel && panel.gameObject.activeSelf;
        public int Index => index;
        public bool Partial => Visible && current != null && current.layout != M2SlideLayout.FullScreen;

        public void Initialize(M2Presentation owner, Transform canvas, TMP_FontAsset font, Material textBlurMaterial)
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
            headingBackdrop = TextBackdrop(heading, textBlurMaterial);
            footerBackdrop = TextBackdrop(footer, textBlurMaterial);
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

        UnityEngine.UI.RawImage TextBackdrop(TMP_Text label, Material material)
        {
            var background = new GameObject(label.name + " blurred background", typeof(RectTransform), typeof(UnityEngine.UI.RawImage)).GetComponent<UnityEngine.UI.RawImage>();
            background.transform.SetParent(panel, false);
            background.transform.SetSiblingIndex(label.transform.GetSiblingIndex());
            background.material = material;
            background.raycastTarget = false;
            return background;
        }

        void PositionVideoLabel(TMP_Text label, UnityEngine.UI.RawImage background, bool top)
        {
            float maxWidth = Mathf.Max(1, panel.rect.width - 48);
            Vector2 size = label.GetPreferredValues(label.text, maxWidth, Mathf.Infinity);
            size.x = Mathf.Min(maxWidth, Mathf.Ceil(size.x));
            size.y = Mathf.Ceil(size.y);
            var anchor = new Vector2(.5f, top ? 1 : 0);
            var textRect = label.rectTransform;
            textRect.anchorMin = textRect.anchorMax = textRect.pivot = anchor;
            textRect.anchoredPosition = new Vector2(0, top ? -22 : 22);
            textRect.sizeDelta = size;
            var rect = background.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = textRect.anchoredPosition + new Vector2(0, top ? 6 : -6);
            rect.sizeDelta = size + new Vector2(24, 12);
        }

        // Fill the panel with a centered crop on both axes.
        public static Rect VideoCrop(float sourceAspect, float panelAspect)
        {
            if (sourceAspect > panelAspect)
            {
                float width = panelAspect / sourceAspect;
                return new Rect((1 - width) * .5f, 0, width, 1);
            }
            float height = sourceAspect / panelAspect;
            return new Rect(0, (1 - height) * .5f, 1, height);
        }

        void UpdateVideoBackdrop(UnityEngine.UI.RawImage background, Texture texture, Rect videoUv)
        {
            background.texture = texture ? texture : Texture2D.blackTexture;
            background.rectTransform.GetWorldCorners(corners);
            Vector3 min = panel.InverseTransformPoint(corners[0]);
            Vector3 max = panel.InverseTransformPoint(corners[2]);
            Rect area = panel.rect;
            background.uvRect = new Rect(
                videoUv.x + (min.x - area.x) / area.width * videoUv.width,
                videoUv.y + (min.y - area.y) / area.height * videoUv.height,
                (max.x - min.x) / area.width * videoUv.width,
                (max.y - min.y) / area.height * videoUv.height);
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
            StopVideo();
            ApplyLayout();
            started = Time.unscaledTimeAsDouble - Math.Max(0, elapsed);
            footer.text = presentation.manager.IsServer ? $"{index + 1} / {count}    PgUp / PgDn: previous / next    Esc: live view" : $"{index + 1} / {count}    Following presenter";
            panel.gameObject.SetActive(true);
            if (!string.IsNullOrEmpty(current.videoPath)) StartVideo(current.videoPath);
            UpdateMedia();
        }

        void StartVideo(string path)
        {
            if (!video)
            {
                video = gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
                video.playOnAwake = false;
                video.source = UnityEngine.Video.VideoSource.Url;
                video.renderMode = UnityEngine.Video.VideoRenderMode.APIOnly;
                video.audioOutputMode = UnityEngine.Video.VideoAudioOutputMode.None;
                video.isLooping = true;
                video.prepareCompleted += PreparedVideo;
                video.errorReceived += VideoError;
            }
            videoFailed = false;
            placeholder.text = "Loading preview...";
            video.url = Application.streamingAssetsPath.TrimEnd('/') + "/" + path;
            video.Prepare();
        }

        void PreparedVideo(UnityEngine.Video.VideoPlayer player)
        {
            if (current == null || string.IsNullOrEmpty(current.videoPath)) return;
            double elapsed = Math.Max(0, Time.unscaledTimeAsDouble - started);
            if (player.canSetTime && player.length > 0) player.time = elapsed % player.length;
            player.Play();
        }

        void VideoError(UnityEngine.Video.VideoPlayer player, string message)
        {
            videoFailed = true;
            placeholder.text = "Preview could not be loaded.";
            Debug.LogWarning("M2 video preview: " + message, this);
        }

        void StopVideo()
        {
            if (video) video.Stop();
            if (picture) picture.texture = null;
        }

        void ApplyLayout()
        {
            layoutSize = ((RectTransform)panel.parent).rect.size;
            bool portrait = layoutSize.y > layoutSize.x;
            Rect viewport = CalculateViewport(current.layout, portrait && current.portraitPanelHeight > 0 ? current.portraitPanelHeight : current.panelWidth, portrait, originalViewport, out var overlay);
            Vector2 min = overlay.min, max = overlay.max;
            bool previewLeft = ReferenceEquals(current, PreviewSlides[1]);
            bool previewRight = ReferenceEquals(current, PreviewSlides[2]);
            heading.text = portrait && previewLeft ? "TOP-PANEL PREVIEW" : portrait && previewRight ? "BOTTOM-PANEL PREVIEW" : current.title;
            placeholder.text = "MEDIA PLACEHOLDER\n\nAssign an image or animation frames\nto this stage's Slides list.\n\n" +
                (current.layout == M2SlideLayout.FullScreen ? "The game stays running underneath." :
                portrait ? "Your player remains in the game area " + (current.layout == M2SlideLayout.LeftPanel ? "below" : "above") + " this panel." :
                "Your player remains in the game area beside this panel.");
            bool textSlide = !string.IsNullOrEmpty(current.body);
            if (textSlide) placeholder.text = current.body;
            placeholder.alignment = textSlide ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Center;
            placeholder.enableAutoSizing = false;
            placeholder.fontSizeMin = 18;
            placeholder.fontSizeMax = 32;
            placeholder.fontSize = textSlide ? 32 : 25;
            SetRect(placeholder.rectTransform, new Vector2(.08f, .16f), new Vector2(.92f, .8f), Vector2.zero, Vector2.zero);
            SetRect(panel, min, max, Vector2.zero, Vector2.zero);
            if (view) view.rect = viewport;
            presentation.hud.SetPresentationViewport(viewport);
            bool isVideo = !string.IsNullOrEmpty(current.videoPath);
            headingBackdrop.gameObject.SetActive(isVideo);
            footerBackdrop.gameObject.SetActive(isVideo);
            aspect.enabled = !isVideo;
            if (isVideo)
            {
                SetRect(mediaArea, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                SetRect(picture.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            }
            else
            {
                picture.uvRect = new Rect(0, 0, 1, 1);
                heading.rectTransform.pivot = footer.rectTransform.pivot = new Vector2(.5f, .5f);
                SetRect(heading.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(20, -76), new Vector2(-20, -20));
                SetRect(footer.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(16, 16), new Vector2(-16, 60));
                SetRect(mediaArea, Vector2.zero, Vector2.one, new Vector2(20, 76), new Vector2(-20, -88));
            }
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
            if (!string.IsNullOrEmpty(current.videoPath))
            {
                var frame = video && video.isPrepared && !videoFailed ? video.texture : null;
                picture.gameObject.SetActive(frame);
                placeholder.gameObject.SetActive(!frame);
                if (frame)
                {
                    picture.texture = frame;
                    picture.uvRect = VideoCrop((float)frame.width / frame.height, panel.rect.width / panel.rect.height);
                }
                PositionVideoLabel(heading, headingBackdrop, true);
                PositionVideoLabel(footer, footerBackdrop, false);
                UpdateVideoBackdrop(headingBackdrop, frame, picture.uvRect);
                UpdateVideoBackdrop(footerBackdrop, frame, picture.uvRect);
                return;
            }
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
            StopVideo();
            RestoreCamera();
            if (panel) panel.gameObject.SetActive(false);
            index = -1; current = null;
        }
        void OnDisable() => Hide();
    }
}
